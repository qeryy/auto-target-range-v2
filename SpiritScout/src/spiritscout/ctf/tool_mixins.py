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
from typing import Any

from spiritscout.core.models import Task
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
