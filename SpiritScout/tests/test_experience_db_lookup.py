"""经验库定位：知识库是会话级的，提炼经验是工作区共享的，不能按同级目录推。

现场两层 bug 叠在一起：① KnowledgeStore 只有私有 _path，getattr(db_path) 拿到空串，
Path("") 变成 "."；② 就算拿到路径，kb.db 在 sessions/<id>/ 下，而 experience.db
在 <工作区>/.spiritscout/ 下——同级推永远落空，而且**静默**退化。
"""

from __future__ import annotations

import contextlib
import pathlib
import sqlite3
import tempfile
import unittest
from pathlib import Path

from spiritscout.knowledge.store import KnowledgeStore

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


def _make_experience_db(dir_: Path) -> Path:
    dir_.mkdir(parents=True, exist_ok=True)
    db = dir_ / "experience.db"
    con = sqlite3.connect(db)
    con.execute(
        "CREATE TABLE insights (insight_id TEXT PRIMARY KEY, insight TEXT,"
        " context TEXT, tags TEXT, source_session TEXT, source_model TEXT,"
        " ts REAL, merged_into TEXT, absorbed_from TEXT, fp TEXT)")
    con.execute("INSERT INTO insights (insight_id, insight, context, tags, ts)"
                " VALUES ('a','经验正文','','[]',1.0)")
    con.commit()
    con.close()
    return db


def _locate(kb: KnowledgeStore) -> Path | None:
    """与 _attach_kb_guidance 相同的定位逻辑（沿祖先目录找）。"""
    raw = getattr(kb, "db_path", None) or getattr(kb, "_path", None) or ""
    if not str(raw):
        return None
    here = Path(str(raw)).resolve()
    for parent in (here.parent, *here.parents):
        cand = parent / "experience.db"
        if cand.exists():
            return cand
    return None


class DbPathExposureTests(unittest.TestCase):
    def test_knowledge_store_exposes_db_path(self) -> None:
        root = _tmpdir()
        kb = KnowledgeStore(root / "knowledge.db", auto_seed=False)
        try:
            self.assertTrue(getattr(kb, "db_path", ""), "必须暴露公开路径")
            self.assertEqual(Path(kb.db_path).name, "knowledge.db")
            self.assertEqual(Path(kb.db_path), Path(kb._path))
        finally:
            kb.close()

    def test_empty_lookup_would_yield_dot(self) -> None:
        """反例：没有公开属性时 getattr 拿到空串，Path("") 就是当前目录。"""
        class Bare:
            pass

        self.assertEqual(Path(str(getattr(Bare(), "db_path", "") or "")), Path("."))


class AncestorSearchTests(unittest.TestCase):
    def test_finds_shared_db_from_session_level(self) -> None:
        """会话级 kb.db → 往上两级才是工作区的 .spiritscout。"""
        root = _tmpdir()
        shared = root / ".spiritscout"
        _make_experience_db(shared)
        session_dir = shared / "sessions" / "sess-x"
        session_dir.mkdir(parents=True)
        kb = KnowledgeStore(session_dir / "kb.db", auto_seed=False)
        try:
            found = _locate(kb)
            self.assertIsNotNone(found, "应当沿祖先目录找到共享经验库")
            self.assertEqual(found, shared / "experience.db")
        finally:
            kb.close()

    def test_returns_none_when_absent(self) -> None:
        root = _tmpdir()
        kb = KnowledgeStore(root / "deep" / "kb.db", auto_seed=False)
        try:
            self.assertIsNone(_locate(kb))
        finally:
            kb.close()


if __name__ == "__main__":
    unittest.main()
