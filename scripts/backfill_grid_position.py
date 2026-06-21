#!/usr/bin/env python3
"""Backfill session_drivers.grid_position from FastF1 results for already-imported races.

Reads each race session from the DB, loads its FastF1 results (from the local cache — no
telemetry re-import), and UPDATEs grid_position per driver. Run after migration 010.

    python scripts/backfill_grid_position.py
"""
from __future__ import annotations

import math
import os
import sys
from pathlib import Path

DEFAULT_DATABASE_URL = "postgresql://race_telemetry:race_telemetry@localhost:5432/race_telemetry"
CACHE_DIR = Path("data/fastf1-cache")


def grid_or_none(value) -> int | None:
    if value is None:
        return None
    try:
        f = float(value)
    except (TypeError, ValueError):
        return None
    if math.isnan(f) or f < 0:
        return None
    return int(f)


def main() -> int:
    import psycopg
    import fastf1

    CACHE_DIR.mkdir(parents=True, exist_ok=True)
    fastf1.Cache.enable_cache(str(CACHE_DIR.resolve()))

    url = os.environ.get("RACE_TELEMETRY_DATABASE_URL", DEFAULT_DATABASE_URL)
    with psycopg.connect(url) as conn, conn.cursor() as cur:
        cur.execute(
            "SELECT session_id, year, event_name FROM sessions WHERE session_type = 'R' ORDER BY session_id"
        )
        sessions = cur.fetchall()

        total_updated = 0
        for session_id, year, event_name in sessions:
            try:
                session = fastf1.get_session(year, event_name, "R")
                session.load(laps=False, telemetry=False, weather=False, messages=False)
                results = session.results
            except Exception as exc:  # FastF1 resolution/load can fail per event
                print(f"  ! {session_id}: could not load results ({exc})")
                continue

            if results is None or len(results) == 0:
                print(f"  - {session_id}: no results")
                continue

            updated = 0
            for _, row in results.iterrows():
                code = str(row.get("Abbreviation") or "").upper()
                grid = grid_or_none(row.get("GridPosition"))
                if not code or grid is None:
                    continue
                cur.execute(
                    "UPDATE session_drivers SET grid_position = %s WHERE session_id = %s AND driver_code = %s",
                    (grid, session_id, code),
                )
                updated += cur.rowcount
            conn.commit()
            total_updated += updated
            print(f"  ✓ {session_id}: {updated} drivers")

        print(f"Done. Updated grid_position for {total_updated} driver rows across {len(sessions)} races.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
