"""CTF agent 的运行上限与取值集。

从 `ctf/agents.py` 搬出来的——工具实现（`tool_mixins.py`）和 agent 主体都要用，
留在原文件里会让 mixin 反向依赖 agent。
"""

from __future__ import annotations

ASK_USER_REPEAT_LIMIT = 3
ASK_USER_TOTAL_LIMIT = 3
_FETCH_RECORD_CAP = 3600
_SAND_MODEL_CAP = 3600
_HTTP_METHODS = ("GET", "POST", "PUT", "DELETE", "PATCH", "HEAD", "OPTIONS")
_HTTP_SAFE_METHODS = frozenset({"GET", "HEAD", "OPTIONS"})
