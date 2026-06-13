"""Reusable value conversion helpers for the FastF1 session importer."""

from __future__ import annotations

import json
from dataclasses import dataclass
from datetime import UTC, datetime, timedelta
from typing import Any

EMPTY_METADATA = "{}"


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
