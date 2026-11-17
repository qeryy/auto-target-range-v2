"""经验表的列顺序：ALTER TABLE 补的列不按 _COLUMNS 的顺序排，按位置映射会全错位。

现场：加了 `mode` 之后，补列把 mode 插在 merged_into **之前**，于是 `ts` 的值
被对到 `merged_into` 上——所有经验都被判成「已经合并过」，二次提炼与合并全挂。
"""

from __future__ import annotations

import contextlib
import pathlib
import sqlite3
import tempfile
import unittest
from pathlib import Path

from spiritscout.knowledge import experience as E

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


def _old_lib(rows: list[tuple[str, str, float]]) -> Path:
    """模拟老库：只有最初的 7 列，没有 merged_into / mode。"""
    d = _tmpdir()
    db = d / "experience.db"
    con = sqlite3.connect(db)
    con.execute(
        "CREATE TABLE insights(insight_id TEXT PRIMARY KEY, insight TEXT,"
        " context TEXT, tags TEXT, source_session TEXT, source_model TEXT, ts REAL)")
    for iid, text, ts in rows:
        con.execute("INSERT INTO insights VALUES (?,?,'','[]','s1','master',?)",
                    (iid, text, ts))
    con.commit()
    con.close()
    return db


class ColumnOrderTests(unittest.TestCase):
    def test_old_lib_reads_correct_values(self) -> None:
        """老库补列后，各字段仍要读到自己的值。"""
        db = _old_lib([("a", "第一条经验", 1000.0), ("b", "第二条经验", 2000.0)])
        conn = E._load_conn(db)          # 打开即补列
        try:
            cols = [r[1] for r in conn.execute("PRAGMA table_info(insights)")]
            self.assertIn("mode", cols)
            row = E._row(conn, "a")
        finally:
            conn.close()
        self.assertIsNotNone(row)
        self.assertEqual(row["insight"], "第一条经验")
        self.assertEqual(row["ts"], 1000.0, "ts 不能被读成别的列")
        self.assertEqual(str(row.get("merged_into") or ""), "",
                         "没有被合并过就该是空串")
        self.assertEqual(row.get("mode"), E.DEFAULT_MODE)

    def test_old_lib_entries_are_not_treated_as_merged(self) -> None:
        """最关键的一条：老库条目不能被误判成「已经合并过」。"""
        db = _old_lib([("a", "经验甲", 1.0), ("b", "经验乙", 2.0)])
        rows = E._read_all(db)
        self.assertEqual(len(rows), 2)
        for row in rows:
            self.assertEqual(str(row.get("merged_into") or ""), "",
                             f'{row["insight_id"]} 被误判为已合并')

    def test_read_page_and_recent_agree_on_count(self) -> None:
        db = _old_lib([("a", "一", 1.0), ("b", "二", 2.0)])
        page = E.read_page(db, limit=10)
        recent = E.read_recent(db, limit=10)
        self.assertEqual(page["total"], 2)
        self.assertEqual(len(recent["items"]), 2)
        self.assertEqual(recent["items"][0]["insight"], "二", "按时间倒序")

    def test_mode_column_lands_wherever_alter_puts_it(self) -> None:
        """断言实现不再依赖列位置：列名对了，值就对。"""
        db = _old_lib([("a", "经验", 5.0)])
        conn = E._load_conn(db)
        try:
            cols = [r[1] for r in conn.execute("PRAGMA table_info(insights)")]
            mode_pos = cols.index("mode")
            merged_pos = cols.index("merged_into") if "merged_into" in cols else -1
            # 无论 mode 在 merged_into 前还是后，映射都必须正确
            row = E._row(conn, "a")
        finally:
            conn.close()
        self.assertIsNotNone(row)
        self.assertEqual(row["mode"], E.DEFAULT_MODE)
        self.assertEqual(str(row.get("merged_into") or ""), "")
        self.assertIn(mode_pos, range(len(cols)))
        self.assertTrue(merged_pos < 0 or merged_pos != mode_pos)


if __name__ == "__main__":
    unittest.main()
