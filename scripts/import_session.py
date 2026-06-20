#!/usr/bin/env python3
"""Import a cached FastF1 session into TimescaleDB.

The importer is intentionally cache-friendly: it enables the project FastF1
cache before resolving/loading the session, extracts raw FastF1 car and
position streams concurrently by driver, and streams high-volume sample rows
into PostgreSQL with COPY by default. Use --drivers and --limit-laps for fast
smoke tests before importing full race sessions.
"""

from __future__ import annotations

import argparse
import logging
import os
import sys
import time
import zlib
from concurrent.futures import ThreadPoolExecutor, as_completed
from datetime import datetime, timedelta
from pathlib import Path
from typing import Any, Iterable, Sequence

REPO_ROOT = Path(__file__).resolve().parents[1]
if str(REPO_ROOT) not in sys.path:
    sys.path.insert(0, str(REPO_ROOT))

from scripts.download_session import (
    DEFAULT_CACHE_DIR,
    VALID_SESSIONS,
    build_session_id,
    configure_logging,
    get_driver_code,
    get_event_value,
    load_fastf1,
    parse_driver_filter,
    positive_int,
    prepare_fastf1_cache,
    select_driver_codes,
)

from scripts.import_helpers import (
    DriverLapWindow,
    DriverSampleRows,
    EMPTY_METADATA,
    ImportSummary,
    bool_or_none,
    brake_to_pct,
    clean_value,
    column_values,
    float_or_none,
    int_or_none,
    json_metadata,
    percentage_or_none,
    str_or_none,
    timedelta_to_ms,
    timestamp_or_none,
)
from scripts.import_writers import BatchWriter, CopyWriter, execute_many, sample_writer

DEFAULT_DATABASE_URL = "postgresql://race_telemetry:race_telemetry@localhost:5432/race_telemetry"
DEFAULT_BATCH_SIZE = 100000
DEFAULT_TELEMETRY_WORKERS = 1
SAMPLE_WRITE_METHODS = {"copy", "insert"}

TELEMETRY_COLUMNS = (
    "sample_time_utc",
    "session_id",
    "driver_code",
    "lap_number",
    "session_time_ms",
    "lap_time_ms",
    "speed_kmh",
    "throttle_pct",
    "brake_pct",
    "gear",
    "rpm",
    "drs",
    "sample_source",
    "source_sample_index",
    "metadata",
)
POSITION_COLUMNS = (
    "sample_time_utc",
    "session_id",
    "driver_code",
    "lap_number",
    "x",
    "y",
    "z",
    "track_status",
    "sample_source",
    "source_sample_index",
    "metadata",
)
ALIGNED_TELEMETRY_COLUMNS = (
    "sample_time_utc",
    "session_id",
    "session_key",
    "driver_number",
    "driver_code",
    "lap_number",
    "sample_index",
    "session_time_ms",
    "lap_time_ms",
    "speed",
    "rpm",
    "n_gear",
    "throttle",
    "brake",
    "drs",
    "x",
    "y",
    "z",
    "location_status",
    "source_car_time",
    "source_location_time",
    "car_sample_age_ms",
    "location_sample_age_ms",
    "is_interpolated_car",
    "is_interpolated_location",
    "quality_flags",
    "alignment_version",
    "alignment_method",
)
LAP_TELEMETRY_DISTANCE_COLUMNS = (
    "session_id",
    "session_key",
    "driver_number",
    "driver_code",
    "lap_number",
    "distance_m",
    "normalized_track_progress",
    "lap_elapsed_time_ms",
    "session_time_ms",
    "speed_kmh",
    "throttle_pct",
    "brake_pct",
    "gear",
    "rpm",
    "drs",
    "x",
    "y",
    "z",
    "source_sample_before_time_utc",
    "source_sample_after_time_utc",
    "interpolated",
    "quality_flags",
    "alignment_version",
)
LAP_TELEMETRY_QUALITY_COLUMNS = (
    "session_id",
    "driver_number",
    "lap_number",
    "official_lap_duration_ms",
    "telemetry_covered_duration_ms",
    "first_sample_offset_ms",
    "last_sample_offset_ms",
    "maximum_car_data_gap_ms",
    "maximum_position_gap_ms",
    "final_integrated_distance_m",
    "interpolated_car_data_percentage",
    "interpolated_position_percentage",
    "stale_sample_percentage",
    "distance_delta_validation_ms",
    "quality_status",
    "quality_messages",
)
TELEMETRY_DIAGNOSTIC_COLUMNS = (
    "session_id",
    "session_key",
    "driver_number",
    "driver_code",
    "stream_name",
    "sample_count",
    "start_time",
    "end_time",
    "min_delta_ms",
    "median_delta_ms",
    "p90_delta_ms",
    "p99_delta_ms",
    "max_delta_ms",
    "estimated_frequency_hz",
    "duplicate_count",
    "out_of_order_count",
    "warning_flags",
)

TELEMETRY_INSERT_SQL = """
INSERT INTO telemetry_samples (
    sample_time_utc, session_id, driver_code, lap_number, session_time_ms,
    lap_time_ms, speed_kmh, throttle_pct, brake_pct, gear, rpm, drs,
    sample_source, source_sample_index, metadata
)
VALUES (%s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s)
ON CONFLICT (sample_time_utc, session_id, driver_code) DO NOTHING
"""

POSITION_INSERT_SQL = """
INSERT INTO position_samples (
    sample_time_utc, session_id, driver_code, lap_number, x, y, z,
    track_status, sample_source, source_sample_index, metadata
)
VALUES (%s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s)
ON CONFLICT (sample_time_utc, session_id, driver_code) DO NOTHING
"""

ALIGNED_TELEMETRY_INSERT_SQL = """
INSERT INTO aligned_telemetry_10hz (
    sample_time_utc, session_id, session_key, driver_number, driver_code,
    lap_number, sample_index, session_time_ms, lap_time_ms,
    speed, rpm, n_gear, throttle, brake, drs,
    x, y, z, location_status,
    source_car_time, source_location_time,
    car_sample_age_ms, location_sample_age_ms,
    is_interpolated_car, is_interpolated_location,
    quality_flags, alignment_version, alignment_method
)
VALUES (%s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s)
ON CONFLICT (sample_time_utc, session_key, driver_number) DO NOTHING
"""

LAP_TELEMETRY_DISTANCE_INSERT_SQL = """
INSERT INTO lap_telemetry_by_distance (
    session_id, session_key, driver_number, driver_code, lap_number,
    distance_m, normalized_track_progress,
    lap_elapsed_time_ms, session_time_ms,
    speed_kmh, throttle_pct, brake_pct, gear, rpm, drs,
    x, y, z,
    source_sample_before_time_utc, source_sample_after_time_utc,
    interpolated, quality_flags, alignment_version
)
VALUES (%s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s)
ON CONFLICT (session_id, driver_number, lap_number, distance_m) DO NOTHING
"""

LAP_TELEMETRY_QUALITY_INSERT_SQL = """
INSERT INTO lap_telemetry_quality (
    session_id, driver_number, lap_number,
    official_lap_duration_ms, telemetry_covered_duration_ms,
    first_sample_offset_ms, last_sample_offset_ms,
    maximum_car_data_gap_ms, maximum_position_gap_ms,
    final_integrated_distance_m,
    interpolated_car_data_percentage, interpolated_position_percentage,
    stale_sample_percentage, distance_delta_validation_ms,
    quality_status, quality_messages
)
VALUES (%s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s)
ON CONFLICT (session_id, driver_number, lap_number) DO UPDATE SET
    official_lap_duration_ms = EXCLUDED.official_lap_duration_ms,
    telemetry_covered_duration_ms = EXCLUDED.telemetry_covered_duration_ms,
    first_sample_offset_ms = EXCLUDED.first_sample_offset_ms,
    last_sample_offset_ms = EXCLUDED.last_sample_offset_ms,
    maximum_car_data_gap_ms = EXCLUDED.maximum_car_data_gap_ms,
    maximum_position_gap_ms = EXCLUDED.maximum_position_gap_ms,
    final_integrated_distance_m = EXCLUDED.final_integrated_distance_m,
    interpolated_car_data_percentage = EXCLUDED.interpolated_car_data_percentage,
    interpolated_position_percentage = EXCLUDED.interpolated_position_percentage,
    stale_sample_percentage = EXCLUDED.stale_sample_percentage,
    distance_delta_validation_ms = EXCLUDED.distance_delta_validation_ms,
    quality_status = EXCLUDED.quality_status,
    quality_messages = EXCLUDED.quality_messages
"""

TELEMETRY_DIAGNOSTIC_INSERT_SQL = """
INSERT INTO telemetry_ingestion_diagnostics (
    session_id, session_key, driver_number, driver_code, stream_name,
    sample_count, start_time, end_time,
    min_delta_ms, median_delta_ms, p90_delta_ms, p99_delta_ms, max_delta_ms,
    estimated_frequency_hz, duplicate_count, out_of_order_count, warning_flags
)
VALUES (%s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s)
"""


def database_url() -> str:
    return os.environ.get("RACE_TELEMETRY_DATABASE_URL", DEFAULT_DATABASE_URL)


def require_psycopg():
    try:
        import psycopg
    except ModuleNotFoundError as exc:
        raise RuntimeError(
            "psycopg is not installed. Run `.venv/bin/python -m pip install -r scripts/requirements.txt`."
        ) from exc
    return psycopg


def delete_existing_session(connection: Any, session_id: str) -> None:
    with connection.cursor() as cursor:
        cursor.execute("DELETE FROM sessions WHERE session_id = %s", (session_id,))


def ensure_import_mode(connection: Any, session_id: str, mode: str) -> None:
    with connection.cursor() as cursor:
        cursor.execute("SELECT 1 FROM sessions WHERE session_id = %s", (session_id,))
        exists = cursor.fetchone() is not None

    if exists and mode == "fail":
        raise RuntimeError(f"Session {session_id} already exists. Use --mode replace or --mode upsert.")
    if exists and mode == "replace":
        delete_existing_session(connection, session_id)


def clear_upsert_children(connection: Any, session_id: str) -> None:
    tables = [
        "lap_telemetry_quality",
        "lap_telemetry_by_distance",
        "telemetry_ingestion_diagnostics",
        "aligned_telemetry_10hz",
        "telemetry_samples",
        "position_samples",
        "circuit_markers",
        "weather_samples",
        "track_status_events",
        "session_status_events",
        "race_control_messages",
    ]
    with connection.cursor() as cursor:
        for table in tables:
            cursor.execute(f"DELETE FROM {table} WHERE session_id = %s", (session_id,))


def load_session(fastf1: Any, args: argparse.Namespace) -> Any:
    prepare_fastf1_cache(fastf1, args.cache_dir)
    session = fastf1.get_session(args.year, args.event, args.session)
    session.load(laps=True, telemetry=True, weather=True, messages=True)
    return session


def session_start(session: Any) -> datetime | None:
    resolved_start = timestamp_or_none(getattr(session, "date", None))
    if resolved_start is not None:
        return resolved_start

    event = getattr(session, "event", None)
    for key in ("Session1DateUtc", "Session2DateUtc", "Session3DateUtc", "Session4DateUtc", "Session5DateUtc"):
        value = get_event_value(event, key) if event is not None else None
        parsed = timestamp_or_none(value)
        if parsed is not None:
            return parsed

    laps = getattr(session, "laps", None)
    if laps is not None and "LapStartDate" in laps:
        first = timestamp_or_none(laps["LapStartDate"].dropna().min())
        if first is not None:
            return first
    return None


def absolute_from_session_time(start: datetime | None, session_time: Any) -> datetime | None:
    ms = timedelta_to_ms(session_time)
    if start is None or ms is None:
        return None
    return start + timedelta(milliseconds=ms)


def race_control_time_fields(start: datetime | None, value: Any) -> tuple[datetime | None, int | None]:
    message_time = timestamp_or_none(value)
    if message_time is None:
        session_time_ms = timedelta_to_ms(value)
        return absolute_from_session_time(start, value), session_time_ms

    if start is None:
        return message_time, None

    session_time_ms = int(round((message_time - start).total_seconds() * 1000))
    return message_time, session_time_ms if session_time_ms >= 0 else None


def stable_session_key(session_id: str) -> int:
    """Project-local integer session key for UI-aligned telemetry materialization."""

    return zlib.crc32(session_id.encode("utf-8")) & 0x7FFFFFFF


def selected_laps(session: Any, driver_codes: Sequence[str], limit_laps: int | None) -> Any:
    laps = session.laps.pick_drivers(list(driver_codes))
    if limit_laps is not None:
        return laps.groupby("Driver", group_keys=False).head(limit_laps)
    return laps


def build_driver_refs(session: Any, driver_codes: Sequence[str]) -> list[tuple[str, str]]:
    rows = []
    selected = set(driver_codes)
    for driver_ref in getattr(session, "drivers", []):
        driver_code = get_driver_code(session, driver_ref)
        if driver_code in selected:
            rows.append((driver_code, str(driver_ref)))
    return rows


def build_driver_lap_windows(laps: Any) -> dict[str, list[DriverLapWindow]]:
    """Map each selected driver to sorted lap session-time windows."""

    windows: dict[str, list[DriverLapWindow]] = {}
    for _, lap in laps.sort_values(["Driver", "LapNumber"]).iterlaps():
        driver_code = str_or_none(lap.get("Driver"))
        lap_number = int_or_none(lap.get("LapNumber"))
        start_ms = timedelta_to_ms(lap.get("LapStartTime"))
        end_ms = timedelta_to_ms(lap.get("Time"))
        if driver_code is None or lap_number is None or start_ms is None or end_ms is None:
            continue
        windows.setdefault(driver_code, []).append(
            DriverLapWindow(
                lap_number=lap_number,
                start_ms=start_ms,
                end_ms=end_ms,
            )
        )
    return windows


def build_session_row(session: Any, args: argparse.Namespace, session_id: str) -> tuple[Any, ...]:
    event = getattr(session, "event", None)
    event_name = get_event_value(event, "EventName") if event is not None else None
    circuit_name = get_event_value(event, "Location") if event is not None else None
    country = get_event_value(event, "Country") if event is not None else None
    start = session_start(session)
    return (
        session_id,
        args.year,
        event_name or args.event,
        circuit_name,
        country,
        args.session,
        start,
        None,
        "fastf1",
        json_metadata(
            {
                "imported_by": "scripts/import_session.py",
                "session_key": stable_session_key(session_id),
                "session_key_source": "project_crc32_session_id",
            }
        ),
    )


def build_driver_rows(session: Any, session_id: str, driver_codes: Sequence[str]) -> list[tuple[Any, ...]]:
    rows = []
    for driver_ref in getattr(session, "drivers", []):
        driver_code = get_driver_code(session, driver_ref)
        if driver_code not in driver_codes:
            continue
        driver_info = session.get_driver(driver_ref)
        rows.append(
            (
                session_id,
                driver_code,
                int_or_none(driver_info.get("DriverNumber")),
                str_or_none(driver_info.get("FullName")),
                str_or_none(driver_info.get("TeamName")),
                json_metadata({"driver_ref": str(driver_ref)}),
            )
        )
    return rows


def build_lap_rows(laps: Any, session_id: str) -> list[tuple[Any, ...]]:
    rows = []
    for _, lap in laps.iterlaps():
        driver_code = str_or_none(lap.get("Driver"))
        lap_number = int_or_none(lap.get("LapNumber"))
        if driver_code is None or lap_number is None:
            continue
        lap_start = timestamp_or_none(lap.get("LapStartDate"))
        lap_time_ms = timedelta_to_ms(lap.get("LapTime"))
        lap_end = lap_start + timedelta(milliseconds=lap_time_ms) if lap_start and lap_time_ms else None
        rows.append(
            (
                f"{session_id}-{driver_code.lower()}-{lap_number}",
                session_id,
                driver_code,
                lap_number,
                int_or_none(lap.get("Stint")),
                lap_start,
                lap_end,
                lap_time_ms,
                timedelta_to_ms(lap.get("Sector1Time")),
                timedelta_to_ms(lap.get("Sector2Time")),
                timedelta_to_ms(lap.get("Sector3Time")),
                str_or_none(lap.get("Compound")),
                int_or_none(lap.get("TyreLife")),
                timedelta_to_ms(lap.get("PitOutTime")) is not None,
                timedelta_to_ms(lap.get("PitInTime")) is not None,
                timedelta_to_ms(lap.get("PitOutTime")),
                timedelta_to_ms(lap.get("PitInTime")),
                bool_or_none(lap.get("Deleted")) or False,
                bool_or_none(lap.get("IsAccurate")),
                json_metadata({"team": str_or_none(lap.get("Team"))}),
            )
        )
    return rows


def iter_lap_assignments(
    session_times: Sequence[Any],
    lap_windows: Sequence[DriverLapWindow],
) -> Iterable[tuple[int, int, int, int]]:
    """Assign a monotonic driver sample stream to lap windows in one linear pass."""

    if not lap_windows:
        return

    lap_index = 0
    lap_count = len(lap_windows)
    for sample_index, session_time in enumerate(session_times):
        session_time_ms = timedelta_to_ms(session_time)
        if session_time_ms is None:
            continue

        while lap_index < lap_count and session_time_ms > lap_windows[lap_index].end_ms:
            lap_index += 1
        if lap_index >= lap_count:
            break

        window = lap_windows[lap_index]
        if session_time_ms < window.start_ms:
            continue

        yield sample_index, window.lap_number, session_time_ms, session_time_ms - window.start_ms


def build_driver_telemetry_rows(
    telemetry: Any,
    session_id: str,
    driver_code: str,
    lap_windows: Sequence[DriverLapWindow],
) -> list[tuple[Any, ...]]:
    if telemetry is None or not lap_windows:
        return []

    dates = column_values(telemetry, "Date")
    session_times = column_values(telemetry, "SessionTime")
    speeds = column_values(telemetry, "Speed")
    throttles = column_values(telemetry, "Throttle")
    brakes = column_values(telemetry, "Brake")
    gears = column_values(telemetry, "nGear")
    rpms = column_values(telemetry, "RPM")
    drs_values = column_values(telemetry, "DRS")
    sources = column_values(telemetry, "Source")

    rows: list[tuple[Any, ...]] = []
    for sample_index, lap_number, session_time_ms, lap_time_ms in iter_lap_assignments(session_times, lap_windows):
        sample_time_utc = timestamp_or_none(dates[sample_index])
        if sample_time_utc is None:
            continue
        rows.append(
            (
                sample_time_utc,
                session_id,
                driver_code,
                lap_number,
                session_time_ms,
                lap_time_ms,
                float_or_none(speeds[sample_index]),
                percentage_or_none(throttles[sample_index]),
                brake_to_pct(brakes[sample_index]),
                int_or_none(gears[sample_index]),
                float_or_none(rpms[sample_index]),
                int_or_none(drs_values[sample_index]),
                str_or_none(sources[sample_index]),
                sample_index,
                EMPTY_METADATA,
            )
        )
    return rows


def build_driver_position_rows(
    position: Any,
    session_id: str,
    driver_code: str,
    lap_windows: Sequence[DriverLapWindow],
) -> list[tuple[Any, ...]]:
    if position is None or not lap_windows:
        return []

    dates = column_values(position, "Date")
    session_times = column_values(position, "SessionTime")
    xs = column_values(position, "X")
    ys = column_values(position, "Y")
    zs = column_values(position, "Z")
    statuses = column_values(position, "Status")
    sources = column_values(position, "Source")

    rows: list[tuple[Any, ...]] = []
    for sample_index, lap_number, _session_time_ms, _lap_time_ms in iter_lap_assignments(session_times, lap_windows):
        sample_time_utc = timestamp_or_none(dates[sample_index])
        if sample_time_utc is None:
            continue
        rows.append(
            (
                sample_time_utc,
                session_id,
                driver_code,
                lap_number,
                float_or_none(xs[sample_index]),
                float_or_none(ys[sample_index]),
                float_or_none(zs[sample_index]),
                str_or_none(statuses[sample_index]),
                str_or_none(sources[sample_index]),
                sample_index,
                EMPTY_METADATA,
            )
        )
    return rows


def extract_driver_samples(
    session: Any,
    session_id: str,
    driver_code: str,
    driver_ref: str,
    lap_windows: Sequence[DriverLapWindow],
    include_telemetry: bool,
    include_position: bool,
) -> DriverSampleRows:
    start = time.perf_counter()

    # FastF1 exposes raw streams per driver on the loaded session; using those
    # avoids thousands of repeated per-lap object calls for full-race imports.
    telemetry = None
    position = None
    if include_telemetry:
        try:
            telemetry = getattr(session, "car_data", {}).get(driver_ref)
        except Exception as exc:
            logging.warning("Skipping telemetry for %s: %s", driver_code, exc)
    if include_position:
        try:
            position = getattr(session, "pos_data", {}).get(driver_ref)
        except Exception as exc:
            logging.warning("Skipping position for %s: %s", driver_code, exc)

    telemetry_rows = build_driver_telemetry_rows(telemetry, session_id, driver_code, lap_windows)
    position_rows = build_driver_position_rows(position, session_id, driver_code, lap_windows)
    return DriverSampleRows(
        driver_code=driver_code,
        lap_count=len(lap_windows),
        telemetry_rows=telemetry_rows,
        position_rows=position_rows,
        elapsed_seconds=time.perf_counter() - start,
    )


def build_circuit_rows(session: Any, session_id: str) -> tuple[tuple[Any, ...] | None, list[tuple[Any, ...]]]:
    try:
        circuit_info = session.get_circuit_info()
    except Exception as exc:
        logging.warning("Skipping circuit info: %s", exc)
        return None, []

    metadata_row = (
        session_id,
        float_or_none(getattr(circuit_info, "rotation", None)),
        "fastf1",
        json_metadata(),
    )
    marker_rows: list[tuple[Any, ...]] = []
    for marker_type, attr in [
        ("corner", "corners"),
        ("marshal_light", "marshal_lights"),
        ("marshal_sector", "marshal_sectors"),
    ]:
        markers = getattr(circuit_info, attr, None)
        if markers is None:
            continue
        for marker in markers.itertuples(index=False):
            data = marker._asdict()
            marker_rows.append(
                (
                    session_id,
                    marker_type,
                    int_or_none(data.get("Number")),
                    str_or_none(data.get("Letter")),
                    float_or_none(data.get("X")),
                    float_or_none(data.get("Y")),
                    float_or_none(data.get("Angle")),
                    float_or_none(data.get("Distance")),
                    json_metadata(),
                )
            )
    return metadata_row, [row for row in marker_rows if row[4] is not None and row[5] is not None]


def build_weather_rows(session: Any, session_id: str, start: datetime | None) -> list[tuple[Any, ...]]:
    weather = getattr(session, "weather_data", None)
    if weather is None:
        return []
    rows = []
    for sample in weather.itertuples(index=False):
        data = sample._asdict()
        sample_time = absolute_from_session_time(start, data.get("Time"))
        session_time_ms = timedelta_to_ms(data.get("Time"))
        if sample_time is None or session_time_ms is None:
            continue
        rows.append(
            (
                session_id,
                sample_time,
                session_time_ms,
                float_or_none(data.get("AirTemp")),
                float_or_none(data.get("TrackTemp")),
                float_or_none(data.get("Humidity")),
                float_or_none(data.get("Pressure")),
                bool_or_none(data.get("Rainfall")),
                int_or_none(data.get("WindDirection")),
                float_or_none(data.get("WindSpeed")),
                json_metadata(),
            )
        )
    return rows


def build_track_status_rows(session: Any, session_id: str) -> list[tuple[Any, ...]]:
    track_status = getattr(session, "track_status", None)
    if track_status is None:
        return []
    rows = []
    for event in track_status.itertuples(index=False):
        data = event._asdict()
        event_time_ms = timedelta_to_ms(data.get("Time"))
        status = str_or_none(data.get("Status"))
        if event_time_ms is None or status is None:
            continue
        rows.append((session_id, event_time_ms, status, str_or_none(data.get("Message")), json_metadata()))
    return rows


def build_session_status_rows(session: Any, session_id: str) -> list[tuple[Any, ...]]:
    session_status = getattr(session, "session_status", None)
    if session_status is None:
        return []
    rows = []
    for event in session_status.itertuples(index=False):
        data = event._asdict()
        event_time_ms = timedelta_to_ms(data.get("Time"))
        status = str_or_none(data.get("Status"))
        if event_time_ms is None or status is None:
            continue
        rows.append((session_id, event_time_ms, status, json_metadata()))
    return rows


def build_race_control_rows(session: Any, session_id: str, start: datetime | None) -> list[tuple[Any, ...]]:
    messages = getattr(session, "race_control_messages", None)
    if messages is None:
        return []
    rows = []
    for message in messages.itertuples(index=False):
        data = message._asdict()
        message_time, session_time_ms = race_control_time_fields(start, data.get("Time"))
        lap_number = int_or_none(data.get("Lap"))
        if lap_number is not None and lap_number <= 0:
            lap_number = None
        rows.append(
            (
                session_id,
                message_time,
                session_time_ms,
                str_or_none(data.get("Category")),
                str_or_none(data.get("Message")) or "",
                str_or_none(data.get("Status")),
                str_or_none(data.get("Flag")),
                str_or_none(data.get("Scope")),
                str_or_none(data.get("Sector")),
                int_or_none(data.get("RacingNumber")),
                lap_number,
                json_metadata(),
            )
        )
    return [row for row in rows if row[4]]


def collect_sample_rows(
    args: argparse.Namespace,
    session: Any,
    laps: Any,
    session_id: str,
    driver_refs: Sequence[tuple[str, str]],
) -> tuple[list[tuple[Any, ...]], list[tuple[Any, ...]], float]:
    driver_windows = build_driver_lap_windows(laps)
    driver_items = [
        (driver_code, driver_ref, driver_windows.get(driver_code, []))
        for driver_code, driver_ref in driver_refs
        if driver_windows.get(driver_code)
    ]
    total_drivers = len(driver_items)
    total_laps = sum(len(lap_windows) for _, _, lap_windows in driver_items)

    if total_drivers == 0 or (not args.include_telemetry and not args.include_position):
        return [], [], 0.0

    logging.info(
        "Extracting samples for %d driver(s), %d lap(s), %d worker(s)",
        total_drivers,
        total_laps,
        args.telemetry_workers,
    )
    completed = 0
    extraction_seconds = 0.0
    telemetry_rows: list[tuple[Any, ...]] = []
    position_rows: list[tuple[Any, ...]] = []
    with ThreadPoolExecutor(max_workers=args.telemetry_workers) as executor:
        futures = [
            executor.submit(
                extract_driver_samples,
                session,
                session_id,
                driver_code,
                driver_ref,
                lap_windows,
                args.include_telemetry,
                args.include_position,
            )
            for driver_code, driver_ref, lap_windows in driver_items
        ]
        for future in as_completed(futures):
            result = future.result()
            completed += 1
            extraction_seconds += result.elapsed_seconds
            telemetry_rows.extend(result.telemetry_rows)
            position_rows.extend(result.position_rows)
            logging.info(
                "[%d/%d] %s laps=%d: telemetry=%s position=%s extracted in %.2fs",
                completed,
                total_drivers,
                result.driver_code,
                result.lap_count,
                f"{len(result.telemetry_rows):,}",
                f"{len(result.position_rows):,}",
                result.elapsed_seconds,
            )

    return telemetry_rows, position_rows, extraction_seconds


def build_lap_metadata_by_driver(
    lap_rows: Sequence[tuple[Any, ...]],
) -> dict[str, dict[int, dict[str, Any]]]:
    metadata: dict[str, dict[int, dict[str, Any]]] = {}
    for row in lap_rows:
        driver_code = str(row[2]).upper()
        lap_number = int(row[3])
        metadata.setdefault(driver_code, {})[lap_number] = {
            "lap_start_utc": row[5],
            "lap_end_utc": row[6],
            "official_lap_duration_ms": row[7],
            "is_pit_out_lap": bool(row[13]),
            "is_pit_in_lap": bool(row[14]),
            "is_deleted": bool(row[15]),
            "is_accurate": bool_or_none(row[16]),
        }
    return metadata


def quality_status_from_messages(messages: Sequence[str]) -> str:
    if any(message.startswith("INVALID_") for message in messages):
        return "invalid"
    if any(message.startswith("INCOMPLETE_") for message in messages):
        return "incomplete"
    if messages:
        return "valid_with_warnings"
    return "valid"


def bounded_numeric_interp(target: float, xs: Sequence[float], ys: Sequence[float]) -> float | None:
    import numpy as np

    left_index = int(np.searchsorted(xs, target, side="right") - 1)
    right_index = int(np.searchsorted(xs, target, side="left"))
    if left_index < 0 or right_index >= len(xs):
        return None
    if xs[left_index] == target:
        value = ys[left_index]
    elif right_index == left_index:
        value = ys[left_index]
    else:
        left_x = xs[left_index]
        right_x = xs[right_index]
        if right_x <= left_x:
            return None
        weight = (target - left_x) / (right_x - left_x)
        value = ys[left_index] + ((ys[right_index] - ys[left_index]) * weight)
    return None if value != value else float(value)


def nearest_numeric_value(target: float, xs: Sequence[float], ys: Sequence[float]) -> float | None:
    import numpy as np

    if len(xs) == 0:
        return None
    right_index = int(np.searchsorted(xs, target, side="left"))
    if right_index <= 0:
        value = ys[0]
    elif right_index >= len(xs):
        value = ys[-1]
    else:
        left_index = right_index - 1
        left_distance = abs(target - xs[left_index])
        right_distance = abs(xs[right_index] - target)
        value = ys[left_index] if left_distance <= right_distance else ys[right_index]
    return None if value != value else float(value)


def stream_diagnostics(
    rows: Sequence[tuple[Any, ...]],
    *,
    session_id: str,
    session_key: int,
    driver_number: int,
    driver_code: str,
    stream_name: str,
    max_interpolation_gap_ms: int,
) -> tuple[Any, ...]:
    try:
        import pandas as pd
    except ModuleNotFoundError as exc:
        raise RuntimeError(
            "pandas is required for aligned telemetry materialization. "
            "Run `.venv/bin/python -m pip install -r scripts/requirements.txt`."
        ) from exc

    if not rows:
        return (
            session_id,
            session_key,
            driver_number,
            driver_code,
            stream_name,
            0,
            None,
            None,
            None,
            None,
            None,
            None,
            None,
            None,
            0,
            0,
            ["EMPTY_STREAM"],
        )

    times = pd.to_datetime([row[0] for row in rows], utc=True)
    deltas = times.to_series().diff().dt.total_seconds().mul(1000)
    positive_deltas = deltas[deltas > 0]
    duplicate_count = int(times.duplicated().sum())
    out_of_order_count = int((deltas < 0).sum())
    median_delta_ms = float(positive_deltas.median()) if not positive_deltas.empty else None
    max_delta_ms = float(positive_deltas.max()) if not positive_deltas.empty else None
    warnings: list[str] = []
    if duplicate_count:
        warnings.append("DUPLICATE_SOURCE_TIMESTAMP")
    if out_of_order_count:
        warnings.append("OUT_OF_ORDER_SOURCE_DATA")
    if max_delta_ms is not None and max_delta_ms > max_interpolation_gap_ms:
        warnings.append("SOURCE_GAP_TOO_LARGE")
    if not warnings:
        warnings = []

    return (
        session_id,
        session_key,
        driver_number,
        driver_code,
        stream_name,
        len(rows),
        times.min().to_pydatetime(),
        times.max().to_pydatetime(),
        float(positive_deltas.min()) if not positive_deltas.empty else None,
        median_delta_ms,
        float(positive_deltas.quantile(0.90)) if not positive_deltas.empty else None,
        float(positive_deltas.quantile(0.99)) if not positive_deltas.empty else None,
        max_delta_ms,
        (1000.0 / median_delta_ms) if median_delta_ms and median_delta_ms > 0 else None,
        duplicate_count,
        out_of_order_count,
        warnings,
    )


def deduplicate_stream_frame(rows: Sequence[tuple[Any, ...]], columns: Sequence[str]) -> Any:
    import pandas as pd

    frame = pd.DataFrame(rows, columns=columns)
    if frame.empty:
        return frame
    frame["_source_order"] = range(len(frame))
    frame["sample_time_utc"] = pd.to_datetime(frame["sample_time_utc"], utc=True)
    frame = (
        frame.sort_values(["sample_time_utc", "_source_order"])
        .drop_duplicates(subset=["sample_time_utc"], keep="last")
        .sort_values("sample_time_utc")
        .reset_index(drop=True)
    )
    return frame


def series_on_grid(frame: Any, grid: Any, column: str, *, interpolate: bool) -> Any:
    import pandas as pd

    if column not in frame:
        return pd.Series([None] * len(grid), index=grid)

    source = pd.Series(frame[column].to_numpy(dtype=object), index=frame["sample_time_utc"])
    combined_index = source.index.union(grid).sort_values()
    combined = source.reindex(combined_index)
    if interpolate:
        combined = pd.to_numeric(combined, errors="coerce").interpolate(method="time")
    else:
        combined = combined.ffill()
    return combined.reindex(grid)


def previous_source_times(frame: Any, grid: Any) -> Any:
    import pandas as pd

    source_times = pd.Series(frame["sample_time_utc"].to_numpy(), index=frame["sample_time_utc"])
    combined_index = source_times.index.union(grid).sort_values()
    return source_times.reindex(combined_index).ffill().reindex(grid)


def next_source_times(frame: Any, grid: Any) -> Any:
    source_times = frame["sample_time_utc"].iloc[::-1]
    reverse = source_times.to_numpy()
    import pandas as pd

    source = pd.Series(reverse, index=source_times)
    combined_index = source.index.union(grid).sort_values(ascending=False)
    return source.reindex(combined_index).ffill().reindex(grid)


def lap_for_session_time(session_time_ms: int | None, lap_windows: Sequence[DriverLapWindow]) -> tuple[int | None, int | None]:
    if session_time_ms is None:
        return None, None
    for window in lap_windows:
        if window.start_ms <= session_time_ms <= window.end_ms:
            return window.lap_number, session_time_ms - window.start_ms
    return None, None


def materialize_aligned_telemetry(
    session_id: str,
    session_key: int,
    driver_rows: Sequence[tuple[Any, ...]],
    telemetry_rows: Sequence[tuple[Any, ...]],
    position_rows: Sequence[tuple[Any, ...]],
    lap_windows_by_driver: dict[str, list[DriverLapWindow]],
    *,
    output_frequency_hz: int = 10,
    max_car_data_interpolation_gap_ms: int = 1000,
    max_position_interpolation_gap_ms: int = 1000,
    max_source_age_ms: int = 750,
    alignment_method: str = "time_grid_linear_ffill_v1",
) -> tuple[list[tuple[Any, ...]], list[tuple[Any, ...]]]:
    import pandas as pd

    driver_numbers = {
        str(row[1]).upper(): int(row[2])
        for row in driver_rows
        if row[1] is not None and row[2] is not None
    }
    telemetry_by_driver: dict[str, list[tuple[Any, ...]]] = {}
    position_by_driver: dict[str, list[tuple[Any, ...]]] = {}
    for row in telemetry_rows:
        telemetry_by_driver.setdefault(str(row[2]).upper(), []).append(row)
    for row in position_rows:
        position_by_driver.setdefault(str(row[2]).upper(), []).append(row)

    aligned_rows: list[tuple[Any, ...]] = []
    diagnostic_rows: list[tuple[Any, ...]] = []
    interval_ms = int(round(1000 / output_frequency_hz))

    for driver_code in sorted(set(telemetry_by_driver).intersection(position_by_driver)):
        driver_number = driver_numbers.get(driver_code)
        if driver_number is None:
            logging.warning("Skipping aligned telemetry for %s: missing driver number", driver_code)
            continue

        raw_car_rows = telemetry_by_driver[driver_code]
        raw_position_rows = position_by_driver[driver_code]
        diagnostic_rows.append(
            stream_diagnostics(
                raw_car_rows,
                session_id=session_id,
                session_key=session_key,
                driver_number=driver_number,
                driver_code=driver_code,
                stream_name="raw_car_telemetry",
                max_interpolation_gap_ms=max_car_data_interpolation_gap_ms,
            )
        )
        diagnostic_rows.append(
            stream_diagnostics(
                raw_position_rows,
                session_id=session_id,
                session_key=session_key,
                driver_number=driver_number,
                driver_code=driver_code,
                stream_name="raw_location_telemetry",
                max_interpolation_gap_ms=max_position_interpolation_gap_ms,
            )
        )

        car = deduplicate_stream_frame(raw_car_rows, TELEMETRY_COLUMNS)
        location = deduplicate_stream_frame(raw_position_rows, POSITION_COLUMNS)
        if car.empty or location.empty:
            continue

        start = max(car["sample_time_utc"].min(), location["sample_time_utc"].min()).ceil(f"{interval_ms}ms")
        end = min(car["sample_time_utc"].max(), location["sample_time_utc"].max()).floor(f"{interval_ms}ms")
        if start > end:
            continue
        grid = pd.date_range(start=start, end=end, freq=f"{interval_ms}ms", tz="UTC")
        if grid.empty:
            continue

        session_times = pd.to_numeric(series_on_grid(car, grid, "session_time_ms", interpolate=True), errors="coerce").round()
        speed = series_on_grid(car, grid, "speed_kmh", interpolate=True)
        rpm = series_on_grid(car, grid, "rpm", interpolate=True)
        throttle = series_on_grid(car, grid, "throttle_pct", interpolate=True)
        gear = series_on_grid(car, grid, "gear", interpolate=False)
        brake = series_on_grid(car, grid, "brake_pct", interpolate=False)
        drs = series_on_grid(car, grid, "drs", interpolate=False)

        x = series_on_grid(location, grid, "x", interpolate=True)
        y = series_on_grid(location, grid, "y", interpolate=True)
        z = series_on_grid(location, grid, "z", interpolate=True)
        location_status = series_on_grid(location, grid, "track_status", interpolate=False)

        car_source_times = previous_source_times(car, grid)
        location_source_times = previous_source_times(location, grid)
        car_next_times = next_source_times(car, grid)
        location_next_times = next_source_times(location, grid)
        car_exact_times = set(car["sample_time_utc"])
        location_exact_times = set(location["sample_time_utc"])

        lap_windows = lap_windows_by_driver.get(driver_code, [])
        for sample_index, sample_time in enumerate(grid):
            session_time_ms = int(session_times.iloc[sample_index]) if pd.notna(session_times.iloc[sample_index]) else None
            lap_number, lap_time_ms = lap_for_session_time(session_time_ms, lap_windows)
            car_source_time = car_source_times.iloc[sample_index]
            location_source_time = location_source_times.iloc[sample_index]
            car_next_time = car_next_times.iloc[sample_index]
            location_next_time = location_next_times.iloc[sample_index]

            car_age_ms = (
                int((sample_time - car_source_time).total_seconds() * 1000)
                if pd.notna(car_source_time)
                else None
            )
            location_age_ms = (
                int((sample_time - location_source_time).total_seconds() * 1000)
                if pd.notna(location_source_time)
                else None
            )
            flags: list[str] = []
            if car_age_ms is None:
                flags.append("MISSING_CAR_DATA")
            elif car_age_ms > max_source_age_ms:
                flags.append("CAR_SAMPLE_TOO_OLD")
            if location_age_ms is None:
                flags.append("MISSING_LOCATION_DATA")
            elif location_age_ms > max_source_age_ms:
                flags.append("LOCATION_SAMPLE_TOO_OLD")
            car_gap_too_large = False
            location_gap_too_large = False
            if pd.notna(car_source_time) and pd.notna(car_next_time):
                car_gap_ms = (car_next_time - car_source_time).total_seconds() * 1000
                if car_gap_ms > max_car_data_interpolation_gap_ms:
                    car_gap_too_large = True
                    flags.append("CAR_GAP_TOO_LARGE")
            if pd.notna(location_source_time) and pd.notna(location_next_time):
                location_gap_ms = (location_next_time - location_source_time).total_seconds() * 1000
                if location_gap_ms > max_position_interpolation_gap_ms:
                    location_gap_too_large = True
                    flags.append("LOCATION_GAP_TOO_LARGE")

            car_channels_valid = (
                car_age_ms is not None
                and car_age_ms <= max_source_age_ms
                and not car_gap_too_large
            )
            position_channels_valid = (
                location_age_ms is not None
                and location_age_ms <= max_source_age_ms
                and not location_gap_too_large
            )

            aligned_rows.append(
                (
                    sample_time.to_pydatetime(),
                    session_id,
                    session_key,
                    driver_number,
                    driver_code,
                    lap_number,
                    sample_index,
                    session_time_ms,
                    lap_time_ms,
                    float_or_none(speed.iloc[sample_index]) if car_channels_valid else None,
                    float_or_none(rpm.iloc[sample_index]) if car_channels_valid else None,
                    int_or_none(gear.iloc[sample_index]) if car_channels_valid else None,
                    float_or_none(throttle.iloc[sample_index]) if car_channels_valid else None,
                    float_or_none(brake.iloc[sample_index]) if car_channels_valid else None,
                    int_or_none(drs.iloc[sample_index]) if car_channels_valid else None,
                    float_or_none(x.iloc[sample_index]) if position_channels_valid else None,
                    float_or_none(y.iloc[sample_index]) if position_channels_valid else None,
                    float_or_none(z.iloc[sample_index]) if position_channels_valid else None,
                    str_or_none(location_status.iloc[sample_index]) if position_channels_valid else None,
                    car_source_time.to_pydatetime() if pd.notna(car_source_time) else None,
                    location_source_time.to_pydatetime() if pd.notna(location_source_time) else None,
                    car_age_ms,
                    location_age_ms,
                    sample_time not in car_exact_times,
                    sample_time not in location_exact_times,
                    flags or ["OK"],
                    1,
                    alignment_method,
                )
            )

    return aligned_rows, diagnostic_rows


def materialize_lap_telemetry_by_distance(
    session_id: str,
    session_key: int,
    driver_rows: Sequence[tuple[Any, ...]],
    lap_rows: Sequence[tuple[Any, ...]],
    telemetry_rows: Sequence[tuple[Any, ...]],
    position_rows: Sequence[tuple[Any, ...]],
    *,
    distance_step_m: float = 5.0,
    max_car_data_interpolation_gap_ms: int = 1000,
    max_position_interpolation_gap_ms: int = 1000,
    max_source_age_ms: int = 750,
    validation_tolerance_ms: int = 100,
    alignment_version: int = 1,
) -> tuple[list[tuple[Any, ...]], list[tuple[Any, ...]]]:
    import numpy as np
    import pandas as pd

    driver_numbers = {
        str(row[1]).upper(): int(row[2])
        for row in driver_rows
        if row[1] is not None and row[2] is not None
    }
    lap_metadata_by_driver = build_lap_metadata_by_driver(lap_rows)

    telemetry_by_lap: dict[tuple[str, int], list[tuple[Any, ...]]] = {}
    for row in telemetry_rows:
        driver_code = str(row[2]).upper()
        lap_number = int_or_none(row[3])
        if lap_number is None:
            continue
        telemetry_by_lap.setdefault((driver_code, lap_number), []).append(row)

    position_by_lap: dict[tuple[str, int], list[tuple[Any, ...]]] = {}
    for row in position_rows:
        driver_code = str(row[2]).upper()
        lap_number = int_or_none(row[3])
        if lap_number is None:
            continue
        position_by_lap.setdefault((driver_code, lap_number), []).append(row)

    distance_rows: list[tuple[Any, ...]] = []
    quality_rows: list[tuple[Any, ...]] = []

    for driver_code, laps_for_driver in sorted(lap_metadata_by_driver.items()):
        driver_number = driver_numbers.get(driver_code)
        if driver_number is None:
            continue

        for lap_number, lap_metadata in sorted(laps_for_driver.items()):
            official_lap_duration_ms = int_or_none(lap_metadata.get("official_lap_duration_ms"))
            lap_start_utc = timestamp_or_none(lap_metadata.get("lap_start_utc"))
            messages: list[str] = []

            if lap_metadata.get("is_pit_in_lap"):
                messages.append("WARNING_PIT_IN_LAP")
            if lap_metadata.get("is_pit_out_lap"):
                messages.append("WARNING_PIT_OUT_LAP")
            if lap_metadata.get("is_deleted"):
                messages.append("WARNING_FASTF1_DELETED_LAP")
            if lap_metadata.get("is_accurate") is False:
                messages.append("WARNING_FASTF1_INACCURATE_LAP")

            telemetry_lap_rows = telemetry_by_lap.get((driver_code, lap_number), [])
            if official_lap_duration_ms is None:
                messages.append("INVALID_MISSING_OFFICIAL_LAP_DURATION")
            if lap_start_utc is None:
                messages.append("INVALID_MISSING_LAP_START_TIME")
            if len(telemetry_lap_rows) < 2:
                messages.append("INVALID_MISSING_CAR_TELEMETRY")

            telemetry_covered_duration_ms: int | None = None
            first_sample_offset_ms: int | None = None
            last_sample_offset_ms: int | None = None
            maximum_car_data_gap_ms: int | None = None
            maximum_position_gap_ms: int | None = None
            final_integrated_distance_m: float | None = None
            interpolated_car_data_percentage = 0.0
            interpolated_position_percentage = 0.0
            stale_sample_percentage = 0.0
            distance_delta_validation_ms: int | None = None

            if any(message.startswith("INVALID_") for message in messages):
                quality_rows.append(
                    (
                        session_id,
                        driver_number,
                        lap_number,
                        official_lap_duration_ms,
                        telemetry_covered_duration_ms,
                        first_sample_offset_ms,
                        last_sample_offset_ms,
                        maximum_car_data_gap_ms,
                        maximum_position_gap_ms,
                        final_integrated_distance_m,
                        interpolated_car_data_percentage,
                        interpolated_position_percentage,
                        stale_sample_percentage,
                        distance_delta_validation_ms,
                        quality_status_from_messages(messages),
                        messages,
                    )
                )
                continue

            telemetry_frame = pd.DataFrame(telemetry_lap_rows, columns=TELEMETRY_COLUMNS)
            telemetry_frame["sample_time_utc"] = pd.to_datetime(telemetry_frame["sample_time_utc"], utc=True)
            telemetry_frame = telemetry_frame.dropna(subset=["lap_time_ms", "session_time_ms", "speed_kmh"])
            telemetry_frame = (
                telemetry_frame.sort_values(["lap_time_ms", "sample_time_utc"])
                .drop_duplicates(subset=["lap_time_ms"], keep="last")
                .reset_index(drop=True)
            )

            if len(telemetry_frame) < 2:
                messages.append("INVALID_INSUFFICIENT_CAR_TELEMETRY")
                quality_rows.append(
                    (
                        session_id,
                        driver_number,
                        lap_number,
                        official_lap_duration_ms,
                        telemetry_covered_duration_ms,
                        first_sample_offset_ms,
                        last_sample_offset_ms,
                        maximum_car_data_gap_ms,
                        maximum_position_gap_ms,
                        final_integrated_distance_m,
                        interpolated_car_data_percentage,
                        interpolated_position_percentage,
                        stale_sample_percentage,
                        distance_delta_validation_ms,
                        quality_status_from_messages(messages),
                        messages,
                    )
                )
                continue

            lap_times = telemetry_frame["lap_time_ms"].astype(float).to_numpy()
            session_times = telemetry_frame["session_time_ms"].astype(float).to_numpy()
            speeds = telemetry_frame["speed_kmh"].astype(float).to_numpy()
            throttles = telemetry_frame["throttle_pct"].astype(float).to_numpy()
            brakes = telemetry_frame["brake_pct"].astype(float).to_numpy()
            rpms = telemetry_frame["rpm"].astype(float).to_numpy()
            gears = telemetry_frame["gear"].astype(float).to_numpy()
            drs_values = telemetry_frame["drs"].astype(float).to_numpy()
            sample_times = telemetry_frame["sample_time_utc"].tolist()

            lap_deltas = np.diff(lap_times)
            if np.any(lap_deltas < 0):
                messages.append("INVALID_NON_MONOTONIC_LAP_TIME")
            positive_lap_deltas = lap_deltas[lap_deltas > 0]
            maximum_car_data_gap_ms = int(round(float(positive_lap_deltas.max()))) if positive_lap_deltas.size else None

            distance_series = np.zeros(len(telemetry_frame), dtype=float)
            for index in range(1, len(telemetry_frame)):
                delta_seconds = max(0.0, (lap_times[index] - lap_times[index - 1]) / 1000.0)
                average_speed_mps = ((speeds[index - 1] + speeds[index]) / 2.0) / 3.6
                distance_series[index] = distance_series[index - 1] + (average_speed_mps * delta_seconds)

            final_integrated_distance_m = float(distance_series[-1]) if len(distance_series) else None
            telemetry_covered_duration_ms = int(round(float(lap_times[-1] - lap_times[0]))) if len(lap_times) else None
            first_sample_offset_ms = int(round(float(lap_times[0]))) if len(lap_times) else None
            last_sample_offset_ms = (
                max(0, int(round(float(official_lap_duration_ms - lap_times[-1]))))
                if official_lap_duration_ms is not None and len(lap_times)
                else None
            )

            if final_integrated_distance_m is None or final_integrated_distance_m <= 0:
                messages.append("INVALID_NON_POSITIVE_FINAL_DISTANCE")
            if maximum_car_data_gap_ms is not None and maximum_car_data_gap_ms > max_car_data_interpolation_gap_ms:
                messages.append("INCOMPLETE_CAR_GAP_TOO_LARGE")
            if first_sample_offset_ms is not None and first_sample_offset_ms > max_source_age_ms:
                messages.append("INCOMPLETE_LATE_LAP_START_COVERAGE")
            if last_sample_offset_ms is not None and last_sample_offset_ms > max_source_age_ms:
                messages.append("INCOMPLETE_EARLY_LAP_END_COVERAGE")

            position_frame = pd.DataFrame(position_by_lap.get((driver_code, lap_number), []), columns=POSITION_COLUMNS)
            if not position_frame.empty and lap_start_utc is not None:
                position_frame["sample_time_utc"] = pd.to_datetime(position_frame["sample_time_utc"], utc=True)
                position_frame["lap_elapsed_time_ms"] = (
                    (position_frame["sample_time_utc"] - pd.Timestamp(lap_start_utc))
                    .dt.total_seconds()
                    .mul(1000)
                )
                position_frame = position_frame.dropna(subset=["lap_elapsed_time_ms"])
                position_frame = position_frame[
                    (position_frame["lap_elapsed_time_ms"] >= 0)
                    & (position_frame["lap_elapsed_time_ms"] <= official_lap_duration_ms + max_source_age_ms)
                ]
                position_frame = (
                    position_frame.sort_values(["lap_elapsed_time_ms", "sample_time_utc"])
                    .drop_duplicates(subset=["lap_elapsed_time_ms"], keep="last")
                    .reset_index(drop=True)
                )
            if position_frame.empty:
                messages.append("WARNING_MISSING_POSITION_DATA")
                position_elapsed = np.array([], dtype=float)
                position_sample_times: list[Any] = []
                xs = np.array([], dtype=float)
                ys = np.array([], dtype=float)
                zs = np.array([], dtype=float)
            else:
                position_elapsed = position_frame["lap_elapsed_time_ms"].astype(float).to_numpy()
                position_sample_times = position_frame["sample_time_utc"].tolist()
                xs = position_frame["x"].astype(float).to_numpy()
                ys = position_frame["y"].astype(float).to_numpy()
                zs = position_frame["z"].astype(float).to_numpy()
                position_deltas = np.diff(position_elapsed)
                positive_position_deltas = position_deltas[position_deltas > 0]
                maximum_position_gap_ms = int(round(float(positive_position_deltas.max()))) if positive_position_deltas.size else None
                if maximum_position_gap_ms is not None and maximum_position_gap_ms > max_position_interpolation_gap_ms:
                    messages.append("INCOMPLETE_POSITION_GAP_TOO_LARGE")

            if any(message.startswith("INVALID_") for message in messages):
                quality_rows.append(
                    (
                        session_id,
                        driver_number,
                        lap_number,
                        official_lap_duration_ms,
                        telemetry_covered_duration_ms,
                        first_sample_offset_ms,
                        last_sample_offset_ms,
                        maximum_car_data_gap_ms,
                        maximum_position_gap_ms,
                        final_integrated_distance_m,
                        interpolated_car_data_percentage,
                        interpolated_position_percentage,
                        stale_sample_percentage,
                        distance_delta_validation_ms,
                        quality_status_from_messages(messages),
                        messages,
                    )
                )
                continue

            finish_projected_lap_elapsed_ms = int(round(float(lap_times[-1])))
            distance_delta_validation_ms = abs(finish_projected_lap_elapsed_ms - official_lap_duration_ms)
            if distance_delta_validation_ms > validation_tolerance_ms:
                messages.append("INCOMPLETE_PROJECTED_FINISH_MISMATCH")

            max_grid_distance = int(np.floor(final_integrated_distance_m / distance_step_m) * distance_step_m)
            grid = np.arange(0.0, max_grid_distance + distance_step_m, distance_step_m, dtype=float)
            if grid.size == 0:
                grid = np.array([0.0], dtype=float)
            grid = grid[grid <= final_integrated_distance_m + 1e-9]
            if grid.size == 0 or not np.isclose(grid[-1], final_integrated_distance_m):
                grid = np.append(grid, float(final_integrated_distance_m))

            interpolated_car_rows = 0
            interpolated_position_rows = 0
            stale_rows = 0

            for distance_m in grid:
                right_index = int(np.searchsorted(distance_series, distance_m, side="left"))
                left_index = max(0, right_index - 1)
                if right_index >= len(distance_series):
                    right_index = len(distance_series) - 1
                exact_match = bool(np.isclose(distance_series[left_index], distance_m) or np.isclose(distance_series[right_index], distance_m))
                target_elapsed_ms = bounded_numeric_interp(distance_m, distance_series, lap_times)
                target_session_time_ms = bounded_numeric_interp(distance_m, distance_series, session_times)

                source_before_time = sample_times[left_index]
                source_after_time = sample_times[right_index]
                car_local_gap_ms = abs(float(lap_times[right_index] - lap_times[left_index]))
                if target_elapsed_ms is None:
                    car_source_age_ms = None
                else:
                    car_source_age_ms = int(round(min(
                        abs(target_elapsed_ms - float(lap_times[left_index])),
                        abs(float(lap_times[right_index]) - target_elapsed_ms),
                    )))

                row_flags = [message for message in messages if message.startswith("WARNING_")]
                car_values_valid = True
                if car_local_gap_ms > max_car_data_interpolation_gap_ms:
                    row_flags.append("CAR_GAP_TOO_LARGE")
                    car_values_valid = False
                if car_source_age_ms is None:
                    row_flags.append("MISSING_CAR_DATA")
                    car_values_valid = False
                elif car_source_age_ms > max_source_age_ms:
                    row_flags.append("CAR_SAMPLE_TOO_OLD")
                    car_values_valid = False

                speed_value = bounded_numeric_interp(distance_m, distance_series, speeds) if car_values_valid else None
                throttle_value = bounded_numeric_interp(distance_m, distance_series, throttles) if car_values_valid else None
                brake_value = nearest_numeric_value(distance_m, distance_series, brakes) if car_values_valid else None
                gear_value = nearest_numeric_value(distance_m, distance_series, gears) if car_values_valid else None
                rpm_value = bounded_numeric_interp(distance_m, distance_series, rpms) if car_values_valid else None
                drs_value = nearest_numeric_value(distance_m, distance_series, drs_values) if car_values_valid else None

                x_value = y_value = z_value = None
                position_interpolated = False
                if target_elapsed_ms is None or position_elapsed.size == 0:
                    row_flags.append("MISSING_POSITION_DATA")
                else:
                    position_right_index = int(np.searchsorted(position_elapsed, target_elapsed_ms, side="left"))
                    position_left_index = max(0, position_right_index - 1)
                    if position_right_index >= len(position_elapsed):
                        position_right_index = len(position_elapsed) - 1
                    position_exact = bool(
                        np.isclose(position_elapsed[position_left_index], target_elapsed_ms)
                        or np.isclose(position_elapsed[position_right_index], target_elapsed_ms)
                    )
                    position_interpolated = not position_exact
                    position_local_gap_ms = abs(float(position_elapsed[position_right_index] - position_elapsed[position_left_index]))
                    position_source_age_ms = int(round(min(
                        abs(target_elapsed_ms - float(position_elapsed[position_left_index])),
                        abs(float(position_elapsed[position_right_index]) - target_elapsed_ms),
                    )))
                    if position_local_gap_ms > max_position_interpolation_gap_ms:
                        row_flags.append("POSITION_GAP_TOO_LARGE")
                    elif position_source_age_ms > max_source_age_ms:
                        row_flags.append("POSITION_SAMPLE_TOO_OLD")
                    else:
                        x_value = bounded_numeric_interp(target_elapsed_ms, position_elapsed, xs)
                        y_value = bounded_numeric_interp(target_elapsed_ms, position_elapsed, ys)
                        z_value = bounded_numeric_interp(target_elapsed_ms, position_elapsed, zs)

                if not exact_match:
                    interpolated_car_rows += 1
                if position_interpolated:
                    interpolated_position_rows += 1
                if any(flag in {"CAR_GAP_TOO_LARGE", "CAR_SAMPLE_TOO_OLD", "POSITION_GAP_TOO_LARGE", "POSITION_SAMPLE_TOO_OLD", "MISSING_CAR_DATA", "MISSING_POSITION_DATA"} for flag in row_flags):
                    stale_rows += 1

                normalized_track_progress = 0.0 if final_integrated_distance_m <= 0 else min(1.0, distance_m / final_integrated_distance_m)
                distance_rows.append(
                    (
                        session_id,
                        session_key,
                        driver_number,
                        driver_code,
                        lap_number,
                        float(round(distance_m, 3)),
                        float(round(normalized_track_progress, 6)),
                        int(round(target_elapsed_ms)) if target_elapsed_ms is not None and car_values_valid else None,
                        int(round(target_session_time_ms)) if target_session_time_ms is not None and car_values_valid else None,
                        speed_value,
                        throttle_value,
                        brake_value,
                        int(round(gear_value)) if gear_value is not None else None,
                        rpm_value,
                        int(round(drs_value)) if drs_value is not None else None,
                        x_value,
                        y_value,
                        z_value,
                        source_before_time.to_pydatetime() if hasattr(source_before_time, "to_pydatetime") else source_before_time,
                        source_after_time.to_pydatetime() if hasattr(source_after_time, "to_pydatetime") else source_after_time,
                        not exact_match,
                        row_flags or ["OK"],
                        alignment_version,
                    )
                )

            if grid.size > 0:
                interpolated_car_data_percentage = (interpolated_car_rows / len(grid)) * 100.0
                interpolated_position_percentage = (interpolated_position_rows / len(grid)) * 100.0
                stale_sample_percentage = (stale_rows / len(grid)) * 100.0

            quality_rows.append(
                (
                    session_id,
                    driver_number,
                    lap_number,
                    official_lap_duration_ms,
                    telemetry_covered_duration_ms,
                    first_sample_offset_ms,
                    last_sample_offset_ms,
                    maximum_car_data_gap_ms,
                    maximum_position_gap_ms,
                    final_integrated_distance_m,
                    interpolated_car_data_percentage,
                    interpolated_position_percentage,
                    stale_sample_percentage,
                    distance_delta_validation_ms,
                    quality_status_from_messages(messages),
                    messages,
                )
            )

    return distance_rows, quality_rows


def copy_sample_rows(
    database_url: str,
    table_name: str,
    columns: Sequence[str],
    rows: Sequence[tuple[Any, ...]],
    batch_size: int,
    key_indexes: tuple[int, ...] = (0, 1, 2),
) -> tuple[int, float, int]:
    psycopg = require_psycopg()
    with psycopg.connect(database_url, autocommit=False) as connection:
        writer = CopyWriter(connection, table_name, columns, batch_size, key_indexes)
        writer.add_many(rows)
        writer.flush()
        connection.commit()
        return writer.total, writer.write_seconds, writer.duplicates


def stream_sample_rows(
    connection: Any,
    args: argparse.Namespace,
    session: Any,
    laps: Any,
    session_id: str,
    driver_refs: Sequence[tuple[str, str]],
    driver_rows: Sequence[tuple[Any, ...]],
    lap_rows: Sequence[tuple[Any, ...]],
) -> tuple[int, int, int, int, int, int]:
    if args.parallel_sample_copy and args.sample_write_method == "copy":
        telemetry_rows, position_rows, extraction_seconds = collect_sample_rows(
            args,
            session,
            laps,
            session_id,
            driver_refs,
        )
        aligned_rows: list[tuple[Any, ...]] = []
        diagnostic_rows: list[tuple[Any, ...]] = []
        distance_rows: list[tuple[Any, ...]] = []
        quality_rows: list[tuple[Any, ...]] = []
        if args.include_aligned_telemetry:
            aligned_rows, diagnostic_rows = materialize_aligned_telemetry(
                session_id,
                stable_session_key(session_id),
                driver_rows,
                telemetry_rows,
                position_rows,
                build_driver_lap_windows(laps),
                max_car_data_interpolation_gap_ms=args.max_car_data_interpolation_gap_ms,
                max_position_interpolation_gap_ms=args.max_position_interpolation_gap_ms,
                max_source_age_ms=args.max_source_age_ms,
            )
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
            )
        start = time.perf_counter()
        with ThreadPoolExecutor(max_workers=4) as executor:
            telemetry_future = executor.submit(
                copy_sample_rows,
                args.database_url,
                "telemetry_samples",
                TELEMETRY_COLUMNS,
                telemetry_rows,
                args.batch_size,
            )
            position_future = executor.submit(
                copy_sample_rows,
                args.database_url,
                "position_samples",
                POSITION_COLUMNS,
                position_rows,
                args.batch_size,
            )
            aligned_future = (
                executor.submit(
                    copy_sample_rows,
                    args.database_url,
                    "aligned_telemetry_10hz",
                    ALIGNED_TELEMETRY_COLUMNS,
                    aligned_rows,
                    args.batch_size,
                    (0, 2, 3),
                )
                if aligned_rows
                else None
            )
            distance_future = (
                executor.submit(
                    copy_sample_rows,
                    args.database_url,
                    "lap_telemetry_by_distance",
                    LAP_TELEMETRY_DISTANCE_COLUMNS,
                    distance_rows,
                    args.batch_size,
                    (0, 2, 4, 5),
                )
                if distance_rows
                else None
            )
            quality_future = (
                executor.submit(
                    copy_sample_rows,
                    args.database_url,
                    "lap_telemetry_quality",
                    LAP_TELEMETRY_QUALITY_COLUMNS,
                    quality_rows,
                    args.batch_size,
                    (0, 1, 2),
                )
                if quality_rows
                else None
            )
            telemetry_count, telemetry_write_seconds, telemetry_duplicates = telemetry_future.result()
            position_count, position_write_seconds, position_duplicates = position_future.result()
            if aligned_future is not None:
                aligned_count, aligned_write_seconds, aligned_duplicates = aligned_future.result()
            else:
                aligned_count, aligned_write_seconds, aligned_duplicates = 0, 0.0, 0
            if distance_future is not None:
                distance_count, distance_write_seconds, distance_duplicates = distance_future.result()
            else:
                distance_count, distance_write_seconds, distance_duplicates = 0, 0.0, 0
            if quality_future is not None:
                quality_count, quality_write_seconds, quality_duplicates = quality_future.result()
            else:
                quality_count, quality_write_seconds, quality_duplicates = 0, 0.0, 0
        execute_many(connection, TELEMETRY_DIAGNOSTIC_INSERT_SQL, diagnostic_rows, args.batch_size)
        logging.info(
            "Parallel sample copy completed: telemetry=%s position=%s aligned=%s distance=%s lap_quality=%s diagnostics=%s extraction_worker_seconds=%.2f write_seconds=%.2f wall_seconds=%.2f duplicates_skipped=%d",
            f"{telemetry_count:,}",
            f"{position_count:,}",
            f"{aligned_count:,}",
            f"{distance_count:,}",
            f"{quality_count:,}",
            f"{len(diagnostic_rows):,}",
            extraction_seconds,
            telemetry_write_seconds + position_write_seconds + aligned_write_seconds + distance_write_seconds + quality_write_seconds,
            time.perf_counter() - start,
            telemetry_duplicates + position_duplicates + aligned_duplicates + distance_duplicates + quality_duplicates,
        )
        return telemetry_count, position_count, aligned_count, len(diagnostic_rows), distance_count, quality_count

    if args.include_aligned_telemetry:
        telemetry_rows, position_rows, extraction_seconds = collect_sample_rows(
            args,
            session,
            laps,
            session_id,
            driver_refs,
        )
        aligned_rows, diagnostic_rows = materialize_aligned_telemetry(
            session_id,
            stable_session_key(session_id),
            driver_rows,
            telemetry_rows,
            position_rows,
            build_driver_lap_windows(laps),
            max_car_data_interpolation_gap_ms=args.max_car_data_interpolation_gap_ms,
            max_position_interpolation_gap_ms=args.max_position_interpolation_gap_ms,
            max_source_age_ms=args.max_source_age_ms,
        )
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
        )
        telemetry_writer = sample_writer(
            connection,
            args,
            "telemetry_samples",
            TELEMETRY_INSERT_SQL,
            TELEMETRY_COLUMNS,
        )
        position_writer = sample_writer(
            connection,
            args,
            "position_samples",
            POSITION_INSERT_SQL,
            POSITION_COLUMNS,
        )
        aligned_writer = CopyWriter(
            connection,
            "aligned_telemetry_10hz",
            ALIGNED_TELEMETRY_COLUMNS,
            args.batch_size,
            (0, 2, 3),
        )
        distance_writer = CopyWriter(
            connection,
            "lap_telemetry_by_distance",
            LAP_TELEMETRY_DISTANCE_COLUMNS,
            args.batch_size,
            (0, 2, 4, 5),
        )
        quality_writer = sample_writer(
            connection,
            args,
            "lap_telemetry_quality",
            LAP_TELEMETRY_QUALITY_INSERT_SQL,
            LAP_TELEMETRY_QUALITY_COLUMNS,
        )
        telemetry_writer.add_many(telemetry_rows)
        position_writer.add_many(position_rows)
        aligned_writer.add_many(aligned_rows)
        distance_writer.add_many(distance_rows)
        quality_writer.add_many(quality_rows)
        telemetry_writer.flush()
        position_writer.flush()
        aligned_writer.flush()
        distance_writer.flush()
        quality_writer.flush()
        execute_many(connection, TELEMETRY_DIAGNOSTIC_INSERT_SQL, diagnostic_rows, args.batch_size)
        logging.info(
            "Sample write completed: telemetry=%s position=%s aligned=%s distance=%s lap_quality=%s diagnostics=%s extraction_worker_seconds=%.2f",
            f"{telemetry_writer.total:,}",
            f"{position_writer.total:,}",
            f"{aligned_writer.total:,}",
            f"{distance_writer.total:,}",
            f"{quality_writer.total:,}",
            f"{len(diagnostic_rows):,}",
            extraction_seconds,
        )
        return telemetry_writer.total, position_writer.total, aligned_writer.total, len(diagnostic_rows), distance_writer.total, quality_writer.total

    driver_windows = build_driver_lap_windows(laps)
    driver_items = [
        (driver_code, driver_ref, driver_windows.get(driver_code, []))
        for driver_code, driver_ref in driver_refs
        if driver_windows.get(driver_code)
    ]
    total_drivers = len(driver_items)
    total_laps = sum(len(lap_windows) for _, _, lap_windows in driver_items)
    telemetry_writer = sample_writer(
        connection,
        args,
        "telemetry_samples",
        TELEMETRY_INSERT_SQL,
        TELEMETRY_COLUMNS,
    )
    position_writer = sample_writer(
        connection,
        args,
        "position_samples",
        POSITION_INSERT_SQL,
        POSITION_COLUMNS,
    )

    if total_drivers == 0 or (not args.include_telemetry and not args.include_position):
        return 0, 0, 0, 0, 0, 0

    logging.info(
        "Streaming samples for %d driver(s), %d lap(s), %d worker(s), write_method=%s batch_size=%d",
        total_drivers,
        total_laps,
        args.telemetry_workers,
        args.sample_write_method,
        args.batch_size,
    )
    completed = 0
    extraction_seconds = 0.0
    with ThreadPoolExecutor(max_workers=args.telemetry_workers) as executor:
        futures = [
            executor.submit(
                extract_driver_samples,
                session,
                session_id,
                driver_code,
                driver_ref,
                lap_windows,
                args.include_telemetry,
                args.include_position,
            )
            for driver_code, driver_ref, lap_windows in driver_items
        ]
        for future in as_completed(futures):
            result = future.result()
            completed += 1
            extraction_seconds += result.elapsed_seconds
            telemetry_writer.add_many(result.telemetry_rows)
            position_writer.add_many(result.position_rows)
            logging.info(
                "[%d/%d] %s laps=%d: telemetry=%s position=%s extracted in %.2fs",
                completed,
                total_drivers,
                result.driver_code,
                result.lap_count,
                f"{len(result.telemetry_rows):,}",
                f"{len(result.position_rows):,}",
                result.elapsed_seconds,
            )

    telemetry_writer.flush()
    position_writer.flush()
    logging.info(
        "Sample streaming completed: telemetry=%s position=%s extraction_worker_seconds=%.2f write_seconds=%.2f duplicates_skipped=%d",
        f"{telemetry_writer.total:,}",
        f"{position_writer.total:,}",
        extraction_seconds,
        getattr(telemetry_writer, "write_seconds", 0.0) + getattr(position_writer, "write_seconds", 0.0),
        getattr(telemetry_writer, "duplicates", 0) + getattr(position_writer, "duplicates", 0),
    )
    return telemetry_writer.total, position_writer.total, 0, 0, 0, 0


def insert_parent_rows(
    connection: Any,
    args: argparse.Namespace,
    session_id: str,
    session_row: tuple[Any, ...],
    driver_rows: Sequence[tuple[Any, ...]],
    lap_rows: Sequence[tuple[Any, ...]],
) -> None:
    execute_many(
        connection,
        """
            INSERT INTO sessions (
                session_id, year, event_name, circuit_name, country, session_type,
                session_start_utc, session_end_utc, source, metadata
            )
            VALUES (%s, %s, %s, %s, %s, %s, %s, %s, %s, %s)
            ON CONFLICT (session_id) DO UPDATE SET
                year = EXCLUDED.year,
                event_name = EXCLUDED.event_name,
                circuit_name = EXCLUDED.circuit_name,
                country = EXCLUDED.country,
                session_type = EXCLUDED.session_type,
                session_start_utc = EXCLUDED.session_start_utc,
                session_end_utc = EXCLUDED.session_end_utc,
                source = EXCLUDED.source,
                metadata = EXCLUDED.metadata
            """,
        [session_row],
        args.batch_size,
    )
    execute_many(
        connection,
        """
            INSERT INTO session_drivers (session_id, driver_code, driver_number, full_name, team_name, metadata)
            VALUES (%s, %s, %s, %s, %s, %s)
            ON CONFLICT (session_id, driver_code) DO UPDATE SET
                driver_number = EXCLUDED.driver_number,
                full_name = EXCLUDED.full_name,
                team_name = EXCLUDED.team_name,
                metadata = EXCLUDED.metadata
            """,
        driver_rows,
        args.batch_size,
    )
    execute_many(
        connection,
        """
            INSERT INTO laps (
                lap_id, session_id, driver_code, lap_number, stint_number, lap_start_utc,
                lap_end_utc, lap_time_ms, sector_1_ms, sector_2_ms, sector_3_ms,
                compound, tyre_life, is_pit_out_lap, is_pit_in_lap,
                pit_out_session_time_ms, pit_in_session_time_ms, is_deleted,
                is_accurate, metadata
            )
            VALUES (%s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s)
            ON CONFLICT (session_id, driver_code, lap_number) DO UPDATE SET
                lap_start_utc = EXCLUDED.lap_start_utc,
                lap_end_utc = EXCLUDED.lap_end_utc,
                lap_time_ms = EXCLUDED.lap_time_ms,
                sector_1_ms = EXCLUDED.sector_1_ms,
                sector_2_ms = EXCLUDED.sector_2_ms,
                sector_3_ms = EXCLUDED.sector_3_ms,
                compound = EXCLUDED.compound,
                tyre_life = EXCLUDED.tyre_life,
                is_pit_out_lap = EXCLUDED.is_pit_out_lap,
                is_pit_in_lap = EXCLUDED.is_pit_in_lap,
                pit_out_session_time_ms = EXCLUDED.pit_out_session_time_ms,
                pit_in_session_time_ms = EXCLUDED.pit_in_session_time_ms,
                is_deleted = EXCLUDED.is_deleted,
                is_accurate = EXCLUDED.is_accurate,
                metadata = EXCLUDED.metadata
            """,
        lap_rows,
        args.batch_size,
    )


def insert_context_rows(
    connection: Any,
    args: argparse.Namespace,
    circuit_metadata_row: tuple[Any, ...] | None,
    circuit_marker_rows: Sequence[tuple[Any, ...]],
    weather_rows: Sequence[tuple[Any, ...]],
    track_status_rows: Sequence[tuple[Any, ...]],
    session_status_rows: Sequence[tuple[Any, ...]],
    race_control_rows: Sequence[tuple[Any, ...]],
) -> None:
    if circuit_metadata_row is not None:
        execute_many(
            connection,
            """
                INSERT INTO circuit_metadata (session_id, rotation_degrees, source, metadata)
                VALUES (%s, %s, %s, %s)
                ON CONFLICT (session_id) DO UPDATE SET
                    rotation_degrees = EXCLUDED.rotation_degrees,
                    source = EXCLUDED.source,
                    metadata = EXCLUDED.metadata
                """,
            [circuit_metadata_row],
            args.batch_size,
        )
    execute_many(
        connection,
        """
            INSERT INTO circuit_markers (
                session_id, marker_type, marker_number, marker_letter, x, y,
                angle_degrees, distance_m, metadata
            )
            VALUES (%s, %s, %s, %s, %s, %s, %s, %s, %s)
            """,
        circuit_marker_rows,
        args.batch_size,
    )
    execute_many(
        connection,
        """
            INSERT INTO weather_samples (
                session_id, sample_time_utc, session_time_ms, air_temp_c, track_temp_c,
                humidity_pct, pressure_mbar, rainfall, wind_direction_deg, wind_speed_mps, metadata
            )
            VALUES (%s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s)
            ON CONFLICT (sample_time_utc, session_id) DO NOTHING
            """,
        weather_rows,
        args.batch_size,
    )
    execute_many(
        connection,
        """
            INSERT INTO track_status_events (session_id, event_time_ms, status_code, message, metadata)
            VALUES (%s, %s, %s, %s, %s)
            ON CONFLICT (session_id, event_time_ms, status_code) DO NOTHING
            """,
        track_status_rows,
        args.batch_size,
    )
    execute_many(
        connection,
        """
            INSERT INTO session_status_events (session_id, event_time_ms, status, metadata)
            VALUES (%s, %s, %s, %s)
            ON CONFLICT (session_id, event_time_ms, status) DO NOTHING
            """,
        session_status_rows,
        args.batch_size,
    )
    execute_many(
        connection,
        """
            INSERT INTO race_control_messages (
                session_id, message_time_utc, session_time_ms, category, message, status,
                flag, scope, sector, racing_number, lap_number, metadata
            )
            VALUES (%s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s)
            """,
        race_control_rows,
        args.batch_size,
    )


def cleanup_failed_parallel_import(connection: Any, session_id: str) -> None:
    try:
        with connection.transaction():
            delete_existing_session(connection, session_id)
    except Exception as exc:
        logging.error("Could not clean up failed import for %s: %s", session_id, exc)


def insert_import(connection: Any, args: argparse.Namespace, session: Any, session_id: str, driver_codes: list[str]) -> ImportSummary:
    start = time.perf_counter()

    laps = selected_laps(session, driver_codes, args.limit_laps)
    driver_refs = build_driver_refs(session, driver_codes)
    session_start_utc = session_start(session)

    session_row = build_session_row(session, args, session_id)
    driver_rows = build_driver_rows(session, session_id, driver_codes)
    lap_rows = build_lap_rows(laps, session_id)
    circuit_metadata_row, circuit_marker_rows = build_circuit_rows(session, session_id) if args.include_context else (None, [])
    weather_rows = build_weather_rows(session, session_id, session_start_utc) if args.include_context else []
    track_status_rows = build_track_status_rows(session, session_id) if args.include_context else []
    session_status_rows = build_session_status_rows(session, session_id) if args.include_context else []
    race_control_rows = build_race_control_rows(session, session_id, session_start_utc) if args.include_context else []

    logging.info(
        "Importing %s: drivers=%d laps=%d mode=%s batch_size=%d sample_write_method=%s parallel_sample_copy=%s",
        session_id,
        len(driver_rows),
        len(lap_rows),
        args.mode,
        args.batch_size,
        args.sample_write_method,
        args.parallel_sample_copy,
    )

    if args.parallel_sample_copy and args.sample_write_method == "copy":
        try:
            with connection.transaction():
                ensure_import_mode(connection, session_id, args.mode)
                if args.mode == "upsert":
                    clear_upsert_children(connection, session_id)
                insert_parent_rows(connection, args, session_id, session_row, driver_rows, lap_rows)

            telemetry_count, position_count, aligned_count, diagnostic_count, distance_count, quality_count = stream_sample_rows(
                connection,
                args,
                session,
                laps,
                session_id,
                driver_refs,
                driver_rows,
                lap_rows,
            )

            with connection.transaction():
                insert_context_rows(
                    connection,
                    args,
                    circuit_metadata_row,
                    circuit_marker_rows,
                    weather_rows,
                    track_status_rows,
                    session_status_rows,
                    race_control_rows,
                )
        except Exception:
            cleanup_failed_parallel_import(connection, session_id)
            raise
    else:
        with connection.transaction():
            ensure_import_mode(connection, session_id, args.mode)
            if args.mode == "upsert":
                clear_upsert_children(connection, session_id)
            insert_parent_rows(connection, args, session_id, session_row, driver_rows, lap_rows)
            telemetry_count, position_count, aligned_count, diagnostic_count, distance_count, quality_count = stream_sample_rows(
                connection,
                args,
                session,
                laps,
                session_id,
                driver_refs,
                driver_rows,
                lap_rows,
            )
            insert_context_rows(
                connection,
                args,
                circuit_metadata_row,
                circuit_marker_rows,
                weather_rows,
                track_status_rows,
                session_status_rows,
                race_control_rows,
            )

    return ImportSummary(
        session_id=session_id,
        mode=args.mode,
        drivers=len(driver_rows),
        laps=len(lap_rows),
        telemetry_samples=telemetry_count,
        position_samples=position_count,
        aligned_samples=aligned_count,
        telemetry_diagnostics=diagnostic_count,
        distance_alignment_rows=distance_count,
        lap_quality_rows=quality_count,
        circuit_markers=len(circuit_marker_rows),
        weather_samples=len(weather_rows),
        track_status_events=len(track_status_rows),
        session_status_events=len(session_status_rows),
        race_control_messages=len(race_control_rows),
        elapsed_seconds=time.perf_counter() - start,
    )


def print_summary(summary: ImportSummary) -> None:
    print("Import completed successfully.")
    print(f"Session ID: {summary.session_id}")
    print(f"Mode: {summary.mode}")
    print(f"Drivers: {summary.drivers}")
    print(f"Laps: {summary.laps}")
    print(f"Telemetry samples: {summary.telemetry_samples:,}")
    print(f"Position samples: {summary.position_samples:,}")
    print(f"Aligned 10Hz samples: {summary.aligned_samples:,}")
    print(f"Telemetry diagnostics: {summary.telemetry_diagnostics:,}")
    print(f"Distance-aligned samples: {summary.distance_alignment_rows:,}")
    print(f"Lap quality rows: {summary.lap_quality_rows:,}")
    print(f"Circuit markers: {summary.circuit_markers}")
    print(f"Weather samples: {summary.weather_samples}")
    print(f"Track status events: {summary.track_status_events}")
    print(f"Session status events: {summary.session_status_events}")
    print(f"Race-control messages: {summary.race_control_messages}")
    print(f"Elapsed: {timedelta(seconds=round(summary.elapsed_seconds))}")


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description="Import a cached FastF1 session into TimescaleDB.")
    parser.add_argument("--year", type=int, required=True, help="Championship year, for example 2024.")
    parser.add_argument("--event", required=True, help='Event accepted by FastF1, for example "Monza".')
    parser.add_argument(
        "--session",
        default="R",
        type=str.upper,
        choices=sorted(VALID_SESSIONS),
        help="Session identifier. Default: R.",
    )
    parser.add_argument("--drivers", dest="driver_filter", type=parse_driver_filter)
    parser.add_argument("--limit-laps", type=positive_int)
    parser.add_argument("--cache-dir", type=Path, default=DEFAULT_CACHE_DIR)
    parser.add_argument("--database-url", default=database_url())
    parser.add_argument("--batch-size", type=positive_int, default=DEFAULT_BATCH_SIZE)
    parser.add_argument(
        "--sample-write-method",
        choices=sorted(SAMPLE_WRITE_METHODS),
        default="copy",
        help="How to write telemetry and position sample rows. Default: copy.",
    )
    parser.add_argument(
        "--telemetry-workers",
        type=positive_int,
        default=DEFAULT_TELEMETRY_WORKERS,
        help=f"Worker threads for per-driver FastF1 sample extraction. Default: {DEFAULT_TELEMETRY_WORKERS}.",
    )
    parser.set_defaults(parallel_sample_copy=True)
    parser.add_argument(
        "--parallel-sample-copy",
        dest="parallel_sample_copy",
        action="store_true",
        help="Copy telemetry_samples and position_samples concurrently using separate database connections. Default.",
    )
    parser.add_argument(
        "--no-parallel-sample-copy",
        dest="parallel_sample_copy",
        action="store_false",
        help="Use the older single-connection sample COPY path.",
    )
    parser.add_argument("--mode", choices=["fail", "replace", "upsert"], default="fail")
    parser.add_argument("--skip-telemetry", dest="include_telemetry", action="store_false")
    parser.add_argument("--skip-position", dest="include_position", action="store_false")
    parser.set_defaults(include_aligned_telemetry=True)
    parser.add_argument(
        "--skip-aligned-telemetry",
        dest="include_aligned_telemetry",
        action="store_false",
        help="Do not materialize aligned_telemetry_10hz rows for UI replay.",
    )
    parser.add_argument(
        "--max-car-data-interpolation-gap-ms",
        type=positive_int,
        default=1000,
        help="Maximum raw car-telemetry interpolation gap in milliseconds for derived alignment tables. Default: 1000.",
    )
    parser.add_argument(
        "--max-position-interpolation-gap-ms",
        type=positive_int,
        default=1000,
        help="Maximum raw position interpolation gap in milliseconds for derived alignment tables. Default: 1000.",
    )
    parser.add_argument(
        "--max-source-age-ms",
        type=positive_int,
        default=750,
        help="Maximum acceptable source-sample age in milliseconds for derived alignment tables. Default: 750.",
    )
    parser.add_argument(
        "--distance-alignment-step-m",
        type=float,
        default=5.0,
        help="Distance step in metres for lap_telemetry_by_distance. Default: 5.0.",
    )
    parser.add_argument("--skip-context", dest="include_context", action="store_false")
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

    try:
        fastf1 = load_fastf1()
        session = load_session(fastf1, args)
        event = getattr(session, "event", None)
        official_event_name = get_event_value(event, "EventName") if event is not None else None
        session_id = build_session_id(args.year, official_event_name or args.event, args.session)
        driver_codes = select_driver_codes(session, args.driver_filter)

        psycopg = require_psycopg()
        with psycopg.connect(args.database_url, autocommit=False) as connection:
            summary = insert_import(connection, args, session, session_id, driver_codes)
            connection.commit()
    except Exception as exc:
        logging.error("%s", exc)
        return 1

    print_summary(summary)
    return 0


if __name__ == "__main__":
    sys.exit(main())
