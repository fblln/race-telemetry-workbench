"""Support code for imported race-session database surface EDA.

This module audits the non-lap-specific data surfaces in the local
TimescaleDB import: session metadata, drivers, weather, status timelines, race
control, circuit annotations, ingestion diagnostics, raw telemetry coverage,
position coverage, and aligned 10 Hz replay data.
"""

from __future__ import annotations

import json
import os
from dataclasses import dataclass
from pathlib import Path
from typing import Any


REPO_ROOT = Path(__file__).resolve().parents[1]
ARTIFACT_DIR = REPO_ROOT / "artifacts" / "race-database-surface-eda"
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


def database_url() -> str:
    return os.environ.get("RACE_TELEMETRY_DATABASE_URL", DEFAULT_DATABASE_URL)


def sqlalchemy_url(url: str | None = None) -> str:
    resolved = url or database_url()
    if resolved.startswith("postgresql://"):
        return resolved.replace("postgresql://", "postgresql+psycopg://", 1)
    return resolved


def engine(url: str | None = None):
    return create_engine(sqlalchemy_url(url), pool_pre_ping=True)


def load_session_surface_features(url: str | None = None) -> pd.DataFrame:
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
        where session_type = 'R'
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
        return pd.read_sql_query(sql, connection)


def load_ingestion_diagnostics(url: str | None = None) -> pd.DataFrame:
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
    where s.session_type = 'R'
    order by s.year, s.session_start_utc nulls last, s.event_name, tid.driver_code, tid.stream_name;
    """
    with engine(url).connect() as connection:
        return pd.read_sql_query(sql, connection)


def load_aligned_quality_flags(url: str | None = None) -> pd.DataFrame:
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
    where s.session_type = 'R'
    group by a.session_id, s.year, s.event_name, flag
    order by s.year, s.event_name, rows desc;
    """
    with engine(url).connect() as connection:
        return pd.read_sql_query(sql, connection)


def load_race_control_categories(url: str | None = None) -> pd.DataFrame:
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
    where s.session_type = 'R'
    group by rcm.session_id, s.year, s.event_name, category, flag, scope
    order by s.year, s.event_name, messages desc;
    """
    with engine(url).connect() as connection:
        return pd.read_sql_query(sql, connection)


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
    path = ARTIFACT_DIR / "skrub_database_surface_report.html"
    report = skrub.TableReport(
        classified[report_cols],
        title="Imported race-session database surface quality",
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
    ax.set_title("Imported race-session surface availability")
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
    ax.set_title("Session-level quality flags by imported data surface")
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
    ax.set_title("Context surface density by imported race")
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
    ax.set_title("Imported telemetry stream frequency diagnostics")
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


def build_markdown_summary(
    classified: pd.DataFrame,
    flag_summary: pd.DataFrame,
    year_summary: pd.DataFrame,
    diagnostics: pd.DataFrame,
    aligned_flags: pd.DataFrame,
    race_control_categories: pd.DataFrame,
    figure_paths: list[Path],
    report_path: Path,
) -> str:
    total_sessions = len(classified)
    issue_sessions = int(classified["has_surface_issue"].sum())
    warning_streams = int((diagnostics["warning_flags"].fillna("") != "").sum())
    non_ok_aligned_rows = int(classified["aligned_non_ok_rows"].sum())
    total_aligned = int(classified["aligned_samples"].sum())

    top_flags = aligned_flags.groupby("quality_flag")["rows"].sum().sort_values(ascending=False).head(10)
    top_rc = race_control_categories.groupby("category")["messages"].sum().sort_values(ascending=False).head(10)

    lines = [
        "# Imported race-session database surface EDA summary",
        "",
        "Scope: all imported race sessions (`session_type = 'R'`) in the local TimescaleDB. "
        "The 2025 season is the only complete imported season in this database; 2024 and 2026 are partial imports.",
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
        "## Top race-control categories",
        "",
        top_rc.reset_index(name="messages").to_markdown(index=False),
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
            "- The 2025 race season is complete locally; 2024 and 2026 are partial imports and should not be interpreted as season-level coverage.",
            "- The non-lap context surfaces are populated enough for replay storytelling: weather, status timelines, race control, and circuit markers are present across imported race sessions.",
            "- Aligned 10 Hz telemetry should be audited separately from raw telemetry because replay quality depends on interpolation and quality flags, not just raw sample counts.",
            "",
            "## Recommended next analyses",
            "",
            "- Add per-session duration derivation from raw samples and status events, then flag context surfaces that do not cover the derived session window.",
            "- Decode aligned 10 Hz `quality_flags` into stable categories and track them by session, driver, lap, and time window.",
            "- Add race-control message text clustering for incident taxonomy and duplicate/noise detection.",
            "- Add weather trend anomaly checks by comparing observed sampling cadence and value jumps within each session.",
            "- Decide whether session-level quality summaries should be persisted or remain offline notebook diagnostics.",
        ]
    )
    return "\n".join(lines) + "\n"


def run_analysis(url: str | None = None, write_outputs: bool = True) -> dict[str, Any]:
    thresholds = SurfaceThresholds()
    features = load_session_surface_features(url)
    diagnostics = load_ingestion_diagnostics(url)
    classified = add_quality_flags(features, thresholds, diagnostics)
    aligned_flags = load_aligned_quality_flags(url)
    race_control_categories = load_race_control_categories(url)

    flag_summary = summarize_surface_flags(classified)
    year_summary = summarize_by_year(classified)
    report_path = write_skrub_report(classified)
    figure_paths = [
        plot_surface_availability(classified),
        plot_surface_issues(flag_summary),
        plot_context_density(classified),
        plot_ingestion_frequency(diagnostics),
        plot_race_control_categories(race_control_categories),
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

        metadata = {
            "scope": "imported race sessions",
            "session_count": int(len(classified)),
            "years": sorted(int(year) for year in classified["year"].unique()),
            "skrub_version": skrub.__version__,
            "thresholds": thresholds.__dict__,
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
            figure_paths,
            report_path,
        )
        summary_path = REPO_ROOT / "docs" / "data-quality" / "race-database-surface-eda-summary.md"
        summary_path.parent.mkdir(parents=True, exist_ok=True)
        summary_path.write_text(summary, encoding="utf-8")

    return {
        "thresholds": thresholds,
        "features": features,
        "classified": classified,
        "diagnostics": diagnostics,
        "aligned_flags": aligned_flags,
        "race_control_categories": race_control_categories,
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
    print(f"race sessions: {len(classified)}")
    print(f"sessions with surface issue: {int(classified['has_surface_issue'].sum())}")
    print(f"summary: {result['summary_path']}")
    print(f"skrub report: {result['report_path']}")
