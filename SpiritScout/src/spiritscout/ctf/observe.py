"""观察与摘要：把工具/HTTP 的原始输出压成模型能读的紧凑形态。

从 `ctf/agents.py` 搬出来的——这些是纯函数（输入文本、输出文本），不碰 agent
状态，混在三千行的模块里只会让阅读成本翻倍。

`html_digest` 把页面压成「标题 + 关键文本 + 链接/表单」；`stderr_digest` 把
报错的关键几行提到前面（模型最容易忽略的就是埋在中间的错因）；
`head_tail` 是通用的「头尾保留、中间省略」。
"""

from __future__ import annotations

import copy
import html
import json
import re
from typing import Any

_OBS_EXCERPT_CAP = 4000

_HTML_HINT = re.compile(
    r"<\s*(?:!doctype|html|head|body|div|span|a|p|form|table|script|style|title)\b",
    re.IGNORECASE)

_OBS_HEADERS = ("location", "set-cookie", "server", "x-powered-by",
                "content-type", "www-authenticate", "x-flag", "allow")

def _collapse(text: str) -> str:
    """空白折叠（多行 HTML 片段 → 单行可读文本）。"""
    return re.sub(r"\s+", " ", text).strip()

def _model_messages(messages: list[dict[str, Any]], *, initialized: bool) \
        -> list[dict[str, Any]]:
    """原样回放模型正文、思考与工具调用；仅移除框架内部元数据。

    空正文的工具调用也是有效原生响应，不能合成正文污染缓存前缀。
    嵌套工具参数必须脱离持久状态，防止适配器修改历史。
    """
    return copy.deepcopy([{k: v for k, v in message.items() if not k.startswith("_context_")}
                          for message in messages])

_LOOP_HEAD = re.compile(r"^\s*(?:for|while)\s*\(.*", re.M)

def _stderr_digest(stderr: str) -> str:
    """从 traceback 里提炼一行「错误要点」，让小模型第一眼看到报错本质。

    现场：Qwen-7B 在 `IndexError` 后反复重发同一条命令——完整 stderr 有
    三千字符，报错类型和出错行埋在中间，7B 的注意力抓不住，于是它判断
    「算法写错了」却不改代码。把「错误类型: 消息 ← 第 N 行：源码」提炼成
    一行放在回灌文本最前面，改错这件事才有下手的地方。
    """
    marker = "Traceback (most recent call last)"
    cut = stderr.rfind(marker)
    if cut < 0:
        return ""
    tail_lines = [ln.strip() for ln in stderr[cut:].splitlines() if ln.strip()]
    error_line = tail_lines[-1] if tail_lines else ""
    src_line = ""
    for idx, ln in enumerate(tail_lines):
        if (ln.startswith("File ") and ", line " in ln
                and idx + 1 < len(tail_lines)):
            next_ln = tail_lines[idx + 1]
            if not next_ln.startswith(("File ", "Traceback", "During handling")):
                src_line = next_ln
    lineno = ""
    match = re.findall(r", line (\d+)", stderr[cut:])
    if match:
        lineno = match[-1]
    parts = ["报错要点："]
    if error_line:
        parts.append(error_line)
    if lineno and src_line:
        parts.append(f"← 第 {lineno} 行：{src_line}")
    digest = " ".join(parts)
    return digest if error_line else ""

def _last_outcome_failed(state: dict[str, Any]) -> bool:
    """上一次执行是否报错：决定「重复调用」的提示要不要指向报错本身。"""
    return bool(state.get("last_exit_failed"))

def _source_loops(text: str, limit: int = 12) -> list[str]:
    """摘出源码里的循环头（含行号）：算法就藏在这些循环里。"""
    found: list[str] = []
    for match in _LOOP_HEAD.finditer(text or ""):
        line = match.group(0).strip()
        if len(line) < 6:
            continue
        number = text.count("\n", 0, match.start()) + 1
        found.append(f"  L{number}: {line[:160]}")
        if len(found) >= limit:
            break
    return found

def _complete_tool_history(messages: Any) -> bool:
    """恢复时只接受完整的原生工具往返；不猜测中断工具的执行结果。"""
    if not isinstance(messages, list):
        return False
    pending: set[str] = set()
    for message in messages:
        if not isinstance(message, dict):
            return False
        if message.get("role") == "tool":
            call_id = message.get("tool_call_id")
            if call_id not in pending:
                return False
            pending.remove(call_id)
        else:
            if pending:
                return False
            for call in message.get("tool_calls") or []:
                if not isinstance(call, dict) or not isinstance(call.get("id"), str):
                    return False
                if not call["id"] or call["id"] in pending:
                    return False
                pending.add(call["id"])
    return not pending

def _html_digest(body: str) -> str | None:
    """HTML 响应 → 结构化观察摘要（纯机械提取，不做判断）。

    样式/脚本块剔除（内联 CSS 会把真正的入口信息挤出上下文）；注释单列且
    靠前——开发备注类线索（接口地址、口令提示）常藏于此；另提取标题、表单、
    输入字段、链接、脚本引用与内联脚本。非 HTML 返回 None（回退文本提取）。
    """
    # HTML 文档以标签开头；JS/CSS/JSON 文本里出现 "<div" 之类字面量不算 HTML
    head = body.lstrip("﻿ \t\r\n")[:200].lower()
    if not head.startswith("<") or not _HTML_HINT.search(body[:4000]):
        return None
    m = re.search(r"<title[^>]*>(.*?)</title>", body, re.IGNORECASE | re.DOTALL)
    title = _collapse(m.group(1))[:150] if m else ""
    script_srcs = list(dict.fromkeys(
        s[:150] for s in re.findall(
            r"<script\b[^>]*\bsrc=[\"']([^\"']+)[\"']", body, re.IGNORECASE)))
    inline_js = [s for s in (_collapse(x) for x in re.findall(
        r"<script\b(?![^>]*\bsrc=)[^>]*>(.*?)</script\s*>", body,
        re.IGNORECASE | re.DOTALL)) if s]
    stripped = re.sub(r"<(style|script|noscript)\b[^>]*>.*?</\1\s*>", " ",
                      body, flags=re.IGNORECASE | re.DOTALL)
    comments = [c for c in (_collapse(x) for x in re.findall(
        r"<!--(.*?)-->", stripped, re.DOTALL)) if c and not c.startswith("[if")]
    stripped = re.sub(r"<!--.*?-->", " ", stripped, flags=re.DOTALL)
    forms: list[str] = []
    for tag in re.findall(r"<form\b[^>]*>", stripped, re.IGNORECASE)[:5]:
        action = re.search(r"\baction=[\"']([^\"']*)[\"']", tag, re.IGNORECASE)
        method = re.search(r"\bmethod=[\"']([^\"']*)[\"']", tag, re.IGNORECASE)
        forms.append(f"{method.group(1).upper() if method else 'GET'} "
                     f"{action.group(1) if action else ''}".strip())
    field_bits: list[str] = []
    for attrs in re.findall(r"<input\b([^>]*)>", stripped, re.IGNORECASE)[:10]:
        nm = re.search(r"\bname=[\"']([^\"']+)[\"']", attrs, re.IGNORECASE)
        if not nm:
            continue
        val = re.search(r"\bvalue=[\"']([^\"']*)[\"']", attrs, re.IGNORECASE)
        field_bits.append(
            f"{nm.group(1)}={val.group(1)[:60]}" if val else nm.group(1))
    links: list[str] = []
    for href in re.findall(
            r"<(?:a|link|area)\b[^>]*\bhref=[\"']([^\"']+)[\"']",
            stripped, re.IGNORECASE):
        if href.startswith(("#", "javascript:", "mailto:", "data:")) or href in links:
            continue
        links.append(href[:200])
        if len(links) >= 25:
            break
    text = _collapse(html.unescape(re.sub(r"<[^>]+>", " ", stripped)))
    # 段序即优先级：页面文案（可见文本）排在接口清单（链接/表单/脚本）之前
    # ——排在末尾时最容易被总帽截掉，而页面想让解题者读到的话（题面提示、
    # 使用说明）恰是第一手情报；文案过长时保头保尾（提示常在中后段）
    parts = [f"[HTML 摘要] 样式/脚本块已剔除（原文 {len(body)} 字符）"]
    if title:
        parts.append(f"[标题] {title}")
    if comments:
        parts.append("[注释] " + " ｜ ".join(c[:500] for c in comments[:5])[:1200])
    if text:
        visible = text[:1000]
        if len(text) > 1600:
            visible = (text[:1100]
                       + f"\n…[可见文本中段略去 {len(text) - 1600} 字符，末尾接下]…\n"
                       + text[-500:])
        parts.append("[可见文本] " + visible)
    if links:
        parts.append("[链接] " + " ".join(links)[:500])
    if forms:
        parts.append("[表单] " + " ｜ ".join(forms)[:300])
    if field_bits:
        parts.append("[输入字段] " + ", ".join(field_bits[:10])[:300])
    if script_srcs:
        parts.append("[脚本引用] " + " ".join(script_srcs)[:250])
    if inline_js:
        parts.append("[内联脚本] " + " ｜ ".join(inline_js[:3])[:600])
    return "\n".join(parts)[:_OBS_EXCERPT_CAP]

_STR_LITERAL = re.compile(r"""["'`]([^"'`\n]{2,200})["'`]""")

_CALL_SITE = re.compile(
    r"""(?:fetch|axios(?:\.\w+)?|\$\.(?:get|post|ajax|getJSON)|\.open|WebSocket"""
    r"""|EventSource|\.(?:get|post|put|delete|patch))\s*\(\s*["'`]([^"'`\s]{1,200})["'`]""",
    re.IGNORECASE)

_TOKEN_HINT = re.compile(
    r"(token|key|secret|passw|admin|cmd|command|exec|shell|flag|auth)", re.IGNORECASE)

def _text_digest(body: str, content_type: str = "") -> str | None:
    """非 HTML 文本（JS/JSON/纯文本）→ 机械提取：调用点、地址串、敏感词字面量。

    前端代码与接口应答是理解应用行为的现场；整段压缩代码直接进上下文会挤爆
    预算且读不出结构，这里只做机械提取（提出来的都是原文片段，不做判断），
    另附首尾原文——怎么用它由模型决定。
    """
    if len(body) < 200 and not _CALL_SITE.search(body):
        return None
    sites = list(dict.fromkeys(m for m in _CALL_SITE.findall(body) if m.strip()))[:30]
    paths = list(dict.fromkeys(
        s for s in _STR_LITERAL.findall(body)
        if s.startswith(("/", "http://", "https://", "ws://", "wss://"))
        and not s.startswith(("//", "/**"))))[:40]
    tokens = list(dict.fromkeys(
        s for s in _STR_LITERAL.findall(body)
        if _TOKEN_HINT.search(s) and 2 < len(s) < 120))[:15]
    if not (sites or paths or tokens):
        return None
    kind = "JSON/文本" if "json" in content_type.lower() else "脚本/文本"
    note = "结构提取如下，尾部附首尾原文"
    if len(body) > 6000:
        note = ("超出单次上下文预算，结构提取 + 首尾原文如下；"
                "需要完整内容可用 headers.Range 分段请求")
    parts = [f"[{kind} 提取] 原文 {len(body)} 字符（{note}）"]
    if sites:
        parts.append("[调用点] " + " ｜ ".join(sites)[:900])
    if paths:
        parts.append("[地址串] " + " ".join(paths)[:700])
    if tokens:
        parts.append("[敏感词字面量] " + " ｜ ".join(tokens)[:500])
    head = body[:1400]
    tail = body[-700:] if len(body) > 2200 else ""
    parts.append("[原文首段] " + head)
    if tail:
        parts.append("[原文尾段] " + tail)
    return "\n".join(parts)[:_OBS_EXCERPT_CAP]

def _http_observation(desc: str, res: Any, url: str,
                      flags: list[str]) -> dict[str, Any]:
    """HTTP 结果 → 观察条目：状态/跳转/高信号响应头/正文摘要（HTML 结构化）。"""
    parsed = res.parsed if isinstance(res.parsed, dict) else {}
    obs: dict[str, Any] = {"action": desc,
                           "status": parsed.get("status_code", 0)}
    final = str(parsed.get("final_url") or "")
    if final and final.rstrip("/") != url.rstrip("/"):
        obs["final_url"] = final[:300]
    if parsed.get("scope_note"):
        obs["scope_note"] = str(parsed["scope_note"])
    headers = {str(k).lower(): str(v)
               for k, v in (parsed.get("headers") or {}).items()}
    picked = {k: headers[k][:300] for k in _OBS_HEADERS if k in headers}
    if picked:
        obs["headers"] = picked
    body = res.stdout or ""
    ctype = headers.get("content-type", "")    # 脚本/样式/JSON 文本不走 HTML 摘要（否则会被当作标签剥离只剩碎片）
    scripty = any(k in ctype.lower() for k in
                  ("javascript", "ecmascript", "json", "css"))
    obs["excerpt"] = ((None if scripty else _html_digest(body))
                      or _text_digest(body, ctype)
                      or body[:_OBS_EXCERPT_CAP])
    if flags:
        obs["flags"] = flags
    return obs

def _signal_line(obs: dict[str, Any]) -> str:
    """观察 → 一行信号（进共享缓存给同伴看）：标题/注释线索/flag 计数。"""
    excerpt = str(obs.get("excerpt") or "")
    bits: list[str] = []
    for marker in ("[标题] ", "[注释] "):
        idx = excerpt.find(marker)
        if idx >= 0:
            bits.append(_collapse(excerpt[idx + len(marker):].split("\n")[0])[:80])
    if obs.get("final_url"):
        bits.append(f"→ {str(obs['final_url'])[:60]}")
    if obs.get("flags"):
        bits.append(f"flag×{len(obs['flags'])}")
    return (" " + " ｜ ".join(bits)) if bits else ""

def _head_tail(text: str, cap: int) -> str:
    """超长输出的上下文摘要：头尾保留、中段略去（完整内容已落盘可查）。"""
    if len(text) <= cap:
        return text
    dropped = len(text) - cap
    return (text[:cap * 3 // 4]
            + f"\n…[中段略去 {dropped} 字符——完整输出已存工作区文件，"
              "需要时用沙箱读取]…\n"
            + text[-(cap // 4):])

def _obs_text(obs: dict[str, Any]) -> str:
    """结构化观察 → 回灌模型的工具结果文本。"""
    lines = [f"{obs.get('action', '')} → {obs.get('status', 0)}"]
    if obs.get("final_url"):
        lines.append(f"最终地址: {obs['final_url']}")
    if obs.get("scope_note"):
        lines.append(str(obs["scope_note"]))
    if obs.get("headers"):
        lines.append("响应头: " + json.dumps(
            obs["headers"], ensure_ascii=False)[:600])
    if obs.get("excerpt"):
        lines.append("正文:\n" + str(obs["excerpt"]))
    if obs.get("flags"):
        lines.append("Flag 已捕获并提交（FlagManager 确定性提取）: "
                     + ", ".join(str(f) for f in obs["flags"]))
    return "\n".join(lines)
