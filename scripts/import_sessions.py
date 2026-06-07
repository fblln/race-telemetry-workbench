#!/usr/bin/env python3
"""Import multiple FastF1 sessions concurrently.

This orchestrates `scripts/import_session.py` as the reliable unit of work. Each
session import keeps full context enabled by default and uses the single-session
importer's optimized telemetry/position COPY path. The concurrency here is at
the session level, which is the useful shape for importing many sessions from a
season.
"""

from __future__ import annotations

import argparse
import subprocess
import sys
import time
from concurrent.futures import ThreadPoolExecutor, as_completed
from dataclasses import dataclass
from pathlib import Path
from typing import Sequence

REPO_ROOT = Path(__file__).resolve().parents[1]
IMPORT_SESSION_SCRIPT = REPO_ROOT / "scripts" / "import_session.py"

if str(REPO_ROOT) not in sys.path:
    sys.path.insert(0, str(REPO_ROOT))

from scripts.download_session import DEFAULT_CACHE_DIR, VALID_SESSIONS, positive_int
from scripts.import_session import DEFAULT_BATCH_SIZE, DEFAULT_DATABASE_URL, DEFAULT_TELEMETRY_WORKERS


@dataclass(frozen=True)
class ImportTask:
    year: int
    event: str
    session: str

    @property
    def label(self) -> str:
        return f"{self.year} {self.event} {self.session}"


@dataclass(frozen=True)
class ImportTaskResult:
    task: ImportTask
    returncode: int
    elapsed_seconds: float
    output: str


def parse_csv_values(value: str | None) -> list[str]:
    if value is None:
        return []
    return [item.strip() for item in value.split(",") if item.strip()]


def parse_session_codes(value: str | None) -> list[str]:
    sessions = [session.upper() for session in parse_csv_values(value or "R")]
    invalid = sorted(set(sessions).difference(VALID_SESSIONS))
    if invalid:
        raise argparse.ArgumentTypeError(
            f"Unsupported session code(s): {', '.join(invalid)}. Valid sessions: {', '.join(sorted(VALID_SESSIONS))}"
        )
    return sessions


def parse_spec(value: str) -> ImportTask:
    parts = [part.strip() for part in value.split(":")]
    if len(parts) not in (2, 3) or not all(parts):
        raise argparse.ArgumentTypeError("session specs must look like YEAR:EVENT or YEAR:EVENT:SESSION")

    try:
        year = int(parts[0])
    except ValueError as exc:
        raise argparse.ArgumentTypeError("session spec YEAR must be an integer") from exc

    session = (parts[2] if len(parts) == 3 else "R").upper()
    if session not in VALID_SESSIONS:
        raise argparse.ArgumentTypeError(
            f"Unsupported session code: {session}. Valid sessions: {', '.join(sorted(VALID_SESSIONS))}"
        )
    return ImportTask(year=year, event=parts[1], session=session)


def build_tasks(args: argparse.Namespace) -> list[ImportTask]:
    if args.spec:
        return [parse_spec(spec) for spec in args.spec]

    if args.year is None or not args.events:
        raise argparse.ArgumentTypeError("either --spec or both --year and --events are required")

    events = parse_csv_values(args.events)
    sessions = parse_session_codes(args.sessions)
    if not events:
        raise argparse.ArgumentTypeError("--events must contain at least one event")

    return [
        ImportTask(year=args.year, event=event, session=session)
        for event in events
        for session in sessions
    ]


def build_import_command(task: ImportTask, args: argparse.Namespace) -> list[str]:
    command = [
        sys.executable,
        str(IMPORT_SESSION_SCRIPT),
        "--year",
        str(task.year),
        "--event",
        task.event,
        "--session",
        task.session,
        "--mode",
        args.mode,
        "--cache-dir",
        str(args.cache_dir),
        "--database-url",
        args.database_url,
        "--batch-size",
        str(args.batch_size),
        "--telemetry-workers",
        str(args.telemetry_workers),
        "--log-level",
        args.log_level,
    ]
    if not args.parallel_sample_copy:
        command.append("--no-parallel-sample-copy")
    return command


def run_task(task: ImportTask, args: argparse.Namespace) -> ImportTaskResult:
    start = time.perf_counter()
    completed = subprocess.run(
        build_import_command(task, args),
        cwd=REPO_ROOT,
        text=True,
        stdout=subprocess.PIPE,
        stderr=subprocess.STDOUT,
        check=False,
    )
    return ImportTaskResult(
        task=task,
        returncode=completed.returncode,
        elapsed_seconds=time.perf_counter() - start,
        output=completed.stdout,
    )


def run_imports(tasks: Sequence[ImportTask], args: argparse.Namespace) -> list[ImportTaskResult]:
    results: list[ImportTaskResult] = []
    with ThreadPoolExecutor(max_workers=args.workers) as executor:
        futures = [executor.submit(run_task, task, args) for task in tasks]
        for future in as_completed(futures):
            result = future.result()
            results.append(result)
            status = "OK" if result.returncode == 0 else "FAILED"
            print(f"[{status}] {result.task.label} in {result.elapsed_seconds:.1f}s")
            if result.returncode != 0 or args.verbose:
                print(result.output.rstrip())
    return results


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description="Import multiple cached FastF1 sessions concurrently.")
    parser.add_argument("--year", type=int, help="Championship year for --events imports.")
    parser.add_argument("--events", help='Comma-separated events, for example "Bahrain,Monza,Suzuka".')
    parser.add_argument(
        "--sessions",
        default="R",
        help="Comma-separated FastF1 session codes for --events imports. Default: R.",
    )
    parser.add_argument(
        "--spec",
        action="append",
        help='Explicit session spec. Repeatable. Format: YEAR:EVENT or YEAR:EVENT:SESSION, for example "2024:Monza:R".',
    )
    parser.add_argument("--workers", type=positive_int, default=2, help="Concurrent session imports. Default: 2.")
    parser.add_argument("--mode", choices=["fail", "replace", "upsert"], default="fail")
    parser.add_argument("--cache-dir", type=Path, default=DEFAULT_CACHE_DIR)
    parser.add_argument("--database-url", default=DEFAULT_DATABASE_URL)
    parser.add_argument("--batch-size", type=positive_int, default=DEFAULT_BATCH_SIZE)
    parser.add_argument("--telemetry-workers", type=positive_int, default=DEFAULT_TELEMETRY_WORKERS)
    parser.set_defaults(parallel_sample_copy=True)
    parser.add_argument("--parallel-sample-copy", dest="parallel_sample_copy", action="store_true")
    parser.add_argument("--no-parallel-sample-copy", dest="parallel_sample_copy", action="store_false")
    parser.add_argument(
        "--log-level",
        default="WARNING",
        choices=["DEBUG", "INFO", "WARNING", "ERROR"],
    )
    parser.add_argument("--verbose", action="store_true", help="Print successful child importer output.")
    return parser


def main(argv: list[str] | None = None) -> int:
    parser = build_parser()
    args = parser.parse_args(argv)

    try:
        tasks = build_tasks(args)
    except argparse.ArgumentTypeError as exc:
        parser.error(str(exc))

    if not tasks:
        parser.error("no sessions selected")

    start = time.perf_counter()
    print(
        f"Importing {len(tasks)} session(s) with {args.workers} session worker(s), "
        f"full context enabled, parallel_sample_copy={args.parallel_sample_copy}"
    )
    results = run_imports(tasks, args)
    failures = [result for result in results if result.returncode != 0]
    print(f"Completed {len(results) - len(failures)}/{len(results)} session import(s) in {time.perf_counter() - start:.1f}s")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
