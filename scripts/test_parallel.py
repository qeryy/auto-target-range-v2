"""Run unittest modules concurrently with one process per worker."""

from __future__ import annotations

import argparse
import os
import re
import subprocess
import sys
import time
from concurrent.futures import ThreadPoolExecutor, as_completed
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
TESTS = ROOT / "tests"
LOG_ROOT = ROOT / "build" / "test-logs"


def available_modules() -> list[tuple[str, int]]:
    modules: list[tuple[str, int]] = []
    for path in sorted(TESTS.glob("test_*.py")):
        text = path.read_text(encoding="utf-8")
        modules.append((path.stem, len(re.findall(r"^\s*(?:async\s+)?def test_", text, re.MULTILINE))))
    return modules


def run_module(module: str) -> tuple[int, str, str, str]:
    command = [sys.executable, str(Path(__file__).with_name("test_worker.py")), module]
    env = os.environ.copy()
    roots = [str(ROOT), str(TESTS)]
    if env.get("PYTHONPATH"):
        roots.append(env["PYTHONPATH"])
    env["PYTHONPATH"] = os.pathsep.join(roots)
    env.update({
        "PYTHONUTF8": "1",
        "PYTHONIOENCODING": "utf-8",
        "PYTHON_DOTENV_DISABLED": "1",
        "LITELLM_LOCAL_MODEL_COST_MAP": "True",
    })
    completed = subprocess.run(
        command,
        cwd=ROOT,
        env=env,
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
        timeout=1800,
        check=False,
    )
    output = (completed.stdout or "") + (completed.stderr or "")
    log_root = LOG_ROOT / "modules"
    log_root.mkdir(parents=True, exist_ok=True)
    log = log_root / f"{module}.log"
    log.write_text(output, encoding="utf-8")
    return completed.returncode, module, output, str(log)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("modules", nargs="*", help="test module names; default: all test modules")
    parser.add_argument(
        "--workers",
        type=int,
        default=min(12, max(1, (os.cpu_count() or 2) // 2)),
        help="number of parallel Python processes (default: half the logical CPUs, capped at 12)",
    )
    parser.add_argument("--list", action="store_true", help="list selected modules without running")
    args = parser.parse_args()

    catalog = dict(available_modules())
    if args.modules:
        missing = [name for name in args.modules if name not in catalog]
        if missing:
            parser.error("unknown test modules: " + ", ".join(missing))
        selected = [(name, catalog[name]) for name in args.modules]
    else:
        selected = list(catalog.items())

    if args.list:
        for name, count in sorted(selected):
            print(f"{name}: {count} tests")
        return 0

    if args.workers < 1:
        parser.error("--workers must be positive")
    workers = min(args.workers, len(selected))
    print(f"running {len(selected)} isolated modules with {workers} processes")
    started = time.perf_counter()
    failed = False
    failures: list[str] = []
    with ThreadPoolExecutor(max_workers=workers) as pool:
        futures = {pool.submit(run_module, name): name for name, _ in selected}
        for future in as_completed(futures):
            code, module, output, log = future.result()
            status = "PASS" if code == 0 else "FAIL"
            print(f"[{status}] {module}")
            if code != 0:
                failed = True
                failures.append(module)
                tail = "\n".join(output.splitlines()[-80:])
                print(tail)
                print(f"log: {log}")
    elapsed = time.perf_counter() - started
    print(f"finished in {elapsed:.1f}s")
    if failures:
        print("failed modules: " + " ".join(sorted(failures)))
    return 1 if failed else 0


if __name__ == "__main__":
    raise SystemExit(main())
