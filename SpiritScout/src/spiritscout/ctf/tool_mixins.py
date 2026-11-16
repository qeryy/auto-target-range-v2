"""CTF 子 Agent 的工具实现（mixin）。

这些方法原本长在 `CTFBaseAgent` 里——12 个工具、700 多行，和解题主循环、
阶段提示、路线规划混在同一个三千行的类里。抽成 mixin 后职责清楚：
**这个文件只管「一个工具调用怎么执行、结果怎么折成观察」**，
agent 的生命周期与回合编排留在 `agents.py`。

依赖说明：方法之间通过 `self.xxx` 互相调用，靠的是同一份 `self`；mixin 被
`CTFBaseAgent` 继承后这些调用照常成立，不需要额外接线。
"""

from __future__ import annotations

import re
from pathlib import Path
from types import SimpleNamespace
from typing import Any

from spiritscout.agents.ring import attempt_key
from spiritscout.agents.sub.base import RoutePlan
from spiritscout.core.ids import new_id
from spiritscout.core.models import RunMode, Task
from spiritscout.ctf.limits import (
    _FETCH_RECORD_CAP,
    _HTTP_METHODS,
    _HTTP_SAFE_METHODS,
    _SAND_MODEL_CAP,
    ASK_USER_REPEAT_LIMIT,
    ASK_USER_TOTAL_LIMIT,
)
from spiritscout.ctf.observe import (
    _head_tail,
    _http_observation,
    _obs_text,
    _signal_line,
    _stderr_digest,
)
from spiritscout.ctf.outcome import _TOOL_RESULT_CAP, _ToolOutcome


class CTFToolMixin:
    """工具实现：HTTP / 沙箱 / 检索 / 附件分析 / Flag 提交 / 子 Agent 派生。

    每个 `_tool_*` 返回 `_ToolOutcome`（ok、可观测的文本、可选 findings）。
    行为与搬出前逐字一致——这次只是搬家，没有改逻辑。
    """

    async def _tool_analyze_attachments(self, task: Task) -> _ToolOutcome:
        """对全部附件做魔数识别 + 字符串提取，文本进 Flag 提取。"""
        attachments = self._attachments_of(task)
        if not attachments:
            return _ToolOutcome(ok=False, text="本题没有附件。")
        findings: list[dict[str, Any]] = []
        notes: list[str] = []
        body_lines: list[str] = []
        all_strings: list[str] = []
        # 正文节选按总预算分配：多条附件时若每个都给满，拼起来会超过回灌上限，
        # 被从头截断的恰恰是「正文」——模型于是继续认为没读到材料，转头又去问用户。
        per_file = min(2400, max(800, 4800 // max(1, len(attachments))))
        for path in attachments:
            res = await self.ctx.tools.call(
                "file_analyze", {"path": path, "text_excerpt_chars": per_file},
                **self._tool_ctx(task))
            if not res.ok:
                notes.append(f"{path}: {res.error}")
                body_lines.append(f"{path}: 解析失败 {str(res.error)[:120]}")
                continue
            parsed = res.parsed
            notes.append(f"{path}: magic={parsed['magic']} size={parsed['size']}")
            strings = parsed.get("strings", [])
            all_strings.extend(strings)
            findings.append({"type": "file", "path": path,
                             "magic": parsed["magic"], "size": parsed["size"]})
            self.cache.put(self.route_namespace, f"file|{path}", parsed)
            head = (f"{path}: magic={parsed['magic']} size={parsed['size']} "
                    f"is_elf={parsed.get('is_elf')}"
                    + (f" lines={parsed.get('line_count')}" if parsed.get("line_count") else ""))
            # 文本类附件（源码/配置/脚本）的正文排在字符串样本前面：解题材料就是它，
            # 只给字符串样本等于让模型凭空猜算法，排在后面又会被回灌上限截掉。
            excerpt = str(parsed.get("text_excerpt") or "")
            if excerpt:
                body_lines.append(head + f"\n  正文（{Path(str(path)).name}）:\n{excerpt}")
                continue
            body_lines.append(head + "\n  字符串样本: "
                              + " | ".join(str(s)[:120] for s in strings[:20]))
        return _ToolOutcome(ok=True, value=0.3,
                            findings=findings, note="; ".join(notes),
                            text="\n".join(body_lines)[:_TOOL_RESULT_CAP])

    async def _tool_analyze_text(self, task: Task) -> _ToolOutcome:
        """题面文本取证：全部载荷字符串的编码串链式解码 + 零宽字符统计 + Flag 提取。"""
        from spiritscout.ctf.agents import _challenge_text, _decode_blobs
        text = _challenge_text(self.challenge_of(task))
        decoded_texts = _decode_blobs(text)
        zw = len(re.findall(r"[\u200b\u200c\u200d\u2060]", text))
        obs = {"action": "analyze_text",
               "result": f"可解码串 {len(decoded_texts)} 个，零宽字符 {zw} 个"}
        state_note = f"题面分析：{len(decoded_texts)} 个可解码串，零宽字符={zw}"
        body = ("题面分析结果：可解码串 "
                f"{len(decoded_texts)} 个，零宽字符 {zw} 个。\n"
                + ("解码结果:\n" + "\n".join(d[:600] for d in decoded_texts[:6])
                   if decoded_texts else "（无可解码串）"))
        return _ToolOutcome(ok=True, value=0.15,
                            findings=[],
                            note=state_note, observation=obs, text=body)

    async def _tool_local_tool(self, task: Task, route, state: dict[str, Any],
                               args: dict[str, Any]) -> _ToolOutcome:
        """本地工具：检索或运行用户设置目录里的工具。

        运行由 `local_runner` 负责「复制→跑→对账→还原→删副本」，这里只把结果
        折成观察：`clean=false`（没能还原）本身就是需要停手报告的异常，
        不能当成普通执行成功继续往下推。
        """
        operation = str(args.get("operation") or "search").strip().lower()
        payload = {
            "operation": operation,
            "query": str(args.get("query") or ""),
            "name": str(args.get("name") or ""),
            "args": [str(a) for a in (args.get("args") or [])] if isinstance(args.get("args"), list) else [],
            "action": str(args.get("action") or "analysis"),
        }
        if args.get("timeout") not in (None, ""):
            try:
                payload["timeout"] = float(args["timeout"])
            except (TypeError, ValueError):
                return _ToolOutcome(ok=False, text="timeout 需要是秒数。")
        res = await self.ctx.tools.call("local_tool", payload, **self._tool_ctx(task))
        desc = f"local_tool {operation}"
        if operation == "run":
            clean = bool(res.parsed.get("clean"))
            state["observations"].append({
                "action": desc,
                "result": (f"{res.parsed.get('tool')} 退出码 {res.parsed.get('exit_code')}"
                           + ("（目录已还原）" if clean else "（未能还原，已中止）"))[:160]})
            if not clean:
                return _ToolOutcome(
                    ok=False, note=f"{desc} → 目录未能还原",
                    text=("本地工具运行后目录未能完全还原，已停止使用该工具。\n"
                          + (res.stdout or res.error or "")[:1500]))
            return _ToolOutcome(ok=res.ok, note=f"{desc} → 退出码 {res.parsed.get('exit_code')}",
                                text=(res.stdout or res.error or "")[:4000],
                                metadata=res.parsed)
        state["observations"].append({"action": desc,
                                      "result": (res.stdout or res.error or "")[:160]})
        return _ToolOutcome(ok=res.ok, note=f"{desc} → {res.parsed.get('count', '')}".strip(),
                            text=(res.stdout or res.error or "")[:4000],
                            metadata=res.parsed)

    def _tool_ctx(self, task: Task) -> dict[str, Any]:
        """本题工具调用上下文：沙箱工作目录按赛题隔离。

        多题并行（或同题多分支重攻）时，各题的脚本、产物与上次输出日志
        落 ``sandbox/<赛题 id>/``，互不覆盖；无赛题节点回落任务 id。
        SRC 等非赛题链路不经这里，维持会话级工作目录。
        """
        ctx = getattr(self.ctx, "builtin_ctx", None) or {}
        return {**ctx, "workspace_scope": self.challenge_id(task)}

    async def _tool_probe_paths(self, task: Task, route, state,
                                args: dict[str, Any]) -> _ToolOutcome:
        """敏感路径批量探测：逐条复用 http_probe 通道（范围标注/限速/审计不变）。"""
        ch = self.challenge_of(task)
        base = str(ch.get("url") or task.target or "").rstrip("/")
        if not base.startswith(("http://", "https://")):
            return _ToolOutcome(ok=False, text="probe_paths 需要题面有 HTTP 入口。")
        raw = args.get("paths") if isinstance(args.get("paths"), list) else []
        paths = [str(p) for p in raw if str(p).startswith("/")] \
            or list(self._PROBE_PATHS)
        paths = paths[:40]
        hits: list[str] = []
        failed = 0
        for p in paths:
            res = await self.ctx.tools.call(
                "http_probe", {"url": base + p}, **self._tool_ctx(task))
            if not res.ok:
                failed += 1
                continue
            parsed = res.parsed if isinstance(res.parsed, dict) else {}
            code = parsed.get("status_code", "?")
            if code in (404, "404"):
                continue
            title = str(parsed.get("title", "")).strip()[:40]
            size = parsed.get("content_length", len(res.stdout or ""))
            hits.append(f"{p} → {code} len={size}" + (f" title={title}" if title else ""))
        if not hits:
            return _ToolOutcome(ok=True, value=0.05,
                                note=f"敏感路径探测 {len(paths)} 条未命中",
                                text=(f"探测了 {len(paths)} 条常见敏感路径"
                                      f"（{failed} 条请求失败），其余 404——清单之外，"
                                      "回到页面功能本身找线索。"))
        body = "命中:\n" + "\n".join(hits[:20]) + f"\n（共 {len(hits)}/{len(paths)} 命中）"
        return _ToolOutcome(ok=True, value=min(0.2 + 0.15 * len(hits), 0.9),
                            findings=[],
                            note=f"敏感路径探测命中 {len(hits)}/{len(paths)}",
                            text=body[:_TOOL_RESULT_CAP])

    async def _tool_submit_flag(self, task: Task, route, state: dict[str, Any],
                                args: dict[str, Any]) -> _ToolOutcome:
        """模型判定的 Flag 提交：登记（去重）→ 赛方接口 → 应答回灌。

        判定权在模型——后端不做内容正则筛选；格式匹配只作登记信息。
        提交成功即写入共享缓存，并行分支据此收敛。
        """
        value = str(args.get("flag", "") or "").strip()
        if not value:
            return _ToolOutcome(ok=False, text="submit_flag 需要 flag 参数（Flag 原文）。")
        # 只在「确实跑过工具」时核对出处：一次没执行就提交，可能是纯推理题
        # （模型心算得出），也可能是测试桩——这两种都没有证据池可查。
        # 真正的幻觉发生在"读了 hint 却把文件名当答案"，那时 executions>0。
        if state.get("executions") and not self._flag_seen_in_evidence(state, value):
            return _ToolOutcome(ok=False, note=f"拒收：{value[:40]} 无出处", text=(
                f"这个值没有在任何工具输出里出现过，不能登记：{value}\n"
                "常见原因：把提示里的**文件名/变量名**直接拼成了 flag，或者凭推测构造。\n"
                "下一步：回到命令/HTTP 的原始输出，找到真正含 flag 的那一行；"
                "如果只看到『flag in <某文件>』这类提示，那说明还要去读那个文件，"
                "而不是把文件名当答案。"))
        if self.flag_manager is None:
            return _ToolOutcome(ok=False, text="本题未装配 Flag 管理器，无法提交。")
        cand = self.flag_manager.register(
            value, challenge_id=self.challenge_id(task), found_by=self.agent_id)
        if cand is None:
            existing = self.flag_manager.get(value)
            if (getattr(self.ctx, "mode", None) == RunMode.COMPETITION
                    and existing is not None and existing.accepted is True
                    and existing.challenge_id == self.challenge_id(task)):
                complete = self._submission_complete(task, existing.submit_response)
                receipt = self._accepted_receipt(task, existing.submit_response,
                                                 complete=complete)
                return _ToolOutcome(
                    ok=True, value=0.0, note=receipt, stop=complete,
                    final_text=receipt if complete else "",
                    text=(receipt if complete else
                          "该 Flag 已由平台确认过；若还有未完成 Flag，继续本题。"))
            if (self.flag_manager.submit_fn is None and existing is not None
                    and not existing.submitted and existing.accepted is None):
                return _ToolOutcome(ok=True, value=0.0, note="Flag 已登记，等待人工提交",
                                    text=self._manual_flag_receipt(value))
            return _ToolOutcome(ok=True, value=0.0, note=f"Flag 重复: {value[:40]}",
                                text="该 Flag 值此前已登记过，无需重复提交。")
        self._record_flag(task, cand.value,
                          route=route.name)   # 先登记候选（并行方向即刻可见）
        result = await self.flag_manager.submit(cand)
        verdict = {True: "判定正确", False: "判定错误", None: "未获平台判定"}[
            result.accepted]
        # 裁决态回填共享记录：accepted 才是同伴的收敛信号；rejected 让同伴
        # 不再重复提交同一个错值；无判定通道（manual）留给人工确认
        self._record_flag(task, cand.value,
                          {True: "accepted", False: "rejected"}.get(
                              result.accepted, "manual"), route=route.name)
        self.ctx.audit.log("agent.flag_submit", self.agent_id, {
            "route": route.name, "challenge": self.challenge_id(task),
            "flag_id": cand.flag_id, "verdict": str(result.accepted),
            "note": str(args.get("note", ""))[:200]})
        if self.flag_manager.submit_fn is None:
            return _ToolOutcome(
                ok=True, value=0.6, findings=[{"type": "flag", "value": cand.value}],
                note="Flag 已登记，等待人工提交", text=self._manual_flag_receipt(cand.value))
        text = (f"已登记并提交：{cand.value}\n平台应答：{verdict}"
                + (f"（{result.submit_response[:200]}）" if result.submit_response else ""))
        if (result.accepted is True
                and getattr(self.ctx, "mode", None) == RunMode.COMPETITION):
            complete = self._submission_complete(task, result.submit_response)
            receipt = self._accepted_receipt(task, result.submit_response,
                                             complete=complete)
            if complete:
                return _ToolOutcome(
                    ok=True, value=0.9,
                    findings=[{"type": "flag", "value": cand.value}],
                    note=receipt, text=receipt, stop=True, final_text=receipt)
            text += f"\n{receipt}\n仍有未完成 Flag，继续本题。"
        return _ToolOutcome(
            ok=True,
            value=0.9 if result.accepted is True else (
                0.2 if result.accepted is False else 0.6),
            findings=[{"type": "flag", "value": cand.value}],
            note=f"Flag 提交 {cand.value} → {verdict}", text=text)

    async def _tool_spawn(self, task: Task, route, state: dict[str, Any],
                          args: dict[str, Any]) -> _ToolOutcome:
        """派生分支执行者：以当前任务 + 模型撰写的简报起一条独立解题对话，
        阻塞至其收尾，最终结论回灌给当前对话。

        派生时机、数量与分工由模型按任务复杂度自行决策（brief 须说明
        为什么/做什么/边界）；层级上限两代（子→孙）是唯一护栏——防的
        是无限递归，不是进度。分支与父分支共享题级现场（笔记/入口/
        Flag 即时互通，任一路线夺旗全体收敛）。
        """
        depth = int(state.get("depth") or 0)
        if depth >= 2:
            return _ToolOutcome(ok=False, text=(
                "分支层级已达上限（子→孙）：更深的子问题请并入当前工作推进，"
                "或把要点用 note 交给同伴。"))
        brief = str(args.get("brief", "") or "").strip()
        if not brief:
            return _ToolOutcome(ok=False, text="spawn 需要 brief 参数（派什么/为什么/边界）。")
        expect = str(args.get("expect", "") or "").strip()
        seq = int(state.get("child_seq") or 0) + 1
        state["child_seq"] = seq
        # 子路线也要有真实身份：_solve_turn 构造 ExecutionScope 与 journal
        # checkpoint 都读 route.route_id——缺了它 spawn 100% 摔 AttributeError
        child_route = SimpleNamespace(name=f"{route.name}·子{seq}",
                                      route_id=new_id("route"))
        child_plan = RoutePlan(name=f"spawn{seq}", strategy=brief[:200],
                               steps=[{"action": "investigate"}])
        child_state = self._inv_state(task, child_route)
        child_state["depth"] = depth + 1
        self.ctx.audit.log("agent.spawn", self.agent_id, {
            "route": route.name, "challenge": self.challenge_id(task),
            "child": child_route.name, "depth": depth + 1,
            "brief": brief[:400], "expect": expect[:200]})
        state["observations"].append(
            {"action": f"spawn {child_route.name}",
             "result": f"派生分支：{brief[:120]}"})
        while not child_state.get("concluded") and not child_state.get("stall"):
            await self._solve_turn(task, child_route, child_plan)
        conclusion = str(child_state.get("conclusion_text")
                         or child_state.get("stall")
                         or "（分支未给出结论文本）")
        flags = len(child_state.get("flags") or [])
        self.ctx.audit.log("agent.spawn_done", self.agent_id, {
            "route": route.name, "child": child_route.name,
            "flags": flags, "conclusion": conclusion[:400]})
        text = (f"分支「{brief[:120]}」已收尾，结论：\n{conclusion[:2400]}"
                + (f"\n（该分支捕获 Flag {flags} 个）" if flags else ""))
        return _ToolOutcome(ok=True, value=0.4 if flags else 0.2,
                            note=f"分支 {child_route.name} 收尾 flag={flags}",
                            text=text)

    async def _tool_ask_user(self, task: Task, route, state: dict[str, Any],
                             args: dict[str, Any]) -> _ToolOutcome:
        """向用户提问并挂起等待作答（问题与选项由模型撰写，后端只送达回收）。

        用户答复（点选选项或自由输入）与勾选扩围的目标原样回灌；会话停止
        时按「无答复」唤醒，模型据此自行决定继续或收尾。

        同一条路线里重复问同一个问题不重复打搅用户：第一次的答复直接回放，
        并且告诉模型「别问了，用工具推进」。这不是禁止提问，是防止模型在
        「问一句 → 得到答复 → 再问同一句」里空转——本机实测一条路线能这样
        转十几圈、烧掉几十万 token 却一步没走。
        """
        question = str(args.get("question", "") or "").strip()
        if not question:
            return _ToolOutcome(ok=False, text="ask_user 需要 question 参数。")
        asked = state.setdefault("asked_questions", {})
        # 换个说法再问同一件事，按内容去重挡不住：本机实测模型连问 8 次
        # 「确认解题方向？」，每次措辞都不同。按路线给个总次数上限——问到这个数
        # 就自己决策，把不确定点写进结论，而不是继续等用户拍板。
        total = int(state.get("ask_total") or 0) + 1
        state["ask_total"] = total
        if total > ASK_USER_TOTAL_LIMIT:
            self.ctx.audit.log("agent.ask_user_capped", self.agent_id, {
                "route": route.name, "challenge": self.challenge_id(task),
                "total": total, "question": question[:200]})
            return _ToolOutcome(ok=False, text=(
                f"这条路线已经问过 {total - 1} 次，本任务不再转交提问。"
                "解题方向由你基于现场证据自己决定：读附件、跑脚本验证，"
                "把仍不确定的点写进结论。"))
        signature = " ".join(question.split()).lower()
        if signature in asked:
            entry = asked[signature]
            entry["count"] = int(entry.get("count", 1)) + 1
            self.ctx.audit.log("agent.ask_user_deduped", self.agent_id, {
                "route": route.name, "challenge": self.challenge_id(task),
                "count": entry["count"], "question": question[:200]})
            if entry["count"] >= ASK_USER_REPEAT_LIMIT:
                return _ToolOutcome(ok=False, text=(
                    "同一个问题已经问过并得到答复，用户不会再答一遍。停止提问："
                    "读附件、跑脚本、用工具去验证，把结论写进 finish；如果确实"
                    "缺信息，就在结论里写清缺什么。"))
            return _ToolOutcome(ok=True, value=0.0,
                                note=f"重复提问（第 {entry['count']} 次）已折叠",
                                text=(f"这个问题你刚问过，用户当时的答复是：{entry.get('answer') or '（无明确答复）'}\n"
                                      "不要重复提问，直接用工具推进。"))
        options = [str(o).strip()[:200] for o in (args.get("options") or [])[:6]
                   if isinstance(o, (str, int, float)) and str(o).strip()]
        from spiritscout.policy.questions import normalize_targets
        targets = normalize_targets(args.get("targets"))
        broker = getattr(self.ctx, "questions", None)
        if broker is None:
            return _ToolOutcome(ok=False, text=(
                "当前会话没有可用的用户询问通道——基于已有证据自行决策，"
                "并在结论中说明该不确定点。"))
        multiple = bool(args.get("multiple"))
        if getattr(broker, "interactive", True):
            self.ctx.audit.log("agent.ask_user", self.agent_id, {
                "route": route.name, "challenge": self.challenge_id(task),
                "question": question[:500], "options": options,
                "multiple": multiple, "targets": targets})
        result = await broker.ask(self.agent_type.value, question, options, targets,
                                  multiple=multiple)
        if result.get("autonomous"):
            return _ToolOutcome(ok=True, value=0.0, note="全自动模式：交回 Agent 自主决策",
                                text=str(result.get("answer") or "基于已有证据自主决策。"))
        answer = str(result.get("answer") or "").strip()
        added = result.get("scope_added") or []
        if result.get("cancelled"):
            return _ToolOutcome(ok=True, value=0.0,
                                note="ask_user → 会话停止，未获答复",
                                text="用户尚未作答（会话已停止）。基于已有证据自行决策。")
        lines = [f"用户已答复：{answer or '（未给出明确选择，请基于现有证据继续）'}"]
        if added:
            lines.append("以下目标已由用户确认加入本会话授权范围，可继续测试: "
                         + ", ".join(added))
        asked[signature] = {"count": 1, "answer": answer[:400]}
        self.ctx.audit.log("agent.ask_answered", self.agent_id, {
            "route": route.name, "answer": answer[:300], "scope_added": added})
        return _ToolOutcome(ok=True, value=0.1,
                            note=f"用户答复: {answer[:60]}",
                            text="\n".join(lines))

    async def _tool_kb_search(self, args: dict[str, Any]) -> _ToolOutcome:
        """经验知识库检索：检查清单/攻击路径/狩猎模板/平台规则（参考资料）。"""

        from spiritscout.ctf.agents import _load_sec_skills
        query = str(args.get("query", "") or "").strip()
        if not query:
            return _ToolOutcome(ok=False, text="kb_search 需要 query 检索词。")
        kb = self.ctx.knowledge
        if kb is None:
            return _ToolOutcome(ok=False, text="经验知识库未装配。")
        try:
            hits = kb.search(query, limit=6)
        except Exception as exc:  # noqa: BLE001 —— 检索失败作为结果回灌
            return _ToolOutcome(ok=False, text=f"知识库检索失败: {exc}")
        # 安全技能库检索（NoorQureshi/security-skills，MIT）：直接交给 ResourceStore
        # 打分排序。这里不再自己写一遍「任意词子串命中」的粗匹配——两套算法给出不
        # 一致的结果时，agent 从 kb_search 看到的是 A、从 reference_skills 看到的是 B。
        q_lower = query.lower()
        # 当前任务大类：RunMode 的取值与 skill_modes 的 MODE_* 一一对应
        from spiritscout.resources.skill_modes import filter_by_mode  # noqa: PLC0415

        mode_key = str(getattr(getattr(self.ctx, "mode", None), "value", "") or "")
        resources = getattr(kb, "resources", None)
        if resources is not None:
            # 多取一些再筛：先取 3 条再过滤，可能全被当前模式滤掉，等于没给技能。
            # 之前这里**完全没走模式过滤**——模式分类只在下面的兜底分支生效，
            # 而运行时 resources 一定存在，所以那条路从来没跑过。
            # enabled 可能是 None（老条目没这个字段）——只有明确 False 才算停用。
            # 之前写 `sk.get("enabled", True)`，键存在值为 None 时拿到 None，
            # 当假值处理，整批技能全被滤掉，kb_search 永远返回空技能。
            raw = [sk for sk in resources.skills(query, include_body=True, limit=12)
                   if sk.get("enabled") is not False]
            skill_hits = filter_by_mode(raw, mode_key)[:3]
        else:
            from spiritscout.resources.store import (
                _skill_idf,
                _skill_score,
                _skill_terms,
            )

            terms = _skill_terms(query)
            # 只给当前任务模式用得上的技能：SRC 不必看 pwn/stego，CTF 不必看
            # 企业内网那一套。RunMode 的取值与 skill_modes 的 MODE_* 一一对应。
            from spiritscout.resources.skill_modes import filter_by_mode

            mode_key = str(getattr(getattr(self.ctx, "mode", None), "value", "") or "")
            pool = filter_by_mode(
                [sk for sk in _load_sec_skills() if sk.get("enabled", True)], mode_key)
            idf = _skill_idf(terms, pool)
            ranked = sorted(((s, sk) for sk in pool
                             if (s := _skill_score(terms, sk, idf=idf)) > 0),
                            key=lambda pair: -pair[0])
            skill_hits = [sk for _, sk in ranked[:3]]
        if skill_hits:
            body_lines = ["\n=== 安全技能库命中（NoorQureshi/security-skills，MIT）==="]
            for sk in skill_hits[:3]:
                body_lines.append(f"[{sk['skill_id']}] {sk['description'][:120]}")
                body_lines.append(sk["body"][:1500])
                body_lines.append("---")
            body = "\n".join(body_lines)[:4000]
        else:
            body = None
        # 本会话 MCP 工具目录命中（活目录：用户经网页端安装/移除的服务器随
        # 会话装配即时反映，无需入库——想到能力却没想起工具名时在此对上）
        mcp_hits: list[tuple[str, str]] = []
        for exposed, entry in (getattr(self.ctx, "mcp_tools", None) or {}).items():
            desc = str((entry.get("schema") or {}).get("description", ""))
            hay = " ".join((exposed, str(entry.get("server", "")),
                            str(entry.get("tool", "")), desc)).lower()
            if any(w in hay for w in q_lower.split() if len(w) >= 2):
                mcp_hits.append((exposed, desc))
        mcp_text = ""
        if mcp_hits:
            lines = ["\n=== 本会话 MCP 工具命中（当前会话真实目录）==="]
            lines += [f"{exposed} — {desc[:160]}"
                      for exposed, desc in mcp_hits[:8]]
            mcp_text = "\n".join(lines)[:2000]
        if not hits and not skill_hits and not mcp_hits:
            return _ToolOutcome(ok=False, text=(
                f"经验库没有命中「{query[:60]}」——按你收集到的信息自行分析。"))
        blocks: list[str] = []
        for h in hits:
            entry = kb.get_entry(h["base"], h["entry_key"])
            if entry:
                blocks.append(self._kb_entry_digest(h["base"], entry))
        titles = "、".join(str(h.get("title", ""))[:24] for h in hits[:4])
        kb_text = "\n\n".join(blocks)[:_TOOL_RESULT_CAP] or "（条目内容为空）"
        skill_text = ("\n\n" + body if body else "")
        return _ToolOutcome(ok=True, value=0.15,
                            note=f"经验库命中 {len(blocks)} 条: {titles}"
                                 + (f" + 安全技能 {len(skill_hits)} 条" if skill_hits else "")
                                 + (f" + MCP 工具 {len(mcp_hits)} 个" if mcp_hits else ""),
                            text=(kb_text + skill_text + mcp_text)[:_TOOL_RESULT_CAP])

    async def _tool_mcp(self, task: Task, name: str,
                        args: dict[str, Any]) -> _ToolOutcome:
        """外部 MCP 工具调用：统一经过 ToolRegistry 的工具权限与危险动作审批。"""
        entry = (getattr(self.ctx, "mcp_tools", None) or {}).get(name)
        if entry is None:
            return _ToolOutcome(ok=False,
                                text=f"MCP 工具 {name} 不在当前会话工具目录。")
        res = await self.ctx.tools.call(name, args, **self._tool_ctx(task))
        text = res.stdout or res.error or res.stderr
        note = f"MCP {entry['server']}/{entry['tool']}"
        if not res.ok:
            reason = res.parsed.get("failure_summary") or res.error or text or "未提供诊断"
            note += " 失败：" + " ".join(str(reason).split())
        return _ToolOutcome(ok=res.ok,
                            value=0.2 if res.ok else 0.0,
                            findings=[],
                            note=note[:120],
                            text=text[:_TOOL_RESULT_CAP] or "（无输出）")

    async def _tool_http(self, task: Task, route, state: dict[str, Any],
                         args: dict[str, Any]) -> _ToolOutcome:
        """通用 HTTP 动作：LLM 提议 → 确定性校验 → 环形避让 → 执行 → 观察。

        测试边界由模型结合用户上下文维护（http_probe 内），参数上限仅防上下文污染。
        """

        from spiritscout.ctf.agents import _challenge_instance_blocker
        resolved = self._resolve_http(task, args)
        if resolved is None:
            return _ToolOutcome(ok=False, text=(
                "目标不合法：path 需为以 / 开头的站内路径，或 url 传完整 "
                "http(s) 地址（须在授权范围内）。"))
        url, params, headers, data = resolved
        method = str(args.get("method", "GET") or "GET").upper()
        if method not in _HTTP_METHODS:
            return _ToolOutcome(
                ok=False, text=f"方法 {method} 不支持，可用：{', '.join(_HTTP_METHODS)}")
        # 有副作用的请求须模型自报行为类别（审批与审计的依据），缺声明不执行；
        # 纯读取方法（GET/HEAD/OPTIONS）不需要——是否敏感由策略门按类别判断
        action = str(args.get("action", "") or "").strip()
        if method not in _HTTP_SAFE_METHODS and not action:
            return _ToolOutcome(ok=False, text=(
                f"{method} 是有副作用的请求，需要 action 参数如实声明行为类别"
                "（analysis / intrusive_write / brute_force / webshell_upload 等）；"
                "缺声明不执行。纯读取请用 GET/HEAD。"))
        base = str(self.challenge_of(task).get("url") or task.target or "").rstrip("/")
        shown = url[len(base):] if base and url.startswith(base) else url
        desc = f"{method} {shown[:100] or '/'}"

        # 记录回放（record-first）：本路线已读取过的地址直接回放当时记录，
        # 不再发请求；需要重新获取（页面状态可能已变化）时模型显式加
        # refresh 参数。Range 等头不同的请求键不同，分段取原文不受影响。
        refresh = bool(args.get("refresh"))
        hsig = ""
        if headers:
            hsig = "|" + "|".join(sorted(
                f"{k}:{str(v)[:60]}" for k, v in headers.items()))[:300]
        # 跨分支共享缓存：同伴已读取过的裸 GET 直接回放，不重复发请求。
        # 回放必须自我披露：回灌文本若无标记，模型会把旧录像当成新鲜响应
        # （登录后再访问仍见登录前快照 → 误判未登录 → 死循环）；带参数的
        # GET 不回放——缓存键不含参数，回放会答非所问。键含方法与请求头
        # 指纹：POST 的应答不能被同伴的 GET 回放（表单提交/登录跳转的
        # 应答顶掉页面快照），Range 等头不同的分段读取各记各的
        shared_key = f"fetch|{method}|{url}{hsig}"
        shared_fetch = self.cache.get(self.shared_namespace, shared_key)
        if (shared_fetch and not refresh and method == "GET"
                and not params and not data):
            state["observations"].append({
                "action": desc,
                "result": "跨分支记录回放（同伴已读取，本次未重复请求）"})
            return _ToolOutcome(ok=True, value=0.0,
                note=f"{desc} → 跨分支回放",
                text=("（记录回放：同伴此前已读取该地址，以下为当时的记录，"
                      "本次未发请求；页面状态可能已变化——登录/写入之后要看"
                      "最新现场，在参数中加 \"refresh\": true 重新获取）\n"
                      + str(shared_fetch))[:_FETCH_RECORD_CAP])
        rkey = attempt_key(method, url, params) + hsig
        fetches = state.setdefault("fetches", {})
        if (method == "GET" and not params and not data and not refresh
                and rkey in fetches):
            record = fetches[rkey]
            prior = record.get("obs") if isinstance(record.get("obs"), dict) else {}
            state["observations"].append({
                "action": desc,
                "result": f"记录回放（第 {record.get('round', '?')} 回合已读取）"})
            self.ctx.audit.log("agent.reread", self.agent_id, {
                "route": route.name, "challenge": self.challenge_id(task),
                "turn": int(state.get("turn") or 0), "url": url[:200],
                "from_round": record.get("round")})
            return _ToolOutcome(
                ok=True, value=0.0, note=f"{desc} → 记录回放",
                text=("（记录回放：该地址已在第 "
                      f"{record.get('round', '?')} 回合读取过，以下为当时的记录，"
                      "本次未重复请求；页面状态可能已变化需要重新获取时，"
                      "在参数中加 \"refresh\": true）\n" + _obs_text(prior))
                [:_FETCH_RECORD_CAP])

        # 环形避让：同伴（含本 Agent 其他路线/前轮尝试）在此入口无产出 → 不重复做功
        akey = attempt_key(method, url, params)
        if self.ctx.ring is not None:
            prior = self.ctx.ring.should_avoid(akey, agent_id=self._ring_actor(route))
            if prior is not None:
                state["observations"].append(
                    {"action": desc, "result":
                     f"环形避让：{prior['agent_name']} 已试此入口（无产出），未重复"})
                self.ctx.audit.log("ring.avoid", self.agent_id, {
                    "route": route.name, "key": akey[:200],
                    "prior_agent": prior["agent_name"]})
                return _ToolOutcome(ok=False, note=f"环形避让：跳过 {desc}", text=(
                    f"{prior['agent_name']} 已经试过 {desc} 且没有产出，"
                    "本次未重复请求。换一个入口或思路。"))

        request: dict[str, Any] = {"url": url, "method": method}
        if action:
            request["action"] = action
        if params:
            request["params"] = params
        if headers:
            request["headers"] = headers
        if data:
            request["data"] = data
        res = await self.ctx.tools.call("http_probe", request, **self._tool_ctx(task))
        if not res.ok:
            if self.ctx.ring is not None:
                self.ctx.ring.record_attempt(
                    akey, agent_id=self._ring_actor(route),
                    agent_name=self._ring_name(route), action="http_request",
                    outcome=0.0, note=str(res.error)[:120])
            state["observations"].append(
                {"action": desc, "result": f"请求失败: {str(res.error)[:100]}"})
            return _ToolOutcome(ok=False, note=f"{desc} → 请求失败", text=(
                f"请求失败（{desc}）：{str(res.error)[:300]}"))
        status = res.parsed.get("status_code", 0)
        obs = _http_observation(desc, res, url, [])
        state["observations"].append(obs)
        fetches[rkey] = {"round": int(state.get("turn") or 0), "obs": obs}
        if method == "GET" and not params and not data:
            # 只有裸 GET 进跨分支回放池：POST/带参请求的应答进池会被
            # 同伴的 GET 回放成语义错位的“现场”（登录态、写操作结果）
            self.cache.put(self.shared_namespace, shared_key,
                           _obs_text(obs)[:_FETCH_RECORD_CAP])
        # 环形共享缓存：入口与关键信号登记，同伴不再重复探同一入口
        signal = _signal_line(obs)
        self._record_site(task, url, f"{status}{signal}", route=route.name)
        value = 0.25 if status < 400 else 0.05
        blocker = _challenge_instance_blocker(status, res.stdout)
        if blocker:
            state["stall"] = blocker
        if self.ctx.ring is not None:
            self.ctx.ring.record_attempt(
                akey, agent_id=self._ring_actor(route),
                agent_name=self._ring_name(route), action="http_request",
                outcome=value, note=f"{status}")
        body_text = _obs_text(obs)
        # 响应原文进证据池：submit_flag 要求 flag 逐字出现过，靠这里兜底
        self._record_evidence(state, body_text)
        return _ToolOutcome(
            ok=True, value=value, observation=obs,
            findings=[],
            note=f"{desc} → {status}",
            text=body_text)

    async def _tool_sandbox(self, task: Task, route, state: dict[str, Any],
                            name: str, args: dict[str, Any]) -> _ToolOutcome:
        """沙箱执行动作（shell/python）：LLM 出代码 → 执行后端运行 → 观察。

        与 http_request 同一原则：LLM 只决定「跑什么」，执行边界由后端
        （Linux bwrap 整机只读/主目录遮蔽；host 后端工作目录锁定 + 环境
        白名单 + 超时杀进程树）与审计留痕约束；输出照常过 Flag 提取与
        观察序列。
        """

        from spiritscout.ctf.agents import _inline_multiline_python, _missing_exec_param
        param = "command" if name == "shell" else "code"
        code = str(args.get(param, "") or "").strip()
        if not code:
            return _ToolOutcome(ok=False, text=_missing_exec_param(name, param, args))
        if name == "shell" and _inline_multiline_python(code):
            # 现场实测：模型把整段循环塞进 python -c "..."，换行和引号全被 shell
            # 吃掉，退化成 SyntaxError / unexpected EOF，然后它以为算法写错了。
            # 这不是解题问题，是调用方式问题——直接告诉它换成代码参数。
            return _ToolOutcome(ok=False, text=(
                "这条命令把多行 Python 塞进了 shell 的 python -c \"...\" 里：shell 会"
                "先吃掉换行和引号，代码到不了解释器（退出码 2 / SyntaxError 都是这个原因）。"
                "多行代码请改用 python 工具，把同样的内容放进 code 参数——code 支持直接换行，"
                "不需要转义也不需要 heredoc。"))
        # 行为类别须模型如实自报（通用执行入口无法确认性质就不执行——
        # 不以 readonly 自动放行）；危险语义由策略门即时请示用户。
        # CTF/竞赛模式下所有工具操作本来就预授权（见 AGENTS.md），漏报 action
        # 不该让整条路线卡死：按 analysis 执行并在结果里标明是补的默认值。
        # SRC 模式保持原样——那里 action 是审批链的输入，缺了就不能猜。
        action = str(args.get("action", "") or "").strip()
        if not action and getattr(self.ctx, "mode", None) in (RunMode.CTF, RunMode.COMPETITION):
            action = "analysis"
            action_defaulted = True
        else:
            action_defaulted = False
        if not action:
            return _ToolOutcome(ok=False, text=(
                f"{name} 工具需要 action 参数：如实声明本次执行的行为类别"
                "（analysis / delete_files / reverse_shell / webshell_upload / "
                "container_escape / brute_force / intrusive_write）。"
                "这是危险动作审批与审计的依据，缺声明不执行。"))
        try:
            timeout = float(args.get("timeout") or 60)
        except (TypeError, ValueError):
            timeout = 60.0
        first = code.splitlines()[0][:80]
        desc = f"{name}: {first}"
        if action_defaulted:
            desc += "（action 未声明，按 analysis 记）"
        res = await self.ctx.tools.call(
            "sandbox_exec",
            {"code": code, "language": "python" if name == "python" else "shell",
             "timeout": timeout, "log_name": f"output_{route.name}",
             "action": action},
            **self._tool_ctx(task))
        if not res.ok and not (res.stdout or res.stderr):
            state["observations"].append(
                {"action": desc, "result": f"执行失败: {str(res.error)[:100]}"})
            return _ToolOutcome(ok=False, note=f"{desc} → 执行失败", text=(
                f"{desc} 执行失败：{str(res.error)[:300]}"))
        rc = res.parsed.get("exit_code", res.returncode)
        status = (f"超时终止（{timeout:.0f}s）" if res.timed_out
                  else f"退出码 {rc}")
        # 督导需要看到实际响应；进程成功不代表假设成立或取得有效线索。
        evidence = _head_tail((res.stdout or res.stderr or "无输出").strip(), 160)
        state["observations"].append(
            {"action": desc,
             "result": f"{status}；{evidence}"})
        workdir = str((res.parsed or {}).get("workdir") or "")
        workdir_hint = str((res.parsed or {}).get("workdir_hint") or workdir)
        attachments_dir = str((res.parsed or {}).get("attachments_dir") or "")
        attachments_rel = str((res.parsed or {}).get("attachments_rel") or "")
        out_file = str((res.parsed or {}).get("output_file") or "")
        # 模型常在沙箱里直接 `cat 附件名`，但附件不在工作目录而在同级 attachments/：
        # 每条执行结果都带上这个位置，省掉它在目录里瞎翻的回合。
        extra = ""
        if attachments_dir:
            rel = f"（相对当前工作目录是 {attachments_rel}/）" if attachments_rel else ""
            extra = f"；题目附件在 {attachments_dir}{rel}"
        head = (f"（{status}，耗时 {res.duration_ms / 1000:.1f}s，"
                f"工作目录 {workdir_hint or '见输出'}——会话内持久；/tmp 即焚{extra}）")
        lines = [f"$ {first}", head]
        if res.stdout:
            lines.append("stdout:\n" + _head_tail(res.stdout, _SAND_MODEL_CAP))
        if res.stderr:
            # 报错要点前置：完整 traceback 在下面，但 7B 模型只看得进第一行。
            digest = _stderr_digest(res.stderr)
            if digest:
                lines.append(digest)
            lines.append("stderr:\n" + _head_tail(res.stderr, 1200))
        if out_file:
            label = "输出已轮转，仅保留最近两代" if res.parsed.get("output_rotated") else "输出已存盘"
            lines.append(f"{label}（不占上下文）: {out_file}"
                         "——需要细节时在沙箱里按需读取")
            if res.parsed.get("previous_output_file"):
                lines.append(f"上一代输出: {res.parsed['previous_output_file']}")
        if res.timed_out:
            lines.append("提示：超时通常意味着脚本在等待输入或死循环——"
                         "交互式脚本请改为非交互（预置输入/设置超时）。")
        state["executions"] = int(state.get("executions") or 0) + 1
        state["last_exit_failed"] = bool(res.parsed.get("exit_code"))
        self._record_evidence(state, (res.stdout or "") + ("\n" + res.stderr if res.stderr else ""))
        # 打印出来的中间量是下一阶段的输入；历史会被压缩折叠，这里留一份。
        if (res.stdout or "").strip():
            state["last_stdout"] = res.stdout.strip()[:1200]
        return _ToolOutcome(
            ok=True, value=0.05, empty=not (res.stdout or "").strip(),
            findings=[],
            note=f"{desc} → {status}；{evidence}",
            text="\n".join(lines), metadata={"output_file": out_file} if out_file else {})
