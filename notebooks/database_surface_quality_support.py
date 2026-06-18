"""Support code for imported race-session database surface EDA.

This module audits the non-lap-specific data surfaces in the local
TimescaleDB import: session metadata, drivers, weather, status timelines, race
control, circuit annotations, ingestion diagnostics, raw telemetry coverage,
position coverage, and aligned 10 Hz replay data.
"""

from __future__ import annotations

import json
import os
import re
from dataclasses import dataclass
from pathlib import Path
from typing import Any


REPO_ROOT = Path(__file__).resolve().parents[1]
DEFAULT_YEAR = 2025
DEFAULT_SESSION_TYPE = "R"
EXPECTED_2025_RACE_SESSIONS = 24

ARTIFACT_DIR = REPO_ROOT / "artifacts" / "2025-race-database-surface-eda"
FIGURE_DIR = ARTIFACT_DIR / "figures"
TABLE_DIR = ARTIFACT_DIR / "tables"
SKRUB_DATA_DIR = ARTIFACT_DIR / "skrub-data"
MPLCONFIG_DIR = ARTIFACT_DIR / "matplotlib"
CACHE_DIR = ARTIFACT_DIR / "cache"

for path in (ARTIFACT_DIR, FIGURE_DIR, TABLE_DIR, SKRUB_DATA_DIR, MPLCONFIG_DIR, CACHE_DIR):
    path.mkdir(parents=True, exist_ok=True)

os.environ.setdefault("SKB_DATA_DIRECTORY", str(SKRUB_DATA_DIR))
os.environ.setdefault("MPLCONFIGDIR", str(MPLCONFIG_DIR))
os.environ.setdefault("XDG_CACHE_HOME", str(CACHE_DIR))
os.environ.setdefault("LOKY_MAX_CPU_COUNT", "4")
os.environ.setdefault("MPLBACKEND", "Agg")

import matplotlib.pyplot as plt
import numpy as np
import pandas as pd
import seaborn as sns
import skrub
from sqlalchemy import create_engine
from sqlalchemy import text


DEFAULT_DATABASE_URL = "postgresql://race_telemetry:race_telemetry@localhost:5432/race_telemetry"


@dataclass(frozen=True)
class SurfaceThresholds:
    """Tunable thresholds for session-level surface quality checks."""

    expected_min_drivers: int = 15
    min_weather_samples: int = 2
    weather_max_gap_ms: int = 180_000
    min_corner_markers: int = 5
    max_car_median_delta_ms: float = 400.0
    max_position_median_delta_ms: float = 400.0
    min_raw_driver_streams: int = 10
    max_aligned_non_ok_pct: float = 5.0
    degraded_window_min_non_ok_pct: float = 1.0
    severe_window_min_non_ok_pct: float = 10.0


@dataclass(frozen=True)
class SurfaceScope:
    """Primary analysis scope for database-surface EDA outputs."""

    year: int = DEFAULT_YEAR
    session_type: str = DEFAULT_SESSION_TYPE
    expected_sessions: int = EXPECTED_2025_RACE_SESSIONS

    @property
    def label(self) -> str:
        return f"{self.year} race sessions"

    @property
    def sql_params(self) -> dict[str, Any]:
        return {"year": self.year, "session_type": self.session_type}


SURFACE_FLAG_COLUMNS = [
    "session_metadata_incomplete",
    "driver_metadata_sparse",
    "lap_metadata_incomplete",
    "raw_telemetry_coverage_issue",
    "raw_position_coverage_issue",
    "aligned_replay_quality_issue",
    "ingestion_diagnostic_warning",
    "weather_surface_issue",
    "status_timeline_sparse",
    "race_control_sparse_or_untimed",
    "circuit_annotation_issue",
]

QUALITY_FAMILY_ORDER = [
    "OK",
    "car_gap_too_large",
    "car_sample_too_old",
    "location_gap_too_large",
    "location_sample_too_old",
    "other_unknown",
]

QUALITY_FAMILY_COLORS = {
    "OK": "#D1D5DB",
    "car_gap_too_large": "#B42318",
    "car_sample_too_old": "#E11D48",
    "location_gap_too_large": "#7C3AED",
    "location_sample_too_old": "#2563EB",
    "other_unknown": "#525252",
}

READINESS_LABEL_ORDER = [
    "ready",
    "ready_with_warnings",
    "partial",
    "needs_manual_review",
    "needs_reimport",
]

READINESS_SCORE = {
    "ready": 0,
    "ready_with_warnings": 1,
    "partial": 2,
    "needs_manual_review": 3,
    "needs_reimport": 4,
}

READINESS_COLORS = {
    "ready": "#047857",
    "ready_with_warnings": "#F59E0B",
    "partial": "#F97316",
    "needs_manual_review": "#7C3AED",
    "needs_reimport": "#B42318",
}

QUALITY_FAMILY_SQL = """
case
    when 'CAR_GAP_TOO_LARGE' = any(a.quality_flags) then 'car_gap_too_large'
    when 'CAR_SAMPLE_TOO_OLD' = any(a.quality_flags) then 'car_sample_too_old'
    when 'LOCATION_GAP_TOO_LARGE' = any(a.quality_flags) then 'location_gap_too_large'
    when 'LOCATION_SAMPLE_TOO_OLD' = any(a.quality_flags) then 'location_sample_too_old'
    when 'OK' = any(a.quality_flags) then 'OK'
    else 'other_unknown'
end
"""


def database_url() -> str:
    return os.environ.get("RACE_TELEMETRY_DATABASE_URL", DEFAULT_DATABASE_URL)


def sqlalchemy_url(url: str | None = None) -> str:
    resolved = url or database_url()
    if resolved.startswith("postgresql://"):
        return resolved.replace("postgresql://", "postgresql+psycopg://", 1)
    return resolved


def engine(url: str | None = None):
    return create_engine(sqlalchemy_url(url), pool_pre_ping=True)


def load_session_surface_features(url: str | None = None, scope: SurfaceScope = SurfaceScope()) -> pd.DataFrame:
    """Build one row per imported race session with surface completeness metrics."""

    sql = """
    with race_sessions as (
        select
            dense_rank() over (order by year, session_start_utc nulls last, event_name) as import_round,
            session_id,
            year,
            event_name,
            circuit_name,
            country,
            session_type,
            session_start_utc,
            session_end_utc,
            source,
            imported_at_utc
        from sessions
        where year = :year
          and session_type = :session_type
    ),
    drivers as (
        select
            sd.session_id,
            count(*) as driver_count,
            count(*) filter (where sd.driver_number is null) as missing_driver_number,
            count(*) filter (where sd.full_name is null or sd.full_name = '') as missing_full_name,
            count(*) filter (where sd.team_name is null or sd.team_name = '') as missing_team_name
        from session_drivers sd
        join race_sessions rs using (session_id)
        group by sd.session_id
    ),
    laps as (
        select
            l.session_id,
            count(*) as lap_rows,
            count(distinct l.driver_code) as lap_driver_count,
            count(*) filter (where l.lap_time_ms is null) as missing_lap_time_rows,
            count(*) filter (where l.sector_1_ms is null or l.sector_2_ms is null or l.sector_3_ms is null) as missing_sector_rows,
            count(*) filter (where l.is_accurate = false) as inaccurate_lap_rows,
            count(*) filter (where l.is_deleted) as deleted_lap_rows,
            count(*) filter (where l.is_pit_in_lap or l.is_pit_out_lap) as pit_lap_rows,
            max(l.lap_number) as max_lap_number
        from laps l
        join race_sessions rs using (session_id)
        group by l.session_id
    ),
    telemetry as (
        select
            t.session_id,
            count(*) as telemetry_samples,
            count(distinct t.driver_code) as telemetry_driver_count,
            min(t.sample_time_utc) as telemetry_start_utc,
            max(t.sample_time_utc) as telemetry_end_utc,
            count(*) filter (where t.session_time_ms is null) as telemetry_session_time_nulls,
            count(*) filter (where t.lap_time_ms is null) as telemetry_lap_time_nulls,
            count(*) filter (where t.speed_kmh is null) as telemetry_speed_nulls,
            count(*) filter (where t.rpm is null) as telemetry_rpm_nulls
        from telemetry_samples t
        join race_sessions rs using (session_id)
        group by t.session_id
    ),
    position as (
        select
            p.session_id,
            count(*) as position_samples,
            count(distinct p.driver_code) as position_driver_count,
            min(p.sample_time_utc) as position_start_utc,
            max(p.sample_time_utc) as position_end_utc,
            count(*) filter (where p.x is null or p.y is null) as position_xy_nulls,
            count(distinct p.track_status) filter (where p.track_status is not null) as position_track_status_values
        from position_samples p
        join race_sessions rs using (session_id)
        group by p.session_id
    ),
    aligned as (
        select
            a.session_id,
            count(*) as aligned_samples,
            count(distinct a.driver_code) as aligned_driver_count,
            min(a.sample_time_utc) as aligned_start_utc,
            max(a.sample_time_utc) as aligned_end_utc,
            count(*) filter (where not ('OK' = any(a.quality_flags))) as aligned_non_ok_rows,
            count(*) filter (where a.is_interpolated_car) as aligned_interpolated_car_rows,
            count(*) filter (where a.is_interpolated_location) as aligned_interpolated_location_rows
        from aligned_telemetry_10hz a
        join race_sessions rs using (session_id)
        group by a.session_id
    ),
    weather as (
        select
            w.session_id,
            count(*) as weather_samples,
            min(w.session_time_ms) as weather_start_ms,
            max(w.session_time_ms) as weather_end_ms,
            count(*) filter (where w.air_temp_c is null) as weather_air_temp_nulls,
            count(*) filter (where w.track_temp_c is null) as weather_track_temp_nulls,
            count(*) filter (where w.humidity_pct is null) as weather_humidity_nulls,
            count(*) filter (where w.pressure_mbar is null) as weather_pressure_nulls,
            count(*) filter (where w.rainfall is null) as weather_rainfall_nulls,
            count(*) filter (where w.rainfall) as weather_rainfall_true_samples,
            min(w.air_temp_c) as min_air_temp_c,
            max(w.air_temp_c) as max_air_temp_c,
            min(w.track_temp_c) as min_track_temp_c,
            max(w.track_temp_c) as max_track_temp_c,
            min(w.pressure_mbar) as min_pressure_mbar,
            max(w.pressure_mbar) as max_pressure_mbar,
            max(w.session_time_ms - prev_session_time_ms) as weather_max_gap_ms
        from (
            select
                w.*,
                lag(w.session_time_ms) over (
                    partition by w.session_id order by w.session_time_ms
                ) as prev_session_time_ms
            from weather_samples w
            join race_sessions rs using (session_id)
        ) w
        group by w.session_id
    ),
    track_status as (
        select
            tse.session_id,
            count(*) as track_status_events,
            count(distinct tse.status_code) as track_status_code_count,
            count(*) filter (where tse.status_code = '2') as yellow_status_events,
            count(*) filter (where tse.status_code = '4') as safety_car_status_events,
            count(*) filter (where tse.status_code in ('6', '7')) as vsc_status_events,
            count(*) filter (where tse.status_code = '5') as red_flag_status_events
        from track_status_events tse
        join race_sessions rs using (session_id)
        group by tse.session_id
    ),
    session_status as (
        select
            sse.session_id,
            count(*) as session_status_events,
            count(distinct sse.status) as session_status_value_count
        from session_status_events sse
        join race_sessions rs using (session_id)
        group by sse.session_id
    ),
    race_control as (
        select
            rcm.session_id,
            count(*) as race_control_messages,
            count(*) filter (where rcm.session_time_ms is null) as race_control_missing_session_time,
            count(*) filter (where rcm.message_time_utc is null) as race_control_missing_message_time,
            count(*) filter (where rcm.category is null) as race_control_missing_category,
            count(*) filter (where rcm.racing_number is not null) as race_control_driver_scoped,
            count(*) filter (where rcm.lap_number is not null) as race_control_lap_scoped,
            count(distinct rcm.category) filter (where rcm.category is not null) as race_control_category_count
        from race_control_messages rcm
        join race_sessions rs using (session_id)
        group by rcm.session_id
    ),
    circuit as (
        select
            rs.session_id,
            count(cm.session_id) as circuit_metadata_rows,
            count(cm.session_id) filter (where cm.rotation_degrees is null) as circuit_rotation_nulls,
            count(cmk.*) as circuit_markers,
            count(cmk.*) filter (where cmk.marker_type = 'corner') as corner_markers,
            count(cmk.*) filter (where cmk.marker_type = 'marshal_light') as marshal_light_markers,
            count(cmk.*) filter (where cmk.marker_type = 'marshal_sector') as marshal_sector_markers,
            count(cmk.*) filter (where cmk.distance_m is null) as marker_distance_nulls
        from race_sessions rs
        left join circuit_metadata cm using (session_id)
        left join circuit_markers cmk using (session_id)
        group by rs.session_id
    )
    select
        rs.*,
        coalesce(d.driver_count, 0) as driver_count,
        coalesce(d.missing_driver_number, 0) as missing_driver_number,
        coalesce(d.missing_full_name, 0) as missing_full_name,
        coalesce(d.missing_team_name, 0) as missing_team_name,
        coalesce(l.lap_rows, 0) as lap_rows,
        coalesce(l.lap_driver_count, 0) as lap_driver_count,
        coalesce(l.missing_lap_time_rows, 0) as missing_lap_time_rows,
        coalesce(l.missing_sector_rows, 0) as missing_sector_rows,
        coalesce(l.inaccurate_lap_rows, 0) as inaccurate_lap_rows,
        coalesce(l.deleted_lap_rows, 0) as deleted_lap_rows,
        coalesce(l.pit_lap_rows, 0) as pit_lap_rows,
        l.max_lap_number,
        coalesce(t.telemetry_samples, 0) as telemetry_samples,
        coalesce(t.telemetry_driver_count, 0) as telemetry_driver_count,
        t.telemetry_start_utc,
        t.telemetry_end_utc,
        coalesce(t.telemetry_session_time_nulls, 0) as telemetry_session_time_nulls,
        coalesce(t.telemetry_lap_time_nulls, 0) as telemetry_lap_time_nulls,
        coalesce(t.telemetry_speed_nulls, 0) as telemetry_speed_nulls,
        coalesce(t.telemetry_rpm_nulls, 0) as telemetry_rpm_nulls,
        coalesce(p.position_samples, 0) as position_samples,
        coalesce(p.position_driver_count, 0) as position_driver_count,
        p.position_start_utc,
        p.position_end_utc,
        coalesce(p.position_xy_nulls, 0) as position_xy_nulls,
        coalesce(p.position_track_status_values, 0) as position_track_status_values,
        coalesce(a.aligned_samples, 0) as aligned_samples,
        coalesce(a.aligned_driver_count, 0) as aligned_driver_count,
        a.aligned_start_utc,
        a.aligned_end_utc,
        coalesce(a.aligned_non_ok_rows, 0) as aligned_non_ok_rows,
        coalesce(a.aligned_interpolated_car_rows, 0) as aligned_interpolated_car_rows,
        coalesce(a.aligned_interpolated_location_rows, 0) as aligned_interpolated_location_rows,
        coalesce(w.weather_samples, 0) as weather_samples,
        w.weather_start_ms,
        w.weather_end_ms,
        coalesce(w.weather_air_temp_nulls, 0) as weather_air_temp_nulls,
        coalesce(w.weather_track_temp_nulls, 0) as weather_track_temp_nulls,
        coalesce(w.weather_humidity_nulls, 0) as weather_humidity_nulls,
        coalesce(w.weather_pressure_nulls, 0) as weather_pressure_nulls,
        coalesce(w.weather_rainfall_nulls, 0) as weather_rainfall_nulls,
        coalesce(w.weather_rainfall_true_samples, 0) as weather_rainfall_true_samples,
        w.min_air_temp_c,
        w.max_air_temp_c,
        w.min_track_temp_c,
        w.max_track_temp_c,
        w.min_pressure_mbar,
        w.max_pressure_mbar,
        w.weather_max_gap_ms,
        coalesce(ts.track_status_events, 0) as track_status_events,
        coalesce(ts.track_status_code_count, 0) as track_status_code_count,
        coalesce(ts.yellow_status_events, 0) as yellow_status_events,
        coalesce(ts.safety_car_status_events, 0) as safety_car_status_events,
        coalesce(ts.vsc_status_events, 0) as vsc_status_events,
        coalesce(ts.red_flag_status_events, 0) as red_flag_status_events,
        coalesce(ss.session_status_events, 0) as session_status_events,
        coalesce(ss.session_status_value_count, 0) as session_status_value_count,
        coalesce(rc.race_control_messages, 0) as race_control_messages,
        coalesce(rc.race_control_missing_session_time, 0) as race_control_missing_session_time,
        coalesce(rc.race_control_missing_message_time, 0) as race_control_missing_message_time,
        coalesce(rc.race_control_missing_category, 0) as race_control_missing_category,
        coalesce(rc.race_control_driver_scoped, 0) as race_control_driver_scoped,
        coalesce(rc.race_control_lap_scoped, 0) as race_control_lap_scoped,
        coalesce(rc.race_control_category_count, 0) as race_control_category_count,
        coalesce(c.circuit_metadata_rows, 0) as circuit_metadata_rows,
        coalesce(c.circuit_rotation_nulls, 0) as circuit_rotation_nulls,
        coalesce(c.circuit_markers, 0) as circuit_markers,
        coalesce(c.corner_markers, 0) as corner_markers,
        coalesce(c.marshal_light_markers, 0) as marshal_light_markers,
        coalesce(c.marshal_sector_markers, 0) as marshal_sector_markers,
        coalesce(c.marker_distance_nulls, 0) as marker_distance_nulls
    from race_sessions rs
    left join drivers d using (session_id)
    left join laps l using (session_id)
    left join telemetry t using (session_id)
    left join position p using (session_id)
    left join aligned a using (session_id)
    left join weather w using (session_id)
    left join track_status ts using (session_id)
    left join session_status ss using (session_id)
    left join race_control rc using (session_id)
    left join circuit c using (session_id)
    order by rs.year, rs.session_start_utc nulls last, rs.event_name;
    """
    with engine(url).connect() as connection:
        return pd.read_sql_query(text(sql), connection, params=scope.sql_params)


def load_ingestion_diagnostics(url: str | None = None, scope: SurfaceScope = SurfaceScope()) -> pd.DataFrame:
    sql = """
    select
        tid.session_id,
        s.year,
        s.event_name,
        tid.driver_code,
        tid.driver_number,
        tid.stream_name,
        tid.sample_count,
        tid.min_delta_ms,
        tid.median_delta_ms,
        tid.p90_delta_ms,
        tid.p99_delta_ms,
        tid.max_delta_ms,
        tid.estimated_frequency_hz,
        tid.duplicate_count,
        tid.out_of_order_count,
        array_to_string(tid.warning_flags, ',') as warning_flags
    from telemetry_ingestion_diagnostics tid
    join sessions s using (session_id)
    where s.year = :year
      and s.session_type = :session_type
    order by s.year, s.session_start_utc nulls last, s.event_name, tid.driver_code, tid.stream_name;
    """
    with engine(url).connect() as connection:
        return pd.read_sql_query(text(sql), connection, params=scope.sql_params)


def load_aligned_quality_flags(url: str | None = None, scope: SurfaceScope = SurfaceScope()) -> pd.DataFrame:
    sql = """
    select
        a.session_id,
        s.year,
        s.event_name,
        flag as quality_flag,
        count(*) as rows
    from aligned_telemetry_10hz a
    join sessions s using (session_id)
    cross join lateral unnest(a.quality_flags) as flag
    where s.year = :year
      and s.session_type = :session_type
    group by a.session_id, s.year, s.event_name, flag
    order by s.year, s.event_name, rows desc;
    """
    with engine(url).connect() as connection:
        return pd.read_sql_query(text(sql), connection, params=scope.sql_params)


def load_aligned_quality_by_race(url: str | None = None, scope: SurfaceScope = SurfaceScope()) -> pd.DataFrame:
    sql = f"""
    with aligned_rows as (
        select
            a.session_id,
            s.year,
            s.event_name,
            {QUALITY_FAMILY_SQL} as quality_family,
            not ('OK' = any(a.quality_flags)) as is_non_ok,
            ('CAR_GAP_TOO_LARGE' = any(a.quality_flags) or 'CAR_SAMPLE_TOO_OLD' = any(a.quality_flags)) as is_car_related,
            ('LOCATION_GAP_TOO_LARGE' = any(a.quality_flags) or 'LOCATION_SAMPLE_TOO_OLD' = any(a.quality_flags)) as is_location_related
        from aligned_telemetry_10hz a
        join sessions s using (session_id)
        where s.year = :year
          and s.session_type = :session_type
    )
    select
        session_id,
        year,
        event_name,
        count(*) as aligned_rows,
        count(*) filter (where is_non_ok) as non_ok_rows,
        count(*) filter (where quality_family = 'car_gap_too_large') as car_gap_rows,
        count(*) filter (where quality_family = 'car_sample_too_old') as car_sample_old_rows,
        count(*) filter (where quality_family = 'location_gap_too_large') as location_gap_rows,
        count(*) filter (where quality_family = 'location_sample_too_old') as location_sample_old_rows,
        count(*) filter (where quality_family = 'other_unknown') as other_unknown_rows,
        count(*) filter (where is_car_related) as car_related_rows,
        count(*) filter (where is_location_related) as location_related_rows,
        count(distinct quality_family) filter (where is_non_ok) as non_ok_family_count
    from aligned_rows
    group by session_id, year, event_name
    order by non_ok_rows desc, event_name;
    """
    with engine(url).connect() as connection:
        df = pd.read_sql_query(text(sql), connection, params=scope.sql_params)
    df["non_ok_pct"] = df["non_ok_rows"] / df["aligned_rows"].replace({0: np.nan}) * 100
    df["car_related_pct_of_non_ok"] = df["car_related_rows"] / df["non_ok_rows"].replace({0: np.nan}) * 100
    df["location_related_pct_of_non_ok"] = df["location_related_rows"] / df["non_ok_rows"].replace({0: np.nan}) * 100
    return df


def load_aligned_quality_by_driver(url: str | None = None, scope: SurfaceScope = SurfaceScope()) -> pd.DataFrame:
    sql = f"""
    with aligned_rows as (
        select
            a.session_id,
            s.year,
            s.event_name,
            a.driver_code,
            {QUALITY_FAMILY_SQL} as quality_family,
            not ('OK' = any(a.quality_flags)) as is_non_ok,
            ('CAR_GAP_TOO_LARGE' = any(a.quality_flags) or 'CAR_SAMPLE_TOO_OLD' = any(a.quality_flags)) as is_car_related,
            ('LOCATION_GAP_TOO_LARGE' = any(a.quality_flags) or 'LOCATION_SAMPLE_TOO_OLD' = any(a.quality_flags)) as is_location_related
        from aligned_telemetry_10hz a
        join sessions s using (session_id)
        where s.year = :year
          and s.session_type = :session_type
          and a.driver_code is not null
    )
    select
        session_id,
        year,
        event_name,
        driver_code,
        count(*) as aligned_rows,
        count(*) filter (where is_non_ok) as non_ok_rows,
        count(*) filter (where quality_family = 'car_gap_too_large') as car_gap_rows,
        count(*) filter (where quality_family = 'car_sample_too_old') as car_sample_old_rows,
        count(*) filter (where quality_family = 'location_gap_too_large') as location_gap_rows,
        count(*) filter (where quality_family = 'location_sample_too_old') as location_sample_old_rows,
        count(*) filter (where quality_family = 'other_unknown') as other_unknown_rows,
        count(*) filter (where is_car_related) as car_related_rows,
        count(*) filter (where is_location_related) as location_related_rows
    from aligned_rows
    group by session_id, year, event_name, driver_code
    order by non_ok_rows desc, event_name, driver_code;
    """
    with engine(url).connect() as connection:
        df = pd.read_sql_query(text(sql), connection, params=scope.sql_params)
    df["non_ok_pct"] = df["non_ok_rows"] / df["aligned_rows"].replace({0: np.nan}) * 100
    return df


def load_aligned_quality_by_lap(url: str | None = None, scope: SurfaceScope = SurfaceScope()) -> pd.DataFrame:
    sql = f"""
    with aligned_rows as (
        select
            a.session_id,
            s.year,
            s.event_name,
            a.driver_code,
            a.lap_number,
            {QUALITY_FAMILY_SQL} as quality_family,
            not ('OK' = any(a.quality_flags)) as is_non_ok
        from aligned_telemetry_10hz a
        join sessions s using (session_id)
        where s.year = :year
          and s.session_type = :session_type
          and a.driver_code is not null
          and a.lap_number is not null
    ),
    lap_quality as (
        select
            session_id,
            year,
            event_name,
            driver_code,
            lap_number,
            count(*) as aligned_rows,
            count(*) filter (where is_non_ok) as non_ok_rows,
            count(*) filter (where quality_family = 'car_gap_too_large') as car_gap_rows,
            count(*) filter (where quality_family = 'car_sample_too_old') as car_sample_old_rows,
            count(*) filter (where quality_family = 'location_gap_too_large') as location_gap_rows,
            count(*) filter (where quality_family = 'location_sample_too_old') as location_sample_old_rows,
            count(*) filter (where quality_family = 'other_unknown') as other_unknown_rows
        from aligned_rows
        group by session_id, year, event_name, driver_code, lap_number
    )
    select
        lq.*,
        l.lap_time_ms,
        l.is_pit_in_lap,
        l.is_pit_out_lap,
        l.is_accurate,
        l.is_deleted,
        (l.lap_time_ms is null) as missing_lap_time,
        (l.is_pit_in_lap or l.is_pit_out_lap) as is_pit_lap,
        coalesce(l.is_accurate = false, false) as is_fastf1_inaccurate
    from lap_quality lq
    left join laps l
      on l.session_id = lq.session_id
     and l.driver_code = lq.driver_code
     and l.lap_number = lq.lap_number
    order by non_ok_rows desc, event_name, driver_code, lap_number;
    """
    with engine(url).connect() as connection:
        df = pd.read_sql_query(text(sql), connection, params=scope.sql_params)
    df["non_ok_pct"] = df["non_ok_rows"] / df["aligned_rows"].replace({0: np.nan}) * 100
    return df


def load_aligned_degraded_segments(url: str | None = None, scope: SurfaceScope = SurfaceScope()) -> pd.DataFrame:
    sql = f"""
    with non_ok_rows as (
        select
            a.session_id,
            s.year,
            s.event_name,
            a.driver_code,
            a.session_time_ms,
            a.lap_number,
            {QUALITY_FAMILY_SQL} as quality_family,
            case
                when lag(a.session_time_ms) over (
                    partition by a.session_id, a.driver_code order by a.session_time_ms
                ) is null then 1
                when a.session_time_ms - lag(a.session_time_ms) over (
                    partition by a.session_id, a.driver_code order by a.session_time_ms
                ) > 150 then 1
                else 0
            end as starts_segment
        from aligned_telemetry_10hz a
        join sessions s using (session_id)
        where s.year = :year
          and s.session_type = :session_type
          and a.driver_code is not null
          and a.session_time_ms is not null
          and not ('OK' = any(a.quality_flags))
    ),
    grouped as (
        select
            *,
            sum(starts_segment) over (
                partition by session_id, driver_code order by session_time_ms rows unbounded preceding
            ) as segment_id
        from non_ok_rows
    ),
    segments as (
        select
            session_id,
            year,
            event_name,
            driver_code,
            segment_id,
            min(session_time_ms) as start_ms,
            max(session_time_ms) as end_ms,
            count(*) as rows,
            min(lap_number) as start_lap,
            max(lap_number) as end_lap,
            count(*) filter (where quality_family = 'car_gap_too_large') as car_gap_rows,
            count(*) filter (where quality_family = 'car_sample_too_old') as car_sample_old_rows,
            count(*) filter (where quality_family = 'location_gap_too_large') as location_gap_rows,
            count(*) filter (where quality_family = 'location_sample_too_old') as location_sample_old_rows,
            count(*) filter (where quality_family = 'other_unknown') as other_unknown_rows
        from grouped
        group by session_id, year, event_name, driver_code, segment_id
    )
    select
        *,
        greatest(end_ms - start_ms + 100, 100) as duration_ms
    from segments
    order by duration_ms desc, rows desc, event_name, driver_code;
    """
    with engine(url).connect() as connection:
        df = pd.read_sql_query(text(sql), connection, params=scope.sql_params)
    family_cols = [
        "car_gap_rows",
        "car_sample_old_rows",
        "location_gap_rows",
        "location_sample_old_rows",
        "other_unknown_rows",
    ]
    if not df.empty:
        family_names = {
            "car_gap_rows": "car_gap_too_large",
            "car_sample_old_rows": "car_sample_too_old",
            "location_gap_rows": "location_gap_too_large",
            "location_sample_old_rows": "location_sample_too_old",
            "other_unknown_rows": "other_unknown",
        }
        df["dominant_family"] = df[family_cols].idxmax(axis=1).map(family_names)
    else:
        df["dominant_family"] = pd.Series(dtype="object")
    return df


def load_aligned_quality_windows(
    url: str | None = None,
    scope: SurfaceScope = SurfaceScope(),
    window_ms: int = 30_000,
) -> pd.DataFrame:
    params = {**scope.sql_params, "window_ms": window_ms}
    sql = f"""
    with aligned_rows as (
        select
            a.session_id,
            s.year,
            s.event_name,
            a.driver_code,
            floor(a.session_time_ms::numeric / :window_ms)::bigint * :window_ms as window_start_ms,
            {QUALITY_FAMILY_SQL} as quality_family,
            not ('OK' = any(a.quality_flags)) as is_non_ok,
            coalesce(l.is_pit_in_lap or l.is_pit_out_lap, false) as is_pit_lap
        from aligned_telemetry_10hz a
        join sessions s using (session_id)
        left join laps l
          on l.session_id = a.session_id
         and l.driver_code = a.driver_code
         and l.lap_number = a.lap_number
        where s.year = :year
          and s.session_type = :session_type
          and a.driver_code is not null
          and a.session_time_ms is not null
    ),
    windows as (
        select
            session_id,
            year,
            event_name,
            driver_code,
            window_start_ms,
            window_start_ms + :window_ms as window_end_ms,
            count(*) as aligned_rows,
            count(*) filter (where is_non_ok) as non_ok_rows,
            count(*) filter (where quality_family = 'car_gap_too_large') as car_gap_rows,
            count(*) filter (where quality_family = 'car_sample_too_old') as car_sample_old_rows,
            count(*) filter (where quality_family = 'location_gap_too_large') as location_gap_rows,
            count(*) filter (where quality_family = 'location_sample_too_old') as location_sample_old_rows,
            count(*) filter (where quality_family = 'other_unknown') as other_unknown_rows,
            count(*) filter (where is_pit_lap) as pit_lap_rows
        from aligned_rows
        group by session_id, year, event_name, driver_code, window_start_ms
    ),
    status_intervals as (
        select
            tse.session_id,
            tse.event_time_ms as start_ms,
            lead(tse.event_time_ms, 1, 9223372036854775807) over (
                partition by tse.session_id order by tse.event_time_ms
            ) as end_ms,
            tse.status_code
        from track_status_events tse
        join sessions s using (session_id)
        where s.year = :year
          and s.session_type = :session_type
    ),
    window_context as (
        select
            w.session_id,
            w.driver_code,
            w.window_start_ms,
            count(distinct rcm.race_control_message_id) filter (
                where rcm.session_time_ms >= w.window_start_ms
                  and rcm.session_time_ms < w.window_end_ms
            ) as race_control_messages,
            count(distinct rcm.race_control_message_id) filter (
                where rcm.session_time_ms >= w.window_start_ms
                  and rcm.session_time_ms < w.window_end_ms
                  and lower(coalesce(rcm.category, '') || ' ' || coalesce(rcm.message, '')) similar to '%(flag|safety|vsc|virtual|red|yellow|incident|investigat|penalt)%'
            ) as incident_like_messages,
            bool_or(si.status_code = '2') as has_yellow,
            bool_or(si.status_code = '4') as has_safety_car,
            bool_or(si.status_code = '5') as has_red_flag,
            bool_or(si.status_code in ('6', '7')) as has_vsc
        from windows w
        left join race_control_messages rcm
          on rcm.session_id = w.session_id
         and rcm.session_time_ms >= w.window_start_ms
         and rcm.session_time_ms < w.window_end_ms
        left join status_intervals si
          on si.session_id = w.session_id
         and si.start_ms < w.window_end_ms
         and si.end_ms > w.window_start_ms
        group by w.session_id, w.driver_code, w.window_start_ms
    )
    select
        w.*,
        coalesce(wc.race_control_messages, 0) as race_control_messages,
        coalesce(wc.incident_like_messages, 0) as incident_like_messages,
        coalesce(wc.has_yellow, false) as has_yellow,
        coalesce(wc.has_safety_car, false) as has_safety_car,
        coalesce(wc.has_red_flag, false) as has_red_flag,
        coalesce(wc.has_vsc, false) as has_vsc
    from windows w
    left join window_context wc
      on wc.session_id = w.session_id
     and wc.driver_code = w.driver_code
     and wc.window_start_ms = w.window_start_ms
    order by w.event_name, w.driver_code, w.window_start_ms;
    """
    with engine(url).connect() as connection:
        df = pd.read_sql_query(text(sql), connection, params=params)
    df["non_ok_pct"] = df["non_ok_rows"] / df["aligned_rows"].replace({0: np.nan}) * 100
    df["has_status_context"] = df[["has_yellow", "has_safety_car", "has_red_flag", "has_vsc"]].any(axis=1)
    df["has_race_control_context"] = df["race_control_messages"] > 0
    df["has_pit_context"] = df["pit_lap_rows"] > 0
    df["has_incident_context"] = df["has_status_context"] | (df["incident_like_messages"] > 0)
    return df


def load_session_duration_coverage(url: str | None = None, scope: SurfaceScope = SurfaceScope()) -> pd.DataFrame:
    """Derive session duration and per-surface start/end windows for 2025 races."""

    sql = """
    with race_sessions as (
        select
            session_id,
            year,
            event_name,
            session_type,
            session_start_utc,
            session_end_utc
        from sessions
        where year = :year
          and session_type = :session_type
    ),
    telemetry as (
        select
            t.session_id,
            count(*) as telemetry_samples,
            min(t.session_time_ms) filter (where t.session_time_ms is not null) as telemetry_start_ms,
            max(t.session_time_ms) filter (where t.session_time_ms is not null) as telemetry_end_ms
        from telemetry_samples t
        join race_sessions rs using (session_id)
        group by t.session_id
    ),
    position as (
        select
            p.session_id,
            count(*) as position_samples,
            min((extract(epoch from (p.sample_time_utc - rs.session_start_utc)) * 1000)::bigint) filter (
                where rs.session_start_utc is not null
            ) as position_start_ms,
            max((extract(epoch from (p.sample_time_utc - rs.session_start_utc)) * 1000)::bigint) filter (
                where rs.session_start_utc is not null
            ) as position_end_ms
        from position_samples p
        join race_sessions rs using (session_id)
        group by p.session_id
    ),
    aligned as (
        select
            a.session_id,
            count(*) as aligned_samples,
            min(a.session_time_ms) filter (where a.session_time_ms is not null) as aligned_start_ms,
            max(a.session_time_ms) filter (where a.session_time_ms is not null) as aligned_end_ms
        from aligned_telemetry_10hz a
        join race_sessions rs using (session_id)
        group by a.session_id
    ),
    weather as (
        select
            w.session_id,
            count(*) as weather_samples,
            min(w.session_time_ms) as weather_start_ms,
            max(w.session_time_ms) as weather_end_ms
        from weather_samples w
        join race_sessions rs using (session_id)
        group by w.session_id
    ),
    track_status as (
        select
            tse.session_id,
            count(*) as track_status_events,
            min(tse.event_time_ms) as track_status_start_ms,
            max(tse.event_time_ms) as track_status_end_ms
        from track_status_events tse
        join race_sessions rs using (session_id)
        group by tse.session_id
    ),
    session_status as (
        select
            sse.session_id,
            count(*) as session_status_events,
            min(sse.event_time_ms) as session_status_start_ms,
            max(sse.event_time_ms) as session_status_end_ms,
            max(sse.event_time_ms) filter (
                where lower(sse.status) in ('finished', 'finalised', 'ends')
            ) as session_status_finished_ms
        from session_status_events sse
        join race_sessions rs using (session_id)
        group by sse.session_id
    ),
    race_control as (
        select
            rcm.session_id,
            count(*) as race_control_messages,
            min(rcm.session_time_ms) filter (where rcm.session_time_ms is not null) as race_control_start_ms,
            max(rcm.session_time_ms) filter (where rcm.session_time_ms is not null) as race_control_end_ms
        from race_control_messages rcm
        join race_sessions rs using (session_id)
        group by rcm.session_id
    )
    select
        rs.session_id,
        rs.year,
        rs.event_name,
        rs.session_start_utc,
        rs.session_end_utc,
        coalesce(t.telemetry_samples, 0) as telemetry_samples,
        t.telemetry_start_ms,
        t.telemetry_end_ms,
        coalesce(p.position_samples, 0) as position_samples,
        p.position_start_ms,
        p.position_end_ms,
        coalesce(a.aligned_samples, 0) as aligned_samples,
        a.aligned_start_ms,
        a.aligned_end_ms,
        coalesce(w.weather_samples, 0) as weather_samples,
        w.weather_start_ms,
        w.weather_end_ms,
        coalesce(ts.track_status_events, 0) as track_status_events,
        ts.track_status_start_ms,
        ts.track_status_end_ms,
        coalesce(ss.session_status_events, 0) as session_status_events,
        ss.session_status_start_ms,
        ss.session_status_end_ms,
        ss.session_status_finished_ms,
        coalesce(rc.race_control_messages, 0) as race_control_messages,
        rc.race_control_start_ms,
        rc.race_control_end_ms
    from race_sessions rs
    left join telemetry t using (session_id)
    left join position p using (session_id)
    left join aligned a using (session_id)
    left join weather w using (session_id)
    left join track_status ts using (session_id)
    left join session_status ss using (session_id)
    left join race_control rc using (session_id)
    order by rs.session_start_utc nulls last, rs.event_name;
    """
    with engine(url).connect() as connection:
        df = pd.read_sql_query(text(sql), connection, params=scope.sql_params)

    end_cols = [
        "telemetry_end_ms",
        "position_end_ms",
        "aligned_end_ms",
        "weather_end_ms",
        "track_status_end_ms",
        "session_status_finished_ms",
        "session_status_end_ms",
        "race_control_end_ms",
    ]
    df["derived_session_duration_ms"] = df[end_cols].max(axis=1, skipna=True)
    fallback_start = df[["telemetry_start_ms", "position_start_ms"]].min(axis=1, skipna=True)
    fallback_end = df[["telemetry_end_ms", "position_end_ms"]].max(axis=1, skipna=True)
    df["active_replay_start_ms"] = df["aligned_start_ms"].fillna(fallback_start)
    df["active_replay_end_ms"] = df["aligned_end_ms"].fillna(fallback_end)
    df["active_replay_duration_ms"] = df["active_replay_end_ms"] - df["active_replay_start_ms"]
    df["session_end_missing_known_limitation"] = df["session_end_utc"].isna()
    df["duration_source"] = df[end_cols].idxmax(axis=1).str.replace("_end_ms", "", regex=False)
    df.loc[df["duration_source"] == "session_status_finished_ms", "duration_source"] = "session_status_finished"
    df["finished_to_derived_end_gap_ms"] = df["derived_session_duration_ms"] - df["session_status_finished_ms"]

    for surface in ["telemetry", "position", "aligned", "weather", "track_status", "session_status", "race_control"]:
        start = df[f"{surface}_start_ms"]
        end = df[f"{surface}_end_ms"] if surface != "session_status" else df["session_status_end_ms"]
        span = (end - start).clip(lower=0)
        df[f"{surface}_span_ms"] = span
        df[f"{surface}_coverage_ratio"] = span / df["derived_session_duration_ms"].replace({0: np.nan})
        overlap_start = pd.concat([start, df["active_replay_start_ms"]], axis=1).max(axis=1)
        overlap_end = pd.concat([end, df["active_replay_end_ms"]], axis=1).min(axis=1)
        df[f"{surface}_active_overlap_ms"] = (overlap_end - overlap_start).clip(lower=0)
        df[f"{surface}_active_coverage_ratio"] = (
            df[f"{surface}_active_overlap_ms"] / df["active_replay_duration_ms"].replace({0: np.nan})
        )
        df[f"{surface}_starts_after_active_ms"] = start - df["active_replay_start_ms"]
        df[f"{surface}_ends_before_active_end_ms"] = df["active_replay_end_ms"] - end

    return df


def build_surface_coverage_windows(duration_coverage: pd.DataFrame) -> pd.DataFrame:
    surfaces = [
        ("raw telemetry", "telemetry"),
        ("raw position", "position"),
        ("aligned replay", "aligned"),
        ("weather", "weather"),
        ("track status", "track_status"),
        ("session status", "session_status"),
        ("race control", "race_control"),
    ]
    rows = []
    for record in duration_coverage.to_dict("records"):
        for label, prefix in surfaces:
            start = record.get(f"{prefix}_start_ms")
            end = record.get(f"{prefix}_end_ms") if prefix != "session_status" else record.get("session_status_end_ms")
            rows.append(
                {
                    "session_id": record["session_id"],
                    "year": record["year"],
                    "event_name": record["event_name"],
                    "surface": label,
                    "surface_key": prefix,
                    "start_ms": start,
                    "end_ms": end,
                    "span_ms": record.get(f"{prefix}_span_ms"),
                    "derived_session_duration_ms": record["derived_session_duration_ms"],
                    "active_replay_start_ms": record["active_replay_start_ms"],
                    "active_replay_end_ms": record["active_replay_end_ms"],
                    "active_replay_duration_ms": record["active_replay_duration_ms"],
                    "coverage_ratio": record.get(f"{prefix}_coverage_ratio"),
                    "active_coverage_ratio": record.get(f"{prefix}_active_coverage_ratio"),
                    "starts_after_active_ms": record.get(f"{prefix}_starts_after_active_ms"),
                    "ends_before_active_end_ms": record.get(f"{prefix}_ends_before_active_end_ms"),
                }
            )
    return pd.DataFrame(rows)


def normalize_message_text(message: Any) -> str:
    text_value = "" if pd.isna(message) else str(message).lower()
    text_value = re.sub(r"\bcar\s+\d+\b", "car #", text_value)
    text_value = re.sub(r"\bdriver\s+\d+\b", "driver #", text_value)
    text_value = re.sub(r"\blap\s+\d+\b", "lap #", text_value)
    text_value = re.sub(r"\bturn\s+\d+\b", "turn #", text_value)
    text_value = re.sub(r"\b\d+\b", "#", text_value)
    text_value = re.sub(r"\s+", " ", text_value)
    return text_value.strip()


def classify_race_control_taxonomy(row: pd.Series) -> str:
    text_value = " ".join(
        str(row.get(column, "") or "").lower()
        for column in ["category", "flag", "scope", "status", "message"]
    )
    if any(token in text_value for token in ["red flag", "redflag"]):
        return "red_flag"
    if any(token in text_value for token in ["safety car", "safetycar"]):
        return "safety_car"
    if any(token in text_value for token in ["virtual safety car", " vsc", "vsc "]):
        return "vsc"
    if "drs" in text_value:
        return "drs"
    if any(token in text_value for token in ["penalty", "penalised", "penalized", "time penalty", "drive through", "stop/go"]):
        return "penalties"
    if any(token in text_value for token in ["investigat", "noted", "stewards", "summon", "summons", "incident involving"]):
        return "investigations_noted"
    if any(token in text_value for token in ["pit entry", "pit exit", "pit lane", "pitlane"]):
        return "pit_entry_exit"
    if any(token in text_value for token in ["yellow", "green", "blue flag", "double yellow", "clear in sector", "flag"]):
        return "flags"
    return "other"


def load_race_control_messages_detailed(url: str | None = None, scope: SurfaceScope = SurfaceScope()) -> pd.DataFrame:
    sql = """
    select
        rcm.race_control_message_id,
        rcm.session_id,
        s.year,
        s.event_name,
        rcm.message_time_utc,
        rcm.session_time_ms,
        rcm.category,
        rcm.message,
        rcm.status,
        rcm.flag,
        rcm.scope,
        rcm.sector,
        rcm.racing_number,
        sd.driver_code,
        rcm.lap_number
    from race_control_messages rcm
    join sessions s using (session_id)
    left join session_drivers sd
      on sd.session_id = rcm.session_id
     and sd.driver_number = rcm.racing_number
    where s.year = :year
      and s.session_type = :session_type
    order by s.session_start_utc nulls last, s.event_name, rcm.session_time_ms nulls last, rcm.race_control_message_id;
    """
    with engine(url).connect() as connection:
        df = pd.read_sql_query(text(sql), connection, params=scope.sql_params)
    df["taxonomy"] = df.apply(classify_race_control_taxonomy, axis=1)
    df["normalized_message"] = df["message"].map(normalize_message_text)
    df["missing_session_time"] = df["session_time_ms"].isna()
    df["missing_lap_scope"] = df["lap_number"].isna()
    df["missing_driver_scope"] = df["racing_number"].isna()
    df["is_driver_scoped"] = df["racing_number"].notna()
    df["is_lap_scoped"] = df["lap_number"].notna()
    return df


def summarize_race_control_taxonomy(race_control_messages: pd.DataFrame) -> pd.DataFrame:
    summary = (
        race_control_messages.groupby("taxonomy", dropna=False)
        .agg(
            messages=("race_control_message_id", "size"),
            missing_session_time=("missing_session_time", "sum"),
            missing_lap_scope=("missing_lap_scope", "sum"),
            missing_driver_scope=("missing_driver_scope", "sum"),
            driver_scoped=("is_driver_scoped", "sum"),
            lap_scoped=("is_lap_scoped", "sum"),
            sessions=("session_id", "nunique"),
        )
        .reset_index()
        .sort_values(["messages", "taxonomy"], ascending=[False, True])
    )
    for column in ["missing_session_time", "missing_lap_scope", "missing_driver_scope", "driver_scoped", "lap_scoped"]:
        summary[f"{column}_pct"] = summary[column] / summary["messages"].replace({0: np.nan}) * 100
    return summary


def find_race_control_duplicates(race_control_messages: pd.DataFrame) -> pd.DataFrame:
    duplicates = (
        race_control_messages.groupby(["session_id", "event_name", "normalized_message"], dropna=False)
        .agg(
            messages=("race_control_message_id", "size"),
            first_session_time_ms=("session_time_ms", "min"),
            last_session_time_ms=("session_time_ms", "max"),
            taxonomy=("taxonomy", lambda values: ",".join(sorted(set(values)))),
            example_message=("message", "first"),
        )
        .reset_index()
    )
    duplicates = duplicates[duplicates["messages"] > 1].copy()
    duplicates["span_ms"] = duplicates["last_session_time_ms"] - duplicates["first_session_time_ms"]
    return duplicates.sort_values(["messages", "span_ms", "event_name"], ascending=[False, True, True])


def preserve_race_control_examples(race_control_messages: pd.DataFrame, max_examples_per_bucket: int = 5) -> pd.DataFrame:
    columns = [
        "taxonomy",
        "event_name",
        "session_time_ms",
        "lap_number",
        "driver_code",
        "category",
        "flag",
        "scope",
        "message",
    ]
    return (
        race_control_messages.sort_values(["taxonomy", "session_time_ms"], na_position="last")
        .groupby("taxonomy", group_keys=False)
        .head(max_examples_per_bucket)[columns]
        .reset_index(drop=True)
    )


def load_track_status_events_detailed(url: str | None = None, scope: SurfaceScope = SurfaceScope()) -> pd.DataFrame:
    sql = """
    select
        tse.session_id,
        s.year,
        s.event_name,
        tse.event_time_ms,
        tse.status_code,
        tse.message
    from track_status_events tse
    join sessions s using (session_id)
    where s.year = :year
      and s.session_type = :session_type
    order by s.session_start_utc nulls last, s.event_name, tse.event_time_ms, tse.status_code;
    """
    with engine(url).connect() as connection:
        return pd.read_sql_query(text(sql), connection, params=scope.sql_params)


def build_track_status_intervals(status_events: pd.DataFrame, session_duration_coverage: pd.DataFrame) -> pd.DataFrame:
    if status_events.empty:
        return pd.DataFrame()
    duration_lookup = session_duration_coverage.set_index("session_id")["derived_session_duration_ms"].to_dict()
    rows = []
    status_labels = {
        "1": "clear",
        "2": "yellow",
        "4": "safety_car",
        "5": "red_flag",
        "6": "vsc_deployed",
        "7": "vsc_ending",
    }
    for _, group in status_events.groupby("session_id", sort=False):
        group = group.sort_values(["event_time_ms", "status_code"]).copy()
        end_values = group["event_time_ms"].shift(-1)
        session_duration = duration_lookup.get(group["session_id"].iloc[0])
        end_values = end_values.fillna(session_duration)
        for row, end_ms in zip(group.itertuples(index=False), end_values, strict=True):
            rows.append(
                {
                    "session_id": row.session_id,
                    "year": row.year,
                    "event_name": row.event_name,
                    "start_ms": row.event_time_ms,
                    "end_ms": end_ms,
                    "duration_ms": max(float(end_ms) - float(row.event_time_ms), 0.0) if pd.notna(end_ms) else np.nan,
                    "status_code": row.status_code,
                    "status_label": status_labels.get(str(row.status_code), "unknown"),
                    "message": row.message,
                }
            )
    return pd.DataFrame(rows)


def summarize_status_race_control_overlap(
    status_intervals: pd.DataFrame,
    race_control_messages: pd.DataFrame,
) -> pd.DataFrame:
    rows = []
    timed_messages = race_control_messages.dropna(subset=["session_time_ms"])
    for interval in status_intervals.itertuples(index=False):
        messages = timed_messages[
            (timed_messages["session_id"] == interval.session_id)
            & (timed_messages["session_time_ms"] >= interval.start_ms)
            & (timed_messages["session_time_ms"] < interval.end_ms)
        ]
        rows.append(
            {
                "session_id": interval.session_id,
                "event_name": interval.event_name,
                "status_label": interval.status_label,
                "start_ms": interval.start_ms,
                "end_ms": interval.end_ms,
                "duration_ms": interval.duration_ms,
                "race_control_messages": len(messages),
                "incident_messages": int(messages["taxonomy"].isin(["flags", "safety_car", "vsc", "red_flag", "investigations_noted", "penalties"]).sum()),
                "taxonomies": ",".join(sorted(messages["taxonomy"].dropna().unique())),
            }
        )
    return pd.DataFrame(rows)


def load_weather_samples_detailed(url: str | None = None, scope: SurfaceScope = SurfaceScope()) -> pd.DataFrame:
    sql = """
    select
        w.session_id,
        s.year,
        s.event_name,
        w.sample_time_utc,
        w.session_time_ms,
        w.air_temp_c,
        w.track_temp_c,
        w.humidity_pct,
        w.pressure_mbar,
        w.rainfall,
        w.wind_direction_deg,
        w.wind_speed_mps
    from weather_samples w
    join sessions s using (session_id)
    where s.year = :year
      and s.session_type = :session_type
    order by s.session_start_utc nulls last, s.event_name, w.session_time_ms;
    """
    with engine(url).connect() as connection:
        return pd.read_sql_query(text(sql), connection, params=scope.sql_params)


def summarize_weather_cadence_and_jumps(weather_samples: pd.DataFrame) -> tuple[pd.DataFrame, pd.DataFrame]:
    weather = weather_samples.sort_values(["session_id", "session_time_ms"]).copy()
    weather["gap_ms"] = weather.groupby("session_id")["session_time_ms"].diff()
    for column in ["air_temp_c", "track_temp_c", "humidity_pct", "pressure_mbar", "wind_speed_mps"]:
        weather[f"{column}_delta"] = weather.groupby("session_id")[column].diff().abs()
    wind_delta = weather.groupby("session_id")["wind_direction_deg"].diff().abs()
    weather["wind_direction_delta_deg"] = np.minimum(wind_delta, 360 - wind_delta)
    weather["rainfall_changed"] = weather.groupby("session_id")["rainfall"].transform(
        lambda values: values.ne(values.shift()) & values.shift().notna()
    )

    summary = (
        weather.groupby(["session_id", "year", "event_name"], sort=False)
        .agg(
            samples=("session_time_ms", "size"),
            start_ms=("session_time_ms", "min"),
            end_ms=("session_time_ms", "max"),
            median_gap_ms=("gap_ms", "median"),
            p95_gap_ms=("gap_ms", lambda values: values.quantile(0.95)),
            max_gap_ms=("gap_ms", "max"),
            max_air_temp_delta_c=("air_temp_c_delta", "max"),
            max_track_temp_delta_c=("track_temp_c_delta", "max"),
            max_humidity_delta_pct=("humidity_pct_delta", "max"),
            max_pressure_delta_mbar=("pressure_mbar_delta", "max"),
            max_wind_speed_delta_mps=("wind_speed_mps_delta", "max"),
            max_wind_direction_delta_deg=("wind_direction_delta_deg", "max"),
            rainfall_samples=("rainfall", "sum"),
            rainfall_transitions=("rainfall_changed", "sum"),
        )
        .reset_index()
    )
    summary["large_gap_flag"] = summary["max_gap_ms"].fillna(0) > SurfaceThresholds().weather_max_gap_ms
    summary["temperature_jump_flag"] = (summary["max_air_temp_delta_c"].fillna(0) > 5) | (summary["max_track_temp_delta_c"].fillna(0) > 8)
    summary["pressure_jump_flag"] = summary["max_pressure_delta_mbar"].fillna(0) > 5
    summary["humidity_jump_flag"] = summary["max_humidity_delta_pct"].fillna(0) > 20
    summary["wind_jump_flag"] = (summary["max_wind_speed_delta_mps"].fillna(0) > 5) | (summary["max_wind_direction_delta_deg"].fillna(0) > 90)

    transitions = weather[weather["rainfall_changed"]].copy()
    transitions = transitions[
        [
            "session_id",
            "year",
            "event_name",
            "session_time_ms",
            "rainfall",
            "air_temp_c",
            "track_temp_c",
            "humidity_pct",
            "pressure_mbar",
        ]
    ]
    return summary, transitions


def build_context_timeline_bins(
    race_control_messages: pd.DataFrame,
    status_intervals: pd.DataFrame,
    weather_transitions: pd.DataFrame,
    aligned_windows: pd.DataFrame,
    bin_ms: int = 300_000,
) -> pd.DataFrame:
    replay_bins = (
        aligned_windows.assign(bin_start_ms=(aligned_windows["window_start_ms"] // bin_ms) * bin_ms)
        .groupby(["session_id", "year", "event_name", "bin_start_ms"], sort=False)
        .agg(
            driver_windows=("driver_code", "size"),
            degraded_driver_windows=("non_ok_pct", lambda values: int((values >= SurfaceThresholds().degraded_window_min_non_ok_pct).sum())),
            severe_driver_windows=("non_ok_pct", lambda values: int((values >= SurfaceThresholds().severe_window_min_non_ok_pct).sum())),
            max_non_ok_pct=("non_ok_pct", "max"),
        )
        .reset_index()
    )
    rc_bins = (
        race_control_messages.dropna(subset=["session_time_ms"])
        .assign(bin_start_ms=lambda df: (df["session_time_ms"] // bin_ms).astype("int64") * bin_ms)
        .groupby(["session_id", "bin_start_ms"], sort=False)
        .agg(
            race_control_messages=("race_control_message_id", "size"),
            incident_messages=("taxonomy", lambda values: int(values.isin(["flags", "safety_car", "vsc", "red_flag", "investigations_noted", "penalties"]).sum())),
            safety_car_messages=("taxonomy", lambda values: int((values == "safety_car").sum())),
            vsc_messages=("taxonomy", lambda values: int((values == "vsc").sum())),
            penalty_messages=("taxonomy", lambda values: int((values == "penalties").sum())),
        )
        .reset_index()
    )
    weather_bins = (
        weather_transitions.assign(bin_start_ms=lambda df: (df["session_time_ms"] // bin_ms).astype("int64") * bin_ms)
        .groupby(["session_id", "bin_start_ms"], sort=False)
        .agg(rainfall_transitions=("session_time_ms", "size"))
        .reset_index()
        if not weather_transitions.empty
        else pd.DataFrame(columns=["session_id", "bin_start_ms", "rainfall_transitions"])
    )
    status_rows = []
    for interval in status_intervals.itertuples(index=False):
        if pd.isna(interval.end_ms):
            continue
        start_bin = int(interval.start_ms // bin_ms) * bin_ms
        end_bin = int(max(interval.end_ms - 1, interval.start_ms) // bin_ms) * bin_ms
        for bin_start in range(start_bin, end_bin + 1, bin_ms):
            status_rows.append(
                {
                    "session_id": interval.session_id,
                    "bin_start_ms": bin_start,
                    "has_yellow": interval.status_label == "yellow",
                    "has_safety_car": interval.status_label == "safety_car",
                    "has_red_flag": interval.status_label == "red_flag",
                    "has_vsc": interval.status_label in {"vsc_deployed", "vsc_ending"},
                }
            )
    status_bins = pd.DataFrame(status_rows)
    if status_bins.empty:
        status_bins = pd.DataFrame(columns=["session_id", "bin_start_ms", "has_yellow", "has_safety_car", "has_red_flag", "has_vsc"])
    else:
        status_bins = status_bins.groupby(["session_id", "bin_start_ms"], sort=False).any().reset_index()

    timeline = replay_bins.merge(rc_bins, on=["session_id", "bin_start_ms"], how="left")
    timeline = timeline.merge(weather_bins, on=["session_id", "bin_start_ms"], how="left")
    timeline = timeline.merge(status_bins, on=["session_id", "bin_start_ms"], how="left")
    fill_zero = [
        "race_control_messages",
        "incident_messages",
        "safety_car_messages",
        "vsc_messages",
        "penalty_messages",
        "rainfall_transitions",
    ]
    timeline[fill_zero] = timeline[fill_zero].fillna(0).astype(int)
    for column in ["has_yellow", "has_safety_car", "has_red_flag", "has_vsc"]:
        timeline[column] = timeline[column].fillna(False).astype(bool)
    timeline["has_status_context"] = timeline[["has_yellow", "has_safety_car", "has_red_flag", "has_vsc"]].any(axis=1)
    timeline["has_context_event"] = (
        (timeline["incident_messages"] > 0)
        | timeline["has_status_context"]
        | (timeline["rainfall_transitions"] > 0)
    )
    timeline["degraded_window_rate"] = timeline["degraded_driver_windows"] / timeline["driver_windows"].replace({0: np.nan}) * 100
    return timeline.sort_values(["event_name", "bin_start_ms"])


def load_race_control_categories(url: str | None = None, scope: SurfaceScope = SurfaceScope()) -> pd.DataFrame:
    sql = """
    select
        rcm.session_id,
        s.year,
        s.event_name,
        coalesce(rcm.category, 'missing') as category,
        coalesce(rcm.flag, 'missing') as flag,
        coalesce(rcm.scope, 'missing') as scope,
        count(*) as messages,
        count(*) filter (where rcm.session_time_ms is null) as missing_session_time,
        count(*) filter (where rcm.lap_number is not null) as lap_scoped,
        count(*) filter (where rcm.racing_number is not null) as driver_scoped
    from race_control_messages rcm
    join sessions s using (session_id)
    where s.year = :year
      and s.session_type = :session_type
    group by rcm.session_id, s.year, s.event_name, category, flag, scope
    order by s.year, s.event_name, messages desc;
    """
    with engine(url).connect() as connection:
        return pd.read_sql_query(text(sql), connection, params=scope.sql_params)


def add_quality_flags(
    df: pd.DataFrame,
    thresholds: SurfaceThresholds = SurfaceThresholds(),
    diagnostics: pd.DataFrame | None = None,
) -> pd.DataFrame:
    result = df.copy()
    result["telemetry_null_pct"] = (
        (result["telemetry_session_time_nulls"] + result["telemetry_lap_time_nulls"] + result["telemetry_speed_nulls"])
        / (result["telemetry_samples"].replace({0: np.nan}) * 3)
        * 100
    )
    result["position_xy_null_pct"] = result["position_xy_nulls"] / result["position_samples"].replace({0: np.nan}) * 100
    result["aligned_non_ok_pct"] = result["aligned_non_ok_rows"] / result["aligned_samples"].replace({0: np.nan}) * 100
    result["race_control_untimed_pct"] = (
        result["race_control_missing_session_time"] / result["race_control_messages"].replace({0: np.nan}) * 100
    )
    result["weather_null_pct"] = (
        result[[
            "weather_air_temp_nulls",
            "weather_track_temp_nulls",
            "weather_humidity_nulls",
            "weather_pressure_nulls",
            "weather_rainfall_nulls",
        ]].sum(axis=1)
        / (result["weather_samples"].replace({0: np.nan}) * 5)
        * 100
    )

    result["session_metadata_incomplete"] = (
        result["session_start_utc"].isna()
        | result["event_name"].isna()
        | result["country"].isna()
    )
    # session_end_utc is currently absent for imported sessions. Track it as a
    # metric, but do not flag every otherwise usable session as bad.
    result["session_end_missing"] = result["session_end_utc"].isna()
    result["driver_metadata_sparse"] = (
        (result["driver_count"] < thresholds.expected_min_drivers)
        | (result["missing_driver_number"] > 0)
        | (result["missing_team_name"] > 0)
    )
    result["lap_metadata_incomplete"] = (
        (result["lap_rows"] == 0)
        | (result["lap_driver_count"] < thresholds.expected_min_drivers)
        | (result["missing_lap_time_rows"] > 0)
    )
    result["raw_telemetry_coverage_issue"] = (
        (result["telemetry_driver_count"] < thresholds.min_raw_driver_streams)
        | (result["telemetry_samples"] == 0)
        | (result["telemetry_null_pct"].fillna(100) > 0)
    )
    result["raw_position_coverage_issue"] = (
        (result["position_driver_count"] < thresholds.min_raw_driver_streams)
        | (result["position_samples"] == 0)
        | (result["position_xy_null_pct"].fillna(100) > 0)
    )
    result["aligned_replay_quality_issue"] = (
        (result["aligned_samples"] == 0)
        | (result["aligned_driver_count"] < thresholds.min_raw_driver_streams)
        | (result["aligned_non_ok_pct"].fillna(100) > thresholds.max_aligned_non_ok_pct)
    )
    result["ingestion_diagnostic_warning"] = False
    if diagnostics is not None and not diagnostics.empty:
        warnings = (
            diagnostics.assign(has_warning=diagnostics["warning_flags"].fillna("").astype(str).str.len() > 0)
            .groupby("session_id")["has_warning"]
            .any()
            .rename("ingestion_diagnostic_warning")
            .reset_index()
        )
        result = result.drop(columns=["ingestion_diagnostic_warning"]).merge(warnings, on="session_id", how="left")
        result["ingestion_diagnostic_warning"] = result["ingestion_diagnostic_warning"].fillna(False).astype(bool)
    result["weather_surface_issue"] = (
        (result["weather_samples"] < thresholds.min_weather_samples)
        | (result["weather_null_pct"].fillna(100) > 0)
        | (result["weather_max_gap_ms"].fillna(0) > thresholds.weather_max_gap_ms)
    )
    result["status_timeline_sparse"] = (
        (result["track_status_events"] == 0)
        | (result["session_status_events"] == 0)
    )
    result["race_control_sparse_or_untimed"] = (
        (result["race_control_messages"] == 0)
        | (result["race_control_untimed_pct"].fillna(0) > 10)
    )
    result["circuit_annotation_issue"] = (
        (result["circuit_metadata_rows"] == 0)
        | (result["corner_markers"] < thresholds.min_corner_markers)
        | (result["marker_distance_nulls"] > 0)
    )
    result["surface_issue_count"] = result[SURFACE_FLAG_COLUMNS].sum(axis=1)
    result["has_surface_issue"] = result["surface_issue_count"] > 0
    return result


def summarize_surface_flags(classified: pd.DataFrame) -> pd.DataFrame:
    rows = []
    total = len(classified)
    for column in SURFACE_FLAG_COLUMNS:
        count = int(classified[column].sum())
        rows.append({"surface_flag": column, "sessions": count, "pct_sessions": count / total * 100 if total else 0.0})
    return pd.DataFrame(rows).sort_values(["sessions", "surface_flag"], ascending=[False, True])


def summarize_by_year(classified: pd.DataFrame) -> pd.DataFrame:
    summary = (
        classified.groupby("year")
        .agg(
            sessions=("session_id", "size"),
            sessions_with_issue=("has_surface_issue", "sum"),
            median_surface_issue_count=("surface_issue_count", "median"),
            telemetry_samples=("telemetry_samples", "sum"),
            position_samples=("position_samples", "sum"),
            aligned_samples=("aligned_samples", "sum"),
            weather_samples=("weather_samples", "sum"),
            race_control_messages=("race_control_messages", "sum"),
        )
        .reset_index()
    )
    summary["issue_pct"] = summary["sessions_with_issue"] / summary["sessions"] * 100
    return summary


def summarize_aligned_context_overlap(
    aligned_windows: pd.DataFrame,
    thresholds: SurfaceThresholds = SurfaceThresholds(),
) -> pd.DataFrame:
    """Summarize whether degraded replay windows overlap contextual race events."""

    rows = []
    checks = [
        ("all_windows", aligned_windows["aligned_rows"] >= 0),
        ("degraded_windows", aligned_windows["non_ok_pct"] >= thresholds.degraded_window_min_non_ok_pct),
        ("severe_windows", aligned_windows["non_ok_pct"] >= thresholds.severe_window_min_non_ok_pct),
    ]
    for bucket, mask in checks:
        subset = aligned_windows[mask].copy()
        total = len(subset)
        if total == 0:
            rows.append(
                {
                    "window_bucket": bucket,
                    "windows": 0,
                    "pct_with_race_control": 0.0,
                    "pct_with_status": 0.0,
                    "pct_with_pit_context": 0.0,
                    "pct_with_incident_context": 0.0,
                    "median_non_ok_pct": 0.0,
                    "p95_non_ok_pct": 0.0,
                }
            )
            continue
        rows.append(
            {
                "window_bucket": bucket,
                "windows": total,
                "pct_with_race_control": subset["has_race_control_context"].mean() * 100,
                "pct_with_status": subset["has_status_context"].mean() * 100,
                "pct_with_pit_context": subset["has_pit_context"].mean() * 100,
                "pct_with_incident_context": subset["has_incident_context"].mean() * 100,
                "median_non_ok_pct": subset["non_ok_pct"].median(),
                "p95_non_ok_pct": subset["non_ok_pct"].quantile(0.95),
            }
        )
    return pd.DataFrame(rows)


def summarize_aligned_lap_context(
    aligned_laps: pd.DataFrame,
    thresholds: SurfaceThresholds = SurfaceThresholds(),
) -> pd.DataFrame:
    """Compare aligned quality for ordinary, pit, inaccurate, and missing-time laps."""

    rows = []
    categories = {
        "all_laps": aligned_laps["aligned_rows"] >= 0,
        "pit_laps": aligned_laps["is_pit_lap"].fillna(False),
        "fastf1_inaccurate_laps": aligned_laps["is_fastf1_inaccurate"].fillna(False),
        "missing_lap_time_laps": aligned_laps["missing_lap_time"].fillna(False),
        "ordinary_laps": ~(
            aligned_laps["is_pit_lap"].fillna(False)
            | aligned_laps["is_fastf1_inaccurate"].fillna(False)
            | aligned_laps["missing_lap_time"].fillna(False)
        ),
    }
    for label, mask in categories.items():
        subset = aligned_laps[mask]
        total = len(subset)
        rows.append(
            {
                "lap_bucket": label,
                "laps": total,
                "laps_with_any_non_ok": int((subset["non_ok_rows"] > 0).sum()) if total else 0,
                "laps_with_severe_non_ok": int((subset["non_ok_pct"] >= thresholds.severe_window_min_non_ok_pct).sum()) if total else 0,
                "median_non_ok_pct": subset["non_ok_pct"].median() if total else 0.0,
                "p95_non_ok_pct": subset["non_ok_pct"].quantile(0.95) if total else 0.0,
                "non_ok_rows": int(subset["non_ok_rows"].sum()) if total else 0,
            }
        )
    return pd.DataFrame(rows)


def top_aligned_desktop_watchlist(
    aligned_drivers: pd.DataFrame,
    degraded_segments: pd.DataFrame,
    aligned_windows: pd.DataFrame,
    thresholds: SurfaceThresholds = SurfaceThresholds(),
) -> pd.DataFrame:
    """Rank race/driver pairs most likely to need replay diagnostics in the desktop app."""

    driver = aligned_drivers.copy()
    if degraded_segments.empty:
        longest = pd.DataFrame(columns=["session_id", "driver_code", "longest_segment_ms", "segments_over_2s"])
    else:
        longest = (
            degraded_segments.groupby(["session_id", "driver_code"])
            .agg(
                longest_segment_ms=("duration_ms", "max"),
                segments_over_2s=("duration_ms", lambda values: int((values >= 2_000).sum())),
            )
            .reset_index()
        )
    severe_windows = (
        aligned_windows.assign(is_severe=aligned_windows["non_ok_pct"] >= thresholds.severe_window_min_non_ok_pct)
        .groupby(["session_id", "driver_code"])
        .agg(
            degraded_windows=("non_ok_pct", lambda values: int((values >= thresholds.degraded_window_min_non_ok_pct).sum())),
            severe_windows=("is_severe", "sum"),
            windows_with_incident_context=("has_incident_context", "sum"),
            max_window_non_ok_pct=("non_ok_pct", "max"),
        )
        .reset_index()
    )
    watchlist = driver.merge(longest, on=["session_id", "driver_code"], how="left")
    watchlist = watchlist.merge(severe_windows, on=["session_id", "driver_code"], how="left")
    fill_cols = [
        "longest_segment_ms",
        "segments_over_2s",
        "degraded_windows",
        "severe_windows",
        "windows_with_incident_context",
        "max_window_non_ok_pct",
    ]
    watchlist[fill_cols] = watchlist[fill_cols].fillna(0)
    watchlist["desktop_replay_risk_score"] = (
        watchlist["non_ok_pct"] * 4
        + watchlist["max_window_non_ok_pct"] * 0.8
        + watchlist["segments_over_2s"] * 2
        + watchlist["severe_windows"] * 0.5
    )
    watchlist["desktop_guidance"] = np.select(
        [
            watchlist["longest_segment_ms"] >= 5_000,
            watchlist["severe_windows"] >= 3,
            watchlist["non_ok_pct"] >= thresholds.max_aligned_non_ok_pct,
            watchlist["degraded_windows"] > 0,
        ],
        [
            "warn_before_replay",
            "show_replay_quality_overlay",
            "show_replay_quality_overlay",
            "diagnostics_only",
        ],
        default="no_action",
    )
    return watchlist.sort_values(
        ["desktop_replay_risk_score", "non_ok_rows", "event_name", "driver_code"],
        ascending=[False, False, True, True],
    )


def summarize_surface_coverage(coverage_windows: pd.DataFrame) -> pd.DataFrame:
    rows = []
    for surface, group in coverage_windows.groupby("surface", sort=False):
        rows.append(
            {
                "surface": surface,
                "sessions": group["session_id"].nunique(),
                "median_coverage_ratio": group["coverage_ratio"].median(),
                "median_active_coverage_ratio": group["active_coverage_ratio"].median(),
                "min_active_coverage_ratio": group["active_coverage_ratio"].min(),
                "sessions_starting_after_active": int((group["starts_after_active_ms"].fillna(0) > 60_000).sum()),
                "sessions_ending_before_active_end": int((group["ends_before_active_end_ms"].fillna(0) > 60_000).sum()),
            }
        )
    return pd.DataFrame(rows)


def summarize_context_replay_correlation(context_timeline_bins: pd.DataFrame) -> pd.DataFrame:
    rows = []
    buckets = {
        "all_bins": context_timeline_bins["driver_windows"] >= 0,
        "context_event_bins": context_timeline_bins["has_context_event"],
        "no_context_event_bins": ~context_timeline_bins["has_context_event"],
        "race_control_incident_bins": context_timeline_bins["incident_messages"] > 0,
        "status_bins": context_timeline_bins["has_status_context"],
        "rainfall_transition_bins": context_timeline_bins["rainfall_transitions"] > 0,
    }
    for label, mask in buckets.items():
        subset = context_timeline_bins[mask]
        rows.append(
            {
                "bin_bucket": label,
                "bins": len(subset),
                "median_degraded_window_rate": subset["degraded_window_rate"].median() if len(subset) else 0.0,
                "p95_degraded_window_rate": subset["degraded_window_rate"].quantile(0.95) if len(subset) else 0.0,
                "median_max_non_ok_pct": subset["max_non_ok_pct"].median() if len(subset) else 0.0,
                "p95_max_non_ok_pct": subset["max_non_ok_pct"].quantile(0.95) if len(subset) else 0.0,
                "severe_driver_windows": int(subset["severe_driver_windows"].sum()) if len(subset) else 0,
            }
        )
    return pd.DataFrame(rows)


def load_circuit_marker_quality(url: str | None = None, scope: SurfaceScope = SurfaceScope()) -> pd.DataFrame:
    """Return marker rows joined to position-coordinate bounds for geometry QA."""

    sql = """
    with race_sessions as (
        select
            session_id,
            year,
            event_name,
            session_start_utc
        from sessions
        where year = :year
          and session_type = :session_type
    ),
    position_valid as (
        select
            p.session_id,
            p.driver_code,
            p.x,
            p.y
        from position_samples p
        join race_sessions rs using (session_id)
        where p.x is not null
          and p.y is not null
    ),
    position_bounds as (
        select
            session_id,
            count(*) as position_xy_samples,
            count(distinct driver_code) as position_driver_count,
            min(x) as x_min,
            max(x) as x_max,
            min(y) as y_min,
            max(y) as y_max,
            percentile_cont(0.01) within group (order by x) as x_p01,
            percentile_cont(0.99) within group (order by x) as x_p99,
            percentile_cont(0.01) within group (order by y) as y_p01,
            percentile_cont(0.99) within group (order by y) as y_p99
        from position_valid
        group by session_id
    )
    select
        rs.session_id,
        rs.year,
        rs.event_name,
        cm.rotation_degrees,
        cmk.circuit_marker_id,
        cmk.marker_type,
        cmk.marker_number,
        cmk.marker_letter,
        cmk.x,
        cmk.y,
        cmk.angle_degrees,
        cmk.distance_m,
        pb.position_xy_samples,
        pb.position_driver_count,
        pb.x_min,
        pb.x_max,
        pb.y_min,
        pb.y_max,
        pb.x_p01,
        pb.x_p99,
        pb.y_p01,
        pb.y_p99
    from race_sessions rs
    left join circuit_metadata cm using (session_id)
    left join circuit_markers cmk using (session_id)
    left join position_bounds pb using (session_id)
    order by rs.session_start_utc nulls last, rs.event_name, cmk.marker_type, cmk.marker_number nulls last, cmk.marker_letter nulls last;
    """
    with engine(url).connect() as connection:
        markers = pd.read_sql_query(text(sql), connection, params=scope.sql_params)

    markers["has_marker"] = markers["circuit_marker_id"].notna()
    x_span = (markers["x_max"] - markers["x_min"]).abs()
    y_span = (markers["y_max"] - markers["y_min"]).abs()
    markers["minmax_pad_x"] = np.maximum(x_span * 0.08, 500)
    markers["minmax_pad_y"] = np.maximum(y_span * 0.08, 500)
    markers["core_pad_x"] = np.maximum((markers["x_p99"] - markers["x_p01"]).abs() * 0.05, 250)
    markers["core_pad_y"] = np.maximum((markers["y_p99"] - markers["y_p01"]).abs() * 0.05, 250)
    markers["outside_minmax_bounds"] = markers["has_marker"] & (
        (markers["x"] < markers["x_min"] - markers["minmax_pad_x"])
        | (markers["x"] > markers["x_max"] + markers["minmax_pad_x"])
        | (markers["y"] < markers["y_min"] - markers["minmax_pad_y"])
        | (markers["y"] > markers["y_max"] + markers["minmax_pad_y"])
    )
    markers["outside_core_bounds"] = markers["has_marker"] & (
        (markers["x"] < markers["x_p01"] - markers["core_pad_x"])
        | (markers["x"] > markers["x_p99"] + markers["core_pad_x"])
        | (markers["y"] < markers["y_p01"] - markers["core_pad_y"])
        | (markers["y"] > markers["y_p99"] + markers["core_pad_y"])
    )
    markers["marker_coordinate_issue"] = np.select(
        [
            markers["outside_minmax_bounds"],
            markers["outside_core_bounds"],
            markers["has_marker"] & markers["distance_m"].isna(),
        ],
        [
            "outside_position_bounds",
            "outside_core_trace_bounds",
            "missing_distance",
        ],
        default="none",
    )
    return markers


def summarize_circuit_marker_quality(
    marker_quality: pd.DataFrame,
    thresholds: SurfaceThresholds = SurfaceThresholds(),
) -> pd.DataFrame:
    rows = []
    for _, group in marker_quality.groupby(["session_id", "year", "event_name"], sort=False):
        markers = group[group["has_marker"]]
        marker_count = int(len(markers))
        corner_count = int((markers["marker_type"] == "corner").sum())
        marshal_light_count = int((markers["marker_type"] == "marshal_light").sum())
        marshal_sector_count = int((markers["marker_type"] == "marshal_sector").sum())
        outside_minmax = int(markers["outside_minmax_bounds"].sum())
        outside_core = int(markers["outside_core_bounds"].sum())
        distance_nulls = int(markers["distance_m"].isna().sum())
        has_metadata = group["rotation_degrees"].notna().any()
        position_samples = int(group["position_xy_samples"].dropna().max()) if group["position_xy_samples"].notna().any() else 0

        if not has_metadata or position_samples == 0 or marker_count == 0:
            label = "needs_reimport"
            recommendation = "reimport"
        elif outside_minmax > 0:
            label = "needs_manual_review"
            recommendation = "inspect"
        elif corner_count < thresholds.min_corner_markers:
            label = "partial"
            recommendation = "label_in_ui"
        elif outside_core > 0 or distance_nulls > 0:
            label = "ready_with_warnings"
            recommendation = "inspect" if outside_core > 0 else "label_in_ui"
        else:
            label = "ready"
            recommendation = "no_action"

        rows.append(
            {
                "session_id": group["session_id"].iloc[0],
                "year": int(group["year"].iloc[0]),
                "event_name": group["event_name"].iloc[0],
                "circuit_metadata_present": bool(has_metadata),
                "rotation_present": bool(group["rotation_degrees"].notna().any()),
                "position_xy_samples": position_samples,
                "position_driver_count": int(group["position_driver_count"].dropna().max()) if group["position_driver_count"].notna().any() else 0,
                "marker_count": marker_count,
                "corner_markers": corner_count,
                "marshal_light_markers": marshal_light_count,
                "marshal_sector_markers": marshal_sector_count,
                "marker_distance_nulls": distance_nulls,
                "markers_outside_position_bounds": outside_minmax,
                "markers_outside_core_bounds": outside_core,
                "circuit_context_readiness": label,
                "circuit_context_recommendation": recommendation,
            }
        )
    return pd.DataFrame(rows)


def select_circuit_marker_example_sessions(marker_summary: pd.DataFrame, max_sessions: int = 6) -> list[str]:
    ranked = marker_summary.copy()
    ranked["priority"] = (
        ranked["markers_outside_position_bounds"] * 100
        + ranked["markers_outside_core_bounds"] * 10
        + (ranked["circuit_context_readiness"] != "ready").astype(int)
        + ranked["marker_count"].clip(0, 50) / 100
    )
    selected = ranked.sort_values(["priority", "event_name"], ascending=[False, True]).head(max_sessions)
    return selected["session_id"].tolist()


def load_position_trace_samples_for_marker_examples(
    url: str | None,
    scope: SurfaceScope,
    session_ids: list[str],
    max_points_per_session: int = 2500,
) -> pd.DataFrame:
    if not session_ids:
        return pd.DataFrame(columns=["session_id", "event_name", "driver_code", "sample_time_utc", "x", "y"])
    sql = """
    with driver_counts as (
        select
            p.session_id,
            p.driver_code,
            count(*) as samples,
            row_number() over (partition by p.session_id order by count(*) desc, p.driver_code) as driver_rank
        from position_samples p
        join sessions s using (session_id)
        where s.year = :year
          and s.session_type = :session_type
          and p.session_id = any(:session_ids)
          and p.x is not null
          and p.y is not null
        group by p.session_id, p.driver_code
    ),
    selected_positions as (
        select
            p.session_id,
            s.event_name,
            p.driver_code,
            p.sample_time_utc,
            p.x,
            p.y,
            row_number() over (partition by p.session_id order by p.sample_time_utc) as rn,
            count(*) over (partition by p.session_id) as session_samples
        from position_samples p
        join sessions s using (session_id)
        join driver_counts dc
          on dc.session_id = p.session_id
         and dc.driver_code = p.driver_code
         and dc.driver_rank = 1
        where s.year = :year
          and s.session_type = :session_type
          and p.session_id = any(:session_ids)
          and p.x is not null
          and p.y is not null
    )
    select
        session_id,
        event_name,
        driver_code,
        sample_time_utc,
        x,
        y
    from selected_positions
    where rn = 1
       or rn = session_samples
       or mod(rn, greatest(1, ceil(session_samples / cast(:max_points_per_session as numeric))::int)) = 0
    order by event_name, sample_time_utc;
    """
    params = {**scope.sql_params, "session_ids": session_ids, "max_points_per_session": max_points_per_session}
    with engine(url).connect() as connection:
        return pd.read_sql_query(text(sql), connection, params=params)


def build_product_readiness(
    classified: pd.DataFrame,
    aligned_races: pd.DataFrame,
    aligned_windows: pd.DataFrame,
    desktop_watchlist: pd.DataFrame,
    session_duration_coverage: pd.DataFrame,
    coverage_summary: pd.DataFrame,
    marker_summary: pd.DataFrame,
) -> tuple[pd.DataFrame, pd.DataFrame]:
    """Convert EDA metrics into desktop/API readiness labels and recommendations."""

    rows = []
    severe_by_session = (
        desktop_watchlist.groupby("session_id")
        .agg(
            affected_drivers=("driver_code", "nunique"),
            severe_windows=("severe_windows", "sum"),
            degraded_windows=("degraded_windows", "sum"),
            max_window_non_ok_pct=("max_window_non_ok_pct", "max"),
            longest_segment_ms=("longest_segment_ms", "max"),
        )
        .reset_index()
    )
    affected_window_time = (
        aligned_windows[aligned_windows["non_ok_pct"] >= SurfaceThresholds().degraded_window_min_non_ok_pct]
        .drop_duplicates(["session_id", "window_start_ms"])
        .groupby("session_id")
        .agg(affected_unique_replay_windows=("window_start_ms", "size"))
        .reset_index()
    )
    marker_lookup = marker_summary.set_index("session_id") if not marker_summary.empty else pd.DataFrame()
    duration_lookup = session_duration_coverage.set_index("session_id") if not session_duration_coverage.empty else pd.DataFrame()
    coverage_lookup = coverage_summary.set_index("surface") if not coverage_summary.empty else pd.DataFrame()
    raw_position_time_limitation = "raw position" in coverage_lookup.index
    for row in classified.itertuples(index=False):
        session_id = row.session_id
        marker_row = marker_lookup.loc[session_id] if session_id in marker_lookup.index else None
        duration_row = duration_lookup.loc[session_id] if session_id in duration_lookup.index else None
        severe_row = severe_by_session[severe_by_session["session_id"] == session_id]
        affected_drivers = int(severe_row["affected_drivers"].iloc[0]) if not severe_row.empty else 0
        severe_windows = int(severe_row["severe_windows"].iloc[0]) if not severe_row.empty else 0
        degraded_windows = int(severe_row["degraded_windows"].iloc[0]) if not severe_row.empty else 0
        max_window_non_ok_pct = float(severe_row["max_window_non_ok_pct"].iloc[0]) if not severe_row.empty else 0.0
        longest_segment_ms = float(severe_row["longest_segment_ms"].iloc[0]) if not severe_row.empty else 0.0
        affected_time_row = affected_window_time[affected_window_time["session_id"] == session_id]
        affected_unique_windows = int(affected_time_row["affected_unique_replay_windows"].iloc[0]) if not affected_time_row.empty else 0

        if row.session_metadata_incomplete or row.driver_metadata_sparse:
            catalog = "partial"
        elif row.session_end_missing:
            catalog = "ready_with_warnings"
        else:
            catalog = "ready"

        if row.telemetry_samples == 0 or row.position_samples == 0:
            raw_streams = "needs_reimport"
        elif row.raw_telemetry_coverage_issue or row.raw_position_coverage_issue:
            raw_streams = "partial"
        elif row.ingestion_diagnostic_warning:
            raw_streams = "ready_with_warnings"
        else:
            raw_streams = "ready"

        if row.aligned_samples == 0:
            replay = "needs_reimport"
        elif row.aligned_replay_quality_issue or severe_windows >= 8 or longest_segment_ms >= 10_000:
            replay = "partial"
        elif row.aligned_non_ok_pct > 0 or degraded_windows > 0:
            replay = "ready_with_warnings"
        else:
            replay = "ready"

        if row.weather_surface_issue or row.status_timeline_sparse:
            context = "partial"
        elif row.race_control_sparse_or_untimed:
            context = "ready_with_warnings"
        else:
            context = "ready"

        if marker_row is None:
            circuit_context = "needs_reimport"
            marker_recommendation = "reimport"
            marker_issues = 0
        else:
            circuit_context = str(marker_row["circuit_context_readiness"])
            marker_recommendation = str(marker_row["circuit_context_recommendation"])
            marker_issues = int(marker_row["markers_outside_position_bounds"]) + int(marker_row["markers_outside_core_bounds"])

        labels = [catalog, raw_streams, replay, context, circuit_context]
        worst_score = max(READINESS_SCORE[label] for label in labels)
        schema_importer_follow_up = bool(raw_position_time_limitation or row.session_end_missing)
        if "needs_reimport" in labels:
            recommendation = "reimport"
        elif marker_recommendation == "inspect" or "needs_manual_review" in labels:
            recommendation = "inspect"
        elif any(label in {"ready_with_warnings", "partial"} for label in labels):
            recommendation = "label_in_ui"
        elif schema_importer_follow_up:
            recommendation = "schema_importer_change"
        else:
            recommendation = "no_action"

        systematic = []
        if row.session_end_missing:
            systematic.append("session_end_utc missing")
        if raw_position_time_limitation:
            systematic.append("position coverage approximated from UTC offsets")
        systematic_limitations = "; ".join(systematic)

        rows.append(
            {
                "session_id": session_id,
                "year": int(row.year),
                "event_name": row.event_name,
                "catalog_readiness": catalog,
                "raw_stream_readiness": raw_streams,
                "replay_readiness": replay,
                "context_readiness": context,
                "circuit_context_readiness": circuit_context,
                "overall_readiness_score": int(worst_score),
                "final_recommendation": recommendation,
                "schema_importer_follow_up": schema_importer_follow_up,
                "affected_drivers": affected_drivers,
                "affected_replay_windows": degraded_windows,
                "severe_replay_windows": severe_windows,
                "affected_rows": int(row.aligned_non_ok_rows),
                "affected_session_time_pct": round(
                    min(
                        100.0,
                        affected_unique_windows
                        * 30_000
                        / max(
                            float(duration_row.get("active_replay_duration_ms", 1) if duration_row is not None else 1),
                            1.0,
                        )
                        * 100,
                    ),
                    3,
                ),
                "max_window_non_ok_pct": max_window_non_ok_pct,
                "longest_degraded_segment_ms": longest_segment_ms,
                "marker_coordinate_issues": marker_issues,
                "systematic_known_limitations": systematic_limitations,
                "product_impact": "; ".join(
                    impact
                    for impact, label in [
                        ("launcher", catalog),
                        ("bounded raw API/MCP", raw_streams),
                        ("desktop replay", replay),
                        ("context panels", context),
                        ("track rendering", circuit_context),
                    ]
                    if label != "ready"
                ),
            }
        )

    readiness = pd.DataFrame(rows)
    recommendation_summary = (
        readiness.groupby("final_recommendation", as_index=False)
        .agg(
            sessions=("session_id", "size"),
            max_readiness_score=("overall_readiness_score", "max"),
            affected_drivers=("affected_drivers", "sum"),
            affected_rows=("affected_rows", "sum"),
            severe_replay_windows=("severe_replay_windows", "sum"),
            marker_coordinate_issues=("marker_coordinate_issues", "sum"),
            schema_importer_follow_up_sessions=("schema_importer_follow_up", "sum"),
        )
    )
    recommendation_order = ["no_action", "label_in_ui", "inspect", "reimport", "schema_importer_change"]
    recommendation_summary = (
        recommendation_summary.set_index("final_recommendation")
        .reindex(recommendation_order, fill_value=0)
        .reset_index()
    )
    return readiness, recommendation_summary


def cluster_race_control_text(race_control_messages: pd.DataFrame, n_clusters: int = 10) -> tuple[pd.DataFrame, pd.DataFrame]:
    text_df = race_control_messages.copy()
    text_df["cluster_text"] = text_df["normalized_message"].fillna("").str.strip()
    text_df = text_df[text_df["cluster_text"] != ""].copy()
    if text_df.empty:
        return text_df, pd.DataFrame()

    max_clusters = max(2, min(n_clusters, text_df["cluster_text"].nunique(), len(text_df)))
    try:
        from sklearn.cluster import KMeans
        from sklearn.feature_extraction.text import TfidfVectorizer

        vectorizer = TfidfVectorizer(min_df=2, ngram_range=(1, 2), stop_words="english")
        matrix = vectorizer.fit_transform(text_df["cluster_text"])
        model = KMeans(n_clusters=max_clusters, n_init=20, random_state=42)
        text_df["text_cluster"] = model.fit_predict(matrix).astype(int)
        terms = np.array(vectorizer.get_feature_names_out())
        center_terms: dict[int, str] = {}
        for cluster_id, center in enumerate(model.cluster_centers_):
            top_idx = np.argsort(center)[-6:][::-1]
            center_terms[cluster_id] = ", ".join(terms[top_idx])
        text_df["cluster_terms"] = text_df["text_cluster"].map(center_terms)
        method = "tfidf_kmeans"
    except Exception:
        grouped = text_df.groupby(["taxonomy", "cluster_text"]).ngroup()
        text_df["text_cluster"] = grouped.astype(int)
        text_df["cluster_terms"] = text_df["taxonomy"]
        method = "taxonomy_normalized_text_fallback"

    summary = (
        text_df.groupby(["text_cluster", "cluster_terms"], dropna=False)
        .agg(
            messages=("race_control_message_id", "size"),
            sessions=("session_id", "nunique"),
            taxonomy_mix=("taxonomy", lambda values: ",".join(sorted(set(values)))),
            driver_scoped_pct=("is_driver_scoped", lambda values: values.mean() * 100),
            lap_scoped_pct=("is_lap_scoped", lambda values: values.mean() * 100),
            example_message=("message", "first"),
        )
        .reset_index()
        .sort_values(["messages", "text_cluster"], ascending=[False, True])
    )
    summary["cluster_method"] = method
    text_df["cluster_method"] = method
    return text_df, summary


def plot_product_readiness_dashboard(product_readiness: pd.DataFrame) -> Path:
    path = FIGURE_DIR / "product_readiness_dashboard.svg"
    lens_columns = [
        "catalog_readiness",
        "raw_stream_readiness",
        "replay_readiness",
        "context_readiness",
        "circuit_context_readiness",
    ]
    label_map = {label: index for index, label in enumerate(READINESS_LABEL_ORDER)}
    heat = product_readiness.set_index("event_name")[lens_columns].apply(lambda col: col.map(label_map)).astype(int)
    cmap = sns.color_palette([READINESS_COLORS[label] for label in READINESS_LABEL_ORDER], as_cmap=True)
    fig, ax = plt.subplots(figsize=(11, max(7, len(heat) * 0.32)))
    sns.heatmap(
        heat,
        cmap=cmap,
        vmin=-0.5,
        vmax=len(READINESS_LABEL_ORDER) - 0.5,
        linewidths=0.35,
        linecolor="white",
        cbar=False,
        annot=product_readiness.set_index("event_name")[lens_columns].replace(
            {
                "ready": "ready",
                "ready_with_warnings": "warning",
                "partial": "partial",
                "needs_manual_review": "review",
                "needs_reimport": "reimport",
            }
        ),
        fmt="",
        annot_kws={"fontsize": 7},
        ax=ax,
    )
    ax.set_title("2025 product-readiness labels by session and surface")
    ax.set_xlabel("")
    ax.set_ylabel("")
    ax.set_xticklabels(["catalog", "raw streams", "aligned replay", "context", "circuit markers"], rotation=25, ha="right")
    fig.tight_layout()
    fig.savefig(path, format="svg")
    plt.close(fig)
    return path


def plot_product_recommendation_summary(recommendation_summary: pd.DataFrame) -> Path:
    path = FIGURE_DIR / "product_recommendation_summary.svg"
    order = ["no_action", "label_in_ui", "inspect", "reimport", "schema_importer_change"]
    plot_df = recommendation_summary.set_index("final_recommendation").reindex(order, fill_value=0).reset_index()
    colors = {
        "no_action": "#047857",
        "label_in_ui": "#F59E0B",
        "inspect": "#7C3AED",
        "reimport": "#B42318",
        "schema_importer_change": "#2563EB",
    }
    fig, ax = plt.subplots(figsize=(9.5, 4.8))
    bars = ax.bar(plot_df["final_recommendation"], plot_df["sessions"], color=[colors[label] for label in plot_df["final_recommendation"]])
    ax.set_title("Final desktop/API recommendation by 2025 race")
    ax.set_xlabel("")
    ax.set_ylabel("Sessions")
    ax.set_ylim(0, max(1, plot_df["sessions"].max()) * 1.25)
    ax.tick_params(axis="x", rotation=20)
    for bar, value in zip(bars, plot_df["sessions"], strict=True):
        ax.text(bar.get_x() + bar.get_width() / 2, bar.get_height() + 0.15, str(int(value)), ha="center", va="bottom")
    fig.tight_layout()
    fig.savefig(path, format="svg")
    plt.close(fig)
    return path


def plot_circuit_marker_quality(marker_summary: pd.DataFrame) -> Path:
    path = FIGURE_DIR / "circuit_marker_quality_summary.svg"
    plot_df = marker_summary.copy()
    fig, axes = plt.subplots(1, 2, figsize=(13, max(7, len(plot_df) * 0.28)), sharey=True)
    axes[0].barh(plot_df["event_name"], plot_df["corner_markers"], color="#2563EB", label="corners")
    axes[0].barh(plot_df["event_name"], plot_df["marshal_light_markers"], left=plot_df["corner_markers"], color="#059669", label="marshal lights")
    axes[0].barh(
        plot_df["event_name"],
        plot_df["marshal_sector_markers"],
        left=plot_df["corner_markers"] + plot_df["marshal_light_markers"],
        color="#7C3AED",
        label="marshal sectors",
    )
    axes[0].set_title("Imported circuit markers")
    axes[0].set_xlabel("Markers")
    axes[0].invert_yaxis()
    axes[0].legend(frameon=False, loc="lower right")
    issue_count = plot_df["markers_outside_position_bounds"] + plot_df["markers_outside_core_bounds"]
    axes[1].barh(plot_df["event_name"], issue_count, color="#B42318")
    axes[1].set_title("Markers outside imported position bounds")
    axes[1].set_xlabel("Flagged markers")
    fig.tight_layout()
    fig.savefig(path, format="svg")
    plt.close(fig)
    return path


def plot_circuit_marker_overlays(
    marker_quality: pd.DataFrame,
    position_trace_samples: pd.DataFrame,
) -> Path:
    path = FIGURE_DIR / "circuit_marker_overlay_examples.svg"
    sessions = position_trace_samples[["session_id", "event_name"]].drop_duplicates()
    if sessions.empty:
        path.write_text("<svg xmlns=\"http://www.w3.org/2000/svg\"></svg>\n", encoding="utf-8")
        return path
    cols = 2
    rows = int(np.ceil(len(sessions) / cols))
    fig, axes = plt.subplots(rows, cols, figsize=(12, max(6, rows * 4.2)))
    axes_arr = np.array(axes).reshape(-1)
    marker_colors = {"corner": "#B42318", "marshal_light": "#2563EB", "marshal_sector": "#059669"}
    for ax, session in zip(axes_arr, sessions.itertuples(index=False), strict=False):
        trace = position_trace_samples[position_trace_samples["session_id"] == session.session_id]
        markers = marker_quality[(marker_quality["session_id"] == session.session_id) & marker_quality["has_marker"]]
        ax.plot(trace["x"], trace["y"], color="#9CA3AF", linewidth=1.0, alpha=0.65)
        for marker_type, group in markers.groupby("marker_type"):
            ax.scatter(
                group["x"],
                group["y"],
                s=34,
                color=marker_colors.get(marker_type, "#111827"),
                label=marker_type,
                alpha=0.88,
                edgecolors="#FFFFFF",
                linewidths=0.35,
            )
        flagged = markers[markers["outside_minmax_bounds"] | markers["outside_core_bounds"]]
        if not flagged.empty:
            ax.scatter(flagged["x"], flagged["y"], s=95, facecolors="none", edgecolors="#B42318", linewidths=1.8, label="flagged")
        ax.set_title(session.event_name)
        ax.set_aspect("equal", adjustable="datalim")
        ax.axis("off")
    for ax in axes_arr[len(sessions):]:
        ax.axis("off")
    handles, labels = axes_arr[0].get_legend_handles_labels()
    if handles:
        fig.legend(handles, labels, loc="upper center", ncols=4, frameon=False)
    fig.suptitle("Circuit markers over imported position traces", y=0.995)
    fig.tight_layout(rect=[0, 0, 1, 0.96])
    fig.savefig(path, format="svg")
    plt.close(fig)
    return path


def plot_weather_trend_panels(
    weather_samples: pd.DataFrame,
    weather_summary: pd.DataFrame,
    max_sessions: int = 6,
) -> Path:
    path = FIGURE_DIR / "weather_trend_panels.svg"
    ranked = weather_summary.assign(
        priority=lambda df: df["rainfall_transitions"].fillna(0) * 10
        + df["temperature_jump_flag"].astype(int) * 5
        + df["wind_jump_flag"].astype(int) * 2
        + df["large_gap_flag"].astype(int)
    ).sort_values(["priority", "event_name"], ascending=[False, True])
    selected_ids = ranked.head(max_sessions)["session_id"].tolist()
    plot_df = weather_samples[weather_samples["session_id"].isin(selected_ids)].copy()
    if plot_df.empty:
        path.write_text("<svg xmlns=\"http://www.w3.org/2000/svg\"></svg>\n", encoding="utf-8")
        return path
    sessions = ranked[ranked["session_id"].isin(selected_ids)][["session_id", "event_name"]]
    fig, axes = plt.subplots(len(sessions), 1, figsize=(13, max(7, len(sessions) * 2.2)), sharex=False)
    axes_arr = np.array(axes).reshape(-1)
    for ax, session in zip(axes_arr, sessions.itertuples(index=False), strict=True):
        session_weather = plot_df[plot_df["session_id"] == session.session_id].sort_values("session_time_ms")
        x = session_weather["session_time_ms"] / 60_000
        ax.plot(x, session_weather["air_temp_c"], color="#2563EB", label="air temp")
        ax.plot(x, session_weather["track_temp_c"], color="#B42318", label="track temp")
        rain = session_weather["rainfall"].fillna(False).astype(bool)
        if rain.any():
            ax.fill_between(x, 0, 1, where=rain, color="#93C5FD", alpha=0.22, transform=ax.get_xaxis_transform())
        ax.set_title(session.event_name, loc="left", fontsize=10)
        ax.set_ylabel("C")
        ax.grid(axis="x", alpha=0.16)
    axes_arr[-1].set_xlabel("Session time (minutes)")
    handles, labels = axes_arr[0].get_legend_handles_labels()
    fig.legend(handles, labels, loc="upper center", ncols=2, frameon=False)
    fig.suptitle("Weather trends for rainfall or large-shift 2025 races", y=0.995)
    fig.tight_layout(rect=[0, 0, 1, 0.965])
    fig.savefig(path, format="svg")
    plt.close(fig)
    return path


def plot_race_control_text_clusters(cluster_summary: pd.DataFrame) -> Path:
    path = FIGURE_DIR / "race_control_text_clusters.svg"
    plot_df = cluster_summary.sort_values("messages", ascending=True).tail(12)
    fig, ax = plt.subplots(figsize=(11, max(5, len(plot_df) * 0.38)))
    labels = plot_df["text_cluster"].astype(str) + ": " + plot_df["cluster_terms"].astype(str).str.slice(0, 42)
    ax.barh(labels, plot_df["messages"], color="#5B677A")
    ax.set_title("Race-control text clusters")
    ax.set_xlabel("Messages")
    ax.set_ylabel("")
    for index, row in enumerate(plot_df.itertuples()):
        ax.text(row.messages + 4, index, f"{row.messages}", va="center", fontsize=8)
    fig.tight_layout()
    fig.savefig(path, format="svg")
    plt.close(fig)
    return path


def plot_race_control_taxonomy(taxonomy_summary: pd.DataFrame) -> Path:
    path = FIGURE_DIR / "race_control_taxonomy_mix.svg"
    plot_df = taxonomy_summary.sort_values("messages", ascending=True)
    fig, ax = plt.subplots(figsize=(10, 5.8))
    ax.barh(plot_df["taxonomy"], plot_df["messages"], color="#5B677A")
    ax.set_title("2025 race-control taxonomy")
    ax.set_xlabel("Messages")
    ax.set_ylabel("")
    for index, row in enumerate(plot_df.itertuples()):
        ax.text(row.messages + 5, index, f"{row.messages}", va="center", fontsize=9)
    fig.tight_layout()
    fig.savefig(path, format="svg")
    plt.close(fig)
    return path


def plot_status_timeline_strips(status_intervals: pd.DataFrame) -> Path:
    path = FIGURE_DIR / "status_timeline_strips.svg"
    colors = {
        "clear": "#D1D5DB",
        "yellow": "#F59E0B",
        "safety_car": "#B42318",
        "red_flag": "#DC2626",
        "vsc_deployed": "#7C3AED",
        "vsc_ending": "#A78BFA",
        "unknown": "#6B7280",
    }
    events = status_intervals[["event_name"]].drop_duplicates()["event_name"].tolist()
    y_lookup = {event: index for index, event in enumerate(events)}
    fig, ax = plt.subplots(figsize=(13, max(7, len(events) * 0.32)))
    for label, color in colors.items():
        subset = status_intervals[status_intervals["status_label"] == label]
        if subset.empty:
            continue
        ax.hlines(
            y=subset["event_name"].map(y_lookup),
            xmin=subset["start_ms"] / 60_000,
            xmax=subset["end_ms"] / 60_000,
            color=color,
            linewidth=4,
            label=label,
            alpha=0.9,
        )
    ax.set_yticks(range(len(events)))
    ax.set_yticklabels(events)
    ax.invert_yaxis()
    ax.set_xlabel("Session time (minutes)")
    ax.set_ylabel("")
    ax.set_title("2025 track-status intervals")
    ax.grid(axis="x", alpha=0.2)
    ax.legend(loc="upper center", bbox_to_anchor=(0.5, -0.08), ncols=4, frameon=False)
    fig.tight_layout()
    fig.savefig(path, format="svg")
    plt.close(fig)
    return path


def plot_weather_cadence_jumps(weather_summary: pd.DataFrame) -> Path:
    path = FIGURE_DIR / "weather_cadence_jumps.svg"
    plot_df = weather_summary.sort_values("max_gap_ms", ascending=False).copy()
    fig, axes = plt.subplots(1, 2, figsize=(13, max(6, len(plot_df) * 0.25)), sharey=True)
    axes[0].barh(plot_df["event_name"], plot_df["max_gap_ms"] / 60_000, color="#2563EB")
    axes[0].set_title("Weather max sample gap")
    axes[0].set_xlabel("Minutes")
    axes[0].invert_yaxis()
    jump_cols = [
        "max_air_temp_delta_c",
        "max_track_temp_delta_c",
        "max_humidity_delta_pct",
        "max_pressure_delta_mbar",
        "max_wind_speed_delta_mps",
    ]
    jump_score = plot_df[jump_cols].fillna(0).max(axis=1)
    axes[1].barh(plot_df["event_name"], jump_score, color="#059669")
    axes[1].set_title("Largest weather value jump")
    axes[1].set_xlabel("Native units")
    fig.tight_layout()
    fig.savefig(path, format="svg")
    plt.close(fig)
    return path


def plot_context_timeline_density(context_timeline_bins: pd.DataFrame) -> Path:
    path = FIGURE_DIR / "context_timeline_density.svg"
    plot_df = context_timeline_bins.copy()
    plot_df["context_score"] = (
        plot_df["incident_messages"]
        + plot_df["rainfall_transitions"] * 2
        + plot_df["has_status_context"].astype(int) * 2
    )
    heat = plot_df.pivot_table(index="event_name", columns="bin_start_ms", values="context_score", aggfunc="max", fill_value=0)
    events = plot_df[["event_name"]].drop_duplicates()["event_name"].tolist()
    heat = heat.loc[events]
    fig, ax = plt.subplots(figsize=(14, max(7, len(heat) * 0.3)))
    sns.heatmap(
        heat,
        cmap=sns.color_palette(["#F9FAFB", "#DBEAFE", "#FDE68A", "#F97316", "#B42318"], as_cmap=True),
        linewidths=0.1,
        linecolor="white",
        cbar_kws={"label": "Context event density score"},
        ax=ax,
    )
    ax.set_title("5-minute context-event density by 2025 race")
    ax.set_xlabel("5-minute bin from session start")
    ax.set_ylabel("")
    tick_positions = ax.get_xticks()
    labels = []
    columns = list(heat.columns)
    for pos in tick_positions:
        index = int(round(pos - 0.5))
        labels.append(f"{int(columns[index] / 60_000)}" if 0 <= index < len(columns) else "")
    ax.set_xticklabels(labels, rotation=0)
    fig.tight_layout()
    fig.savefig(path, format="svg")
    plt.close(fig)
    return path


def assert_expected_scope(classified: pd.DataFrame, scope: SurfaceScope) -> None:
    actual = len(classified)
    if actual != scope.expected_sessions:
        raise RuntimeError(
            f"Expected {scope.expected_sessions} {scope.label} for season-level conclusions, found {actual}. "
            "Check imports before regenerating the surface EDA outputs."
        )


def save_table(df: pd.DataFrame, name: str) -> Path:
    path = TABLE_DIR / name
    if path.suffix == ".csv":
        df.to_csv(path, index=False)
    elif path.suffix == ".parquet":
        df.to_parquet(path, index=False)
    else:
        raise ValueError(f"Unsupported suffix: {path.suffix}")
    return path


def write_skrub_report(classified: pd.DataFrame) -> Path:
    report_cols = [
        "year",
        "event_name",
        "driver_count",
        "lap_rows",
        "telemetry_samples",
        "position_samples",
        "aligned_samples",
        "weather_samples",
        "track_status_events",
        "session_status_events",
        "race_control_messages",
        "corner_markers",
        "surface_issue_count",
        *SURFACE_FLAG_COLUMNS,
    ]
    path = ARTIFACT_DIR / "skrub_2025_race_database_surface_report.html"
    report = skrub.TableReport(
        classified[report_cols],
        title="2025 race-session database surface quality",
        n_rows=20,
        order_by="year",
        compute_associations=False,
        plot_distributions=False,
        verbose=0,
    )
    report.write_html(path)
    return path


def plot_surface_availability(classified: pd.DataFrame) -> Path:
    path = FIGURE_DIR / "surface_availability_heatmap.svg"
    columns = {
        "drivers": "driver_count",
        "laps": "lap_rows",
        "raw car": "telemetry_samples",
        "position": "position_samples",
        "aligned 10Hz": "aligned_samples",
        "weather": "weather_samples",
        "track status": "track_status_events",
        "session status": "session_status_events",
        "race control": "race_control_messages",
        "corners": "corner_markers",
    }
    heat = pd.DataFrame({label: (classified[col] > 0).astype(int) for label, col in columns.items()})
    heat.index = classified["year"].astype(str) + " " + classified["event_name"]
    fig, ax = plt.subplots(figsize=(10.5, max(7, len(heat) * 0.28)))
    sns.heatmap(heat, cmap=sns.color_palette(["#F3F4F6", "#2563EB"], as_cmap=True), cbar=False, linewidths=0.4, linecolor="white", ax=ax)
    ax.set_title("2025 race-session surface availability")
    ax.set_xlabel("")
    ax.set_ylabel("")
    fig.tight_layout()
    fig.savefig(path, format="svg")
    plt.close(fig)
    return path


def plot_surface_issues(flag_summary: pd.DataFrame) -> Path:
    path = FIGURE_DIR / "surface_issue_counts.svg"
    plot_df = flag_summary.sort_values("sessions", ascending=True)
    fig, ax = plt.subplots(figsize=(10, 5.8))
    ax.barh(plot_df["surface_flag"], plot_df["sessions"], color="#546A7B")
    ax.set_title("2025 race-session quality flags by imported data surface")
    ax.set_xlabel("Sessions flagged")
    ax.set_ylabel("")
    for index, row in enumerate(plot_df.itertuples()):
        ax.text(row.sessions + 0.15, index, f"{row.sessions} ({row.pct_sessions:.1f}%)", va="center", fontsize=9)
    fig.tight_layout()
    fig.savefig(path, format="svg")
    plt.close(fig)
    return path


def plot_context_density(classified: pd.DataFrame) -> Path:
    path = FIGURE_DIR / "context_density_by_session.svg"
    metrics = classified[[
        "year",
        "event_name",
        "weather_samples",
        "track_status_events",
        "session_status_events",
        "race_control_messages",
        "corner_markers",
    ]].copy()
    metrics["session"] = metrics["year"].astype(str) + " " + metrics["event_name"]
    plot = metrics.set_index("session").drop(columns=["year", "event_name"])
    plot = np.log10(plot + 1)
    fig, ax = plt.subplots(figsize=(9.5, max(7, len(plot) * 0.28)))
    sns.heatmap(plot, cmap="viridis", linewidths=0.4, linecolor="white", cbar_kws={"label": "log10(count + 1)"}, ax=ax)
    ax.set_title("Context surface density by 2025 race")
    ax.set_xlabel("")
    ax.set_ylabel("")
    fig.tight_layout()
    fig.savefig(path, format="svg")
    plt.close(fig)
    return path


def plot_ingestion_frequency(diagnostics: pd.DataFrame) -> Path:
    path = FIGURE_DIR / "ingestion_frequency_by_stream.svg"
    plot_df = diagnostics.dropna(subset=["estimated_frequency_hz"]).copy()
    fig, ax = plt.subplots(figsize=(9, 5.5))
    sns.boxplot(data=plot_df, x="stream_name", y="estimated_frequency_hz", hue="year", ax=ax)
    ax.set_title("2025 telemetry stream frequency diagnostics")
    ax.set_xlabel("Stream")
    ax.set_ylabel("Estimated frequency (Hz)")
    ax.grid(axis="y", alpha=0.25)
    fig.tight_layout()
    fig.savefig(path, format="svg")
    plt.close(fig)
    return path


def plot_race_control_categories(categories: pd.DataFrame) -> Path:
    path = FIGURE_DIR / "race_control_category_mix.svg"
    top = (
        categories.groupby(["year", "category"], dropna=False)["messages"]
        .sum()
        .reset_index()
        .sort_values("messages", ascending=False)
    )
    top_categories = top.groupby("category")["messages"].sum().nlargest(8).index
    top = top[top["category"].isin(top_categories)]
    fig, ax = plt.subplots(figsize=(10, 5.8))
    sns.barplot(data=top, x="messages", y="category", hue="year", ax=ax)
    ax.set_title("Race-control message category mix")
    ax.set_xlabel("Messages")
    ax.set_ylabel("")
    fig.tight_layout()
    fig.savefig(path, format="svg")
    plt.close(fig)
    return path


def plot_aligned_driver_heatmap(aligned_drivers: pd.DataFrame) -> Path:
    path = FIGURE_DIR / "aligned_driver_non_ok_heatmap.svg"
    plot_df = aligned_drivers.pivot_table(
        index="event_name",
        columns="driver_code",
        values="non_ok_pct",
        aggfunc="max",
        fill_value=0,
    )
    race_order = (
        aligned_drivers[["event_name", "year"]]
        .drop_duplicates()
        .assign(event_order=lambda df: range(len(df)))
        .set_index("event_name")["event_order"]
    )
    plot_df = plot_df.loc[sorted(plot_df.index, key=lambda event: race_order.get(event, 999))]
    fig, ax = plt.subplots(figsize=(13, max(7, len(plot_df) * 0.28)))
    sns.heatmap(
        plot_df,
        cmap=sns.color_palette(["#F9FAFB", "#FDE68A", "#F97316", "#B42318"], as_cmap=True),
        linewidths=0.25,
        linecolor="white",
        cbar_kws={"label": "Non-OK aligned rows (%)"},
        ax=ax,
    )
    ax.set_title("Aligned replay non-OK percentage by 2025 race and driver")
    ax.set_xlabel("Driver")
    ax.set_ylabel("")
    fig.tight_layout()
    fig.savefig(path, format="svg")
    plt.close(fig)
    return path


def _window_dominant_family(row: pd.Series) -> str:
    family_counts = {
        "car_gap_too_large": row.get("car_gap_rows", 0),
        "car_sample_too_old": row.get("car_sample_old_rows", 0),
        "location_gap_too_large": row.get("location_gap_rows", 0),
        "location_sample_too_old": row.get("location_sample_old_rows", 0),
        "other_unknown": row.get("other_unknown_rows", 0),
    }
    if row.get("non_ok_rows", 0) <= 0:
        return "OK"
    return max(family_counts, key=family_counts.get)


def plot_aligned_quality_strips(
    aligned_windows: pd.DataFrame,
    watchlist: pd.DataFrame,
    max_pairs: int = 10,
) -> Path:
    path = FIGURE_DIR / "aligned_quality_replay_strips.svg"
    selected = watchlist.head(max_pairs)[["session_id", "event_name", "driver_code"]].copy()
    selected["row_label"] = selected["event_name"] + " / " + selected["driver_code"]
    plot_df = aligned_windows.merge(selected, on=["session_id", "event_name", "driver_code"], how="inner")
    plot_df = plot_df.copy()
    plot_df["dominant_family"] = plot_df.apply(_window_dominant_family, axis=1)
    labels = selected["row_label"].tolist()
    y_lookup = {label: index for index, label in enumerate(labels)}
    plot_df["y"] = plot_df["row_label"].map(y_lookup)
    fig, ax = plt.subplots(figsize=(13, max(4.5, len(labels) * 0.38)))
    for family in QUALITY_FAMILY_ORDER:
        subset = plot_df[plot_df["dominant_family"] == family]
        if subset.empty:
            continue
        ax.scatter(
            subset["window_start_ms"] / 60_000,
            subset["y"],
            s=np.clip(subset["non_ok_pct"].fillna(0) * 10 + 8, 8, 90),
            color=QUALITY_FAMILY_COLORS[family],
            label=family,
            alpha=0.85 if family != "OK" else 0.35,
            linewidths=0,
        )
    context_subset = plot_df[plot_df["has_incident_context"]]
    if not context_subset.empty:
        ax.scatter(
            context_subset["window_start_ms"] / 60_000,
            context_subset["y"] + 0.18,
            marker="|",
            s=80,
            color="#111827",
            label="status/race-control context",
            alpha=0.75,
        )
    pit_subset = plot_df[plot_df["has_pit_context"]]
    if not pit_subset.empty:
        ax.scatter(
            pit_subset["window_start_ms"] / 60_000,
            pit_subset["y"] - 0.18,
            marker="v",
            s=18,
            color="#047857",
            label="pit lap",
            alpha=0.75,
        )
    ax.set_yticks(range(len(labels)))
    ax.set_yticklabels(labels)
    ax.invert_yaxis()
    ax.set_xlabel("Session time (minutes)")
    ax.set_ylabel("")
    ax.set_title("Representative 30s replay-quality strips for highest-risk race/driver pairs")
    ax.grid(axis="x", alpha=0.2)
    ax.legend(loc="upper center", bbox_to_anchor=(0.5, -0.12), ncols=3, frameon=False)
    fig.tight_layout()
    fig.savefig(path, format="svg")
    plt.close(fig)
    return path


def plot_aligned_context_overlap(context_overlap: pd.DataFrame) -> Path:
    path = FIGURE_DIR / "aligned_context_overlap.svg"
    plot_df = context_overlap.melt(
        id_vars=["window_bucket"],
        value_vars=["pct_with_race_control", "pct_with_status", "pct_with_pit_context", "pct_with_incident_context"],
        var_name="context_type",
        value_name="pct_windows",
    )
    fig, ax = plt.subplots(figsize=(9, 5.2))
    sns.barplot(data=plot_df, x="window_bucket", y="pct_windows", hue="context_type", ax=ax)
    ax.set_title("Race-control/status overlap for degraded replay windows")
    ax.set_xlabel("")
    ax.set_ylabel("Windows with context (%)")
    ax.set_ylim(0, max(5, plot_df["pct_windows"].max() * 1.2))
    ax.legend(title="", loc="upper left", frameon=False)
    fig.tight_layout()
    fig.savefig(path, format="svg")
    plt.close(fig)
    return path


def plot_surface_coverage_heatmap(coverage_windows: pd.DataFrame) -> Path:
    path = FIGURE_DIR / "surface_active_coverage_heatmap.svg"
    plot_df = coverage_windows.pivot_table(
        index="event_name",
        columns="surface",
        values="active_coverage_ratio",
        aggfunc="max",
        fill_value=0,
    )
    event_order = (
        coverage_windows[["event_name"]]
        .drop_duplicates()
        .assign(event_order=lambda df: range(len(df)))
        .set_index("event_name")["event_order"]
    )
    surface_order = ["raw telemetry", "raw position", "aligned replay", "weather", "track status", "session status", "race control"]
    plot_df = plot_df.loc[sorted(plot_df.index, key=lambda event: event_order.get(event, 999)), surface_order]
    fig, ax = plt.subplots(figsize=(11, max(7, len(plot_df) * 0.3)))
    sns.heatmap(
        plot_df,
        cmap=sns.color_palette(["#FEE2E2", "#FEF3C7", "#D1FAE5", "#047857"], as_cmap=True),
        vmin=0,
        vmax=1,
        linewidths=0.35,
        linecolor="white",
        cbar_kws={"label": "Active replay-window coverage ratio"},
        ax=ax,
    )
    ax.set_title("2025 surface coverage over active replay window")
    ax.set_xlabel("")
    ax.set_ylabel("")
    fig.tight_layout()
    fig.savefig(path, format="svg")
    plt.close(fig)
    return path


def plot_surface_coverage_windows(coverage_windows: pd.DataFrame) -> Path:
    path = FIGURE_DIR / "surface_coverage_windows.svg"
    surface_order = ["raw telemetry", "raw position", "aligned replay", "weather", "track status", "session status", "race control"]
    colors = {
        "raw telemetry": "#2563EB",
        "raw position": "#7C3AED",
        "aligned replay": "#0F766E",
        "weather": "#059669",
        "track status": "#F97316",
        "session status": "#111827",
        "race control": "#B42318",
    }
    event_order = coverage_windows[["event_name"]].drop_duplicates()["event_name"].tolist()
    event_y = {event: index for index, event in enumerate(event_order)}
    offsets = np.linspace(-0.28, 0.28, len(surface_order))
    offset_lookup = dict(zip(surface_order, offsets, strict=True))

    fig, ax = plt.subplots(figsize=(14, max(8, len(event_order) * 0.42)))
    for surface in surface_order:
        subset = coverage_windows[(coverage_windows["surface"] == surface) & coverage_windows["start_ms"].notna() & coverage_windows["end_ms"].notna()]
        y_values = subset["event_name"].map(event_y) + offset_lookup[surface]
        ax.hlines(
            y=y_values,
            xmin=subset["start_ms"] / 60_000,
            xmax=subset["end_ms"] / 60_000,
            colors=colors[surface],
            linewidth=2.2,
            label=surface,
            alpha=0.9,
        )
    active = coverage_windows[coverage_windows["surface"] == "aligned replay"]
    if not active.empty:
        for row in active.itertuples():
            y = event_y[row.event_name]
            ax.hlines(
                y=y,
                xmin=row.active_replay_start_ms / 60_000,
                xmax=row.active_replay_end_ms / 60_000,
                colors="#9CA3AF",
                linewidth=7,
                alpha=0.12,
            )
    ax.set_yticks(range(len(event_order)))
    ax.set_yticklabels(event_order)
    ax.invert_yaxis()
    ax.set_xlabel("Session time (minutes)")
    ax.set_ylabel("")
    ax.set_title("Surface coverage windows by 2025 race")
    ax.grid(axis="x", alpha=0.18)
    ax.legend(loc="upper center", bbox_to_anchor=(0.5, -0.08), ncols=4, frameon=False)
    fig.tight_layout()
    fig.savefig(path, format="svg")
    plt.close(fig)
    return path


def build_markdown_summary(
    classified: pd.DataFrame,
    flag_summary: pd.DataFrame,
    year_summary: pd.DataFrame,
    diagnostics: pd.DataFrame,
    aligned_flags: pd.DataFrame,
    race_control_categories: pd.DataFrame,
    aligned_races: pd.DataFrame,
    aligned_drivers: pd.DataFrame,
    aligned_laps: pd.DataFrame,
    degraded_segments: pd.DataFrame,
    aligned_windows: pd.DataFrame,
    aligned_context_overlap: pd.DataFrame,
    aligned_lap_context: pd.DataFrame,
    desktop_watchlist: pd.DataFrame,
    session_duration_coverage: pd.DataFrame,
    coverage_windows: pd.DataFrame,
    coverage_summary: pd.DataFrame,
    race_control_messages: pd.DataFrame,
    race_control_taxonomy_summary: pd.DataFrame,
    race_control_duplicates: pd.DataFrame,
    race_control_examples: pd.DataFrame,
    status_intervals: pd.DataFrame,
    status_race_control_overlap: pd.DataFrame,
    weather_summary: pd.DataFrame,
    weather_transitions: pd.DataFrame,
    context_timeline_bins: pd.DataFrame,
    context_replay_correlation: pd.DataFrame,
    product_readiness: pd.DataFrame,
    recommendation_summary: pd.DataFrame,
    marker_summary: pd.DataFrame,
    marker_quality: pd.DataFrame,
    race_control_cluster_summary: pd.DataFrame,
    figure_paths: list[Path],
    report_path: Path,
    scope: SurfaceScope = SurfaceScope(),
) -> str:
    total_sessions = len(classified)
    issue_sessions = int(classified["has_surface_issue"].sum())
    warning_streams = int((diagnostics["warning_flags"].fillna("") != "").sum())
    non_ok_aligned_rows = int(classified["aligned_non_ok_rows"].sum())
    total_aligned = int(classified["aligned_samples"].sum())

    top_flags = aligned_flags.groupby("quality_flag")["rows"].sum().sort_values(ascending=False).head(10)
    top_rc = race_control_categories.groupby("category")["messages"].sum().sort_values(ascending=False).head(10)
    degraded_windows = int((aligned_windows["non_ok_pct"] >= SurfaceThresholds().degraded_window_min_non_ok_pct).sum())
    severe_windows = int((aligned_windows["non_ok_pct"] >= SurfaceThresholds().severe_window_min_non_ok_pct).sum())
    longest_segment_ms = int(degraded_segments["duration_ms"].max()) if not degraded_segments.empty else 0
    session_end_missing = int(session_duration_coverage["session_end_missing_known_limitation"].sum())
    weather_min_active_coverage = float(
        coverage_summary.loc[coverage_summary["surface"] == "weather", "min_active_coverage_ratio"].iloc[0]
    )
    race_control_median_active_coverage = float(
        coverage_summary.loc[coverage_summary["surface"] == "race control", "median_active_coverage_ratio"].iloc[0]
    )
    duplicate_message_groups = int(len(race_control_duplicates))
    rainfall_transition_count = int(len(weather_transitions))
    marker_geometry_issues = int(marker_summary["markers_outside_position_bounds"].sum()) if not marker_summary.empty else 0
    sessions_needing_schema_change = int(product_readiness["schema_importer_follow_up"].sum()) if not product_readiness.empty else 0
    readiness_counts = (
        product_readiness["final_recommendation"].value_counts().reindex(
            ["no_action", "label_in_ui", "inspect", "reimport", "schema_importer_change"],
            fill_value=0,
        )
        if not product_readiness.empty
        else pd.Series(dtype=int)
    )
    context_degraded_delta = 0.0
    if not context_replay_correlation.empty:
        corr_lookup = context_replay_correlation.set_index("bin_bucket")
        if {"context_event_bins", "no_context_event_bins"}.issubset(corr_lookup.index):
            context_degraded_delta = (
                corr_lookup.loc["context_event_bins", "median_degraded_window_rate"]
                - corr_lookup.loc["no_context_event_bins", "median_degraded_window_rate"]
            )

    lines = [
        "# 2025 race database-surface EDA summary",
        "",
        f"Scope: `{scope.year}` race sessions (`session_type = '{scope.session_type}'`) in the local TimescaleDB.",
        "",
        f"Guardrail: this summary is generated only when exactly {scope.expected_sessions} 2025 race sessions are present.",
        "",
        "## Data availability",
        "",
        f"- Race sessions inspected: {total_sessions}",
        f"- Sessions with at least one surface quality flag: {issue_sessions} ({issue_sessions / total_sessions * 100:.1f}%)",
        f"- Raw telemetry samples: {int(classified['telemetry_samples'].sum()):,}",
        f"- Raw position samples: {int(classified['position_samples'].sum()):,}",
        f"- Aligned 10 Hz samples: {total_aligned:,}",
        f"- Aligned rows without `OK`: {non_ok_aligned_rows:,} ({non_ok_aligned_rows / total_aligned * 100 if total_aligned else 0:.2f}%)",
        f"- Ingestion diagnostic streams with warnings: {warning_streams:,}",
        f"- Skrub report: `{report_path.relative_to(REPO_ROOT)}`",
        "",
        "## Year summary",
        "",
        year_summary.to_markdown(index=False, floatfmt=".2f"),
        "",
        "## Surface quality flags",
        "",
        flag_summary.to_markdown(index=False, floatfmt=".2f"),
        "",
        "## Highest-issue sessions",
        "",
        classified.sort_values(["surface_issue_count", "year", "event_name"], ascending=[False, True, True])[
            [
                "year",
                "event_name",
                "surface_issue_count",
                "driver_count",
                "lap_rows",
                "telemetry_driver_count",
                "position_driver_count",
                "aligned_driver_count",
                "weather_samples",
                "race_control_messages",
                "corner_markers",
            ]
        ].head(15).to_markdown(index=False, floatfmt=".2f"),
        "",
        "## Top aligned quality flags",
        "",
        top_flags.reset_index(name="rows").to_markdown(index=False),
        "",
        "## Aligned replay quality by race",
        "",
        aligned_races[
            [
                "event_name",
                "aligned_rows",
                "non_ok_rows",
                "non_ok_pct",
                "car_related_pct_of_non_ok",
                "location_related_pct_of_non_ok",
            ]
        ].sort_values("non_ok_pct", ascending=False).head(12).to_markdown(index=False, floatfmt=".2f"),
        "",
        "## Highest-risk replay driver pairs",
        "",
        desktop_watchlist[
            [
                "event_name",
                "driver_code",
                "non_ok_pct",
                "max_window_non_ok_pct",
                "longest_segment_ms",
                "degraded_windows",
                "severe_windows",
                "desktop_guidance",
            ]
        ].head(15).to_markdown(index=False, floatfmt=".2f"),
        "",
        "## Aligned replay window/context overlap",
        "",
        aligned_context_overlap.to_markdown(index=False, floatfmt=".2f"),
        "",
        "## Lap context and aligned replay quality",
        "",
        aligned_lap_context.to_markdown(index=False, floatfmt=".2f"),
        "",
        "## Session duration and surface coverage",
        "",
        session_duration_coverage[
            [
                "event_name",
                "derived_session_duration_ms",
                "duration_source",
                "active_replay_start_ms",
                "active_replay_end_ms",
                "active_replay_duration_ms",
                "finished_to_derived_end_gap_ms",
            ]
        ].to_markdown(index=False, floatfmt=".0f"),
        "",
        "## Surface coverage over active replay windows",
        "",
        coverage_summary.to_markdown(index=False, floatfmt=".2f"),
        "",
        "## Top race-control categories",
        "",
        top_rc.reset_index(name="messages").to_markdown(index=False),
        "",
        "## Race-control taxonomy",
        "",
        race_control_taxonomy_summary.to_markdown(index=False, floatfmt=".2f"),
        "",
        "## Race-control duplicate groups",
        "",
        race_control_duplicates[
            [
                "event_name",
                "messages",
                "span_ms",
                "taxonomy",
                "example_message",
            ]
        ].head(15).to_markdown(index=False, floatfmt=".0f"),
        "",
        "## Status/race-control overlap",
        "",
        status_race_control_overlap.sort_values(["incident_messages", "race_control_messages"], ascending=False)[
            [
                "event_name",
                "status_label",
                "start_ms",
                "end_ms",
                "duration_ms",
                "race_control_messages",
                "incident_messages",
                "taxonomies",
            ]
        ].head(20).to_markdown(index=False, floatfmt=".0f"),
        "",
        "## Weather cadence and jumps",
        "",
        weather_summary[
            [
                "event_name",
                "samples",
                "median_gap_ms",
                "max_gap_ms",
                "max_track_temp_delta_c",
                "max_humidity_delta_pct",
                "rainfall_samples",
                "rainfall_transitions",
                "large_gap_flag",
                "temperature_jump_flag",
                "wind_jump_flag",
            ]
        ].sort_values(["large_gap_flag", "rainfall_transitions", "max_gap_ms"], ascending=[False, False, False]).head(15).to_markdown(index=False, floatfmt=".2f"),
        "",
        "## Context timeline/replay correlation",
        "",
        context_replay_correlation.to_markdown(index=False, floatfmt=".2f"),
        "",
        "## Product-readiness recommendations",
        "",
        recommendation_summary.to_markdown(index=False, floatfmt=".2f"),
        "",
        product_readiness[
            [
                "event_name",
                "catalog_readiness",
                "raw_stream_readiness",
                "replay_readiness",
                "context_readiness",
                "circuit_context_readiness",
                "final_recommendation",
                "schema_importer_follow_up",
                "product_impact",
                "systematic_known_limitations",
            ]
        ].to_markdown(index=False, floatfmt=".2f"),
        "",
        "## Circuit marker geometry QA",
        "",
        marker_summary[
            [
                "event_name",
                "marker_count",
                "corner_markers",
                "marshal_light_markers",
                "marshal_sector_markers",
                "marker_distance_nulls",
                "markers_outside_position_bounds",
                "markers_outside_core_bounds",
                "circuit_context_readiness",
                "circuit_context_recommendation",
            ]
        ].to_markdown(index=False, floatfmt=".2f"),
        "",
        "## Race-control text clusters",
        "",
        race_control_cluster_summary.head(12)[
            [
                "text_cluster",
                "messages",
                "sessions",
                "cluster_terms",
                "taxonomy_mix",
                "example_message",
            ]
        ].to_markdown(index=False, floatfmt=".2f"),
        "",
        "## Visual artifacts",
        "",
    ]
    for path in figure_paths:
        lines.append(f"- `{path.relative_to(REPO_ROOT)}`")
    lines.extend(
        [
            "",
            "## Findings to carry forward",
            "",
            "- `session_end_utc` is absent for imported sessions. This does not block replay because session-relative times exist, but it limits session-duration QA without deriving an end from samples/status.",
            "- The 2025 race season is complete locally and is the only scope represented in these outputs.",
            "- The non-lap context surfaces are populated enough for replay storytelling: weather, status timelines, race control, and circuit markers are present across the 2025 races.",
            "- Aligned 10 Hz telemetry should be audited separately from raw telemetry because replay quality depends on interpolation and quality flags, not just raw sample counts.",
            f"- `session_end_utc` is missing for {session_end_missing} sessions, but session duration can be derived from imported samples and status events for all 24 races.",
            f"- Weather covers at least {weather_min_active_coverage * 100:.1f}% of each active replay window; race-control messages cover a median {race_control_median_active_coverage * 100:.1f}% time span and should be treated as event markers, not continuous context.",
            "- Raw position coverage in this duration table is approximated from UTC offsets because `position_samples` does not store `session_time_ms`; use this as a schema/importer follow-up signal before surfacing user-facing position warnings.",
            f"- Race-control taxonomy produced {len(race_control_taxonomy_summary)} deterministic buckets and {duplicate_message_groups} repeated-message groups for deduplication review.",
            f"- Race-control text clustering produced {len(race_control_cluster_summary)} clusters for incident/noise review.",
            f"- Weather cadence is mostly regular; rainfall transitions found: {rainfall_transition_count}. Weather transitions should be available as timeline markers in the desktop context strip.",
            f"- Circuit-marker geometry QA flagged {marker_geometry_issues} markers outside padded imported position bounds; flagged markers should be inspected before using them as desktop track callouts.",
            f"- Primary final recommendations: no action={int(readiness_counts.get('no_action', 0))}, label in UI={int(readiness_counts.get('label_in_ui', 0))}, inspect={int(readiness_counts.get('inspect', 0))}, reimport={int(readiness_counts.get('reimport', 0))}, schema/importer change={int(readiness_counts.get('schema_importer_change', 0))}. Supporting schema/importer follow-up is present for {sessions_needing_schema_change} sessions.",
            f"- Median degraded replay-window rate is {context_degraded_delta:.2f} percentage points different between context-event and no-context bins; current evidence does not justify treating context events as the main cause of aligned-quality degradation.",
            f"- 30-second replay windows with at least 1% non-OK aligned rows: {degraded_windows:,}; windows with at least 10% non-OK rows: {severe_windows:,}.",
            f"- Longest consecutive degraded aligned segment found: {longest_segment_ms:,} ms.",
            "- The desktop app should treat aligned quality as a diagnostics overlay first, with warnings reserved for sustained or repeated degraded windows rather than isolated stale rows.",
            "",
            "## Recommended next analyses",
            "",
            "- Add raw stream ingestion severity tables that separate normal FastF1 cadence from importer/source problems.",
            "- Decide whether the readiness labels should be persisted in the database or remain offline diagnostics.",
            "- Decide whether session-level quality summaries should be persisted or remain offline notebook diagnostics.",
        ]
    )
    return "\n".join(lines) + "\n"


def run_analysis(
    url: str | None = None,
    write_outputs: bool = True,
    scope: SurfaceScope = SurfaceScope(),
) -> dict[str, Any]:
    thresholds = SurfaceThresholds()
    features = load_session_surface_features(url, scope)
    assert_expected_scope(features, scope)
    diagnostics = load_ingestion_diagnostics(url, scope)
    classified = add_quality_flags(features, thresholds, diagnostics)
    aligned_flags = load_aligned_quality_flags(url, scope)
    race_control_categories = load_race_control_categories(url, scope)
    aligned_races = load_aligned_quality_by_race(url, scope)
    aligned_drivers = load_aligned_quality_by_driver(url, scope)
    aligned_laps = load_aligned_quality_by_lap(url, scope)
    degraded_segments = load_aligned_degraded_segments(url, scope)
    aligned_windows = load_aligned_quality_windows(url, scope)
    aligned_context_overlap = summarize_aligned_context_overlap(aligned_windows, thresholds)
    aligned_lap_context = summarize_aligned_lap_context(aligned_laps, thresholds)
    desktop_watchlist = top_aligned_desktop_watchlist(aligned_drivers, degraded_segments, aligned_windows, thresholds)
    session_duration_coverage = load_session_duration_coverage(url, scope)
    coverage_windows = build_surface_coverage_windows(session_duration_coverage)
    coverage_summary = summarize_surface_coverage(coverage_windows)
    race_control_messages = load_race_control_messages_detailed(url, scope)
    race_control_taxonomy_summary = summarize_race_control_taxonomy(race_control_messages)
    race_control_duplicates = find_race_control_duplicates(race_control_messages)
    race_control_examples = preserve_race_control_examples(race_control_messages)
    status_events = load_track_status_events_detailed(url, scope)
    status_intervals = build_track_status_intervals(status_events, session_duration_coverage)
    status_race_control_overlap = summarize_status_race_control_overlap(status_intervals, race_control_messages)
    weather_samples = load_weather_samples_detailed(url, scope)
    weather_summary, weather_transitions = summarize_weather_cadence_and_jumps(weather_samples)
    context_timeline_bins = build_context_timeline_bins(
        race_control_messages,
        status_intervals,
        weather_transitions,
        aligned_windows,
    )
    context_replay_correlation = summarize_context_replay_correlation(context_timeline_bins)
    marker_quality = load_circuit_marker_quality(url, scope)
    marker_summary = summarize_circuit_marker_quality(marker_quality, thresholds)
    marker_example_sessions = select_circuit_marker_example_sessions(marker_summary)
    marker_position_examples = load_position_trace_samples_for_marker_examples(url, scope, marker_example_sessions)
    product_readiness, recommendation_summary = build_product_readiness(
        classified,
        aligned_races,
        aligned_windows,
        desktop_watchlist,
        session_duration_coverage,
        coverage_summary,
        marker_summary,
    )
    race_control_clustered, race_control_cluster_summary = cluster_race_control_text(race_control_messages)

    flag_summary = summarize_surface_flags(classified)
    year_summary = summarize_by_year(classified)
    report_path = write_skrub_report(classified)
    figure_paths = [
        plot_surface_availability(classified),
        plot_surface_issues(flag_summary),
        plot_context_density(classified),
        plot_ingestion_frequency(diagnostics),
        plot_race_control_categories(race_control_categories),
        plot_aligned_driver_heatmap(aligned_drivers),
        plot_aligned_quality_strips(aligned_windows, desktop_watchlist),
        plot_aligned_context_overlap(aligned_context_overlap),
        plot_surface_coverage_heatmap(coverage_windows),
        plot_surface_coverage_windows(coverage_windows),
        plot_race_control_taxonomy(race_control_taxonomy_summary),
        plot_status_timeline_strips(status_intervals),
        plot_weather_cadence_jumps(weather_summary),
        plot_context_timeline_density(context_timeline_bins),
        plot_product_readiness_dashboard(product_readiness),
        plot_product_recommendation_summary(recommendation_summary),
        plot_circuit_marker_quality(marker_summary),
        plot_circuit_marker_overlays(marker_quality, marker_position_examples),
        plot_weather_trend_panels(weather_samples, weather_summary),
        plot_race_control_text_clusters(race_control_cluster_summary),
    ]

    summary_path = None
    if write_outputs:
        save_table(classified, "session_surface_quality.csv")
        save_table(classified, "session_surface_quality.parquet")
        save_table(flag_summary, "surface_flag_summary.csv")
        save_table(year_summary, "year_summary.csv")
        save_table(diagnostics, "telemetry_ingestion_diagnostics.csv")
        save_table(aligned_flags, "aligned_quality_flags.csv")
        save_table(race_control_categories, "race_control_category_mix.csv")
        save_table(aligned_races, "aligned_quality_by_race.csv")
        save_table(aligned_races, "aligned_quality_by_race.parquet")
        save_table(aligned_drivers, "aligned_quality_by_driver.csv")
        save_table(aligned_drivers, "aligned_quality_by_driver.parquet")
        save_table(aligned_laps, "aligned_quality_by_lap.csv")
        save_table(aligned_laps, "aligned_quality_by_lap.parquet")
        save_table(degraded_segments, "aligned_degraded_segments.csv")
        save_table(degraded_segments, "aligned_degraded_segments.parquet")
        save_table(aligned_windows, "aligned_quality_windows_30s.csv")
        save_table(aligned_windows, "aligned_quality_windows_30s.parquet")
        save_table(aligned_context_overlap, "aligned_context_overlap.csv")
        save_table(aligned_lap_context, "aligned_lap_context.csv")
        save_table(desktop_watchlist, "desktop_replay_quality_watchlist.csv")
        save_table(desktop_watchlist, "desktop_replay_quality_watchlist.parquet")
        save_table(session_duration_coverage, "session_duration_coverage.csv")
        save_table(session_duration_coverage, "session_duration_coverage.parquet")
        save_table(coverage_windows, "session_surface_coverage_windows.csv")
        save_table(coverage_windows, "session_surface_coverage_windows.parquet")
        save_table(coverage_summary, "surface_coverage_summary.csv")
        save_table(race_control_messages, "race_control_messages_classified.csv")
        save_table(race_control_messages, "race_control_messages_classified.parquet")
        save_table(race_control_taxonomy_summary, "race_control_taxonomy_summary.csv")
        save_table(race_control_duplicates, "race_control_duplicate_messages.csv")
        save_table(race_control_examples, "race_control_taxonomy_examples.csv")
        save_table(status_intervals, "track_status_intervals.csv")
        save_table(status_intervals, "track_status_intervals.parquet")
        save_table(status_race_control_overlap, "status_race_control_overlap.csv")
        save_table(weather_summary, "weather_cadence_jumps.csv")
        save_table(weather_transitions, "weather_rainfall_transitions.csv")
        save_table(context_timeline_bins, "context_timeline_bins_5min.csv")
        save_table(context_timeline_bins, "context_timeline_bins_5min.parquet")
        save_table(context_replay_correlation, "context_replay_correlation.csv")
        save_table(product_readiness, "product_readiness.csv")
        save_table(product_readiness, "product_readiness.parquet")
        save_table(recommendation_summary, "product_recommendation_summary.csv")
        save_table(marker_quality, "circuit_marker_quality.csv")
        save_table(marker_summary, "circuit_marker_summary.csv")
        save_table(marker_position_examples, "circuit_marker_position_examples.csv")
        save_table(race_control_clustered, "race_control_text_clusters.csv")
        save_table(race_control_cluster_summary, "race_control_text_cluster_summary.csv")

        metadata = {
            "scope": scope.label,
            "year": scope.year,
            "session_type": scope.session_type,
            "session_count": int(len(classified)),
            "expected_sessions": scope.expected_sessions,
            "skrub_version": skrub.__version__,
            "thresholds": thresholds.__dict__,
            "table_row_counts": {
                "session_surface_quality": int(len(classified)),
                "telemetry_ingestion_diagnostics": int(len(diagnostics)),
                "aligned_quality_flags": int(len(aligned_flags)),
                "aligned_quality_by_race": int(len(aligned_races)),
                "aligned_quality_by_driver": int(len(aligned_drivers)),
                "aligned_quality_by_lap": int(len(aligned_laps)),
                "aligned_degraded_segments": int(len(degraded_segments)),
                "aligned_quality_windows_30s": int(len(aligned_windows)),
                "session_duration_coverage": int(len(session_duration_coverage)),
                "session_surface_coverage_windows": int(len(coverage_windows)),
                "race_control_category_mix": int(len(race_control_categories)),
                "race_control_messages_classified": int(len(race_control_messages)),
                "race_control_duplicate_messages": int(len(race_control_duplicates)),
                "track_status_intervals": int(len(status_intervals)),
                "weather_cadence_jumps": int(len(weather_summary)),
                "weather_rainfall_transitions": int(len(weather_transitions)),
                "context_timeline_bins_5min": int(len(context_timeline_bins)),
                "product_readiness": int(len(product_readiness)),
                "product_recommendation_summary": int(len(recommendation_summary)),
                "circuit_marker_quality": int(len(marker_quality)),
                "circuit_marker_summary": int(len(marker_summary)),
                "circuit_marker_position_examples": int(len(marker_position_examples)),
                "race_control_text_clusters": int(len(race_control_clustered)),
                "race_control_text_cluster_summary": int(len(race_control_cluster_summary)),
            },
            "figure_paths": [str(path.relative_to(REPO_ROOT)) for path in figure_paths],
            "report_path": str(report_path.relative_to(REPO_ROOT)),
        }
        (ARTIFACT_DIR / "metadata.json").write_text(json.dumps(metadata, indent=2), encoding="utf-8")

        summary = build_markdown_summary(
            classified,
            flag_summary,
            year_summary,
            diagnostics,
            aligned_flags,
            race_control_categories,
            aligned_races,
            aligned_drivers,
            aligned_laps,
            degraded_segments,
            aligned_windows,
            aligned_context_overlap,
            aligned_lap_context,
            desktop_watchlist,
            session_duration_coverage,
            coverage_windows,
            coverage_summary,
            race_control_messages,
            race_control_taxonomy_summary,
            race_control_duplicates,
            race_control_examples,
            status_intervals,
            status_race_control_overlap,
            weather_summary,
            weather_transitions,
            context_timeline_bins,
            context_replay_correlation,
            product_readiness,
            recommendation_summary,
            marker_summary,
            marker_quality,
            race_control_cluster_summary,
            figure_paths,
            report_path,
            scope,
        )
        summary_path = REPO_ROOT / "docs" / "data-quality" / "2025-race-database-surface-eda-summary.md"
        summary_path.parent.mkdir(parents=True, exist_ok=True)
        summary_path.write_text(summary, encoding="utf-8")

    return {
        "thresholds": thresholds,
        "scope": scope,
        "features": features,
        "classified": classified,
        "diagnostics": diagnostics,
        "aligned_flags": aligned_flags,
        "race_control_categories": race_control_categories,
        "aligned_races": aligned_races,
        "aligned_drivers": aligned_drivers,
        "aligned_laps": aligned_laps,
        "degraded_segments": degraded_segments,
        "aligned_windows": aligned_windows,
        "aligned_context_overlap": aligned_context_overlap,
        "aligned_lap_context": aligned_lap_context,
        "desktop_watchlist": desktop_watchlist,
        "session_duration_coverage": session_duration_coverage,
        "coverage_windows": coverage_windows,
        "coverage_summary": coverage_summary,
        "race_control_messages": race_control_messages,
        "race_control_taxonomy_summary": race_control_taxonomy_summary,
        "race_control_duplicates": race_control_duplicates,
        "race_control_examples": race_control_examples,
        "status_intervals": status_intervals,
        "status_race_control_overlap": status_race_control_overlap,
        "weather_samples": weather_samples,
        "weather_summary": weather_summary,
        "weather_transitions": weather_transitions,
        "context_timeline_bins": context_timeline_bins,
        "context_replay_correlation": context_replay_correlation,
        "marker_quality": marker_quality,
        "marker_summary": marker_summary,
        "marker_position_examples": marker_position_examples,
        "product_readiness": product_readiness,
        "recommendation_summary": recommendation_summary,
        "race_control_clustered": race_control_clustered,
        "race_control_cluster_summary": race_control_cluster_summary,
        "flag_summary": flag_summary,
        "year_summary": year_summary,
        "figure_paths": figure_paths,
        "report_path": report_path,
        "summary_path": summary_path,
        "skrub_version": skrub.__version__,
    }


if __name__ == "__main__":
    result = run_analysis()
    classified = result["classified"]
    print(f"skrub {result['skrub_version']}")
    print(f"scope: {result['scope'].label}")
    print(f"race sessions: {len(classified)}")
    print(f"sessions with surface issue: {int(classified['has_surface_issue'].sum())}")
    print(f"summary: {result['summary_path']}")
    print(f"skrub report: {result['report_path']}")
