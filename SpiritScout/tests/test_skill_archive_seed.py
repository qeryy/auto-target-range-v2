"""发行版内置技能种子完整性。"""

from __future__ import annotations

import json
from pathlib import Path

from spiritscout.resources.store import builtin_skills

DATA = Path(__file__).resolve().parents[1] / "src" / "spiritscout" / "knowledge" / "data"


class TestSkillArchiveSeed:
    def test_release_seed_has_all_298_unique_skills(self) -> None:
        rows = builtin_skills()
        assert len(rows) == 298
        assert len(set(rows)) == 298
        assert "api-security" in rows
        assert "attack-chain" in rows
        assert "zzy-skills-index" in rows

    def test_archive_seed_contains_content_not_user_runtime_fields(self) -> None:
        rows = json.loads((DATA / "archive_skills.json").read_text(encoding="utf-8"))
        assert len(rows) == 86
        for row in rows:
            assert row["skill_id"]
            assert row["body"].strip()
            assert "version" not in row
            assert "origin" not in row
            assert "created_at" not in row
            assert "updated_at" not in row
