"""技能按任务模式分类：SRC / CTF / 竞赛 三类各拿各的。"""
from __future__ import annotations

import unittest

from spiritscout.resources.skill_modes import (
    ALL_MODES,
    MODE_COMPETITION,
    MODE_CTF,
    MODE_SRC,
    domain_of,
    filter_by_mode,
    modes_for,
    summary,
)


class DomainTests(unittest.TestCase):
    def test_domain_field_wins(self) -> None:
        self.assertEqual(domain_of({"domain": "Web", "skill_id": "ctf-x"}), "web")

    def test_falls_back_to_id_prefix(self) -> None:
        self.assertEqual(domain_of({"skill_id": "competition-jwt-claim-confusion"}), "competition")
        self.assertEqual(domain_of({"skill_id": "ctf-crypto"}), "ctf")
        self.assertEqual(domain_of({"id": "src-hunter"}), "src")

    def test_unknown_id_has_no_domain(self) -> None:
        self.assertEqual(domain_of({"skill_id": "mystery"}), "")


class ModeMappingTests(unittest.TestCase):
    def test_web_skill_belongs_to_both_src_and_ctf(self) -> None:
        modes = modes_for({"domain": "web"})
        self.assertIn(MODE_SRC, modes)
        self.assertIn(MODE_CTF, modes)
        self.assertNotIn(MODE_COMPETITION, modes)

    def test_reverse_skill_is_ctf_only(self) -> None:
        modes = modes_for({"domain": "reverse"})
        self.assertEqual(modes, [MODE_CTF])

    def test_recon_skill_is_src_only(self) -> None:
        modes = modes_for({"domain": "recon"})
        self.assertEqual(modes, [MODE_SRC])

    def test_competition_skill_also_serves_ctf(self) -> None:
        modes = modes_for({"domain": "competition"})
        self.assertIn(MODE_COMPETITION, modes)
        self.assertIn(MODE_CTF, modes)

    def test_unknown_domain_gets_everything(self) -> None:
        """没登记过的领域：三类都给，宁可多给也不让 Agent 手里空着。"""
        self.assertEqual(sorted(modes_for({"domain": "从未见过的领域"})), sorted(ALL_MODES))


_SAMPLE = [
    {"skill_id": "web-sqli", "domain": "web"},
    {"skill_id": "rev-unicorn", "domain": "reverse"},
    {"skill_id": "recon-subdomain", "domain": "recon"},
    {"skill_id": "competition-k8s", "domain": "competition"},
]


class FilterTests(unittest.TestCase):
    SKILLS = _SAMPLE

    def test_src_gets_web_and_recon(self) -> None:
        got = {s["skill_id"] for s in filter_by_mode(self.SKILLS, MODE_SRC)}
        self.assertEqual(got, {"web-sqli", "recon-subdomain"})

    def test_ctf_gets_web_reverse_competition(self) -> None:
        got = {s["skill_id"] for s in filter_by_mode(self.SKILLS, MODE_CTF)}
        self.assertEqual(got, {"web-sqli", "rev-unicorn", "competition-k8s"})

    def test_competition_gets_ctf_pool_plus_own(self) -> None:
        """竞赛题仍是 CTF 方向：CTF 池 + competition 专属一起给。"""
        got = {s["skill_id"] for s in filter_by_mode(self.SKILLS, MODE_COMPETITION)}
        self.assertEqual(got, {"competition-k8s", "web-sqli", "rev-unicorn"})

    def test_unknown_mode_keeps_everything(self) -> None:
        """模式未知时不筛：筛错比多给贵。"""
        self.assertEqual(len(filter_by_mode(self.SKILLS, "")), 4)
        self.assertEqual(len(filter_by_mode(self.SKILLS, "不存在的模式")), 4)

    def test_empty_input_is_fine(self) -> None:
        self.assertEqual(filter_by_mode([], MODE_CTF), [])

    def test_summary_counts(self) -> None:
        got = summary(self.SKILLS)
        self.assertEqual(got[MODE_SRC], 2)      # web + recon
        self.assertEqual(got[MODE_CTF], 3)      # web + reverse + competition
        self.assertEqual(got[MODE_COMPETITION], 1)


if __name__ == "__main__":
    unittest.main()
