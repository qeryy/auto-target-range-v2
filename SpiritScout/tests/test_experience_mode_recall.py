"""经验按任务大类排序：同类排前面，别的大类压后（不丢，只是顺序不同）。"""
from __future__ import annotations

import contextlib
import json
import pathlib
import sqlite3
import tempfile
import unittest
from pathlib import Path

from spiritscout.knowledge.experience import DEFAULT_MODE, read_recent

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


def _make_db(rows: list[tuple[str, str, str]]) -> Path:
    """建一个最小经验库：(insight_id, 正文, mode)。"""
    d = _tmpdir()
    db = d / "experience.db"
    con = sqlite3.connect(db)
    con.execute(
        "CREATE TABLE insights ("
        "insight_id TEXT PRIMARY KEY, insight TEXT, context TEXT, tags TEXT,"
        "source_session TEXT, source_model TEXT, ts REAL,"
        "merged_into TEXT, merged_from TEXT, absorbed_from TEXT,"
        "mode TEXT DEFAULT 'ctf')")
    for i, (iid, text, mode) in enumerate(rows):
        con.execute(
            "INSERT INTO insights (insight_id, insight, context, tags, ts, mode)"
            " VALUES (?,?,?,?,?,?)",
            (iid, text, "", json.dumps([]), 1000 + i, mode))
    con.commit()
    con.close()
    return db


class ModeOrderingTests(unittest.TestCase):
    def test_same_mode_comes_first(self) -> None:
        db = _make_db([
            ("a", "企业内网横向", "src"),
            ("b", "逆向反调试", "ctf"),
            ("c", "竞赛按规则连跑", "competition"),
        ])
        # 默认按时间倒序：c, b, a
        self.assertEqual([x["insight_id"] for x in read_recent(db)["items"]],
                         ["c", "b", "a"])

        ctf = [x["insight_id"] for x in read_recent(db, mode="ctf")["items"]]
        self.assertEqual(ctf[0], "b", "ctf 的排最前")
        self.assertEqual(len(ctf), 3, "别的模式不丢，只是靠后")

        src = [x["insight_id"] for x in read_recent(db, mode="src")["items"]]
        self.assertEqual(src[0], "a")

    def test_nothing_is_dropped(self) -> None:
        db = _make_db([
            ("a", "一", "src"), ("b", "二", "ctf"), ("c", "三", "competition"),
        ])
        for mode in ("src", "ctf", "competition"):
            self.assertEqual(len(read_recent(db, mode=mode)["items"]), 3,
                             f"{mode} 模式下不该丢条目")

    def test_unknown_mode_changes_nothing(self) -> None:
        db = _make_db([("a", "一", "ctf"), ("b", "二", "src")])
        plain = [x["insight_id"] for x in read_recent(db)["items"]]
        weird = [x["insight_id"] for x in read_recent(db, mode="不存在")["items"]]
        self.assertEqual(plain, weird)

    def test_default_mode_is_ctf(self) -> None:
        """历史条目补列时统一落到 ctf。"""
        self.assertEqual(DEFAULT_MODE, "ctf")

    def test_old_db_without_mode_column_still_reads(self) -> None:
        """老库没有 mode 列时，读出来的条目按默认 ctf 处理，不报错。"""
        d = _tmpdir()
        db = d / "old.db"
        con = sqlite3.connect(db)
        con.execute(
            "CREATE TABLE insights (insight_id TEXT PRIMARY KEY, insight TEXT,"
            " context TEXT, tags TEXT, source_session TEXT, source_model TEXT,"
            " ts REAL)")
        con.execute("INSERT INTO insights VALUES ('a','老条目','','[]','','',1.0)")
        con.commit()
        con.close()
        items = read_recent(db)["items"]
        self.assertEqual(len(items), 1)
        self.assertEqual(items[0].get("mode"), DEFAULT_MODE)

    def test_missing_db_returns_empty(self) -> None:
        got = read_recent(_tmpdir() / "nope.db", mode="ctf")
        self.assertEqual(got, {"total": 0, "items": []})


if __name__ == "__main__":
    unittest.main()
