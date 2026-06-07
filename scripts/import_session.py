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
import json
import logging
import os
import sys
import time
from concurrent.futures import ThreadPoolExecutor, as_completed
from dataclasses import dataclass
from datetime import UTC, datetime, timedelta
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


DEFAULT_DATABASE_URL = "postgresql://race_telemetry:race_telemetry@localhost:5432/race_telemetry"
DEFAULT_BATCH_SIZE = 100000
DEFAULT_TELEMETRY_WORKERS = 1
SAMPLE_WRITE_METHODS = {"copy", "insert"}
EMPTY_METADATA = "{}"

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


@dataclass(frozen=True)
class ImportSummary:
    session_id: str
    mode: str
    drivers: int
    laps: int
    telemetry_samples: int
    position_samples: int
    circuit_markers: int
    weather_samples: int
    track_status_events: int
    session_status_events: int
    race_control_messages: int
    elapsed_seconds: float


@dataclass(frozen=True)
class DriverLapWindow:
    lap_number: int
    start_ms: int
    end_ms: int


@dataclass(frozen=True)
class DriverSampleRows:
    driver_code: str
    lap_count: int
    telemetry_rows: list[tuple[Any, ...]]
    position_rows: list[tuple[Any, ...]]
    elapsed_seconds: float


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


def is_missing(value: Any) -> bool:
    try:
        return bool(value is None or value != value)
    except (TypeError, ValueError):
        return value is None


def clean_value(value: Any) -> Any:
    if is_missing(value):
        return None
    if hasattr(value, "item"):
        try:
            return value.item()
        except (TypeError, ValueError):
            pass
    return value


def int_or_none(value: Any) -> int | None:
    value = clean_value(value)
    return int(value) if value is not None else None


def float_or_none(value: Any) -> float | None:
    value = clean_value(value)
    return float(value) if value is not None else None


def str_or_none(value: Any) -> str | None:
    value = clean_value(value)
    if value is None:
        return None
    text = str(value)
    return text if text else None


def bool_or_none(value: Any) -> bool | None:
    value = clean_value(value)
    return bool(value) if value is not None else None


def timestamp_or_none(value: Any) -> datetime | None:
    value = clean_value(value)
    if value is None:
        return None
    if isinstance(value, str):
        try:
            value = datetime.fromisoformat(value.replace("Z", "+00:00"))
        except ValueError:
            return None
    if hasattr(value, "to_pydatetime"):
        value = value.to_pydatetime()
    if isinstance(value, datetime):
        if value.tzinfo is None:
            return value.replace(tzinfo=UTC)
        return value.astimezone(UTC)
    return None


def timedelta_to_ms(value: Any) -> int | None:
    value = clean_value(value)
    if value is None:
        return None
    if hasattr(value, "total_seconds"):
        return int(round(value.total_seconds() * 1000))
    return None


def brake_to_pct(value: Any) -> float | None:
    value = clean_value(value)
    if value is None:
        return None
    if isinstance(value, bool):
        return 100.0 if value else 0.0
    return percentage_or_none(value)


def percentage_or_none(value: Any) -> float | None:
    value = float_or_none(value)
    if value is None:
        return None
    return min(100.0, max(0.0, value))


def json_metadata(payload: dict[str, Any] | None = None) -> str:
    if not payload:
        return EMPTY_METADATA
    return json.dumps(payload)


def column_values(frame: Any, column: str) -> Any:
    if column not in frame:
        return [None] * len(frame)
    return frame[column].to_numpy(dtype=object, copy=False)


def batched(values: Sequence[tuple[Any, ...]], batch_size: int) -> Iterable[Sequence[tuple[Any, ...]]]:
    for start in range(0, len(values), batch_size):
        yield values[start : start + batch_size]


def execute_many(connection: Any, sql: str, rows: Sequence[tuple[Any, ...]], batch_size: int) -> int:
    if not rows:
        return 0
    with connection.cursor() as cursor:
        for batch in batched(rows, batch_size):
            cursor.executemany(sql, batch)
    return len(rows)


class BatchWriter:
    """Bounded insert buffer for streaming large sample tables."""

    def __init__(self, connection: Any, sql: str, table_name: str, batch_size: int) -> None:
        self.connection = connection
        self.sql = sql
        self.table_name = table_name
        self.batch_size = batch_size
        self.buffer: list[tuple[Any, ...]] = []
        self.total = 0

    def add_many(self, rows: Sequence[tuple[Any, ...]]) -> None:
        if not rows:
            return
        self.buffer.extend(rows)
        while len(self.buffer) >= self.batch_size:
            self.flush(self.batch_size)

    def flush(self, limit: int | None = None) -> None:
        if not self.buffer:
            return
        if limit is None:
            batch = self.buffer
            self.buffer = []
        else:
            batch = self.buffer[:limit]
            self.buffer = self.buffer[limit:]
        execute_many(self.connection, self.sql, batch, len(batch))
        self.total += len(batch)
        logging.info("Inserted %s rows: %s", self.table_name, f"{self.total:,}")


class CopyWriter:
    """Bounded COPY buffer for append-heavy sample tables.

    COPY avoids the per-row INSERT protocol cost that dominates full-session
    imports. The writer keeps a compact key set because FastF1 can occasionally
    emit duplicate timestamps for the same driver inside raw source feeds.
    """

    def __init__(
        self,
        connection: Any,
        table_name: str,
        columns: Sequence[str],
        batch_size: int,
        key_indexes: tuple[int, int, int],
    ) -> None:
        self.connection = connection
        self.table_name = table_name
        self.columns = columns
        self.batch_size = batch_size
        self.key_indexes = key_indexes
        self.buffer: list[tuple[Any, ...]] = []
        self.seen_keys: set[tuple[Any, Any, Any]] = set()
        self.total = 0
        self.duplicates = 0
        self.write_seconds = 0.0

    def add_many(self, rows: Sequence[tuple[Any, ...]]) -> None:
        if not rows:
            return
        for row in rows:
            key = tuple(row[index] for index in self.key_indexes)
            if key in self.seen_keys:
                self.duplicates += 1
                continue
            self.seen_keys.add(key)
            self.buffer.append(row)
        while len(self.buffer) >= self.batch_size:
            self.flush(self.batch_size)

    def flush(self, limit: int | None = None) -> None:
        if not self.buffer:
            return
        if limit is None:
            batch = self.buffer
            self.buffer = []
        else:
            batch = self.buffer[:limit]
            self.buffer = self.buffer[limit:]

        start = time.perf_counter()
        column_sql = ", ".join(self.columns)
        with self.connection.cursor() as cursor:
            with cursor.copy(f"COPY {self.table_name} ({column_sql}) FROM STDIN") as copy:
                for row in batch:
                    copy.write_row(row)
        elapsed = time.perf_counter() - start
        self.write_seconds += elapsed
        self.total += len(batch)
        logging.info(
            "Copied %s rows: %s (+%s in %.2fs)",
            self.table_name,
            f"{self.total:,}",
            f"{len(batch):,}",
            elapsed,
        )


def sample_writer(
    connection: Any,
    args: argparse.Namespace,
    table_name: str,
    insert_sql: str,
    columns: Sequence[str],
) -> BatchWriter | CopyWriter:
    if args.sample_write_method == "copy":
        return CopyWriter(connection, table_name, columns, args.batch_size, (0, 1, 2))
    return BatchWriter(connection, insert_sql, table_name, args.batch_size)


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
        json_metadata({"imported_by": "scripts/import_session.py"}),
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
                int_or_none(data.get("Lap")),
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


def copy_sample_rows(
    database_url: str,
    table_name: str,
    columns: Sequence[str],
    rows: Sequence[tuple[Any, ...]],
    batch_size: int,
) -> tuple[int, float, int]:
    psycopg = require_psycopg()
    with psycopg.connect(database_url, autocommit=False) as connection:
        writer = CopyWriter(connection, table_name, columns, batch_size, (0, 1, 2))
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
) -> tuple[int, int]:
    if args.parallel_sample_copy and args.sample_write_method == "copy":
        telemetry_rows, position_rows, extraction_seconds = collect_sample_rows(
            args,
            session,
            laps,
            session_id,
            driver_refs,
        )
        start = time.perf_counter()
        with ThreadPoolExecutor(max_workers=2) as executor:
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
            telemetry_count, telemetry_write_seconds, telemetry_duplicates = telemetry_future.result()
            position_count, position_write_seconds, position_duplicates = position_future.result()
        logging.info(
            "Parallel sample copy completed: telemetry=%s position=%s extraction_worker_seconds=%.2f write_seconds=%.2f wall_seconds=%.2f duplicates_skipped=%d",
            f"{telemetry_count:,}",
            f"{position_count:,}",
            extraction_seconds,
            telemetry_write_seconds + position_write_seconds,
            time.perf_counter() - start,
            telemetry_duplicates + position_duplicates,
        )
        return telemetry_count, position_count

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
        return 0, 0

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
    return telemetry_writer.total, position_writer.total


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
                compound, tyre_life, is_pit_out_lap, is_pit_in_lap, is_deleted,
                is_accurate, metadata
            )
            VALUES (%s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s)
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

            telemetry_count, position_count = stream_sample_rows(connection, args, session, laps, session_id, driver_refs)

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
            telemetry_count, position_count = stream_sample_rows(connection, args, session, laps, session_id, driver_refs)
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
