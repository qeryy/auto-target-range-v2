"""会话用量归属：运行汇总里的快照可能是全场累计，不能整份当成本会话用量。

现场：一道 50 秒解完的题显示 1.3 亿 token——网关恢复的是共享账本，
summary 里写的 `used_tokens` 是**全工作区**的，而本会话审计里只有 7 次调用。
"""

from __future__ import annotations

import contextlib
import json
import pathlib
import tempfile
import unittest
from pathlib import Path

from spiritscout.web.session_usage import session_usage

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


def _audit(path: Path, call_ids: list[str]) -> None:
    """写一份最小审计流：只要 model.call 带 call_id。"""
    with path.open("w", encoding="utf-8") as fh:
        for cid in call_ids:
            fh.write(json.dumps({"type": "model.call", "actor": "ctf-web",
                                 "detail": {"call_id": cid, "model": "m"}}) + "\n")


def _fallback(rows: list[tuple[str, int]]) -> dict:
    series = [{"call_id": cid, "tokens": tok, "cost_usd": 0.0, "model": "m"}
              for cid, tok in rows]
    return {"used_tokens": sum(t for _, t in rows), "used_usd": 0.0,
            "calls": len(series), "call_series": series}


class SessionUsageAttributionTests(unittest.TestCase):
    def _workspace(self) -> tuple[Path, Path]:
        root = _tmpdir()
        ws = root / "sessions" / "sess-x"
        ws.mkdir(parents=True)
        return root, ws

    def test_fallback_is_filtered_by_audit_call_ids(self) -> None:
        """全场快照里只有本会话那几条算数。"""
        root, ws = self._workspace()
        audit = ws / "audit.jsonl"
        _audit(audit, ["call-a", "call-b"])
        # 快照里混着别的会话的历史
        fb = _fallback([("call-a", 1000), ("call-b", 2000), ("call-old", 99_000_000)])
        got = session_usage(ws, audit, root=root, fallback=fb)
        self.assertEqual(got["used_tokens"], 3000, "只算本会话的两条")
        self.assertEqual(got["calls"], 2)

    def test_no_audit_ids_keeps_fallback_untouched(self) -> None:
        """审计里没记录（旧会话）：保留原样，不猜。"""
        root, ws = self._workspace()
        audit = ws / "audit.jsonl"
        _audit(audit, [])
        fb = _fallback([("call-a", 5000)])
        got = session_usage(ws, audit, root=root, fallback=fb)
        self.assertEqual(got["used_tokens"], 5000)

    def test_fallback_without_series_is_harmless(self) -> None:
        """快照里没有逐笔序列时不动它（老数据没有 call_series）。"""
        root, ws = self._workspace()
        audit = ws / "audit.jsonl"
        _audit(audit, ["call-a"])
        fb = {"used_tokens": 4200, "used_usd": 0.0, "calls": 3}
        got = session_usage(ws, audit, root=root, fallback=fb)
        self.assertEqual(got["used_tokens"], 4200)


if __name__ == "__main__":
    unittest.main()
