"""沙箱目录身份：作用域规范化与不跟随链接的目录访问。"""
from __future__ import annotations

import hashlib
import os
import re
from contextlib import contextmanager
from dataclasses import dataclass
from pathlib import Path
from typing import Iterator

_O_DIRECTORY = getattr(os, "O_DIRECTORY", 0)
_O_NOFOLLOW = getattr(os, "O_NOFOLLOW", 0)
_O_NONBLOCK = getattr(os, "O_NONBLOCK", 0)
_IS_WINDOWS = os.name == "nt"


@dataclass(frozen=True)
class DirectoryRef:
    """A directory pinned by fd on POSIX and by a checked path on Windows."""

    path: Path
    fd: int | None = None


def sandbox_scope(scope: str | None) -> str:
    """安全短 ID 保留旧路径；变形 ID 用原文哈希区分，避免不同题目共用目录。"""
    raw = str(scope or "")
    safe = re.sub(r"[^A-Za-z0-9._-]+", "_", raw).strip("._-")[:80]
    if raw == safe:
        return safe
    # ~ 不在可原样保留的字符集中，生成名不会与未变形的原始 ID 撞名。
    return f"{safe[:15]}~{hashlib.sha256(raw.encode('utf-8')).hexdigest()}"


def sandbox_directory(scope: str | None) -> Path:
    safe = sandbox_scope(scope)
    return Path("sandbox") / safe if safe else Path("sandbox")


def sandbox_tmp_directory(scope: str | None) -> Path:
    # 控制父目录不能位于沙箱可写挂载内，否则可被模型替换后重新挂载。
    safe = sandbox_scope(scope)
    return Path(".sandbox-tmp") / ("topic-" + safe if safe else "session")


@contextmanager
def open_directory(
    root: Path, relative: Path, *, create: bool = False
) -> Iterator[DirectoryRef]:
    """Pin a directory against traversal; downstream operations use the helpers below."""
    if relative.is_absolute() or ".." in relative.parts:
        raise ValueError("目录必须是工作区内的相对路径")
    if _IS_WINDOWS:
        current = root.resolve()
        if not current.is_dir() or current.is_symlink():
            raise ValueError(f"目录不可用：{root}")
        for part in relative.parts:
            child = current / part
            if not child.exists():
                if not create:
                    raise FileNotFoundError(child)
                child.mkdir(mode=0o700, exist_ok=True)
            if child.is_symlink():
                raise ValueError(f"目录路径包含链接或非目录：{relative}")
            if not child.is_dir():
                raise ValueError(f"目录路径包含链接或非目录：{relative}")
            resolved = child.resolve()
            if resolved != current and current not in resolved.parents:
                raise ValueError(f"目录越界：{relative}")
            current = resolved
        yield DirectoryRef(current)
        return
    directory = os.open(root, os.O_RDONLY | _O_DIRECTORY | _O_NOFOLLOW)
    try:
        for part in relative.parts:
            if create:
                try:
                    os.mkdir(part, mode=0o700, dir_fd=directory)
                except FileExistsError:
                    pass
            child = os.open(part, os.O_RDONLY | _O_DIRECTORY | _O_NOFOLLOW,
                            dir_fd=directory)
            os.close(directory)
            directory = child
        yield DirectoryRef(root.joinpath(*relative.parts), directory)
    finally:
        os.close(directory)


def _simple_name(name: str) -> str:
    if not name or Path(name).name != name or name in {".", ".."}:
        raise ValueError(f"需要目录内的单层名称：{name!r}")
    return name


def _checked_child(directory: DirectoryRef, name: str) -> Path:
    name = _simple_name(name)
    target = directory.path / name
    if target.is_symlink():
        raise FileNotFoundError(f"拒绝访问链接：{name}")
    resolved = target.resolve(strict=False)
    if resolved != directory.path and directory.path not in resolved.parents:
        raise ValueError(f"文件路径越界：{name}")
    return resolved


def open_at(directory: DirectoryRef, name: str, flags: int, mode: int = 0o666) -> int:
    if directory.fd is not None:
        return os.open(name, flags, mode, dir_fd=directory.fd)
    target = _checked_child(directory, name)
    clean = flags & ~(_O_DIRECTORY | _O_NOFOLLOW | _O_NONBLOCK)
    clean |= getattr(os, "O_BINARY", 0)
    return os.open(target, clean, mode)


def mkdir_at(directory: DirectoryRef, name: str, mode: int = 0o700) -> None:
    if directory.fd is not None:
        os.mkdir(name, mode=mode, dir_fd=directory.fd)
        return
    target = _checked_child(directory, name)
    target.mkdir(mode=mode)


def unlink_at(directory: DirectoryRef, name: str) -> None:
    if directory.fd is not None:
        os.unlink(name, dir_fd=directory.fd)
        return
    os.unlink(_checked_child(directory, name))


def chmod_at(
    directory: DirectoryRef, name: str, mode: int, *, follow_symlinks: bool = False
) -> None:
    if directory.fd is not None:
        os.chmod(
            name,
            mode,
            dir_fd=directory.fd,
            follow_symlinks=follow_symlinks,
        )
        return
    target = _checked_child(directory, name)
    if target.is_symlink() and not follow_symlinks:
        raise ValueError(f"拒绝修改链接：{name}")
    os.chmod(target, mode, follow_symlinks=follow_symlinks)


def link_at(
    source_directory: DirectoryRef,
    source: str,
    target_directory: DirectoryRef,
    target: str,
) -> None:
    if source_directory.fd is not None and target_directory.fd is not None:
        os.link(
            source,
            target,
            src_dir_fd=source_directory.fd,
            dst_dir_fd=target_directory.fd,
            follow_symlinks=False,
        )
        return
    os.link(
        _checked_child(source_directory, source),
        _checked_child(target_directory, target),
        follow_symlinks=False,
    )
