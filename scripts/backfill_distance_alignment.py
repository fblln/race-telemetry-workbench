#!/usr/bin/env python3
"""Backfill distance-domain telemetry projections from existing raw imports."""

from __future__ import annotations

import argparse
import logging
import sys
import time
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Sequence

REPO_ROOT = Path(__file__).resolve().parents[1]
if str(REPO_ROOT) not in sys.path:
    sys.path.insert(0, str(REPO_ROOT))

from scripts.download_session import VALID_SESSIONS, configure_logging
from scripts.import_session import (
    DEFAULT_BATCH_SIZE,
    CopyWriter,
    LAP_TELEMETRY_DISTANCE_COLUMNS,
    LAP_TELEMETRY_QUALITY_COLUMNS,
    POSITION_COLUMNS,
    TELEMETRY_COLUMNS,
    database_url,
    materialize_lap_telemetry_by_distance,
    require_psycopg,
    stable_session_key,
)


@dataclass(frozen=True)
class BackfillSummary:
    session_id: str
    distance_rows: int
    quality_rows: int
    elapsed_seconds: float


def fetch_all(cursor: Any, sql: str, params: Sequence[Any] | None = None) -> list[tuple[Any, ...]]:
    cursor.execute(sql, params or ())
    return list(cursor.fetchall())


def select_session_ids(connection: Any, args: argparse.Namespace) -> list[str]:
    clauses: list[str] = []
    params: list[Any] = []

    if args.session_ids:
        clauses.append("session_id = ANY(%s)")
        params.append(args.session_ids)
    else:
        if args.year is not None:
            clauses.append("year = %s")
            params.append(args.year)
        if args.session_type is not None:
            clauses.append("session_type = %s")
            params.append(args.session_type)

    where_sql = f"WHERE {' AND '.join(clauses)}" if clauses else ""
    limit_sql = "LIMIT %s" if args.limit_sessions is not None else ""
    if args.limit_sessions is not None:
        params.append(args.limit_sessions)

    sql = f"""
        SELECT session_id
        FROM sessions
        {where_sql}
        ORDER BY year DESC, event_name, session_type
        {limit_sql}
    """

    with connection.cursor() as cursor:
        rows = fetch_all(cursor, sql, params)
    session_ids = [str(row[0]) for row in rows]

    if not args.only_missing:
        return session_ids

    with connection.cursor() as cursor:
        existing_rows = fetch_all(
            cursor,
            """
            SELECT DISTINCT session_id
            FROM lap_telemetry_quality
            WHERE session_id = ANY(%s)
            """,
            (session_ids,),
        )

    existing = {str(row[0]) for row in existing_rows}
    return [session_id for session_id in session_ids if session_id not in existing]


def load_driver_rows(connection: Any, session_id: str) -> list[tuple[Any, ...]]:
    sql = """
        SELECT session_id, driver_code, driver_number, full_name, team_name, metadata
        FROM session_drivers
        WHERE session_id = %s
        ORDER BY driver_code
    """
    with connection.cursor() as cursor:
        return fetch_all(cursor, sql, (session_id,))


def load_lap_rows(connection: Any, session_id: str) -> list[tuple[Any, ...]]:
    sql = """
        SELECT
            lap_id,
            session_id,
            driver_code,
            lap_number,
            stint_number,
            lap_start_utc,
            lap_end_utc,
            lap_time_ms,
            sector_1_ms,
            sector_2_ms,
            sector_3_ms,
            compound,
            tyre_life,
            is_pit_out_lap,
            is_pit_in_lap,
            is_deleted,
            is_accurate,
            metadata
        FROM laps
        WHERE session_id = %s
        ORDER BY driver_code, lap_number
    """
    with connection.cursor() as cursor:
        return fetch_all(cursor, sql, (session_id,))


def load_sample_rows(
    connection: Any,
    session_id: str,
    table_name: str,
    columns: Sequence[str],
    order_by: str,
) -> list[tuple[Any, ...]]:
    column_sql = ", ".join(columns)
    sql = f"""
        SELECT {column_sql}
        FROM {table_name}
        WHERE session_id = %s
        ORDER BY {order_by}
    """
    with connection.cursor() as cursor:
        return fetch_all(cursor, sql, (session_id,))


def replace_session_distance_rows(
    connection: Any,
    session_id: str,
    distance_rows: Sequence[tuple[Any, ...]],
    quality_rows: Sequence[tuple[Any, ...]],
    batch_size: int,
) -> tuple[int, int]:
    with connection.transaction():
        with connection.cursor() as cursor:
            cursor.execute("DELETE FROM lap_telemetry_quality WHERE session_id = %s", (session_id,))
            cursor.execute("DELETE FROM lap_telemetry_by_distance WHERE session_id = %s", (session_id,))

        distance_writer = CopyWriter(
            connection,
            "lap_telemetry_by_distance",
            LAP_TELEMETRY_DISTANCE_COLUMNS,
            batch_size,
            (0, 2, 4, 5),
        )
        quality_writer = CopyWriter(
            connection,
            "lap_telemetry_quality",
            LAP_TELEMETRY_QUALITY_COLUMNS,
            batch_size,
            (0, 1, 2),
        )

        distance_writer.add_many(distance_rows)
        quality_writer.add_many(quality_rows)
        distance_writer.flush()
        quality_writer.flush()

    return distance_writer.total, quality_writer.total


def backfill_session(connection: Any, session_id: str, args: argparse.Namespace) -> BackfillSummary:
    start = time.perf_counter()

    driver_rows = load_driver_rows(connection, session_id)
    lap_rows = load_lap_rows(connection, session_id)
    telemetry_rows = load_sample_rows(
        connection,
        session_id,
        "telemetry_samples",
        TELEMETRY_COLUMNS,
        "driver_code, lap_number, lap_time_ms NULLS LAST, sample_time_utc",
    )
    position_rows = load_sample_rows(
        connection,
        session_id,
        "position_samples",
        POSITION_COLUMNS,
        "driver_code, lap_number, sample_time_utc",
    )

    if not driver_rows or not lap_rows:
        raise RuntimeError(f"Session {session_id} is missing required parent rows.")

    distance_rows, quality_rows = materialize_lap_telemetry_by_distance(
        session_id,
        stable_session_key(session_id),
        driver_rows,
        lap_rows,
        telemetry_rows,
        position_rows,
        distance_step_m=args.distance_alignment_step_m,
        max_car_data_interpolation_gap_ms=args.max_car_data_interpolation_gap_ms,
        max_position_interpolation_gap_ms=args.max_position_interpolation_gap_ms,
        max_source_age_ms=args.max_source_age_ms,
        validation_tolerance_ms=args.validation_tolerance_ms,
    )

    written_distance_rows, written_quality_rows = replace_session_distance_rows(
        connection,
        session_id,
        distance_rows,
        quality_rows,
        args.batch_size,
    )

    return BackfillSummary(
        session_id=session_id,
        distance_rows=written_distance_rows,
        quality_rows=written_quality_rows,
        elapsed_seconds=time.perf_counter() - start,
    )


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        description="Backfill lap_telemetry_by_distance and lap_telemetry_quality from imported raw tables."
    )
    parser.add_argument(
        "--session-id",
        dest="session_ids",
        action="append",
        help="Exact session id to backfill. Repeatable. If omitted, filters are used.",
    )
    parser.add_argument("--year", type=int, help="Optional year filter when selecting sessions from the database.")
    parser.add_argument(
        "--session-type",
        default="R",
        type=str.upper,
        choices=sorted(VALID_SESSIONS),
        help="Optional session type filter. Default: R.",
    )
    parser.add_argument(
        "--limit-sessions",
        type=int,
        help="Optional cap on selected sessions after filtering.",
    )
    parser.add_argument(
        "--only-missing",
        action="store_true",
        help="Process only sessions that do not already have lap_telemetry_quality rows.",
    )
    parser.add_argument("--database-url", default=database_url())
    parser.add_argument("--batch-size", type=int, default=DEFAULT_BATCH_SIZE)
    parser.add_argument(
        "--distance-alignment-step-m",
        type=float,
        default=5.0,
        help="Distance step in metres for lap_telemetry_by_distance. Default: 5.0.",
    )
    parser.add_argument(
        "--max-car-data-interpolation-gap-ms",
        type=int,
        default=1000,
        help="Maximum raw car-telemetry interpolation gap in milliseconds. Default: 1000.",
    )
    parser.add_argument(
        "--max-position-interpolation-gap-ms",
        type=int,
        default=1000,
        help="Maximum raw position interpolation gap in milliseconds. Default: 1000.",
    )
    parser.add_argument(
        "--max-source-age-ms",
        type=int,
        default=750,
        help="Maximum acceptable source-sample age in milliseconds. Default: 750.",
    )
    parser.add_argument(
        "--validation-tolerance-ms",
        type=int,
        default=100,
        help="Allowed finish-delta validation tolerance in milliseconds. Default: 100.",
    )
    parser.add_argument(
        "--log-level",
        default="INFO",
        choices=["DEBUG", "INFO", "WARNING", "ERROR"],
    )
    return parser


def main(argv: list[str] | None = None) -> int:
    parser = build_parser()
    args = parser.parse_args(argv)
    configure_logging(args.log_level)

    psycopg = require_psycopg()
    try:
        with psycopg.connect(args.database_url, autocommit=False) as connection:
            session_ids = select_session_ids(connection, args)
            if not session_ids:
                logging.error("No sessions matched the requested filters.")
                return 1

            logging.info("Backfilling distance alignment for %d session(s).", len(session_ids))
            summaries: list[BackfillSummary] = []
            for index, session_id in enumerate(session_ids, start=1):
                try:
                    summary = backfill_session(connection, session_id, args)
                except Exception:
                    connection.rollback()
                    raise

                connection.commit()
                summaries.append(summary)
                logging.info(
                    "[%d/%d] %s distance_rows=%s lap_quality_rows=%s elapsed=%.2fs",
                    index,
                    len(session_ids),
                    session_id,
                    f"{summary.distance_rows:,}",
                    f"{summary.quality_rows:,}",
                    summary.elapsed_seconds,
                )

    except Exception as exc:
        logging.error("%s", exc)
        return 1

    total_distance_rows = sum(summary.distance_rows for summary in summaries)
    total_quality_rows = sum(summary.quality_rows for summary in summaries)
    total_elapsed_seconds = sum(summary.elapsed_seconds for summary in summaries)

    print("Distance-alignment backfill completed successfully.")
    print(f"Sessions: {len(summaries)}")
    print(f"Distance-aligned rows: {total_distance_rows:,}")
    print(f"Lap quality rows: {total_quality_rows:,}")
    print(f"Elapsed worker seconds: {total_elapsed_seconds:.2f}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
