"""Run one unittest module with Windows-safe temporary-directory cleanup."""

from __future__ import annotations

import gc
import sqlite3
import sys
import tempfile
import time
import unittest

_original_cleanup = tempfile.TemporaryDirectory.cleanup


def _close_sqlite_connections() -> None:
    for value in gc.get_objects():
        if not isinstance(value, sqlite3.Connection):
            continue
        try:
            value.close()
        except (sqlite3.ProgrammingError, sqlite3.OperationalError):
            pass


def _windows_safe_cleanup(directory: tempfile.TemporaryDirectory) -> None:
    gc.collect()
    for _ in range(6):
        try:
            _original_cleanup(directory)
            return
        except PermissionError:
            _close_sqlite_connections()
            gc.collect()
            time.sleep(0.05)
    _original_cleanup(directory)


tempfile.TemporaryDirectory.cleanup = _windows_safe_cleanup


def main() -> int:
    if len(sys.argv) != 2:
        print("usage: test_worker.py <test-module>", file=sys.stderr)
        return 2
    suite = unittest.defaultTestLoader.loadTestsFromName(sys.argv[1])
    result = unittest.TextTestRunner(verbosity=2).run(suite)
    return 0 if result.wasSuccessful() else 1


if __name__ == "__main__":
    raise SystemExit(main())
