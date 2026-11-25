"""跨平台测试支持：某些能力在 Windows 上需要额外权限。"""

from __future__ import annotations

import os
import tempfile


def _probe_symlink() -> bool:
    """本机能不能创建符号链接。

    Windows 需要管理员权限或开发者模式；Linux / macOS 直接可用。
    探测失败不是产品缺陷 —— 依赖符号链接的用例在拿不到该能力时应跳过，
    而不是把一个环境限制当成功能回归。
    """
    try:
        with tempfile.TemporaryDirectory() as tmp:
            os.symlink(os.path.join(tmp, "a"), os.path.join(tmp, "b"))
        return True
    except (OSError, NotImplementedError):
        return False


CAN_SYMLINK = _probe_symlink()
