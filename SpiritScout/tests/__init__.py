"""测试包的退出清理。

为什么要这个文件：有些用例建了 SQLite（`bb.db`）后没关连接，Windows 上文件
被占用，`TemporaryDirectory` 的清理会抛 `PermissionError`——而多数用例传了
`ignore_cleanup_errors=True`，这个失败被静默吞掉，目录就留在磁盘上。跑一遍
全量测试能在包目录下攒出几十个 `tmpXXXXXXXX`。

这里在测试进程退出时扫一遍、把「名字像 mkdtemp 产物」且「里面只有测试残留
文件」的目录删掉。判断条件刻意收窄，避免误删人工建的目录：

* 名字形如 ``tmp`` + 8 位小写字母数字下划线（mkdtemp 的默认命名）；
* 位于包目录（`SpiritScout/`）或仓库根目录；
* 里面只有 `.db` / `.db-wal` / `.db-shm` / `.jsonl` / `.yaml` / `.txt` / `.bin`
  这类测试产物。

删除失败一律忽略——这是收尾，不该让测试因为清理而报错。
"""

from __future__ import annotations

import atexit
import contextlib
import os
import re
import shutil
import tempfile
from pathlib import Path

_PKG = Path(__file__).resolve().parent.parent
_ROOT = _PKG.parent
_TEMP = Path(tempfile.gettempdir())


# Windows does not allow deleting an open SQLite database.  Older tests create
# TemporaryDirectory objects and close their stores through addCleanup(), which
# runs after the context manager has already attempted cleanup.  Linux CI never
# observes this ordering bug, but Windows does.  Keep the fixture cleanup
# best-effort on Windows and sweep the remaining test-only files at exit.
if os.name == "nt":
    _OriginalTemporaryDirectory = tempfile.TemporaryDirectory

    class _WindowsTemporaryDirectory(_OriginalTemporaryDirectory):
        def __init__(self, *args, ignore_cleanup_errors: bool = True, **kwargs):
            super().__init__(
                *args,
                ignore_cleanup_errors=ignore_cleanup_errors,
                **kwargs,
            )

    tempfile.TemporaryDirectory = _WindowsTemporaryDirectory

#: mkdtemp 默认命名：tmp + 8 位随机串
_NAME = re.compile(r"^tmp[a-z0-9_]{8}$")
#: 只有这些后缀才认为是测试残留
_SUFFIXES = {".db", ".db-wal", ".db-shm", ".jsonl", ".yaml", ".yml",
             ".txt", ".bin", ".key", ".log", ".json"}


def _is_test_leftover(path: Path) -> bool:
    if not path.is_dir() or not _NAME.match(path.name):
        return False
    try:
        entries = list(path.rglob("*"))
    except OSError:
        return False
    for item in entries:
        if item.is_dir():
            continue
        if item.suffix.lower() not in _SUFFIXES:
            return False
    return True


@atexit.register
def _sweep_temp_dirs() -> None:
    for base in (_PKG, _ROOT, _TEMP):
        if not base.is_dir():
            continue
        try:
            candidates = list(base.iterdir())
        except OSError:
            continue
        for item in candidates:
            if not _is_test_leftover(item):
                continue
            with contextlib.suppress(OSError):
                shutil.rmtree(item, ignore_errors=True)
