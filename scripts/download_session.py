#!/usr/bin/env python3
"""Download and validate a Formula 1 session through FastF1.

This script is intentionally database-free. It is the first import-layer slice:
resolve the requested FastF1 session, fetch the source data into a local cache,
validate that the laps plus core car/position telemetry are available, and write
a compact JSON manifest that later import code can consume or compare against.
"""

from __future__ import annotations

import argparse
import json
import logging
import re
import sys
import time
from dataclasses import asdict, dataclass
from datetime import UTC, datetime, timedelta
from pathlib import Path
from typing import Iterable


DEFAULT_CACHE_DIR = Path("data/fastf1-cache")
DEFAULT_MANIFEST_DIR = Path("data/download-manifests")
VALID_SESSIONS = {"FP1", "FP2", "FP3", "Q", "SQ", "S", "R"}


@dataclass(frozen=True)
class DriverDownloadSummary:
    """Data quality summary for one driver in one session."""

    driver_code: str
    laps: int
    telemetry_samples: int
    position_samples: int
    laps_without_telemetry: list[int]
    laps_without_position: list[int]


@dataclass(frozen=True)
class SessionDownloadSummary:
    """Top-level manifest written after a successful session download."""

    session_id: str
    year: int
    event: str
    official_event_name: str | None
    circuit_name: str | None
    country: str | None
    session: str
    downloaded_at_utc: str
    cache_dir: str
    drivers: list[DriverDownloadSummary]
    elapsed_seconds: float

    @property
    def driver_count(self) -> int:
        return len(self.drivers)

    @property
    def lap_count(self) -> int:
        return sum(driver.laps for driver in self.drivers)

    @property
    def telemetry_sample_count(self) -> int:
        return sum(driver.telemetry_samples for driver in self.drivers)

    @property
    def position_sample_count(self) -> int:
        return sum(driver.position_samples for driver in self.drivers)


def slugify(value: str) -> str:
    """Convert a FastF1 event name into a stable lowercase identifier."""

    slug = re.sub(r"[^a-z0-9]+", "-", value.lower()).strip("-")
    return slug or "session"


def build_session_id(year: int, event: str, session_name: str) -> str:
    """Build the stable session id used by the architecture spec."""

    return f"{year}-{slugify(event)}-{session_name.lower()}"


def parse_driver_filter(value: str | None) -> set[str] | None:
    """Parse a comma-separated driver code filter."""

    if value is None:
        return None

    drivers = {driver.strip().upper() for driver in value.split(",") if driver.strip()}
    if not drivers:
        raise argparse.ArgumentTypeError("--drivers must contain at least one code")
    return drivers


def configure_logging(log_level: str) -> None:
    logging.basicConfig(
        level=getattr(logging, log_level.upper()),
        format="%(asctime)s %(levelname)s %(message)s",
        datefmt="%H:%M:%S",
    )


def load_fastf1():
    """Import FastF1 lazily so unit tests can run without network dependencies."""

    try:
        import fastf1  # type: ignore[import-not-found]
    except ModuleNotFoundError as exc:
        raise RuntimeError(
            "FastF1 is not installed. Run `python3 -m pip install -r scripts/requirements.txt`."
        ) from exc
    return fastf1


def prepare_fastf1_cache(fastf1: object, cache_dir: Path) -> Path:
    """Create and enable the FastF1 cache directory."""

    resolved_cache_dir = cache_dir.resolve()
    resolved_cache_dir.mkdir(parents=True, exist_ok=True)
    fastf1.Cache.enable_cache(str(resolved_cache_dir))  # type: ignore[attr-defined]
    return resolved_cache_dir


def get_event_value(event: object, key: str) -> str | None:
    """Read a value from FastF1's event object without depending on its exact type."""

    try:
        value = event.get(key)  # type: ignore[attr-defined]
    except AttributeError:
        value = getattr(event, key, None)
    if value is None or str(value) == "nan":
        return None
    return str(value)


def get_lap_number(lap: object) -> int:
    value = lap.get("LapNumber")  # type: ignore[attr-defined]
    return int(value)


def count_lap_samples(lap: object, sample_kind: str) -> int:
    """Count per-lap car or position samples, returning zero when FastF1 has none."""

    try:
        if sample_kind == "telemetry":
            data = lap.get_car_data()  # type: ignore[attr-defined]
        elif sample_kind == "position":
            data = lap.get_pos_data()  # type: ignore[attr-defined]
        else:
            raise ValueError(f"Unsupported sample kind: {sample_kind}")
    except Exception as exc:  # FastF1 can raise for incomplete historical laps.
        logging.debug("Could not load %s samples for lap: %s", sample_kind, exc)
        return 0

    return len(data) if data is not None else 0


def normalize_driver_codes(raw_drivers: Iterable[object]) -> list[str]:
    return sorted({str(driver).upper() for driver in raw_drivers if str(driver).strip()})


def get_driver_code(session: object, driver_ref: object) -> str:
    """Return a three-letter driver abbreviation for a FastF1 driver reference."""

    try:
        driver_info = session.get_driver(driver_ref)  # type: ignore[attr-defined]
        code = driver_info.get("Abbreviation")  # type: ignore[attr-defined]
    except Exception:
        code = None

    return str(code or driver_ref).upper()


def select_driver_codes(session: object, driver_filter: set[str] | None) -> list[str]:
    available = normalize_driver_codes(
        get_driver_code(session, driver_ref) for driver_ref in getattr(session, "drivers", [])
    )
    if driver_filter is None:
        return available

    selected = [driver for driver in available if driver in driver_filter]
    missing = sorted(driver_filter.difference(selected))
    if missing:
        raise ValueError(
            f"Requested driver(s) not available in session: {', '.join(missing)}. "
            f"Available drivers: {', '.join(available)}"
        )
    return selected


def summarize_driver(session: object, driver_code: str, limit_laps: int | None) -> DriverDownloadSummary:
    laps = session.laps.pick_drivers(driver_code)  # type: ignore[attr-defined]
    if limit_laps is not None:
        laps = laps.head(limit_laps)

    telemetry_samples = 0
    position_samples = 0
    laps_without_telemetry: list[int] = []
    laps_without_position: list[int] = []

    for _, lap in laps.iterlaps():
        lap_number = get_lap_number(lap)
        telemetry_count = count_lap_samples(lap, "telemetry")
        position_count = count_lap_samples(lap, "position")

        telemetry_samples += telemetry_count
        position_samples += position_count

        if telemetry_count == 0:
            laps_without_telemetry.append(lap_number)
        if position_count == 0:
            laps_without_position.append(lap_number)

    return DriverDownloadSummary(
        driver_code=driver_code,
        laps=len(laps),
        telemetry_samples=telemetry_samples,
        position_samples=position_samples,
        laps_without_telemetry=laps_without_telemetry,
        laps_without_position=laps_without_position,
    )


def validate_summary(summary: SessionDownloadSummary) -> None:
    if summary.driver_count == 0:
        raise ValueError("No drivers were downloaded.")
    if summary.lap_count == 0:
        raise ValueError("No laps were downloaded.")
    if summary.telemetry_sample_count == 0:
        raise ValueError("No telemetry samples were downloaded.")
    if summary.position_sample_count == 0:
        raise ValueError("No position samples were downloaded.")


def build_manifest_stem(
    session_id: str,
    driver_filter: set[str] | None,
    limit_laps: int | None,
) -> str:
    """Build a manifest file stem without changing the canonical session id."""

    if driver_filter is None and limit_laps is None:
        return session_id

    parts = [session_id, "subset"]
    if driver_filter is not None:
        parts.append("-".join(sorted(driver_filter)).lower())
    if limit_laps is not None:
        parts.append(f"first-{limit_laps}-laps")
    return "-".join(parts)


def write_manifest(summary: SessionDownloadSummary, manifest_dir: Path, manifest_stem: str | None = None) -> Path:
    manifest_dir.mkdir(parents=True, exist_ok=True)
    manifest_path = manifest_dir / f"{manifest_stem or summary.session_id}.json"
    payload = asdict(summary)
    payload["totals"] = {
        "drivers": summary.driver_count,
        "laps": summary.lap_count,
        "telemetry_samples": summary.telemetry_sample_count,
        "position_samples": summary.position_sample_count,
    }
    manifest_path.write_text(json.dumps(payload, indent=2) + "\n", encoding="utf-8")
    return manifest_path


def format_elapsed(seconds: float) -> str:
    return str(timedelta(seconds=round(seconds)))


def print_summary(summary: SessionDownloadSummary, manifest_path: Path) -> None:
    print("Download completed successfully.")
    print(f"Session: {summary.year} {summary.event} {summary.session}")
    print(f"Session ID: {summary.session_id}")
    print(f"Drivers: {summary.driver_count}")
    print(f"Laps: {summary.lap_count}")
    print(f"Telemetry samples: {summary.telemetry_sample_count:,}")
    print(f"Position samples: {summary.position_sample_count:,}")
    print(f"Cache: {summary.cache_dir}")
    print(f"Manifest: {manifest_path}")
    print(f"Elapsed: {format_elapsed(summary.elapsed_seconds)}")


def download_session(args: argparse.Namespace) -> tuple[SessionDownloadSummary, Path]:
    start = time.perf_counter()
    manifest_dir = args.manifest_dir.resolve()

    fastf1 = load_fastf1()
    cache_dir = prepare_fastf1_cache(fastf1, args.cache_dir)

    logging.info("Resolving %s %s %s", args.year, args.event, args.session)
    session = fastf1.get_session(args.year, args.event, args.session)

    logging.info("Downloading session data into %s", cache_dir)
    session.load(laps=True, telemetry=True, weather=False, messages=False)

    driver_codes = select_driver_codes(session, args.driver_filter)
    logging.info("Validating %d driver(s): %s", len(driver_codes), ", ".join(driver_codes))

    driver_summaries = [
        summarize_driver(session, driver_code, args.limit_laps) for driver_code in driver_codes
    ]

    event = getattr(session, "event", None)
    official_event_name = get_event_value(event, "EventName") if event is not None else None
    circuit_name = get_event_value(event, "Location") if event is not None else None
    country = get_event_value(event, "Country") if event is not None else None
    event_for_id = official_event_name or args.event

    summary = SessionDownloadSummary(
        session_id=build_session_id(args.year, event_for_id, args.session),
        year=args.year,
        event=args.event,
        official_event_name=official_event_name,
        circuit_name=circuit_name,
        country=country,
        session=args.session,
        downloaded_at_utc=datetime.now(UTC).isoformat(timespec="seconds"),
        cache_dir=str(cache_dir),
        drivers=driver_summaries,
        elapsed_seconds=time.perf_counter() - start,
    )
    validate_summary(summary)
    manifest_stem = build_manifest_stem(summary.session_id, args.driver_filter, args.limit_laps)
    manifest_path = write_manifest(summary, manifest_dir, manifest_stem)
    return summary, manifest_path


def positive_int(value: str) -> int:
    parsed = int(value)
    if parsed <= 0:
        raise argparse.ArgumentTypeError("value must be greater than zero")
    return parsed


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        description="Download and validate a Formula 1 session through FastF1."
    )
    parser.add_argument("--year", type=int, required=True, help="Championship year, for example 2024.")
    parser.add_argument(
        "--event",
        required=True,
        help='Event, circuit, or Grand Prix name accepted by FastF1, for example "Monza".',
    )
    parser.add_argument(
        "--session",
        default="R",
        type=str.upper,
        choices=sorted(VALID_SESSIONS),
        help="Session identifier: FP1, FP2, FP3, Q, SQ, S, or R. Default: R.",
    )
    parser.add_argument(
        "--drivers",
        dest="driver_filter",
        type=parse_driver_filter,
        help="Optional comma-separated driver-code subset, for example VER,HAM,LEC.",
    )
    parser.add_argument(
        "--limit-laps",
        type=positive_int,
        help="Developer shortcut that validates only the first N laps per selected driver.",
    )
    parser.add_argument(
        "--cache-dir",
        type=Path,
        default=DEFAULT_CACHE_DIR,
        help=f"FastF1 cache directory. Default: {DEFAULT_CACHE_DIR}",
    )
    parser.add_argument(
        "--manifest-dir",
        type=Path,
        default=DEFAULT_MANIFEST_DIR,
        help=f"Directory for JSON download manifests. Default: {DEFAULT_MANIFEST_DIR}",
    )
    parser.add_argument(
        "--log-level",
        default="INFO",
        choices=["DEBUG", "INFO", "WARNING", "ERROR"],
        help="Logging verbosity. Default: INFO.",
    )
    return parser


def main(argv: list[str] | None = None) -> int:
    parser = build_parser()
    args = parser.parse_args(argv)
    configure_logging(args.log_level)

    try:
        summary, manifest_path = download_session(args)
    except Exception as exc:
        logging.error("%s", exc)
        return 1

    print_summary(summary, manifest_path)
    return 0


if __name__ == "__main__":
    sys.exit(main())
