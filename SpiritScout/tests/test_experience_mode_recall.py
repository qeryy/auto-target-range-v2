"""经验按任务模式优先：同类标签的排前面，通用条目照样带上。"""
from __future__ import annotations

import json
import sqlite3
import tempfile
import unittest
from pathlib import Path

from spiritscout.knowledge.experience import read_recent


def _make_db(rows: list[tuple[str, str, list[str]]]) -> Path:
    """建一个最小经验库：(insight_id, 正文, 标签)。"""
    d = Path(tempfile.mkdtemp())
    db = d / "experience.db"
    con = sqlite3.connect(db)
    con.execute(
        "CREATE TABLE insights ("
        "insight_id TEXT PRIMARY KEY, insight TEXT, context TEXT, tags TEXT,"
        "source_session TEXT, source_model TEXT, ts REAL,"
        "merged_into TEXT, absorbed_from TEXT, fp TEXT)")
    for i, (iid, text, tags) in enumerate(rows):
        con.execute(
            "INSERT INTO insights (insight_id, insight, context, tags, ts)"
            " VALUES (?,?,?,?,?)",
            (iid, text, "", json.dumps(tags), 1000 + i))
    con.commit()
    con.close()
    return db


class ModeOrderingTests(unittest.TestCase):
    def test_mode_matching_items_come_first(self) -> None:
        db = _make_db([
            ("a", "企业内网横向的经验", ["ad"]),          # 只在 SRC / 竞赛
            ("b", "逆向里常见的反调试", ["reverse"]),     # 只在 CTF
            ("c", "命令退出码不代表绕过成功", ["web"]),   # SRC + CTF
        ])
        # 默认按时间倒序：c, b, a
        got = [x["insight_id"] for x in read_recent(db)["items"]]
        self.assertEqual(got, ["c", "b", "a"])

        # CTF：b(CTF) 和 c(两边都用) 都应排在 a 前面
        ctf = [x["insight_id"] for x in read_recent(db, mode="ctf")["items"]]
        self.assertEqual(ctf[-1], "a", "只属于别的模式的排最后")
        self.assertIn("b", ctf[:2])

        # SRC：a 和 c 都命中
        src = [x["insight_id"] for x in read_recent(db, mode="src")["items"]]
        self.assertEqual(src[-1], "b", "逆向不该压过内网条目")

    def test_unlabelled_items_stay_in_place(self) -> None:
        """标签认不出模式的条目是通用经验，不该被压后。"""
        db = _make_db([
            ("a", "逆向反调试", ["reverse"]),
            ("b", "先验证再下结论", ["方法论"]),
        ])
        got = [x["insight_id"] for x in read_recent(db, mode="src")["items"]]
        self.assertEqual(got[0], "b", "通用条目和命中条目同档，保持时间序在前")

    def test_unknown_mode_changes_nothing(self) -> None:
        db = _make_db([("a", "一", ["reverse"]), ("b", "二", ["web"])])
        plain = [x["insight_id"] for x in read_recent(db)["items"]]
        weird = [x["insight_id"] for x in read_recent(db, mode="不存在的模式")["items"]]
        self.assertEqual(plain, weird)

    def test_missing_db_returns_empty(self) -> None:
        got = read_recent(Path(tempfile.mkdtemp()) / "nope.db", mode="ctf")
        self.assertEqual(got, {"total": 0, "items": []})


if __name__ == "__main__":
    unittest.main()
