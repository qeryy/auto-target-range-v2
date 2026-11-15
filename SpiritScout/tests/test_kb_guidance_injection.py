"""知识引导注入：plan.meta 里算好的引导必须真的进提示词，不能只写不用。

现场问题：`_attach_kb_guidance` 一直往 `plan.meta["kb_guidance"]` 里写东西，
但全仓库没有任何地方读它——「带经验解题」从来没生效。
"""

from __future__ import annotations

import unittest
from types import SimpleNamespace

from spiritscout.ctf.agents import CTFBaseAgent


class _Audit:
    def __init__(self) -> None:
        self.events: list[tuple[str, dict]] = []

    def log(self, kind: str, actor: str, detail: dict) -> None:
        self.events.append((kind, detail))


def _agent() -> CTFBaseAgent:
    obj = CTFBaseAgent.__new__(CTFBaseAgent)
    obj.agent_id = "ctf-web"
    obj.ctx = SimpleNamespace(audit=_Audit())
    return obj


def _plan(guidance: dict | None) -> SimpleNamespace:
    return SimpleNamespace(meta={"kb_guidance": guidance} if guidance else {})


class KbGuidanceInjectionTests(unittest.TestCase):
    def test_distilled_insights_become_a_system_block(self) -> None:
        agent = _agent()
        state: dict = {}
        plan = _plan({"distilled_insights": [
            {"insight": "命令退出码 0 不代表绕过生效", "context": "命令注入"},
        ]})
        block = agent._kb_guidance_message(plan, state)
        self.assertIn("命令退出码 0 不代表绕过生效", block)
        self.assertIn("过往经验", block)
        self.assertEqual(state.get("kb_guidance_block"), block)

    def test_hunt_templates_and_experiences_render(self) -> None:
        agent = _agent()
        block = agent._kb_guidance_message(_plan({
            "hunt_templates": [{"precondition": "能上传", "common_pitfall": "扩展名白名单"}],
            "experiences": [{"title": "IDOR 核验", "verify_method": "换一个用户 ID"}],
        }), {})
        self.assertIn("扩展名白名单", block)
        self.assertIn("IDOR 核验", block)

    def test_no_guidance_yields_nothing(self) -> None:
        agent = _agent()
        self.assertEqual(agent._kb_guidance_message(_plan(None), {}), "")
        self.assertEqual(agent._kb_guidance_message(_plan({}), {}), "")
        # 空内容也不该产出空壳 block
        self.assertEqual(agent._kb_guidance_message(
            _plan({"distilled_insights": [{"insight": "  "}]}), {}), "")

    def test_cached_after_first_render(self) -> None:
        """同一路线只渲染一次：每轮重算会打碎稳定前缀。"""
        agent = _agent()
        state: dict = {}
        plan = _plan({"distilled_insights": [{"insight": "一条经验"}]})
        first = agent._kb_guidance_message(plan, state)
        second = agent._kb_guidance_message(_plan({"distilled_insights": [{"insight": "换一条"}]}), state)
        self.assertEqual(first, second, "命中缓存后不再重算")

    def test_audit_records_the_injection(self) -> None:
        agent = _agent()
        agent._kb_guidance_message(_plan({"distilled_insights": [{"insight": "x"}]}), {})
        kinds = [k for k, _ in agent.ctx.audit.events]
        self.assertIn("agent.kb_guidance_loaded", kinds)


if __name__ == "__main__":
    unittest.main()
