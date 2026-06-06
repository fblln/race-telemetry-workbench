#!/usr/bin/env python3
"""Estimate storage needs from downloaded FastF1 cache and manifests."""

from __future__ import annotations

import argparse
import json
from dataclasses import dataclass
from pathlib import Path


DEFAULT_CACHE_DIR = Path("data/fastf1-cache")
DEFAULT_MANIFEST_DIR = Path("data/download-manifests")


@dataclass(frozen=True)
class StorageEstimate:
    cache_bytes: int
    manifest_count: int
    average_bytes_per_session: float
    race_only_season_bytes: float
    typical_weekend_season_bytes: float


def directory_size_bytes(path: Path) -> int:
    if not path.exists():
        return 0

    total = 0
    for child in path.rglob("*"):
        if child.is_file():
            total += child.stat().st_size
    return total


def canonical_manifest_paths(manifest_dir: Path) -> list[Path]:
    return [
        path
        for path in sorted(manifest_dir.glob("*.json"))
        if path.is_file() and "-subset-" not in path.stem
    ]


def manifest_count(manifest_dir: Path) -> int:
    return len(canonical_manifest_paths(manifest_dir))


def load_manifest_totals(manifest_dir: Path) -> list[dict[str, int]]:
    totals: list[dict[str, int]] = []
    for path in canonical_manifest_paths(manifest_dir):
        data = json.loads(path.read_text(encoding="utf-8"))
        totals.append(data.get("totals", {}))
    return totals


def estimate_storage(
    cache_bytes: int,
    downloaded_sessions: int,
    events_per_year: int,
    sessions_per_event: int,
) -> StorageEstimate:
    if downloaded_sessions <= 0:
        average = 0.0
    else:
        average = cache_bytes / downloaded_sessions

    return StorageEstimate(
        cache_bytes=cache_bytes,
        manifest_count=downloaded_sessions,
        average_bytes_per_session=average,
        race_only_season_bytes=average * events_per_year,
        typical_weekend_season_bytes=average * events_per_year * sessions_per_event,
    )


def format_bytes(value: float) -> str:
    units = ["B", "KB", "MB", "GB", "TB"]
    size = float(value)
    unit = units[0]
    for unit in units:
        if abs(size) < 1024.0 or unit == units[-1]:
            break
        size /= 1024.0
    return f"{size:.1f} {unit}"


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        description="Estimate raw FastF1 cache storage from downloaded session manifests."
    )
    parser.add_argument("--cache-dir", type=Path, default=DEFAULT_CACHE_DIR)
    parser.add_argument("--manifest-dir", type=Path, default=DEFAULT_MANIFEST_DIR)
    parser.add_argument("--events-per-year", type=int, default=24)
    parser.add_argument(
        "--sessions-per-event",
        type=int,
        default=1,
        help="Planning assumption. Default: 1 race session per event.",
    )
    return parser


def main() -> int:
    args = build_parser().parse_args()
    cache_bytes = directory_size_bytes(args.cache_dir)
    downloaded_sessions = manifest_count(args.manifest_dir)
    estimate = estimate_storage(
        cache_bytes=cache_bytes,
        downloaded_sessions=downloaded_sessions,
        events_per_year=args.events_per_year,
        sessions_per_event=args.sessions_per_event,
    )
    totals = load_manifest_totals(args.manifest_dir)

    print("Storage estimate")
    print(f"Cache directory: {args.cache_dir.resolve()}")
    print(f"Observed cache size: {format_bytes(estimate.cache_bytes)}")
    print(f"Observed session manifests: {estimate.manifest_count}")
    print(f"Average raw cache per session: {format_bytes(estimate.average_bytes_per_session)}")
    print(f"Default race-only season estimate: {format_bytes(estimate.race_only_season_bytes)}")
    print(
        "Configured season estimate "
        f"({args.sessions_per_event} session(s) per event): "
        f"{format_bytes(estimate.typical_weekend_season_bytes)}"
    )

    if totals:
        telemetry_samples = sum(total.get("telemetry_samples", 0) for total in totals)
        position_samples = sum(total.get("position_samples", 0) for total in totals)
        print(f"Observed telemetry samples: {telemetry_samples:,}")
        print(f"Observed position samples: {position_samples:,}")

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
