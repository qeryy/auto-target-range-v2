"""git 调用的两个必需环境设定：不交互、路径不转义。

现场背景：灵枢是后台服务，`git push` 需要凭证时如果没关掉交互式提示，git 会
停下来等用户名密码——用户看到的是「点了提交之后卡住」，而不是「缺凭证」。
"""
from __future__ import annotations

import contextlib
import os
import pathlib
import subprocess
import tempfile
import unittest
from unittest import mock

from spiritscout.knowledge import sync

# —— 临时目录自动回收 ——
# 现场：包目录下曾堆了 1323 个 tmpXXXXXXXX（mkdtemp 只建不删）。而且这台机器上
# 系统临时目录对服务进程不可写时，tempfile 会回退到**当前工作目录**，于是全堆在
# 包目录里。这里统一走 TemporaryDirectory，用完由 cleanup 删。
_TMP_HOLDERS: list = []


def _tmpdir():
    td = tempfile.TemporaryDirectory(prefix="sc-test-")
    _TMP_HOLDERS.append(td)
    return pathlib.Path(td.name)


def _cleanup_tmpdirs() -> None:
    while _TMP_HOLDERS:
        holder = _TMP_HOLDERS.pop()
        with contextlib.suppress(OSError):
            holder.cleanup()


def tearDownModule() -> None:  # noqa: N802 —— unittest 约定
    _cleanup_tmpdirs()


class GitHelperEnvTests(unittest.TestCase):
    def setUp(self) -> None:
        self.repo = _tmpdir()
        subprocess.run(["git", "init", "-q", str(self.repo)], check=True)

    def test_terminal_prompt_is_disabled(self) -> None:
        """必须把 GIT_TERMINAL_PROMPT=0 传给子进程。"""
        with mock.patch.object(sync.subprocess, "run",
                               wraps=subprocess.run) as spy:
            sync._git(self.repo, "status")
        kwargs = spy.call_args.kwargs
        self.assertEqual(kwargs["env"].get("GIT_TERMINAL_PROMPT"), "0",
                         "没关交互提示：git 会卡在等输入")

    def test_stdin_is_closed(self) -> None:
        """stdin 也关掉，双保险。"""
        with mock.patch.object(sync.subprocess, "run",
                               wraps=subprocess.run) as spy:
            sync._git(self.repo, "status")
        self.assertIs(spy.call_args.kwargs["stdin"], subprocess.DEVNULL)

    def test_quotepath_stays_off(self) -> None:
        """中文路径不能被转义——否则后续 git show 对不上。"""
        with mock.patch.object(sync.subprocess, "run",
                               wraps=subprocess.run) as spy:
            sync._git(self.repo, "status")
        argv = spy.call_args.args[0]
        self.assertIn("core.quotepath=false", argv)

    def test_existing_environment_is_preserved(self) -> None:
        """只加一个变量，别把 PATH 之类冲掉。"""
        with mock.patch.dict(os.environ, {"SPIRITSCOUT_TEST_MARK": "keep-me"}):
            with mock.patch.object(sync.subprocess, "run",
                                   wraps=subprocess.run) as spy:
                sync._git(self.repo, "status")
        env = spy.call_args.kwargs["env"]
        self.assertEqual(env.get("SPIRITSCOUT_TEST_MARK"), "keep-me")
        self.assertIn("PATH", env)


class PushAuthMessageTests(unittest.TestCase):
    """认证失败时要把「怎么配凭证」说清楚，而不是抛 git 原文。"""

    def _err(self, stderr: str) -> str:
        result = subprocess.CompletedProcess(args=["git"], returncode=128,
                                             stdout="", stderr=stderr)
        try:
            sync._git_ok(result, "推送")
        except Exception as exc:  # noqa: BLE001
            return str(exc)
        return ""

    def test_missing_username_points_at_credentials(self) -> None:
        msg = self._err("fatal: could not read Username for 'https://github.com': "
                        "terminal prompts disabled")
        self.assertIn("gh auth login", msg)
        self.assertIn("SSH", msg)

    def test_authentication_failed_points_at_credentials(self) -> None:
        msg = self._err("remote: Invalid username or password.\n"
                        "fatal: Authentication failed for 'https://github.com/x/y'")
        self.assertIn("凭证", msg)

    def test_ignored_path_says_gitignore_not_credentials(self) -> None:
        """被 .gitignore 挡住时不能误导成凭证问题。"""
        msg = self._err("The following paths are ignored by one of your .gitignore files:\n"
                        "experience-export/inbox/x.json\n"
                        "hint: Disable this message with "
                        '"git config set advice.addIgnoredFile false"')
        self.assertIn("gitignore", msg.lower())
        self.assertNotIn("gh auth login", msg)

    def test_other_failure_keeps_raw_detail(self) -> None:
        msg = self._err("fatal: something else broke")
        self.assertIn("something else broke", msg)


if __name__ == "__main__":
    unittest.main()
