"""Support code for the 2025 telemetry bad-lap EDA notebook.

The notebook is intentionally narrative-first. This module keeps the bounded
database queries, tunable quality rules, clustering, and artifact generation in
one reusable place so the analysis can be rerun from a clean kernel.
"""

from __future__ import annotations

import json
import math
import os
import platform
import re
from dataclasses import asdict, dataclass
from datetime import UTC, datetime
from pathlib import Path
from typing import Any
from urllib.parse import urlparse


REPO_ROOT = Path(__file__).resolve().parents[1]
ARTIFACT_DIR = REPO_ROOT / "artifacts" / "2025-telemetry-bad-lap-eda"
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
from matplotlib.colors import ListedColormap
import numpy as np
import pandas as pd
import seaborn as sns
import skrub
from sklearn.cluster import KMeans
from sklearn.decomposition import PCA
from sklearn.impute import SimpleImputer
from sklearn.metrics import adjusted_rand_score, silhouette_score
from sklearn.pipeline import make_pipeline
from sklearn.preprocessing import RobustScaler
from sqlalchemy import create_engine


DEFAULT_DATABASE_URL = "postgresql://race_telemetry:race_telemetry@localhost:5432/race_telemetry"


@dataclass(frozen=True)
class QualityThresholds:
    """Tunable thresholds for the bad-lap taxonomy."""

    min_car_samples: int = 50
    min_position_samples: int = 100
    min_lap_coverage_ratio: float = 0.80
    path_length_tolerance_pct: float = 0.05
    telemetry_p95_gap_ms: float = 500.0
    telemetry_max_gap_ms: float = 2_000.0
    speed_min_kmh: float = 0.0
    speed_max_kmh: float = 380.0
    rpm_min: float = 0.0
    rpm_max: float = 16_000.0
    gear_min: int = -1
    gear_max: int = 9
    shape_rms_min_kmh: float = 25.0
    shape_rms_iqr_multiplier: float = 3.0
    shape_required_bins: int = 12
    position_segment_mad_multiplier: float = 8.0


CATEGORY_COLUMNS = [
    "missing_or_sparse_telemetry",
    "incomplete_lap_window",
    "distance_reset_or_non_monotonic_distance",
    "implausible_channel_values",
    "shape_mismatch_against_comparable_laps",
    "position_trace_discontinuity",
    "pit_lane_or_safety_car_influenced",
    "timing_session_boundary_artifact",
    "import_or_source_data_anomaly",
    "unknown_needs_inspection",
]

CATEGORY_DISPLAY_NAMES = {
    "missing_or_sparse_telemetry": "missing_or_sparse_telemetry",
    "incomplete_lap_window": "incomplete_lap_window",
    "distance_reset_or_non_monotonic_distance": "distance_reset_or_non_monotonic_distance",
    "implausible_channel_values": "implausible_channel_values",
    "shape_mismatch_against_comparable_laps": "atypical_speed_profile",
    "position_trace_discontinuity": "position_trace_discontinuity",
    "pit_lane_or_safety_car_influenced": "pit_lane_or_safety_car_influenced",
    "timing_session_boundary_artifact": "timing_session_boundary_artifact",
    "import_or_source_data_anomaly": "import_or_source_data_anomaly",
    "unknown_needs_inspection": "unknown_needs_inspection",
}

CATEGORY_PLOT_COLORS = {
    "missing_or_sparse_telemetry": "#4E79A7",
    "incomplete_lap_window": "#76B7B2",
    "distance_reset_or_non_monotonic_distance": "#BAB0AC",
    "implausible_channel_values": "#E15759",
    "atypical_speed_profile": "#B07AA1",
    "position_trace_discontinuity": "#59A14F",
    "pit_lane_or_safety_car_influenced": "#F28E2B",
    "timing_session_boundary_artifact": "#EDC948",
    "import_or_source_data_anomaly": "#9C755F",
    "unknown_needs_inspection": "#FF9DA7",
    "clean": "#8CD17D",
}


PRIMARY_CATEGORY_PRIORITY = [
    "missing_or_sparse_telemetry",
    "timing_session_boundary_artifact",
    "incomplete_lap_window",
    "implausible_channel_values",
    "position_trace_discontinuity",
    "pit_lane_or_safety_car_influenced",
    "shape_mismatch_against_comparable_laps",
    "import_or_source_data_anomaly",
    "unknown_needs_inspection",
]

DATA_INTEGRITY_COLUMNS = [
    "missing_or_sparse_telemetry",
    "incomplete_lap_window",
    "distance_reset_or_non_monotonic_distance",
    "implausible_channel_values",
    "position_trace_discontinuity",
    "timing_session_boundary_artifact",
    "import_or_source_data_anomaly",
]

REPLAY_BLOCKING_COLUMNS = [
    "missing_or_sparse_telemetry",
    "incomplete_lap_window",
    "implausible_channel_values",
    "position_trace_discontinuity",
    "timing_session_boundary_artifact",
]

RACE_CONTEXT_COLUMNS = [
    "pit_lane_or_safety_car_influenced",
]

ANALYTICAL_SHAPE_COLUMNS = [
    "shape_mismatch_against_comparable_laps",
    "unknown_needs_inspection",
]


def category_display_name(category: str) -> str:
    return CATEGORY_DISPLAY_NAMES.get(category, category)


def threshold_table(thresholds: QualityThresholds = QualityThresholds()) -> pd.DataFrame:
    rows = []
    rationale = {
        "min_car_samples": "Reject laps with too little raw car-channel support for replay or lap comparison.",
        "min_position_samples": "Reject laps with too little position support for geometry or track-map work.",
        "min_lap_coverage_ratio": "Flag laps where raw car telemetry covers too little of the timed lap window.",
        "path_length_tolerance_pct": "Flag position paths that are materially shorter or longer than clean same-race laps.",
        "telemetry_p95_gap_ms": "Flag sustained raw car telemetry cadence gaps.",
        "telemetry_max_gap_ms": "Flag single large raw car telemetry gaps.",
        "speed_min_kmh": "Catch impossible negative speed values.",
        "speed_max_kmh": "Catch physically/source-improbable speed values.",
        "rpm_min": "Catch impossible negative RPM values.",
        "rpm_max": "Catch source-improbable RPM values.",
        "gear_min": "Allow neutral/reverse-like source values while catching impossible gears.",
        "gear_max": "Catch impossible gear values.",
        "shape_rms_min_kmh": "Minimum robust speed-profile RMS excess before shape-only review.",
        "shape_rms_iqr_multiplier": "Race-local robust multiplier for speed-profile outliers.",
        "shape_required_bins": "Require enough equal-time bins before shape classification.",
        "position_segment_mad_multiplier": "Race-local robust multiplier for position segment jumps.",
    }
    for name, value in asdict(thresholds).items():
        rows.append(
            {
                "threshold": name,
                "value": value,
                "rationale": rationale.get(name, ""),
            }
        )
    return pd.DataFrame(rows)


def database_url() -> str:
    return os.environ.get("RACE_TELEMETRY_DATABASE_URL", DEFAULT_DATABASE_URL)


def sqlalchemy_url(url: str | None = None) -> str:
    resolved = url or database_url()
    if resolved.startswith("postgresql://"):
        return resolved.replace("postgresql://", "postgresql+psycopg://", 1)
    return resolved


def engine(url: str | None = None):
    return create_engine(sqlalchemy_url(url), pool_pre_ping=True)


def load_session_inventory(url: str | None = None) -> pd.DataFrame:
    sql = """
    select
        dense_rank() over (order by session_start_utc nulls last, event_name) as event_round,
        session_id,
        year,
        event_name,
        circuit_name,
        country,
        session_type,
        session_start_utc,
        session_end_utc
    from sessions
    where year = 2025 and session_type = 'R'
    order by event_round, event_name;
    """
    with engine(url).connect() as connection:
        return pd.read_sql_query(sql, connection)


def load_lap_quality_features(url: str | None = None) -> pd.DataFrame:
    """Load one bounded all-2025-race lap feature table from TimescaleDB."""

    sql = """
    with race_sessions as (
        select
            dense_rank() over (order by session_start_utc nulls last, event_name) as event_round,
            session_id,
            event_name,
            circuit_name,
            country,
            session_start_utc
        from sessions
        where year = 2025 and session_type = 'R'
    ),
    lap_base as (
        select
            rs.event_round,
            rs.session_id,
            rs.event_name,
            rs.circuit_name,
            rs.country,
            sd.driver_number,
            l.driver_code,
            l.lap_number,
            l.stint_number,
            l.lap_start_utc,
            coalesce(
                l.lap_end_utc,
                l.lap_start_utc + make_interval(secs => l.lap_time_ms / 1000.0)
            ) as lap_end_utc,
            l.lap_time_ms,
            l.sector_1_ms,
            l.sector_2_ms,
            l.sector_3_ms,
            l.compound,
            l.tyre_life,
            l.is_pit_out_lap,
            l.is_pit_in_lap,
            l.is_deleted,
            l.is_accurate,
            extract(epoch from (l.lap_start_utc - rs.session_start_utc)) * 1000.0 as lap_start_session_ms,
            extract(epoch from (
                coalesce(l.lap_end_utc, l.lap_start_utc + make_interval(secs => l.lap_time_ms / 1000.0))
                - rs.session_start_utc
            )) * 1000.0 as lap_end_session_ms
        from race_sessions rs
        join laps l using (session_id)
        left join session_drivers sd
            on sd.session_id = l.session_id and sd.driver_code = l.driver_code
    ),
    car as (
        select
            t.session_id,
            t.driver_code,
            t.lap_number,
            count(*) as car_samples,
            min(t.lap_time_ms) as car_lap_time_min_ms,
            max(t.lap_time_ms) as car_lap_time_max_ms,
            avg(t.speed_kmh) as avg_speed_kmh,
            min(t.speed_kmh) as min_speed_kmh,
            max(t.speed_kmh) as max_speed_kmh,
            min(t.throttle_pct) as min_throttle_pct,
            max(t.throttle_pct) as max_throttle_pct,
            min(t.brake_pct) as min_brake_pct,
            max(t.brake_pct) as max_brake_pct,
            min(t.rpm) as min_rpm,
            max(t.rpm) as max_rpm,
            min(t.gear) as min_gear,
            max(t.gear) as max_gear,
            count(*) filter (where t.speed_kmh is null) as speed_nulls,
            count(*) filter (where t.throttle_pct is null) as throttle_nulls,
            count(*) filter (where t.brake_pct is null) as brake_nulls,
            count(*) filter (where t.rpm is null) as rpm_nulls
        from telemetry_samples t
        join race_sessions rs using (session_id)
        where t.lap_number is not null
        group by t.session_id, t.driver_code, t.lap_number
    ),
    car_gaps as (
        select
            session_id,
            driver_code,
            lap_number,
            count(*) filter (where prev_time is not null and sample_time_utc < prev_time) as car_out_of_order_steps,
            count(*) filter (where prev_lap_time_ms is not null and lap_time_ms < prev_lap_time_ms) as car_lap_time_negative_steps,
            max(extract(epoch from (sample_time_utc - prev_time)) * 1000.0)
                filter (where prev_time is not null) as car_max_gap_ms,
            percentile_cont(0.95) within group (
                order by extract(epoch from (sample_time_utc - prev_time)) * 1000.0
            ) filter (where prev_time is not null) as car_p95_gap_ms
        from (
            select
                t.*,
                lag(sample_time_utc) over (
                    partition by t.session_id, t.driver_code, t.lap_number
                    order by sample_time_utc
                ) as prev_time,
                lag(lap_time_ms) over (
                    partition by t.session_id, t.driver_code, t.lap_number
                    order by sample_time_utc
                ) as prev_lap_time_ms
            from telemetry_samples t
            join race_sessions rs using (session_id)
            where t.lap_number is not null
        ) q
        group by session_id, driver_code, lap_number
    ),
    position_steps as (
        select
            session_id,
            driver_code,
            lap_number,
            count(*) as position_samples,
            count(*) filter (where x is null or y is null) as position_xy_nulls,
            sum(
                case
                    when prev_x is null or prev_y is null or x is null or y is null then 0.0
                    else sqrt(power(x - prev_x, 2) + power(y - prev_y, 2))
                end
            ) as position_path_length_units,
            max(
                case
                    when prev_x is null or prev_y is null or x is null or y is null then null
                    else sqrt(power(x - prev_x, 2) + power(y - prev_y, 2))
                end
            ) as position_max_segment_units,
            percentile_cont(0.95) within group (
                order by case
                    when prev_x is null or prev_y is null or x is null or y is null then null
                    else sqrt(power(x - prev_x, 2) + power(y - prev_y, 2))
                end
            ) as position_p95_segment_units
        from (
            select
                p.*,
                lag(x) over (
                    partition by p.session_id, p.driver_code, p.lap_number
                    order by sample_time_utc
                ) as prev_x,
                lag(y) over (
                    partition by p.session_id, p.driver_code, p.lap_number
                    order by sample_time_utc
                ) as prev_y
            from position_samples p
            join race_sessions rs using (session_id)
            where p.lap_number is not null
        ) q
        group by session_id, driver_code, lap_number
    ),
    status_periods as (
        select
            tse.session_id,
            tse.event_time_ms as status_start_ms,
            coalesce(
                lead(tse.event_time_ms) over (
                    partition by tse.session_id order by tse.event_time_ms
                ),
                9223372036854775807
            ) as status_end_ms,
            tse.status_code,
            tse.message
        from track_status_events tse
        join race_sessions rs using (session_id)
    ),
    status_overlap as (
        select
            lb.session_id,
            lb.driver_code,
            lb.lap_number,
            count(*) filter (where sp.status_code = '2') as yellow_periods,
            count(*) filter (where sp.status_code = '4') as safety_car_periods,
            count(*) filter (where sp.status_code in ('6', '7')) as virtual_safety_car_periods,
            count(*) filter (where sp.status_code = '5') as red_flag_periods,
            string_agg(distinct sp.status_code, ',' order by sp.status_code) as overlapped_status_codes
        from lap_base lb
        left join status_periods sp
            on sp.session_id = lb.session_id
            and lb.lap_start_session_ms is not null
            and lb.lap_end_session_ms is not null
            and sp.status_start_ms <= lb.lap_end_session_ms
            and sp.status_end_ms >= lb.lap_start_session_ms
        group by lb.session_id, lb.driver_code, lb.lap_number
    ),
    race_control as (
        select
            lb.session_id,
            lb.driver_code,
            lb.lap_number,
            count(rcm.*) as race_control_messages_on_lap,
            count(rcm.*) filter (
                where rcm.category ilike '%%flag%%'
                   or rcm.message ilike '%%yellow%%'
                   or rcm.message ilike '%%safety car%%'
                   or rcm.message ilike '%%virtual safety car%%'
                   or rcm.message ilike '%%red flag%%'
            ) as race_control_flag_messages_on_lap
        from lap_base lb
        left join race_control_messages rcm
            on rcm.session_id = lb.session_id
            and rcm.lap_number = lb.lap_number
            and (rcm.racing_number is null or rcm.racing_number = lb.driver_number)
        group by lb.session_id, lb.driver_code, lb.lap_number
    )
    select
        lb.*,
        coalesce(car.car_samples, 0) as car_samples,
        car.car_lap_time_min_ms,
        car.car_lap_time_max_ms,
        car.avg_speed_kmh,
        car.min_speed_kmh,
        car.max_speed_kmh,
        car.min_throttle_pct,
        car.max_throttle_pct,
        car.min_brake_pct,
        car.max_brake_pct,
        car.min_rpm,
        car.max_rpm,
        car.min_gear,
        car.max_gear,
        coalesce(car.speed_nulls, 0) as speed_nulls,
        coalesce(car.throttle_nulls, 0) as throttle_nulls,
        coalesce(car.brake_nulls, 0) as brake_nulls,
        coalesce(car.rpm_nulls, 0) as rpm_nulls,
        coalesce(cg.car_out_of_order_steps, 0) as car_out_of_order_steps,
        coalesce(cg.car_lap_time_negative_steps, 0) as car_lap_time_negative_steps,
        cg.car_max_gap_ms,
        cg.car_p95_gap_ms,
        coalesce(ps.position_samples, 0) as position_samples,
        coalesce(ps.position_xy_nulls, 0) as position_xy_nulls,
        ps.position_path_length_units,
        ps.position_max_segment_units,
        ps.position_p95_segment_units,
        coalesce(so.yellow_periods, 0) as yellow_periods,
        coalesce(so.safety_car_periods, 0) as safety_car_periods,
        coalesce(so.virtual_safety_car_periods, 0) as virtual_safety_car_periods,
        coalesce(so.red_flag_periods, 0) as red_flag_periods,
        so.overlapped_status_codes,
        coalesce(rc.race_control_messages_on_lap, 0) as race_control_messages_on_lap,
        coalesce(rc.race_control_flag_messages_on_lap, 0) as race_control_flag_messages_on_lap
    from lap_base lb
    left join car using (session_id, driver_code, lap_number)
    left join car_gaps cg using (session_id, driver_code, lap_number)
    left join position_steps ps using (session_id, driver_code, lap_number)
    left join status_overlap so using (session_id, driver_code, lap_number)
    left join race_control rc using (session_id, driver_code, lap_number)
    order by lb.event_round, lb.session_id, lb.driver_code, lb.lap_number;
    """
    with engine(url).connect() as connection:
        return pd.read_sql_query(sql, connection)


def load_speed_profile_features(url: str | None = None, bins: int = 20) -> pd.DataFrame:
    """Load per-lap speed-shape bins for all 2025 race sessions."""

    sql = f"""
    with race_sessions as (
        select session_id
        from sessions
        where year = 2025 and session_type = 'R'
    ),
    lap_base as (
        select session_id, driver_code, lap_number, lap_time_ms
        from laps
        join race_sessions using (session_id)
        where lap_time_ms is not null and lap_time_ms > 0
    )
    select
        t.session_id,
        t.driver_code,
        t.lap_number,
        least({bins}, greatest(1, width_bucket(t.lap_time_ms, 0, lb.lap_time_ms, {bins}))) as speed_bin,
        avg(t.speed_kmh) as speed_kmh_mean
    from telemetry_samples t
    join lap_base lb
        on lb.session_id = t.session_id
        and lb.driver_code = t.driver_code
        and lb.lap_number = t.lap_number
    where t.lap_time_ms is not null
      and t.lap_time_ms >= 0
      and t.lap_time_ms <= lb.lap_time_ms
      and t.speed_kmh is not null
    group by t.session_id, t.driver_code, t.lap_number, speed_bin
    order by t.session_id, t.driver_code, t.lap_number, speed_bin;
    """
    with engine(url).connect() as connection:
        long = pd.read_sql_query(sql, connection)

    profile = (
        long.pivot_table(
            index=["session_id", "driver_code", "lap_number"],
            columns="speed_bin",
            values="speed_kmh_mean",
            aggfunc="mean",
        )
        .rename(columns=lambda value: f"speed_bin_{int(value):02d}")
        .reset_index()
    )
    return profile


def add_derived_features(
    laps: pd.DataFrame,
    speed_profiles: pd.DataFrame,
    thresholds: QualityThresholds = QualityThresholds(),
) -> pd.DataFrame:
    df = laps.merge(speed_profiles, on=["session_id", "driver_code", "lap_number"], how="left")

    df["car_coverage_ms"] = df["car_lap_time_max_ms"] - df["car_lap_time_min_ms"]
    df["car_coverage_ratio"] = df["car_coverage_ms"] / df["lap_time_ms"].replace({0: np.nan})
    df["telemetry_null_rate"] = (
        df[["speed_nulls", "throttle_nulls", "brake_nulls", "rpm_nulls"]].sum(axis=1)
        / (df["car_samples"].replace({0: np.nan}) * 4)
    )
    df["position_null_rate"] = df["position_xy_nulls"] / df["position_samples"].replace({0: np.nan})

    reference_mask = (
        df["is_accurate"].fillna(False).astype(bool)
        & ~df["is_pit_in_lap"].fillna(False).astype(bool)
        & ~df["is_pit_out_lap"].fillna(False).astype(bool)
        & (df["position_samples"] >= thresholds.min_position_samples)
        & df["position_path_length_units"].notna()
        & (df["car_coverage_ratio"] >= thresholds.min_lap_coverage_ratio)
    )

    session_path_median = (
        df.loc[reference_mask]
        .groupby("session_id")["position_path_length_units"]
        .median()
        .rename("session_reference_path_length_units")
    )
    df = df.merge(session_path_median, on="session_id", how="left")
    df["position_path_ratio"] = (
        df["position_path_length_units"] / df["session_reference_path_length_units"]
    )

    segment_baseline = (
        df.loc[reference_mask & df["position_max_segment_units"].notna()]
        .groupby("session_id")["position_max_segment_units"]
        .agg(
            session_segment_median_units="median",
            session_segment_mad_units=lambda s: float(np.median(np.abs(s - np.median(s)))),
        )
        .reset_index()
    )
    df = df.merge(segment_baseline, on="session_id", how="left")
    df["position_segment_limit_units"] = df["session_segment_median_units"] + (
        thresholds.position_segment_mad_multiplier
        * df["session_segment_mad_units"].replace({0: np.nan})
    )
    df["position_segment_limit_units"] = df["position_segment_limit_units"].fillna(
        df.groupby("session_id")["position_max_segment_units"].transform("quantile", 0.99)
    )

    speed_bin_cols = [col for col in df.columns if col.startswith("speed_bin_")]
    reference_profiles = df.loc[reference_mask, ["session_id", *speed_bin_cols]]
    medians = reference_profiles.groupby("session_id")[speed_bin_cols].median()
    medians.columns = [f"{col}_session_median" for col in medians.columns]
    df = df.merge(medians, on="session_id", how="left")

    residual_cols = []
    for col in speed_bin_cols:
        residual_col = f"{col}_residual"
        df[residual_col] = df[col] - df[f"{col}_session_median"]
        residual_cols.append(residual_col)
    residual_matrix = df[residual_cols].to_numpy(dtype=float)
    df["speed_profile_bins_present"] = np.sum(~np.isnan(df[speed_bin_cols].to_numpy(dtype=float)), axis=1)
    residual_count = np.sum(~np.isnan(residual_matrix), axis=1)
    residual_square_sum = np.nansum(np.square(residual_matrix), axis=1)
    df["speed_shape_rms_kmh"] = np.nan
    valid_residual_rows = residual_count > 0
    df.loc[valid_residual_rows, "speed_shape_rms_kmh"] = np.sqrt(
        residual_square_sum[valid_residual_rows] / residual_count[valid_residual_rows]
    )

    shape_baseline = (
        df.loc[reference_mask & df["speed_shape_rms_kmh"].notna()]
        .groupby("session_id")["speed_shape_rms_kmh"]
        .agg(
            speed_shape_session_median_kmh="median",
            speed_shape_session_q25_kmh=lambda s: float(s.quantile(0.25)),
            speed_shape_session_q75_kmh=lambda s: float(s.quantile(0.75)),
        )
        .reset_index()
    )
    shape_baseline["speed_shape_iqr_kmh"] = (
        shape_baseline["speed_shape_session_q75_kmh"]
        - shape_baseline["speed_shape_session_q25_kmh"]
    )
    df = df.merge(shape_baseline, on="session_id", how="left")
    df["speed_shape_limit_kmh"] = df["speed_shape_session_median_kmh"] + np.maximum(
        thresholds.shape_rms_min_kmh,
        thresholds.shape_rms_iqr_multiplier * df["speed_shape_iqr_kmh"].fillna(0.0),
    )
    return df


def classify_laps(df: pd.DataFrame, thresholds: QualityThresholds = QualityThresholds()) -> pd.DataFrame:
    result = df.copy()

    result["missing_or_sparse_telemetry"] = (
        (result["car_samples"] < thresholds.min_car_samples)
        | (result["position_samples"] < thresholds.min_position_samples)
    )
    result["incomplete_lap_window"] = (
        result["lap_time_ms"].isna()
        | result["lap_start_utc"].isna()
        | result["lap_end_utc"].isna()
        | (result["car_coverage_ratio"] < thresholds.min_lap_coverage_ratio)
    )
    # The imported schema intentionally stores raw car channels and position
    # samples, but it does not store FastF1 Distance. This category remains
    # false and is documented as unavailable rather than inferred.
    result["distance_reset_or_non_monotonic_distance"] = False
    result["implausible_channel_values"] = (
        (result["min_speed_kmh"] < thresholds.speed_min_kmh)
        | (result["max_speed_kmh"] > thresholds.speed_max_kmh)
        | (result["min_rpm"] < thresholds.rpm_min)
        | (result["max_rpm"] > thresholds.rpm_max)
        | (result["min_gear"] < thresholds.gear_min)
        | (result["max_gear"] > thresholds.gear_max)
        | (result["min_throttle_pct"] < 0)
        | (result["max_throttle_pct"] > 100)
        | (result["min_brake_pct"] < 0)
        | (result["max_brake_pct"] > 100)
    ).fillna(False)
    result["shape_mismatch_against_comparable_laps"] = (
        (result["speed_profile_bins_present"] >= thresholds.shape_required_bins)
        & result["speed_shape_rms_kmh"].notna()
        & result["speed_shape_limit_kmh"].notna()
        & (result["speed_shape_rms_kmh"] > result["speed_shape_limit_kmh"])
    )
    result["position_trace_discontinuity"] = (
        result["position_path_ratio"].notna()
        & (
            (result["position_path_ratio"] < (1.0 - thresholds.path_length_tolerance_pct))
            | (result["position_path_ratio"] > (1.0 + thresholds.path_length_tolerance_pct))
            | (
                result["position_segment_limit_units"].notna()
                & result["position_max_segment_units"].notna()
                & (result["position_max_segment_units"] > result["position_segment_limit_units"])
            )
        )
    )
    result["pit_lane_or_safety_car_influenced"] = (
        result["is_pit_in_lap"].fillna(False).astype(bool)
        | result["is_pit_out_lap"].fillna(False).astype(bool)
        | (result["safety_car_periods"] > 0)
        | (result["virtual_safety_car_periods"] > 0)
        | (result["red_flag_periods"] > 0)
    )
    result["timing_session_boundary_artifact"] = (
        result["lap_start_session_ms"].isna()
        | result["lap_end_session_ms"].isna()
        | (result["car_out_of_order_steps"] > 0)
        | (result["car_lap_time_negative_steps"] > 0)
        | (result["car_p95_gap_ms"] > thresholds.telemetry_p95_gap_ms)
        | (result["car_max_gap_ms"] > thresholds.telemetry_max_gap_ms)
    ).fillna(False)
    result["import_or_source_data_anomaly"] = (
        result["is_accurate"].fillna(True).eq(False)
        | result["is_deleted"].fillna(False).astype(bool)
    )

    known_columns = [col for col in CATEGORY_COLUMNS if col != "unknown_needs_inspection"]
    other_known_columns = [
        col
        for col in known_columns
        if col != "shape_mismatch_against_comparable_laps"
    ]
    other_known = result[other_known_columns].any(axis=1)
    # Unknown is reserved for laps with a speed-shape outlier but no obvious
    # contextual/source/telemetry explanation.
    result["unknown_needs_inspection"] = (
        result["shape_mismatch_against_comparable_laps"] & ~other_known
    )
    result["bad_lap_any_category"] = result[CATEGORY_COLUMNS].any(axis=1)

    def primary(row: pd.Series) -> str:
        for category in PRIMARY_CATEGORY_PRIORITY:
            if bool(row.get(category, False)):
                return category
        return "clean"

    result["primary_category"] = result.apply(primary, axis=1)
    result["primary_category_display"] = result["primary_category"].map(category_display_name)
    result["atypical_speed_profile"] = result["shape_mismatch_against_comparable_laps"]
    result["reason_count"] = result[CATEGORY_COLUMNS].astype(bool).sum(axis=1)

    def reason_set(row: pd.Series) -> str:
        reasons = [
            category_display_name(category)
            for category in CATEGORY_COLUMNS
            if bool(row.get(category, False))
        ]
        return " + ".join(reasons) if reasons else "clean"

    result["reason_set"] = result.apply(reason_set, axis=1)
    result["data_integrity_flag"] = result[DATA_INTEGRITY_COLUMNS].any(axis=1)
    result["replay_blocking_integrity_flag"] = result[REPLAY_BLOCKING_COLUMNS].any(axis=1)
    result["race_context_flag"] = result[RACE_CONTEXT_COLUMNS].any(axis=1)
    result["analytical_shape_flag"] = result[ANALYTICAL_SHAPE_COLUMNS].any(axis=1)

    result["coverage_deficit_pct"] = (
        (thresholds.min_lap_coverage_ratio - result["car_coverage_ratio"])
        .clip(lower=0)
        .fillna(0.0)
        * 100
    )
    result.loc[
        result["incomplete_lap_window"] & result["car_coverage_ratio"].isna(),
        "coverage_deficit_pct",
    ] = thresholds.min_lap_coverage_ratio * 100
    result["p95_gap_excess_ms"] = (
        result["car_p95_gap_ms"] - thresholds.telemetry_p95_gap_ms
    ).clip(lower=0).fillna(0.0)
    result["max_gap_excess_ms"] = (
        result["car_max_gap_ms"] - thresholds.telemetry_max_gap_ms
    ).clip(lower=0).fillna(0.0)
    result["path_ratio_deviation_pct"] = (
        (result["position_path_ratio"] - 1.0).abs() * 100
    ).fillna(0.0)
    result["path_ratio_excess_pct"] = (
        result["path_ratio_deviation_pct"] - thresholds.path_length_tolerance_pct * 100
    ).clip(lower=0).fillna(0.0)
    result["max_segment_jump_excess_units"] = (
        result["position_max_segment_units"] - result["position_segment_limit_units"]
    ).clip(lower=0).fillna(0.0)
    result["speed_shape_excess_kmh"] = (
        result["speed_shape_rms_kmh"] - result["speed_shape_limit_kmh"]
    ).clip(lower=0).fillna(0.0)
    result["speed_shape_robust_z"] = (
        (result["speed_shape_rms_kmh"] - result["speed_shape_session_median_kmh"])
        / result["speed_shape_iqr_kmh"].replace({0: np.nan})
    ).replace([np.inf, -np.inf], np.nan).fillna(0.0)
    result["telemetry_null_rate_pct"] = result["telemetry_null_rate"].fillna(0.0) * 100
    result["position_null_rate_pct"] = result["position_null_rate"].fillna(0.0) * 100
    result["null_rate_severity_pct"] = result[
        ["telemetry_null_rate_pct", "position_null_rate_pct"]
    ].max(axis=1)

    result["safe_for_replay"] = ~result["replay_blocking_integrity_flag"]
    result["safe_for_time_domain_analysis"] = ~(
        result["data_integrity_flag"]
        | result["race_context_flag"]
        | result["analytical_shape_flag"]
    )
    result["distance_alignment_status"] = "not_evaluated_requires_distance_projection"
    result["safe_for_lap_comparison"] = result["safe_for_time_domain_analysis"]
    result["safe_for_geometry_reference"] = ~(
        result["data_integrity_flag"]
        | result["race_context_flag"]
        | result["analytical_shape_flag"]
    ) & (result["position_samples"] >= thresholds.min_position_samples)
    result["needs_manual_review"] = (
        result["unknown_needs_inspection"]
        | (
            result["analytical_shape_flag"]
            & ~result["data_integrity_flag"]
            & ~result["race_context_flag"]
        )
        | result["implausible_channel_values"]
    )

    result["product_recommendation"] = np.select(
        [
            result["replay_blocking_integrity_flag"],
            result["needs_manual_review"],
            result["race_context_flag"] | result["import_or_source_data_anomaly"],
        ],
        [
            "exclude",
            "manual_review",
            "keep_with_context_label",
        ],
        default="keep",
    )
    return result


def summarize_categories(classified: pd.DataFrame) -> pd.DataFrame:
    total_laps = len(classified)
    rows = []
    for category in CATEGORY_COLUMNS:
        count = int(classified[category].sum())
        rows.append(
            {
                "category": category_display_name(category),
                "source_column": category,
                "laps": count,
                "pct_of_all_laps": count / total_laps * 100 if total_laps else 0.0,
            }
        )
    return pd.DataFrame(rows).sort_values(["laps", "category"], ascending=[False, True])


def summarize_primary(classified: pd.DataFrame) -> pd.DataFrame:
    total_laps = len(classified)
    summary = (
        classified.groupby("primary_category_display", dropna=False)
        .size()
        .rename("laps")
        .reset_index()
        .sort_values("laps", ascending=False)
    )
    summary = summary.rename(columns={"primary_category_display": "primary_category"})
    summary["pct_of_all_laps"] = summary["laps"] / total_laps * 100 if total_laps else 0.0
    return summary


def summarize_quality_lenses(classified: pd.DataFrame) -> pd.DataFrame:
    total_laps = len(classified)
    rows = []
    lenses = [
        ("data_integrity", "data_integrity_flag"),
        ("replay_blocking_integrity", "replay_blocking_integrity_flag"),
        ("race_context", "race_context_flag"),
        ("analytical_shape", "analytical_shape_flag"),
        ("manual_review", "needs_manual_review"),
    ]
    for lens, column in lenses:
        count = int(classified[column].sum())
        rows.append(
            {
                "lens": lens,
                "laps": count,
                "pct_of_all_laps": count / total_laps * 100 if total_laps else 0.0,
            }
        )
    return pd.DataFrame(rows).sort_values("laps", ascending=False)


def summarize_safety(classified: pd.DataFrame) -> pd.DataFrame:
    total_laps = len(classified)
    rows = []
    for column in [
        "safe_for_replay",
        "safe_for_time_domain_analysis",
        "distance_alignment_status",
        "safe_for_lap_comparison",
        "safe_for_geometry_reference",
        "needs_manual_review",
    ]:
        if column == "distance_alignment_status":
            summary = (
                classified.groupby(column, dropna=False)
                .size()
                .rename("laps")
                .reset_index()
                .rename(columns={column: "value"})
            )
            for _, row in summary.iterrows():
                rows.append(
                    {
                        "derived_flag": column,
                        "value": row["value"],
                        "laps": int(row["laps"]),
                        "pct_of_all_laps": row["laps"] / total_laps * 100 if total_laps else 0.0,
                    }
                )
            continue

        count = int(classified[column].sum())
        rows.append(
            {
                "derived_flag": column,
                "value": "true",
                "laps": count,
                "pct_of_all_laps": count / total_laps * 100 if total_laps else 0.0,
            }
        )
    return pd.DataFrame(rows)


def summarize_recommendations(classified: pd.DataFrame) -> pd.DataFrame:
    total_laps = len(classified)
    summary = (
        classified.groupby("product_recommendation", dropna=False)
        .size()
        .rename("laps")
        .reset_index()
        .sort_values("laps", ascending=False)
    )
    summary["pct_of_all_laps"] = summary["laps"] / total_laps * 100 if total_laps else 0.0
    return summary


def summarize_by_race(classified: pd.DataFrame) -> pd.DataFrame:
    grouped = (
        classified.groupby(["event_round", "event_name"], dropna=False)
        .agg(
            total_laps=("lap_number", "size"),
            bad_laps=("bad_lap_any_category", "sum"),
            source_anomaly=("import_or_source_data_anomaly", "sum"),
            pit_sc_context=("pit_lane_or_safety_car_influenced", "sum"),
            sparse=("missing_or_sparse_telemetry", "sum"),
            incomplete=("incomplete_lap_window", "sum"),
            position=("position_trace_discontinuity", "sum"),
            shape=("shape_mismatch_against_comparable_laps", "sum"),
            timing=("timing_session_boundary_artifact", "sum"),
        )
        .reset_index()
    )
    grouped["bad_pct"] = grouped["bad_laps"] / grouped["total_laps"] * 100
    return grouped.sort_values(["bad_pct", "bad_laps"], ascending=False)


def build_primary_category_audit(classified: pd.DataFrame, limit: int = 80) -> pd.DataFrame:
    audit = classified[
        classified["bad_lap_any_category"] & (classified["reason_count"] > 1)
    ].copy()
    if audit.empty:
        return pd.DataFrame()
    audit["secondary_reason_set"] = audit.apply(
        lambda row: " + ".join(
            category_display_name(category)
            for category in CATEGORY_COLUMNS
            if bool(row.get(category, False)) and category != row["primary_category"]
        ),
        axis=1,
    )
    audit["audit_priority"] = (
        audit["coverage_deficit_pct"]
        + audit["p95_gap_excess_ms"] / 100
        + audit["path_ratio_excess_pct"]
        + audit["speed_shape_excess_kmh"]
        + audit["reason_count"] * 2
    )
    keep_cols = [
        "event_round",
        "event_name",
        "driver_code",
        "lap_number",
        "primary_category_display",
        "secondary_reason_set",
        "reason_count",
        "product_recommendation",
        "safe_for_replay",
        "safe_for_lap_comparison",
        "coverage_deficit_pct",
        "p95_gap_excess_ms",
        "path_ratio_excess_pct",
        "speed_shape_excess_kmh",
        "audit_priority",
    ]
    return (
        audit.sort_values(["audit_priority", "event_round"], ascending=[False, True])
        .head(limit)[keep_cols]
        .rename(columns={"primary_category_display": "primary_category"})
    )


def summarize_category_intersections(classified: pd.DataFrame, limit: int = 30) -> pd.DataFrame:
    total_laps = len(classified)
    total_bad = int(classified["bad_lap_any_category"].sum())
    intersections = (
        classified[classified["bad_lap_any_category"]]
        .groupby(["reason_set", "reason_count"], dropna=False)
        .agg(
            laps=("lap_number", "size"),
            races=("event_name", "nunique"),
            drivers=("driver_code", "nunique"),
        )
        .reset_index()
        .sort_values(["laps", "reason_count", "reason_set"], ascending=[False, True, True])
    )
    intersections["pct_of_bad_laps"] = (
        intersections["laps"] / total_bad * 100 if total_bad else 0.0
    )
    intersections["pct_of_all_laps"] = (
        intersections["laps"] / total_laps * 100 if total_laps else 0.0
    )
    return intersections.head(limit)


def summarize_decision_waterfall(classified: pd.DataFrame) -> pd.DataFrame:
    steps = [
        (
            "source_or_timing_excluded",
            classified["import_or_source_data_anomaly"]
            | classified["timing_session_boundary_artifact"],
        ),
        (
            "missing_or_incomplete_telemetry",
            classified["missing_or_sparse_telemetry"] | classified["incomplete_lap_window"],
        ),
        ("implausible_channel_values", classified["implausible_channel_values"]),
        ("position_trace_discontinuity", classified["position_trace_discontinuity"]),
        ("race_context_influenced", classified["pit_lane_or_safety_car_influenced"]),
        ("atypical_speed_profile", classified["shape_mismatch_against_comparable_laps"]),
    ]
    remaining = pd.Series(True, index=classified.index)
    rows = [
        {
            "step": "total_laps",
            "laps": int(len(classified)),
            "pct_of_all_laps": 100.0 if len(classified) else 0.0,
            "remaining_laps_after_step": int(len(classified)),
        }
    ]
    for label, condition in steps:
        matched = remaining & condition.fillna(False).astype(bool)
        count = int(matched.sum())
        remaining = remaining & ~matched
        rows.append(
            {
                "step": label,
                "laps": count,
                "pct_of_all_laps": count / len(classified) * 100 if len(classified) else 0.0,
                "remaining_laps_after_step": int(remaining.sum()),
            }
        )

    other_bad = remaining & classified["bad_lap_any_category"].fillna(False).astype(bool)
    other_count = int(other_bad.sum())
    remaining = remaining & ~other_bad
    rows.append(
        {
            "step": "other_or_manual_review",
            "laps": other_count,
            "pct_of_all_laps": other_count / len(classified) * 100 if len(classified) else 0.0,
            "remaining_laps_after_step": int(remaining.sum()),
        }
    )
    clean_count = int((remaining & ~classified["bad_lap_any_category"].fillna(False).astype(bool)).sum())
    rows.append(
        {
            "step": "clean_after_waterfall",
            "laps": clean_count,
            "pct_of_all_laps": clean_count / len(classified) * 100 if len(classified) else 0.0,
            "remaining_laps_after_step": 0,
        }
    )
    return pd.DataFrame(rows)


def summarize_primary_by_race(classified: pd.DataFrame) -> pd.DataFrame:
    totals = (
        classified.groupby(["event_round", "event_name"], dropna=False)
        .size()
        .rename("race_laps")
        .reset_index()
    )
    summary = (
        classified.groupby(["event_round", "event_name", "primary_category_display"], dropna=False)
        .size()
        .rename("laps")
        .reset_index()
        .rename(columns={"primary_category_display": "primary_category"})
        .merge(totals, on=["event_round", "event_name"], how="left")
    )
    summary["pct_of_race_laps"] = summary["laps"] / summary["race_laps"] * 100
    return summary.sort_values(["event_round", "primary_category"])


def build_race_drilldowns(
    classified: pd.DataFrame,
    events: tuple[str, ...] = (
        "British Grand Prix",
        "Belgian Grand Prix",
        "Australian Grand Prix",
        "Dutch Grand Prix",
        "São Paulo Grand Prix",
    ),
) -> pd.DataFrame:
    scoped = classified[classified["event_name"].isin(events)].copy()
    if scoped.empty:
        return pd.DataFrame()
    grouped = (
        scoped.groupby(
            ["event_round", "event_name", "lap_number", "primary_category_display"],
            dropna=False,
        )
        .agg(
            drivers=("driver_code", "nunique"),
            flagged_laps=("bad_lap_any_category", "sum"),
            median_lap_time_ms=("lap_time_ms", "median"),
            median_speed_shape_rms_kmh=("speed_shape_rms_kmh", "median"),
            median_car_max_gap_ms=("car_max_gap_ms", "median"),
            pit_sc_context_laps=("pit_lane_or_safety_car_influenced", "sum"),
            source_anomaly_laps=("import_or_source_data_anomaly", "sum"),
        )
        .reset_index()
        .rename(columns={"primary_category_display": "primary_category"})
        .sort_values(["event_round", "lap_number", "primary_category"])
    )
    return grouped


def summarize_driver_race_matrix(
    classified: pd.DataFrame,
    min_laps: int = 10,
) -> pd.DataFrame:
    grouped = (
        classified.groupby(["event_round", "event_name", "driver_code"], dropna=False)
        .agg(
            total_laps=("lap_number", "size"),
            bad_laps=("bad_lap_any_category", "sum"),
            integrity_laps=("data_integrity_flag", "sum"),
            context_laps=("race_context_flag", "sum"),
            shape_laps=("analytical_shape_flag", "sum"),
            replay_unsafe_laps=("safe_for_replay", lambda values: int((~values).sum())),
            lap_comparison_unsafe_laps=("safe_for_lap_comparison", lambda values: int((~values).sum())),
        )
        .reset_index()
    )
    grouped = grouped[grouped["total_laps"] >= min_laps].copy()
    for numerator, pct_col in [
        ("bad_laps", "bad_pct"),
        ("integrity_laps", "integrity_pct"),
        ("context_laps", "context_pct"),
        ("shape_laps", "shape_pct"),
        ("replay_unsafe_laps", "replay_unsafe_pct"),
        ("lap_comparison_unsafe_laps", "lap_comparison_unsafe_pct"),
    ]:
        grouped[pct_col] = grouped[numerator] / grouped["total_laps"] * 100
    return grouped.sort_values(["bad_pct", "event_round"], ascending=[False, True])


def threshold_scenarios(
    thresholds: QualityThresholds = QualityThresholds(),
) -> list[tuple[str, QualityThresholds]]:
    """Return deterministic threshold variants for sensitivity analysis."""

    def variant(**overrides: Any) -> QualityThresholds:
        values = asdict(thresholds)
        values.update(overrides)
        return QualityThresholds(**values)

    return [
        ("baseline", thresholds),
        ("min_car_samples_loose", variant(min_car_samples=40)),
        ("min_car_samples_strict", variant(min_car_samples=75)),
        ("min_position_samples_loose", variant(min_position_samples=80)),
        ("min_position_samples_strict", variant(min_position_samples=150)),
        ("lap_coverage_loose", variant(min_lap_coverage_ratio=0.75)),
        ("lap_coverage_strict", variant(min_lap_coverage_ratio=0.85)),
        ("p95_gap_loose", variant(telemetry_p95_gap_ms=600.0)),
        ("p95_gap_strict", variant(telemetry_p95_gap_ms=450.0)),
        ("max_gap_loose", variant(telemetry_max_gap_ms=2_300.0)),
        ("max_gap_strict", variant(telemetry_max_gap_ms=1_800.0)),
        ("path_tolerance_loose", variant(path_length_tolerance_pct=0.06)),
        ("path_tolerance_strict", variant(path_length_tolerance_pct=0.04)),
        ("shape_rms_loose", variant(shape_rms_min_kmh=30.0)),
        ("shape_rms_strict", variant(shape_rms_min_kmh=20.0)),
    ]


def run_threshold_sensitivity(
    laps: pd.DataFrame,
    speed_profiles: pd.DataFrame,
    thresholds: QualityThresholds = QualityThresholds(),
) -> tuple[pd.DataFrame, pd.DataFrame, pd.DataFrame, pd.DataFrame]:
    scenario_frames = []
    summary_rows = []
    baseline_flags: pd.DataFrame | None = None
    identity_cols = ["session_id", "event_round", "event_name", "driver_code", "lap_number"]

    for scenario, scenario_thresholds in threshold_scenarios(thresholds):
        scenario_features = add_derived_features(laps, speed_profiles, scenario_thresholds)
        scenario_classified = classify_laps(scenario_features, scenario_thresholds)
        scoped = scenario_classified[
            [
                *identity_cols,
                "bad_lap_any_category",
                "safe_for_replay",
                "safe_for_lap_comparison",
                "product_recommendation",
                "primary_category_display",
            ]
        ].copy()
        scoped["scenario"] = scenario
        scoped["threshold_changed"] = scenario != "baseline"
        scenario_frames.append(scoped)

        if scenario == "baseline":
            baseline_flags = scoped.rename(
                columns={
                    "bad_lap_any_category": "baseline_bad_lap_any_category",
                    "safe_for_replay": "baseline_safe_for_replay",
                    "safe_for_lap_comparison": "baseline_safe_for_lap_comparison",
                    "product_recommendation": "baseline_product_recommendation",
                    "primary_category_display": "baseline_primary_category",
                }
            )

        summary_rows.append(
            {
                "scenario": scenario,
                "bad_laps": int(scenario_classified["bad_lap_any_category"].sum()),
                "bad_pct": float(scenario_classified["bad_lap_any_category"].mean() * 100),
                "replay_unsafe_laps": int((~scenario_classified["safe_for_replay"]).sum()),
                "lap_comparison_unsafe_laps": int((~scenario_classified["safe_for_lap_comparison"]).sum()),
                "manual_review_laps": int(scenario_classified["needs_manual_review"].sum()),
                "exclude_laps": int((scenario_classified["product_recommendation"] == "exclude").sum()),
                "context_label_laps": int((scenario_classified["product_recommendation"] == "keep_with_context_label").sum()),
            }
        )

    if baseline_flags is None:
        raise RuntimeError("Threshold sensitivity scenarios did not include a baseline.")

    sensitivity_long = pd.concat(scenario_frames, ignore_index=True).merge(
        baseline_flags[
            [
                *identity_cols,
                "baseline_bad_lap_any_category",
                "baseline_safe_for_replay",
                "baseline_safe_for_lap_comparison",
                "baseline_product_recommendation",
                "baseline_primary_category",
            ]
        ],
        on=identity_cols,
        how="left",
    )
    sensitivity_long["bad_flag_changed"] = (
        sensitivity_long["bad_lap_any_category"]
        != sensitivity_long["baseline_bad_lap_any_category"]
    )
    sensitivity_long["replay_safety_changed"] = (
        sensitivity_long["safe_for_replay"] != sensitivity_long["baseline_safe_for_replay"]
    )
    sensitivity_long["lap_comparison_safety_changed"] = (
        sensitivity_long["safe_for_lap_comparison"]
        != sensitivity_long["baseline_safe_for_lap_comparison"]
    )
    sensitivity_long["recommendation_changed"] = (
        sensitivity_long["product_recommendation"]
        != sensitivity_long["baseline_product_recommendation"]
    )
    sensitivity_long["primary_category_changed"] = (
        sensitivity_long["primary_category_display"]
        != sensitivity_long["baseline_primary_category"]
    )

    threshold_summary = pd.DataFrame(summary_rows)
    baseline_summary = threshold_summary[threshold_summary["scenario"] == "baseline"].iloc[0]
    for column in [
        "bad_laps",
        "replay_unsafe_laps",
        "lap_comparison_unsafe_laps",
        "manual_review_laps",
        "exclude_laps",
        "context_label_laps",
    ]:
        threshold_summary[f"{column}_delta_vs_baseline"] = (
            threshold_summary[column] - baseline_summary[column]
        )
    changed = (
        sensitivity_long[sensitivity_long["scenario"] != "baseline"]
        .groupby("scenario")
        .agg(
            bad_flag_changed_laps=("bad_flag_changed", "sum"),
            replay_safety_changed_laps=("replay_safety_changed", "sum"),
            lap_comparison_safety_changed_laps=("lap_comparison_safety_changed", "sum"),
            recommendation_changed_laps=("recommendation_changed", "sum"),
            primary_category_changed_laps=("primary_category_changed", "sum"),
        )
        .reset_index()
    )
    threshold_summary = threshold_summary.merge(changed, on="scenario", how="left").fillna(0)

    per_lap = (
        sensitivity_long[sensitivity_long["scenario"] != "baseline"]
        .groupby(identity_cols)
        .agg(
            bad_flag_flip_count=("bad_flag_changed", "sum"),
            replay_safety_flip_count=("replay_safety_changed", "sum"),
            lap_comparison_safety_flip_count=("lap_comparison_safety_changed", "sum"),
            recommendation_flip_count=("recommendation_changed", "sum"),
            primary_category_flip_count=("primary_category_changed", "sum"),
        )
        .reset_index()
        .merge(
            baseline_flags[
                [
                    *identity_cols,
                    "baseline_bad_lap_any_category",
                    "baseline_safe_for_replay",
                    "baseline_safe_for_lap_comparison",
                    "baseline_product_recommendation",
                    "baseline_primary_category",
                ]
            ],
            on=identity_cols,
            how="left",
        )
    )
    per_lap["any_sensitivity_flip_count"] = per_lap[
        [
            "bad_flag_flip_count",
            "replay_safety_flip_count",
            "lap_comparison_safety_flip_count",
            "recommendation_flip_count",
            "primary_category_flip_count",
        ]
    ].sum(axis=1)
    borderline = per_lap[per_lap["any_sensitivity_flip_count"] > 0].sort_values(
        ["any_sensitivity_flip_count", "event_round", "driver_code", "lap_number"],
        ascending=[False, True, True, True],
    )

    by_race = (
        per_lap.groupby(["event_round", "event_name"], dropna=False)
        .agg(
            total_laps=("lap_number", "size"),
            threshold_sensitive_laps=("any_sensitivity_flip_count", lambda s: int((s > 0).sum())),
            bad_flag_sensitive_laps=("bad_flag_flip_count", lambda s: int((s > 0).sum())),
            recommendation_sensitive_laps=("recommendation_flip_count", lambda s: int((s > 0).sum())),
        )
        .reset_index()
    )
    by_race["threshold_sensitive_pct"] = (
        by_race["threshold_sensitive_laps"] / by_race["total_laps"] * 100
    )
    by_race = by_race.sort_values(["threshold_sensitive_pct", "threshold_sensitive_laps"], ascending=False)

    by_driver = (
        per_lap.groupby("driver_code", dropna=False)
        .agg(
            total_laps=("lap_number", "size"),
            threshold_sensitive_laps=("any_sensitivity_flip_count", lambda s: int((s > 0).sum())),
            bad_flag_sensitive_laps=("bad_flag_flip_count", lambda s: int((s > 0).sum())),
            recommendation_sensitive_laps=("recommendation_flip_count", lambda s: int((s > 0).sum())),
        )
        .reset_index()
    )
    by_driver["threshold_sensitive_pct"] = (
        by_driver["threshold_sensitive_laps"] / by_driver["total_laps"] * 100
    )
    by_driver = by_driver.sort_values(["threshold_sensitive_pct", "threshold_sensitive_laps"], ascending=False)

    return threshold_summary, by_race, by_driver, borderline


def summarize_by_driver(classified: pd.DataFrame) -> pd.DataFrame:
    grouped = (
        classified.groupby("driver_code", dropna=False)
        .agg(
            total_laps=("lap_number", "size"),
            bad_laps=("bad_lap_any_category", "sum"),
            source_anomaly=("import_or_source_data_anomaly", "sum"),
            pit_sc_context=("pit_lane_or_safety_car_influenced", "sum"),
            sparse=("missing_or_sparse_telemetry", "sum"),
            incomplete=("incomplete_lap_window", "sum"),
            position=("position_trace_discontinuity", "sum"),
            shape=("shape_mismatch_against_comparable_laps", "sum"),
            timing=("timing_session_boundary_artifact", "sum"),
        )
        .reset_index()
    )
    grouped["bad_pct"] = grouped["bad_laps"] / grouped["total_laps"] * 100
    return grouped.sort_values(["bad_laps", "bad_pct"], ascending=False)


def representative_examples(classified: pd.DataFrame, per_category: int = 5) -> pd.DataFrame:
    score_cols = [
        "speed_shape_rms_kmh",
        "position_path_ratio",
        "position_max_segment_units",
        "car_max_gap_ms",
        "car_coverage_ratio",
    ]
    rows = []
    for category in CATEGORY_COLUMNS:
        subset = classified[classified[category]].copy()
        if subset.empty:
            continue
        if category == "shape_mismatch_against_comparable_laps":
            subset = subset.sort_values("speed_shape_rms_kmh", ascending=False)
        elif category == "position_trace_discontinuity":
            subset["position_deviation"] = (subset["position_path_ratio"] - 1.0).abs()
            subset = subset.sort_values(["position_deviation", "position_max_segment_units"], ascending=False)
        elif category == "incomplete_lap_window":
            subset = subset.sort_values("car_coverage_ratio", ascending=True, na_position="first")
        elif category == "timing_session_boundary_artifact":
            subset = subset.sort_values("car_max_gap_ms", ascending=False, na_position="last")
        else:
            subset = subset.sort_values(["event_round", "driver_code", "lap_number"])
        keep_cols = [
            "event_round",
            "event_name",
            "driver_code",
            "lap_number",
            "primary_category_display",
            "reason_count",
            "reason_set",
            "lap_time_ms",
            "car_samples",
            "position_samples",
            *score_cols,
        ]
        sample = subset.head(per_category)[keep_cols].copy()
        sample = sample.rename(columns={"primary_category_display": "primary_category"})
        sample.insert(0, "category", category_display_name(category))
        rows.append(sample)
    if not rows:
        return pd.DataFrame()
    return pd.concat(rows, ignore_index=True)


def shape_cluster_feature_matrix(classified: pd.DataFrame) -> np.ndarray:
    feature_cols = [
        "lap_time_ms",
        "car_samples",
        "position_samples",
        "car_coverage_ratio",
        "car_p95_gap_ms",
        "car_max_gap_ms",
        "position_path_ratio",
        "position_max_segment_units",
        "speed_shape_rms_kmh",
        "telemetry_null_rate",
        "position_null_rate",
        "safety_car_periods",
        "virtual_safety_car_periods",
        "red_flag_periods",
    ] + [col for col in classified.columns if col.startswith("speed_bin_") and not col.endswith("_session_median")]

    cluster_source = classified[feature_cols].replace([np.inf, -np.inf], np.nan)
    preprocessing = make_pipeline(SimpleImputer(strategy="median"), RobustScaler())
    return preprocessing.fit_transform(cluster_source)


def compute_shape_clusters(classified: pd.DataFrame, n_clusters: int = 7) -> tuple[pd.DataFrame, pd.DataFrame]:
    matrix = shape_cluster_feature_matrix(classified)
    pca = PCA(n_components=2, random_state=0)
    coords = pca.fit_transform(matrix)
    kmeans = KMeans(n_clusters=n_clusters, random_state=0, n_init=20)
    labels = kmeans.fit_predict(matrix)

    clustered = classified.copy()
    clustered["cluster"] = labels
    clustered["cluster_x"] = coords[:, 0]
    clustered["cluster_y"] = coords[:, 1]
    clustered["cluster_distance"] = np.linalg.norm(matrix - kmeans.cluster_centers_[labels], axis=1)
    try:
        sil = float(silhouette_score(matrix, labels))
    except Exception:
        sil = math.nan

    profile = (
        clustered.groupby("cluster")
        .agg(
            laps=("lap_number", "size"),
            bad_pct=("bad_lap_any_category", "mean"),
            median_shape_rms=("speed_shape_rms_kmh", "median"),
            median_lap_time_ms=("lap_time_ms", "median"),
            median_car_coverage=("car_coverage_ratio", "median"),
            pit_sc_pct=("pit_lane_or_safety_car_influenced", "mean"),
            source_anomaly_pct=("import_or_source_data_anomaly", "mean"),
            position_discontinuity_pct=("position_trace_discontinuity", "mean"),
        )
        .reset_index()
    )
    profile["bad_pct"] *= 100
    profile["pit_sc_pct"] *= 100
    profile["source_anomaly_pct"] *= 100
    profile["position_discontinuity_pct"] *= 100
    profile["pca_variance_1"] = pca.explained_variance_ratio_[0]
    profile["pca_variance_2"] = pca.explained_variance_ratio_[1]
    profile["silhouette"] = sil
    return clustered, profile.sort_values("bad_pct", ascending=False)


def speed_profile_columns(classified: pd.DataFrame) -> list[str]:
    return [
        col
        for col in classified.columns
        if re.fullmatch(r"speed_bin_\d+", col)
    ]


def build_speed_profile_baselines(classified: pd.DataFrame) -> pd.DataFrame:
    speed_cols = speed_profile_columns(classified)
    reference = classified[
        classified["safe_for_lap_comparison"]
        & classified["is_accurate"].fillna(False).astype(bool)
        & classified["safety_car_periods"].eq(0)
        & classified["virtual_safety_car_periods"].eq(0)
        & classified["red_flag_periods"].eq(0)
        & classified[speed_cols].notna().sum(axis=1).ge(12)
    ].copy()
    if reference.empty:
        return pd.DataFrame()

    long = reference.melt(
        id_vars=["session_id", "event_round", "event_name"],
        value_vars=speed_cols,
        var_name="speed_bin",
        value_name="speed_kmh",
    ).dropna(subset=["speed_kmh"])
    long["speed_bin_number"] = long["speed_bin"].str.extract(r"(\d+)$").astype(int)
    baseline = (
        long.groupby(["session_id", "event_round", "event_name", "speed_bin_number"])
        .agg(
            reference_laps=("speed_kmh", "size"),
            speed_q10_kmh=("speed_kmh", lambda s: float(s.quantile(0.10))),
            speed_median_kmh=("speed_kmh", "median"),
            speed_q90_kmh=("speed_kmh", lambda s: float(s.quantile(0.90))),
        )
        .reset_index()
        .sort_values(["event_round", "speed_bin_number"])
    )
    return baseline


def build_shape_profile_examples(
    classified: pd.DataFrame,
    baselines: pd.DataFrame,
    limit: int = 12,
) -> pd.DataFrame:
    if baselines.empty:
        return pd.DataFrame()
    shape_rows = classified[
        classified["shape_mismatch_against_comparable_laps"]
        & classified["speed_shape_rms_kmh"].notna()
    ].copy()
    if shape_rows.empty:
        return pd.DataFrame()
    examples = shape_rows.sort_values(
        ["needs_manual_review", "speed_shape_excess_kmh", "speed_shape_rms_kmh"],
        ascending=[False, False, False],
    ).head(limit)
    keep_cols = [
        "session_id",
        "event_round",
        "event_name",
        "driver_code",
        "lap_number",
        "primary_category_display",
        "reason_set",
        "lap_time_ms",
        "speed_shape_rms_kmh",
        "speed_shape_excess_kmh",
        "speed_shape_robust_z",
        "is_pit_in_lap",
        "is_pit_out_lap",
        "safety_car_periods",
        "virtual_safety_car_periods",
        "red_flag_periods",
    ]
    return examples[keep_cols].rename(columns={"primary_category_display": "primary_category"})


def build_shape_cluster_exemplars(clustered: pd.DataFrame) -> pd.DataFrame:
    rows = []
    for cluster, subset in clustered.groupby("cluster"):
        primary_counts = subset["primary_category_display"].value_counts()
        common_primary = primary_counts.index[0] if not primary_counts.empty else "unknown"
        exemplar_specs = [
            ("nearest_centroid", subset.sort_values("cluster_distance", ascending=True).head(1)),
            ("highest_shape_severity", subset.sort_values("speed_shape_rms_kmh", ascending=False).head(1)),
            (
                "common_primary_category_example",
                subset[subset["primary_category_display"] == common_primary]
                .sort_values(["speed_shape_rms_kmh", "cluster_distance"], ascending=[False, True])
                .head(1),
            ),
        ]
        for exemplar_type, exemplar in exemplar_specs:
            if exemplar.empty:
                continue
            row = exemplar.iloc[0]
            rows.append(
                {
                    "cluster": int(cluster),
                    "exemplar_type": exemplar_type,
                    "event_round": row["event_round"],
                    "event_name": row["event_name"],
                    "driver_code": row["driver_code"],
                    "lap_number": row["lap_number"],
                    "primary_category": row["primary_category_display"],
                    "common_primary_category": common_primary,
                    "speed_shape_rms_kmh": row["speed_shape_rms_kmh"],
                    "speed_shape_excess_kmh": row["speed_shape_excess_kmh"],
                    "cluster_distance": row["cluster_distance"],
                    "reason_set": row["reason_set"],
                }
            )
    return pd.DataFrame(rows).sort_values(["cluster", "exemplar_type"])


def compute_shape_cluster_stability(
    classified: pd.DataFrame,
    k_values: tuple[int, ...] = (5, 6, 7, 8, 9),
    seeds: tuple[int, ...] = (0, 1, 2),
    sample_size: int = 5000,
) -> pd.DataFrame:
    matrix = shape_cluster_feature_matrix(classified)
    if len(matrix) == 0:
        return pd.DataFrame()
    rng = np.random.default_rng(0)
    if len(matrix) > sample_size:
        sample_idx = np.sort(rng.choice(len(matrix), size=sample_size, replace=False))
        eval_matrix = matrix[sample_idx]
    else:
        eval_matrix = matrix
    rows = []
    labels_by_k: dict[int, list[np.ndarray]] = {}
    for k in k_values:
        labels_for_k = []
        for seed in seeds:
            labels = KMeans(n_clusters=k, random_state=seed, n_init=10).fit_predict(eval_matrix)
            labels_for_k.append(labels)
            try:
                sil = float(silhouette_score(eval_matrix, labels, sample_size=min(2000, len(eval_matrix)), random_state=seed))
            except Exception:
                sil = math.nan
            rows.append(
                {
                    "k": k,
                    "seed": seed,
                    "sampled_laps": len(eval_matrix),
                    "silhouette": sil,
                    "ari_vs_seed_0": math.nan,
                }
            )
        labels_by_k[k] = labels_for_k

    stability = pd.DataFrame(rows)
    for k, labels_for_k in labels_by_k.items():
        reference = labels_for_k[0]
        for seed_index, labels in enumerate(labels_for_k):
            ari = 1.0 if seed_index == 0 else float(adjusted_rand_score(reference, labels))
            stability.loc[(stability["k"] == k) & (stability["seed"] == seeds[seed_index]), "ari_vs_seed_0"] = ari
    return stability.sort_values(["k", "seed"])


def write_skrub_report(classified: pd.DataFrame) -> Path:
    report_cols = [
        "event_round",
        "event_name",
        "driver_code",
        "lap_number",
        "lap_time_ms",
        "is_accurate",
        "is_pit_in_lap",
        "is_pit_out_lap",
        "car_samples",
        "position_samples",
        "car_coverage_ratio",
        "car_p95_gap_ms",
        "car_max_gap_ms",
        "position_path_ratio",
        "speed_shape_rms_kmh",
        "primary_category",
        "primary_category_display",
        "reason_count",
        "reason_set",
        "data_integrity_flag",
        "race_context_flag",
        "analytical_shape_flag",
        "safe_for_replay",
        "safe_for_lap_comparison",
        "safe_for_geometry_reference",
        "needs_manual_review",
        "product_recommendation",
        "bad_lap_any_category",
    ]
    report_path = ARTIFACT_DIR / "skrub_lap_quality_table_report.html"
    report = skrub.TableReport(
        classified[report_cols],
        title="2025 race telemetry lap-quality feature table",
        n_rows=20,
        order_by="event_round",
        compute_associations=False,
        plot_distributions=False,
        verbose=0,
    )
    report.write_html(report_path)
    return report_path


def save_table(df: pd.DataFrame, name: str) -> Path:
    path = TABLE_DIR / name
    if path.suffix == ".csv":
        df.to_csv(path, index=False)
    elif path.suffix == ".parquet":
        df.to_parquet(path, index=False)
    else:
        raise ValueError(f"Unsupported table suffix: {path.suffix}")
    return path


def sanitized_database_identity(url: str | None = None) -> str:
    parsed = urlparse(url or database_url())
    host = parsed.hostname or "unknown-host"
    database = parsed.path.lstrip("/") or "unknown-database"
    port = f":{parsed.port}" if parsed.port else ""
    return f"{host}{port}/{database}"


def write_metadata(
    *,
    thresholds: QualityThresholds,
    classified: pd.DataFrame,
    figure_paths: list[Path],
    table_paths: list[Path],
    report_path: Path | None,
    url: str | None = None,
) -> Path:
    metadata_path = ARTIFACT_DIR / "metadata.json"
    metadata = {
        "scope": {
            "year": 2025,
            "session_type": "R",
            "session_count": int(classified["session_id"].nunique()),
            "lap_count": int(len(classified)),
            "flagged_lap_count": int(classified["bad_lap_any_category"].sum()),
        },
        "database": sanitized_database_identity(url),
        "thresholds": asdict(thresholds),
        "package_versions": {
            "python": platform.python_version(),
            "pandas": pd.__version__,
            "numpy": np.__version__,
            "skrub": skrub.__version__,
        },
        "generated_at_utc": datetime.now(UTC).isoformat(),
        "artifacts": {
            "figures": [str(path.relative_to(REPO_ROOT)) for path in figure_paths],
            "tables": [str(path.relative_to(REPO_ROOT)) for path in table_paths],
            "skrub_report": str(report_path.relative_to(REPO_ROOT)) if report_path else None,
        },
    }
    metadata_path.write_text(json.dumps(metadata, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    return metadata_path


def plot_category_bar(category_summary: pd.DataFrame) -> Path:
    path = FIGURE_DIR / "category_counts.svg"
    fig, ax = plt.subplots(figsize=(10.5, 5.8))
    plot_df = category_summary.sort_values("laps", ascending=True)
    ax.barh(plot_df["category"], plot_df["laps"], color="#546A7B")
    ax.set_title("Bad-lap category counts, 2025 race sessions")
    ax.set_xlabel("Laps flagged")
    ax.set_ylabel("")
    for index, row in enumerate(plot_df.itertuples()):
        ax.text(row.laps + 8, index, f"{row.laps:,} ({row.pct_of_all_laps:.1f}%)", va="center", fontsize=9)
    fig.tight_layout()
    fig.savefig(path, format="svg")
    plt.close(fig)
    return path


def plot_race_comparison(race_summary: pd.DataFrame) -> Path:
    path = FIGURE_DIR / "race_bad_lap_rates.svg"
    fig, ax = plt.subplots(figsize=(12, 7))
    plot_df = race_summary.sort_values("event_round")
    ax.bar(plot_df["event_round"].astype(str), plot_df["bad_pct"], color="#7A5C58")
    ax.set_title("Bad-lap rate by 2025 race")
    ax.set_xlabel("Round")
    ax.set_ylabel("Any-category bad laps (%)")
    ax.set_ylim(0, max(5, plot_df["bad_pct"].max() * 1.15))
    for tick, label in zip(ax.get_xticks(), plot_df["event_name"]):
        pass
    ax.grid(axis="y", alpha=0.25)
    fig.tight_layout()
    fig.savefig(path, format="svg")
    plt.close(fig)
    return path


def plot_category_intersections(intersections: pd.DataFrame) -> Path:
    path = FIGURE_DIR / "category_intersections.svg"
    plot_df = intersections.head(14).sort_values("laps", ascending=True)
    fig, ax = plt.subplots(figsize=(12, 6.8))
    ax.barh(plot_df["reason_set"], plot_df["laps"], color="#4E79A7")
    ax.set_title("Top overlapping bad-lap reason sets")
    ax.set_xlabel("Laps")
    ax.set_ylabel("")
    for index, row in enumerate(plot_df.itertuples()):
        ax.text(row.laps + 6, index, f"{row.laps:,} ({row.pct_of_bad_laps:.1f}%)", va="center", fontsize=8)
    fig.tight_layout()
    fig.savefig(path, format="svg")
    plt.close(fig)
    return path


def plot_decision_waterfall(waterfall: pd.DataFrame) -> Path:
    path = FIGURE_DIR / "decision_waterfall.svg"
    plot_df = waterfall[waterfall["step"] != "total_laps"].copy()
    colors = [
        "#9C755F",
        "#76B7B2",
        "#E15759",
        "#59A14F",
        "#F28E2B",
        "#B07AA1",
        "#FF9DA7",
        "#8CD17D",
    ][: len(plot_df)]
    fig, ax = plt.subplots(figsize=(12, 5.8))
    ax.bar(plot_df["step"], plot_df["laps"], color=colors)
    ax.set_title("Decision waterfall for deterministic lap-quality attribution")
    ax.set_xlabel("")
    ax.set_ylabel("Laps assigned at step")
    ax.tick_params(axis="x", rotation=35)
    for index, row in enumerate(plot_df.itertuples()):
        ax.text(index, row.laps + 35, f"{row.laps:,}\n{row.pct_of_all_laps:.1f}%", ha="center", va="bottom", fontsize=8)
    ax.grid(axis="y", alpha=0.25)
    fig.tight_layout()
    fig.savefig(path, format="svg")
    plt.close(fig)
    return path


def plot_primary_race_decomposition(primary_by_race: pd.DataFrame) -> Path:
    path = FIGURE_DIR / "race_primary_category_decomposition.svg"
    pivot = (
        primary_by_race.pivot_table(
            index=["event_round", "event_name"],
            columns="primary_category",
            values="pct_of_race_laps",
            aggfunc="sum",
            fill_value=0.0,
        )
        .sort_index(level="event_round")
    )
    ordered_columns = [
        category_display_name(category)
        for category in [*PRIMARY_CATEGORY_PRIORITY, "clean"]
        if category_display_name(category) in pivot.columns
    ]
    pivot = pivot[ordered_columns]
    x = np.arange(len(pivot))
    fig, ax = plt.subplots(figsize=(13, 7))
    bottom = np.zeros(len(pivot))
    for category in pivot.columns:
        values = pivot[category].to_numpy(dtype=float)
        ax.bar(
            x,
            values,
            bottom=bottom,
            label=category,
            color=CATEGORY_PLOT_COLORS.get(category, "#BAB0AC"),
            width=0.82,
        )
        bottom += values
    ax.set_title("Primary bad-lap cause decomposition by 2025 race")
    ax.set_xlabel("Round")
    ax.set_ylabel("Race laps (%)")
    ax.set_xticks(x)
    ax.set_xticklabels([str(idx[0]) for idx in pivot.index])
    ax.set_ylim(0, 100)
    ax.grid(axis="y", alpha=0.22)
    ax.legend(loc="center left", bbox_to_anchor=(1.01, 0.5), fontsize=8)
    fig.tight_layout()
    fig.savefig(path, format="svg")
    plt.close(fig)
    return path


def plot_race_drilldowns(race_drilldowns: pd.DataFrame) -> Path:
    path = FIGURE_DIR / "selected_race_drilldowns.svg"
    if race_drilldowns.empty:
        fig, ax = plt.subplots(figsize=(8, 4))
        ax.text(0.5, 0.5, "No selected race drilldown rows available", ha="center", va="center")
        ax.axis("off")
        fig.savefig(path, format="svg")
        plt.close(fig)
        return path

    dominant = (
        race_drilldowns[race_drilldowns["primary_category"] != "clean"]
        .sort_values(["event_round", "lap_number", "flagged_laps"], ascending=[True, True, False])
        .drop_duplicates(["event_round", "event_name", "lap_number"], keep="first")
    )
    events = dominant[["event_round", "event_name"]].drop_duplicates().sort_values("event_round")
    fig, axes = plt.subplots(len(events), 1, figsize=(13, max(5, len(events) * 2.1)), sharex=False)
    if len(events) == 1:
        axes = [axes]

    categories = list(dominant["primary_category"].dropna().unique())
    handles = {}
    for ax, event in zip(axes, events.itertuples(index=False)):
        subset = dominant[dominant["event_name"] == event.event_name]
        for category, rows in subset.groupby("primary_category"):
            sizes = 18 + rows["drivers"].clip(lower=1, upper=20).to_numpy(dtype=float) * 5
            scatter = ax.scatter(
                rows["lap_number"],
                rows["flagged_laps"],
                s=sizes,
                color=CATEGORY_PLOT_COLORS.get(category, "#BAB0AC"),
                alpha=0.82,
                label=category,
                edgecolors="white",
                linewidths=0.4,
            )
            handles.setdefault(category, scatter)
        ax.set_title(f"R{int(event.event_round):02d} {event.event_name}")
        ax.set_ylabel("Flagged driver-laps")
        ax.grid(axis="y", alpha=0.2)
    axes[-1].set_xlabel("Lap number")
    ordered_handles = [handles[category] for category in categories if category in handles]
    ordered_labels = [category for category in categories if category in handles]
    fig.legend(ordered_handles, ordered_labels, loc="center left", bbox_to_anchor=(1.01, 0.5), fontsize=8)
    fig.tight_layout()
    fig.savefig(path, format="svg")
    plt.close(fig)
    return path


def plot_lap_number_heatmap(classified: pd.DataFrame) -> Path:
    path = FIGURE_DIR / "lap_number_primary_category_heatmap.svg"
    category_order = [
        "clean",
        "pit_lane_or_safety_car_influenced",
        "atypical_speed_profile",
        "import_or_source_data_anomaly",
        "timing_session_boundary_artifact",
        "position_trace_discontinuity",
        "implausible_channel_values",
        "missing_or_sparse_telemetry",
        "incomplete_lap_window",
        "unknown_needs_inspection",
    ]
    category_to_code = {category: index for index, category in enumerate(category_order)}
    dominant = (
        classified.groupby(
            ["event_round", "event_name", "lap_number", "primary_category_display"],
            dropna=False,
        )
        .size()
        .rename("driver_laps")
        .reset_index()
        .sort_values(["event_round", "lap_number", "driver_laps"], ascending=[True, True, False])
        .drop_duplicates(["event_round", "event_name", "lap_number"], keep="first")
    )
    dominant["code"] = dominant["primary_category_display"].map(category_to_code).fillna(0)
    heat = dominant.pivot_table(
        index=["event_round", "event_name"],
        columns="lap_number",
        values="code",
        aggfunc="first",
    ).sort_index(level="event_round")
    colors = [CATEGORY_PLOT_COLORS.get(category, "#BAB0AC") for category in category_order]
    fig, ax = plt.subplots(figsize=(14, 7.5))
    sns.heatmap(
        heat,
        cmap=ListedColormap(colors),
        vmin=0,
        vmax=len(category_order) - 1,
        cbar=False,
        linewidths=0,
        ax=ax,
    )
    ax.set_title("Dominant primary lap-quality category by race lap")
    ax.set_xlabel("Lap number")
    ax.set_ylabel("")
    ax.set_yticklabels([f"R{int(idx[0]):02d} {idx[1]}" for idx in heat.index], rotation=0, fontsize=8)
    handles = [
        plt.Line2D([0], [0], marker="s", linestyle="", color=color, label=category, markersize=8)
        for category, color in zip(category_order, colors)
        if category in set(dominant["primary_category_display"])
    ]
    fig.legend(handles=handles, loc="center left", bbox_to_anchor=(1.01, 0.5), fontsize=8)
    fig.tight_layout()
    fig.savefig(path, format="svg")
    plt.close(fig)
    return path


def plot_driver_race_matrix(driver_race_matrix: pd.DataFrame) -> Path:
    path = FIGURE_DIR / "driver_race_quality_matrix.svg"
    metrics = [
        ("bad_pct", "Any flag"),
        ("integrity_pct", "Integrity"),
        ("context_pct", "Context"),
    ]
    fig, axes = plt.subplots(1, len(metrics), figsize=(16, 9), sharey=True)
    for ax, (metric, title) in zip(axes, metrics):
        pivot = driver_race_matrix.pivot_table(
            index="driver_code",
            columns="event_round",
            values=metric,
            aggfunc="mean",
        ).sort_index()
        sns.heatmap(
            pivot,
            cmap="rocket_r",
            vmin=0,
            vmax=max(5, float(driver_race_matrix[metric].max())),
            linewidths=0.25,
            linecolor="white",
            cbar_kws={"label": "% laps"},
            ax=ax,
        )
        ax.set_title(title)
        ax.set_xlabel("Round")
        ax.set_ylabel("")
    fig.suptitle("Driver-by-race bad-lap rate matrix, minimum 10 laps", y=1.02)
    fig.tight_layout()
    fig.savefig(path, format="svg")
    plt.close(fig)
    return path


def plot_recommendation_summary(recommendation_summary: pd.DataFrame) -> Path:
    path = FIGURE_DIR / "recommendation_summary.svg"
    order = ["keep", "keep_with_context_label", "manual_review", "exclude"]
    plot_df = (
        recommendation_summary.set_index("product_recommendation")
        .reindex(order)
        .dropna(subset=["laps"])
        .reset_index()
    )
    colors = {
        "keep": "#8CD17D",
        "keep_with_context_label": "#F28E2B",
        "manual_review": "#B07AA1",
        "exclude": "#E15759",
    }
    fig, ax = plt.subplots(figsize=(9, 5.4))
    ax.bar(
        plot_df["product_recommendation"],
        plot_df["laps"],
        color=[colors.get(value, "#BAB0AC") for value in plot_df["product_recommendation"]],
    )
    ax.set_title("Final lap-quality recommendation buckets")
    ax.set_xlabel("")
    ax.set_ylabel("Laps")
    ax.tick_params(axis="x", rotation=20)
    for index, row in enumerate(plot_df.itertuples()):
        ax.text(index, row.laps + 90, f"{int(row.laps):,}\n{row.pct_of_all_laps:.1f}%", ha="center", va="bottom", fontsize=9)
    ax.grid(axis="y", alpha=0.25)
    fig.tight_layout()
    fig.savefig(path, format="svg")
    plt.close(fig)
    return path


def plot_threshold_sensitivity(threshold_summary: pd.DataFrame) -> Path:
    path = FIGURE_DIR / "threshold_sensitivity.svg"
    plot_df = threshold_summary[threshold_summary["scenario"] != "baseline"].copy()
    plot_df = plot_df.sort_values("bad_laps_delta_vs_baseline")
    fig, ax = plt.subplots(figsize=(12, 7))
    colors = np.where(plot_df["bad_laps_delta_vs_baseline"] >= 0, "#E15759", "#4E79A7")
    ax.barh(plot_df["scenario"], plot_df["bad_laps_delta_vs_baseline"], color=colors)
    ax.axvline(0, color="#111827", linewidth=1)
    ax.set_title("Threshold sensitivity: bad-lap count delta vs baseline")
    ax.set_xlabel("Bad-lap count delta")
    ax.set_ylabel("")
    for index, row in enumerate(plot_df.itertuples()):
        ax.text(
            row.bad_laps_delta_vs_baseline,
            index,
            f" {int(row.bad_laps_delta_vs_baseline):+d}",
            va="center",
            ha="left" if row.bad_laps_delta_vs_baseline >= 0 else "right",
            fontsize=8,
        )
    fig.tight_layout()
    fig.savefig(path, format="svg")
    plt.close(fig)
    return path


def plot_borderline_by_race(threshold_by_race: pd.DataFrame) -> Path:
    path = FIGURE_DIR / "borderline_laps_by_race.svg"
    plot_df = threshold_by_race.sort_values("event_round")
    fig, ax = plt.subplots(figsize=(13, 5.8))
    ax.bar(
        plot_df["event_round"].astype(str),
        plot_df["threshold_sensitive_pct"],
        color="#B07AA1",
    )
    ax.set_title("Threshold-sensitive laps by 2025 race")
    ax.set_xlabel("Round")
    ax.set_ylabel("Laps sensitive to at least one threshold scenario (%)")
    ax.grid(axis="y", alpha=0.25)
    for tick, row in enumerate(plot_df.itertuples()):
        if row.threshold_sensitive_pct > 0:
            ax.text(
                tick,
                row.threshold_sensitive_pct + 0.3,
                f"{row.threshold_sensitive_laps}",
                ha="center",
                va="bottom",
                fontsize=8,
            )
    fig.tight_layout()
    fig.savefig(path, format="svg")
    plt.close(fig)
    return path


def plot_missingness_heatmap(classified: pd.DataFrame) -> Path:
    path = FIGURE_DIR / "missingness_by_race.svg"
    metrics = (
        classified.assign(
            telemetry_null_pct=classified["telemetry_null_rate"].fillna(0) * 100,
            position_null_pct=classified["position_null_rate"].fillna(0) * 100,
            sparse_pct=classified["missing_or_sparse_telemetry"].astype(float) * 100,
            incomplete_pct=classified["incomplete_lap_window"].astype(float) * 100,
        )
        .groupby(["event_round", "event_name"])[
            ["telemetry_null_pct", "position_null_pct", "sparse_pct", "incomplete_pct"]
        ]
        .mean()
        .reset_index()
    )
    heat = metrics.set_index("event_name")[
        ["telemetry_null_pct", "position_null_pct", "sparse_pct", "incomplete_pct"]
    ]
    fig, ax = plt.subplots(figsize=(9, 8))
    sns.heatmap(heat, cmap="mako_r", linewidths=0.4, linecolor="white", cbar_kws={"label": "%"})
    ax.set_title("Missingness and incompleteness patterns by race")
    ax.set_xlabel("")
    ax.set_ylabel("")
    fig.tight_layout()
    fig.savefig(path, format="svg")
    plt.close(fig)
    return path


def plot_shape_clusters(clustered: pd.DataFrame) -> Path:
    path = FIGURE_DIR / "shape_clusters.svg"
    fig, ax = plt.subplots(figsize=(10, 7))
    clean = clustered[~clustered["bad_lap_any_category"]]
    bad = clustered[clustered["bad_lap_any_category"]]
    ax.scatter(clean["cluster_x"], clean["cluster_y"], s=8, c="#CBD5E1", alpha=0.45, label="clean")
    scatter = ax.scatter(
        bad["cluster_x"],
        bad["cluster_y"],
        s=12,
        c=bad["cluster"],
        cmap="tab10",
        alpha=0.80,
        label="bad/category flagged",
    )
    ax.set_title("Lap-shape feature clusters from speed profile and quality metrics")
    ax.set_xlabel("PCA component 1")
    ax.set_ylabel("PCA component 2")
    ax.legend(loc="best")
    fig.colorbar(scatter, ax=ax, label="cluster")
    fig.tight_layout()
    fig.savefig(path, format="svg")
    plt.close(fig)
    return path


def plot_representative_speed_traces(classified: pd.DataFrame) -> Path:
    path = FIGURE_DIR / "representative_speed_shapes.svg"
    speed_cols = [col for col in classified.columns if col.startswith("speed_bin_") and not col.endswith("_session_median")]
    categories = [
        "shape_mismatch_against_comparable_laps",
        "pit_lane_or_safety_car_influenced",
        "position_trace_discontinuity",
        "incomplete_lap_window",
    ]
    examples = []
    for category in categories:
        subset = classified[classified[category] & classified[speed_cols].notna().sum(axis=1).ge(12)]
        if subset.empty:
            continue
        if category == "shape_mismatch_against_comparable_laps":
            row = subset.sort_values("speed_shape_rms_kmh", ascending=False).iloc[0]
        else:
            row = subset.sort_values(["event_round", "driver_code", "lap_number"]).iloc[0]
        examples.append((category, row))

    fig, ax = plt.subplots(figsize=(10, 5.8))
    x = np.arange(1, len(speed_cols) + 1)
    for category, row in examples:
        label = f"{category}: R{int(row.event_round):02d} {row.driver_code} L{int(row.lap_number)}"
        ax.plot(x, row[speed_cols].astype(float).to_numpy(), linewidth=2, marker="o", markersize=3, label=label)
    ax.set_title("Representative lap speed-shape profiles")
    ax.set_xlabel("Equal lap-time bin")
    ax.set_ylabel("Mean speed (km/h)")
    ax.grid(alpha=0.25)
    ax.legend(fontsize=8)
    fig.tight_layout()
    fig.savefig(path, format="svg")
    plt.close(fig)
    return path


def plot_shape_profile_bands(
    classified: pd.DataFrame,
    baselines: pd.DataFrame,
    examples: pd.DataFrame,
) -> Path:
    path = FIGURE_DIR / "shape_profile_quantile_bands.svg"
    speed_cols = speed_profile_columns(classified)
    if baselines.empty or examples.empty:
        fig, ax = plt.subplots(figsize=(8, 4))
        ax.text(0.5, 0.5, "No shape profile examples available", ha="center", va="center")
        ax.axis("off")
        fig.savefig(path, format="svg")
        plt.close(fig)
        return path

    plot_examples = examples.head(6)
    fig, axes = plt.subplots(len(plot_examples), 1, figsize=(11, max(5, len(plot_examples) * 2.1)), sharex=True)
    if len(plot_examples) == 1:
        axes = [axes]
    for ax, example in zip(axes, plot_examples.itertuples(index=False)):
        row = classified[
            (classified["session_id"] == example.session_id)
            & (classified["driver_code"] == example.driver_code)
            & (classified["lap_number"] == example.lap_number)
        ].iloc[0]
        baseline = baselines[baselines["session_id"] == example.session_id].sort_values("speed_bin_number")
        x = baseline["speed_bin_number"].to_numpy(dtype=float)
        ax.fill_between(
            x,
            baseline["speed_q10_kmh"].to_numpy(dtype=float),
            baseline["speed_q90_kmh"].to_numpy(dtype=float),
            color="#CBD5E1",
            alpha=0.55,
            label="clean green 10th-90th pct",
        )
        ax.plot(x, baseline["speed_median_kmh"], color="#111827", linewidth=1.5, label="clean green median")
        ax.plot(
            np.arange(1, len(speed_cols) + 1),
            row[speed_cols].astype(float).to_numpy(),
            color="#E15759",
            linewidth=1.8,
            marker="o",
            markersize=3,
            label="selected lap",
        )
        context = []
        if bool(row["is_pit_in_lap"]) or bool(row["is_pit_out_lap"]):
            context.append("pit")
        if row["safety_car_periods"] > 0:
            context.append("SC")
        if row["virtual_safety_car_periods"] > 0:
            context.append("VSC")
        if row["red_flag_periods"] > 0:
            context.append("red flag")
        context_label = ", ".join(context) if context else "green/no pit marker"
        ax.set_title(
            f"R{int(example.event_round):02d} {example.event_name} "
            f"{example.driver_code} L{int(example.lap_number)} - {context_label}",
            fontsize=9,
        )
        ax.set_ylabel("km/h")
        ax.grid(alpha=0.2)
    axes[-1].set_xlabel("Equal lap-time bin")
    axes[0].legend(loc="upper right", fontsize=8)
    fig.tight_layout()
    fig.savefig(path, format="svg")
    plt.close(fig)
    return path


def plot_shape_cluster_stability(stability: pd.DataFrame) -> Path:
    path = FIGURE_DIR / "shape_cluster_stability.svg"
    fig, axes = plt.subplots(1, 2, figsize=(11, 4.8))
    if stability.empty:
        for ax in axes:
            ax.text(0.5, 0.5, "No cluster stability rows available", ha="center", va="center")
            ax.axis("off")
        fig.savefig(path, format="svg")
        plt.close(fig)
        return path

    summary = (
        stability.groupby("k")
        .agg(
            silhouette_mean=("silhouette", "mean"),
            silhouette_min=("silhouette", "min"),
            silhouette_max=("silhouette", "max"),
            ari_mean=("ari_vs_seed_0", "mean"),
            ari_min=("ari_vs_seed_0", "min"),
            ari_max=("ari_vs_seed_0", "max"),
        )
        .reset_index()
    )
    axes[0].plot(summary["k"], summary["silhouette_mean"], marker="o", color="#4E79A7")
    axes[0].fill_between(summary["k"], summary["silhouette_min"], summary["silhouette_max"], color="#4E79A7", alpha=0.2)
    axes[0].set_title("Sampled silhouette by k")
    axes[0].set_xlabel("k")
    axes[0].set_ylabel("silhouette")
    axes[0].grid(alpha=0.25)
    axes[1].plot(summary["k"], summary["ari_mean"], marker="o", color="#B07AA1")
    axes[1].fill_between(summary["k"], summary["ari_min"], summary["ari_max"], color="#B07AA1", alpha=0.2)
    axes[1].set_title("Seed stability by k")
    axes[1].set_xlabel("k")
    axes[1].set_ylabel("ARI vs seed 0")
    axes[1].set_ylim(0, 1.05)
    axes[1].grid(alpha=0.25)
    fig.tight_layout()
    fig.savefig(path, format="svg")
    plt.close(fig)
    return path


def load_apexline_summary() -> tuple[pd.DataFrame, pd.DataFrame]:
    # Apexline now lives in its own repo: https://github.com/fblln/apexline
    # To regenerate this geometry cross-check, clone it and run:
    #   python scripts/analyze_f1_circuit_gps.py --year 2025 \
    #       --lap-diagnostics-output data/lap-diagnostics-2025.json
    # then copy that file to data/apexline/ here (or point APEXLINE_DIAGNOSTICS at it).
    # Missing file is fine — the notebook just skips the cross-check section.
    override = os.environ.get("APEXLINE_DIAGNOSTICS")
    path = Path(override) if override else REPO_ROOT / "data" / "apexline" / "lap-diagnostics-2025.json"
    if not path.exists():
        return pd.DataFrame(), pd.DataFrame()
    data = json.loads(path.read_text(encoding="utf-8"))
    event_rows = []
    example_rows = []
    for row in data:
        reason_counts = row.get("reason_counts", {})
        event_rows.append(
            {
                "event_name": row.get("event_name"),
                "event_round": row.get("round"),
                "apexline_total_laps": row.get("total_laps"),
                "apexline_bad_laps": row.get("non_compliant_laps"),
                "apexline_good_pct": row.get("compliant_laps", 0) / row.get("total_laps", 1) * 100,
                "apexline_shape_bad_laps": row.get("shape_non_compliant_laps"),
                "apexline_fastf1_inaccurate": reason_counts.get("fastf1_inaccurate", 0),
                "apexline_pit_lap": reason_counts.get("pit_lap", 0),
                "apexline_missing_lap_time": reason_counts.get("missing_lap_time", 0),
                "apexline_path_length_outlier": reason_counts.get("path_length_outlier", 0),
            }
        )
        for example in row.get("worst_shape_laps", []):
            fit = example.get("fit") or {}
            example_rows.append(
                {
                    "event_name": row.get("event_name"),
                    "event_round": row.get("round"),
                    "driver_code": example.get("driver"),
                    "lap_number": example.get("lap_number"),
                    "reasons": ",".join(example.get("reasons", [])),
                    "shape_rmse_m": fit.get("rmse_m"),
                    "shape_p95_m": fit.get("p95_m"),
                    "path_length_m": example.get("path_length_m"),
                    "length_error_pct": example.get("length_error_pct"),
                }
            )
    return pd.DataFrame(event_rows), pd.DataFrame(example_rows)


def format_pct(value: float) -> str:
    return f"{value:.1f}%"


def build_markdown_summary(
    classified: pd.DataFrame,
    thresholds: QualityThresholds,
    thresholds_df: pd.DataFrame,
    category_summary: pd.DataFrame,
    primary_summary: pd.DataFrame,
    lens_summary: pd.DataFrame,
    safety_summary: pd.DataFrame,
    recommendation_summary: pd.DataFrame,
    intersections: pd.DataFrame,
    waterfall: pd.DataFrame,
    race_summary: pd.DataFrame,
    primary_by_race: pd.DataFrame,
    race_drilldowns: pd.DataFrame,
    primary_audit: pd.DataFrame,
    driver_race_matrix: pd.DataFrame,
    threshold_summary: pd.DataFrame,
    threshold_by_race: pd.DataFrame,
    threshold_by_driver: pd.DataFrame,
    borderline_laps: pd.DataFrame,
    speed_profile_baselines: pd.DataFrame,
    shape_profile_examples: pd.DataFrame,
    shape_cluster_exemplars: pd.DataFrame,
    shape_cluster_stability: pd.DataFrame,
    driver_summary: pd.DataFrame,
    examples: pd.DataFrame,
    figure_paths: list[Path],
    report_path: Path,
    apexline_summary: pd.DataFrame,
) -> str:
    total = len(classified)
    any_bad = int(classified["bad_lap_any_category"].sum())
    clean = total - any_bad
    imported_sessions = classified["session_id"].nunique()
    lines = [
        "# 2025 race telemetry bad-lap EDA summary",
        "",
        "Scope: 2025 race sessions (`session_type = 'R'`) imported in the local TimescaleDB. "
        "No FP, qualifying, sprint qualifying, or sprint sessions are included.",
        "",
        "## Data availability",
        "",
        f"- Race sessions inspected: {imported_sessions}",
        f"- Laps inspected: {total:,}",
        f"- Laps with at least one quality/category flag: {any_bad:,} ({format_pct(any_bad / total * 100 if total else 0)})",
        f"- Laps with no quality/category flag: {clean:,} ({format_pct(clean / total * 100 if total else 0)})",
        f"- Skrub report: `{report_path.relative_to(REPO_ROOT)}`",
        "",
        "## Taxonomy",
        "",
        "- `missing_or_sparse_telemetry`: too few car or position samples for a defensible lap-level trace.",
        "- `incomplete_lap_window`: missing timing or less than the configured lap-window coverage in raw car telemetry.",
        "- `distance_reset_or_non_monotonic_distance`: unavailable in this time/raw-domain pass because a persisted distance projection is not yet the authority for notebook classification.",
        "- `implausible_channel_values`: speed, RPM, gear, throttle, or brake outside configured physical/source bounds.",
        "- `atypical_speed_profile`: equal-lap-time speed profile is a robust outlier versus clean laps from the same race. This is a time-domain shape lens, not authoritative distance-domain lap-comparison truth. The compatibility source column remains `shape_mismatch_against_comparable_laps`.",
        "- `position_trace_discontinuity`: position path length or segment jumps are inconsistent with clean same-race laps.",
        "- `pit_lane_or_safety_car_influenced`: pit-in/out, safety car, VSC, or red-flag context overlaps the lap.",
        "- `timing_session_boundary_artifact`: missing session-relative timing, raw sample ordering issues, lap-time reset, or large raw telemetry gaps.",
        "- `import_or_source_data_anomaly`: FastF1 inaccurate/deleted lap marker.",
        "- `unknown_needs_inspection`: speed-shape outlier with no stronger explanatory signal.",
        "",
        "Reason columns are not mutually exclusive. The `primary_category` table assigns each lap to the first applicable category in a deterministic priority order.",
        "`reason_count` and `reason_set` are written to the classified-lap table so overlaps can be filtered without recomputing the taxonomy.",
        "",
        "## Thresholds",
        "",
        thresholds_df.to_markdown(index=False),
        "",
        "## Quality lenses and safety flags",
        "",
        "This EDA is the time/raw-domain quality pass. It separates data-integrity failures from race context and analytical shape outliers. "
        "`safe_for_replay` only excludes replay-blocking integrity failures; context-labeled laps can still be replayed. "
        "`safe_for_time_domain_analysis` is the primary bounded-analysis flag in this notebook. "
        "`safe_for_lap_comparison` is retained only as a deprecated compatibility alias for the current time-bucket comparison surface. "
        "`distance_alignment_status` remains `not_evaluated_requires_distance_projection` until the distance-domain projection exists. "
        "`safe_for_geometry_reference` remains stricter because geometry-reference workflows should avoid source, context, and shape-review laps.",
        "",
        lens_summary.to_markdown(index=False, floatfmt=".2f"),
        "",
        safety_summary.to_markdown(index=False, floatfmt=".2f"),
        "",
        "## Product recommendation buckets",
        "",
        recommendation_summary.to_markdown(index=False, floatfmt=".2f"),
        "",
        "## Category counts",
        "",
        category_summary.to_markdown(index=False, floatfmt=".2f"),
        "",
        "## Primary categories",
        "",
        primary_summary.to_markdown(index=False, floatfmt=".2f"),
        "",
        "## Top category intersections",
        "",
        intersections.head(20).to_markdown(index=False, floatfmt=".2f"),
        "",
        "## Decision waterfall",
        "",
        waterfall.to_markdown(index=False, floatfmt=".2f"),
        "",
        "## Most affected races",
        "",
        race_summary.head(10).to_markdown(index=False, floatfmt=".2f"),
        "",
        "## Primary-category race decomposition",
        "",
        primary_by_race[
            primary_by_race["primary_category"] != "clean"
        ].sort_values(["event_round", "laps"], ascending=[True, False]).head(80).to_markdown(index=False, floatfmt=".2f"),
        "",
        "## Selected race drilldowns",
        "",
        race_drilldowns[race_drilldowns["flagged_laps"] > 0].head(80).to_markdown(index=False, floatfmt=".2f"),
        "",
        "## Driver-by-race quality matrix",
        "",
        driver_race_matrix.head(80).to_markdown(index=False, floatfmt=".2f"),
        "",
        "## Primary-category audit",
        "",
        primary_audit.head(80).to_markdown(index=False, floatfmt=".2f"),
        "",
        "## Threshold sensitivity",
        "",
        "Threshold scenarios loosen and tighten the main classification cutoffs without changing the analysis scope. "
        "The borderline-lap table lists laps whose bad flag, recommendation, safety, or primary category changes under at least one scenario.",
        "",
        threshold_summary.to_markdown(index=False, floatfmt=".2f"),
        "",
        "### Most threshold-sensitive races",
        "",
        threshold_by_race.head(12).to_markdown(index=False, floatfmt=".2f"),
        "",
        "### Most threshold-sensitive drivers",
        "",
        threshold_by_driver.head(12).to_markdown(index=False, floatfmt=".2f"),
        "",
        "### Borderline laps for manual review",
        "",
        borderline_laps.head(80).to_markdown(index=False, floatfmt=".2f"),
        "",
        "## Most affected drivers",
        "",
        driver_summary.head(12).to_markdown(index=False, floatfmt=".2f"),
        "",
        "## Speed-shape baseline checks",
        "",
        "Shape outliers are compared against clean green-flag same-race speed profiles. "
        "The baseline table stores 10th percentile, median, and 90th percentile speed by equal lap-time bin. "
        "Because raw distance is not imported, these are still equal-time profiles rather than lap-distance profiles.",
        "",
        "### Shape profile examples",
        "",
        shape_profile_examples.head(20).to_markdown(index=False, floatfmt=".2f"),
        "",
        "### Shape cluster exemplars",
        "",
        shape_cluster_exemplars.head(40).to_markdown(index=False, floatfmt=".2f"),
        "",
        "### Shape cluster stability",
        "",
        shape_cluster_stability.to_markdown(index=False, floatfmt=".3f"),
        "",
        "## Representative examples",
        "",
        examples.head(60).to_markdown(index=False, floatfmt=".3f"),
        "",
        "## Visual artifacts",
        "",
    ]
    for path in figure_paths:
        lines.append(f"- `{path.relative_to(REPO_ROOT)}`")
    lines.extend(
        [
            "",
            "## Data artifacts",
            "",
            "- `artifacts/2025-telemetry-bad-lap-eda/tables/classified_laps_2025.csv`",
            "- `artifacts/2025-telemetry-bad-lap-eda/tables/classified_laps_2025.parquet`",
            "- `artifacts/2025-telemetry-bad-lap-eda/tables/thresholds_2025.csv`",
            "- `artifacts/2025-telemetry-bad-lap-eda/tables/quality_lens_summary_2025.csv`",
            "- `artifacts/2025-telemetry-bad-lap-eda/tables/safety_summary_2025.csv`",
            "- `artifacts/2025-telemetry-bad-lap-eda/tables/recommendation_summary_2025.csv`",
            "- `artifacts/2025-telemetry-bad-lap-eda/tables/driver_race_quality_matrix_2025.csv`",
            "- `artifacts/2025-telemetry-bad-lap-eda/tables/primary_category_audit_2025.csv`",
            "- `artifacts/2025-telemetry-bad-lap-eda/tables/threshold_sensitivity_2025.csv`",
            "- `artifacts/2025-telemetry-bad-lap-eda/tables/threshold_sensitivity_by_race_2025.csv`",
            "- `artifacts/2025-telemetry-bad-lap-eda/tables/threshold_sensitivity_by_driver_2025.csv`",
            "- `artifacts/2025-telemetry-bad-lap-eda/tables/borderline_laps_2025.csv`",
            "- `artifacts/2025-telemetry-bad-lap-eda/tables/speed_profile_baselines_2025.csv`",
            "- `artifacts/2025-telemetry-bad-lap-eda/tables/shape_profile_examples_2025.csv`",
            "- `artifacts/2025-telemetry-bad-lap-eda/tables/shape_cluster_exemplars_2025.csv`",
            "- `artifacts/2025-telemetry-bad-lap-eda/tables/shape_cluster_stability_2025.csv`",
            "- `artifacts/2025-telemetry-bad-lap-eda/metadata.json`",
        ]
    )
    if not apexline_summary.empty:
        shape_bad = int(apexline_summary["apexline_shape_bad_laps"].sum())
        apex_bad = int(apexline_summary["apexline_bad_laps"].sum())
        apex_total = int(apexline_summary["apexline_total_laps"].sum())
        lines.extend(
            [
                "",
                "## Cross-check with standalone Apexline geometry diagnostics",
                "",
                f"- Apexline inspected {apex_total:,} 2025 race laps and rejected {apex_bad:,} ({format_pct(apex_bad / apex_total * 100 if apex_total else 0)}).",
                f"- Apexline geometry shape-threshold rejects: {shape_bad:,}.",
                "- This notebook uses the imported DB surface for all-lap telemetry/timing/position quality and uses Apexline as a geometry-reference cross-check.",
            ]
        )
    lines.extend(
        [
            "",
            "## Limitations",
            "",
            "- Raw FastF1 `Distance` is not imported, so distance reset/non-monotonic checks are marked unavailable.",
            "- Speed-profile shape outliers are exploratory and should be reviewed against video/race context before treating them as source-data faults.",
            "- Safety-car and flag context uses imported status periods and race-control messages; ambiguous local yellows may need sector-level review.",
            "- Geometry-reference diagnostics from Apexline are event-level plus selected worst-lap examples unless that standalone pipeline is extended to persist every per-lap record.",
            "",
            "## Recommended next analyses",
            "",
            "- Persist per-lap geometry diagnostics from Apexline so GPS-shape categories can be grouped by race, driver, and lap without rerunning FastF1.",
            "- Add importer support for raw/derived distance if distance reset checks become a first-class quality gate.",
            "- Add focused real-database Query API tests around bad-lap and sparse-window behavior before exposing these quality flags in API contracts.",
        ]
    )
    return "\n".join(lines) + "\n"


def run_analysis(url: str | None = None, write_outputs: bool = True) -> dict[str, Any]:
    thresholds = QualityThresholds()
    sessions = load_session_inventory(url)
    laps = load_lap_quality_features(url)
    speed_profiles = load_speed_profile_features(url)
    features = add_derived_features(laps, speed_profiles, thresholds)
    classified = classify_laps(features, thresholds)
    clustered, cluster_profile = compute_shape_clusters(classified)
    speed_profile_baselines = build_speed_profile_baselines(clustered)
    shape_profile_examples = build_shape_profile_examples(clustered, speed_profile_baselines)
    shape_cluster_exemplars = build_shape_cluster_exemplars(clustered)
    shape_cluster_stability = compute_shape_cluster_stability(clustered)

    thresholds_df = threshold_table(thresholds)
    category_summary = summarize_categories(clustered)
    primary_summary = summarize_primary(clustered)
    lens_summary = summarize_quality_lenses(clustered)
    safety_summary = summarize_safety(clustered)
    recommendation_summary = summarize_recommendations(clustered)
    intersections = summarize_category_intersections(clustered)
    waterfall = summarize_decision_waterfall(clustered)
    race_summary = summarize_by_race(clustered)
    primary_by_race = summarize_primary_by_race(clustered)
    race_drilldowns = build_race_drilldowns(clustered)
    primary_audit = build_primary_category_audit(clustered)
    driver_race_matrix = summarize_driver_race_matrix(clustered)
    threshold_summary, threshold_by_race, threshold_by_driver, borderline_laps = (
        run_threshold_sensitivity(laps, speed_profiles, thresholds)
    )
    driver_summary = summarize_by_driver(clustered)
    examples = representative_examples(clustered)
    apexline_summary, apexline_examples = load_apexline_summary()

    report_path = write_skrub_report(clustered)
    figure_paths = [
        plot_category_bar(category_summary),
        plot_race_comparison(race_summary),
        plot_category_intersections(intersections),
        plot_decision_waterfall(waterfall),
        plot_primary_race_decomposition(primary_by_race),
        plot_race_drilldowns(race_drilldowns),
        plot_lap_number_heatmap(clustered),
        plot_driver_race_matrix(driver_race_matrix),
        plot_recommendation_summary(recommendation_summary),
        plot_threshold_sensitivity(threshold_summary),
        plot_borderline_by_race(threshold_by_race),
        plot_missingness_heatmap(clustered),
        plot_shape_clusters(clustered),
        plot_representative_speed_traces(clustered),
        plot_shape_profile_bands(clustered, speed_profile_baselines, shape_profile_examples),
        plot_shape_cluster_stability(shape_cluster_stability),
    ]

    table_paths: list[Path] = []
    metadata_path = None
    if write_outputs:
        table_paths.extend(
            [
                save_table(clustered, "classified_laps_2025.csv"),
                save_table(clustered, "classified_laps_2025.parquet"),
                save_table(thresholds_df, "thresholds_2025.csv"),
                save_table(category_summary, "category_summary_2025.csv"),
                save_table(primary_summary, "primary_category_summary_2025.csv"),
                save_table(lens_summary, "quality_lens_summary_2025.csv"),
                save_table(safety_summary, "safety_summary_2025.csv"),
                save_table(recommendation_summary, "recommendation_summary_2025.csv"),
                save_table(intersections, "category_intersections_2025.csv"),
                save_table(waterfall, "decision_waterfall_2025.csv"),
                save_table(race_summary, "race_summary_2025.csv"),
                save_table(primary_by_race, "primary_category_by_race_2025.csv"),
                save_table(race_drilldowns, "selected_race_drilldowns_2025.csv"),
                save_table(primary_audit, "primary_category_audit_2025.csv"),
                save_table(driver_race_matrix, "driver_race_quality_matrix_2025.csv"),
                save_table(threshold_summary, "threshold_sensitivity_2025.csv"),
                save_table(threshold_by_race, "threshold_sensitivity_by_race_2025.csv"),
                save_table(threshold_by_driver, "threshold_sensitivity_by_driver_2025.csv"),
                save_table(borderline_laps, "borderline_laps_2025.csv"),
                save_table(driver_summary, "driver_summary_2025.csv"),
                save_table(examples, "representative_examples_2025.csv"),
                save_table(cluster_profile, "shape_cluster_profile_2025.csv"),
                save_table(speed_profile_baselines, "speed_profile_baselines_2025.csv"),
                save_table(shape_profile_examples, "shape_profile_examples_2025.csv"),
                save_table(shape_cluster_exemplars, "shape_cluster_exemplars_2025.csv"),
                save_table(shape_cluster_stability, "shape_cluster_stability_2025.csv"),
            ]
        )
        if not apexline_summary.empty:
            table_paths.append(save_table(apexline_summary, "apexline_event_summary_2025.csv"))
        if not apexline_examples.empty:
            table_paths.append(save_table(apexline_examples, "apexline_shape_examples_2025.csv"))
        metadata_path = write_metadata(
            thresholds=thresholds,
            classified=clustered,
            figure_paths=figure_paths,
            table_paths=table_paths,
            report_path=report_path,
            url=url,
        )

        summary = build_markdown_summary(
            clustered,
            thresholds,
            thresholds_df,
            category_summary,
            primary_summary,
            lens_summary,
            safety_summary,
            recommendation_summary,
            intersections,
            waterfall,
            race_summary,
            primary_by_race,
            race_drilldowns,
            primary_audit,
            driver_race_matrix,
            threshold_summary,
            threshold_by_race,
            threshold_by_driver,
            borderline_laps,
            speed_profile_baselines,
            shape_profile_examples,
            shape_cluster_exemplars,
            shape_cluster_stability,
            driver_summary,
            examples,
            figure_paths,
            report_path,
            apexline_summary,
        )
        summary_path = REPO_ROOT / "docs" / "data-quality" / "2025-telemetry-bad-lap-eda-summary.md"
        summary_path.parent.mkdir(parents=True, exist_ok=True)
        summary_path.write_text(summary, encoding="utf-8")
    else:
        summary_path = None

    return {
        "thresholds": thresholds,
        "sessions": sessions,
        "laps": laps,
        "speed_profiles": speed_profiles,
        "features": features,
        "classified": clustered,
        "thresholds_df": thresholds_df,
        "category_summary": category_summary,
        "primary_summary": primary_summary,
        "lens_summary": lens_summary,
        "safety_summary": safety_summary,
        "recommendation_summary": recommendation_summary,
        "intersections": intersections,
        "waterfall": waterfall,
        "race_summary": race_summary,
        "primary_by_race": primary_by_race,
        "race_drilldowns": race_drilldowns,
        "primary_audit": primary_audit,
        "driver_race_matrix": driver_race_matrix,
        "threshold_summary": threshold_summary,
        "threshold_by_race": threshold_by_race,
        "threshold_by_driver": threshold_by_driver,
        "borderline_laps": borderline_laps,
        "driver_summary": driver_summary,
        "examples": examples,
        "cluster_profile": cluster_profile,
        "speed_profile_baselines": speed_profile_baselines,
        "shape_profile_examples": shape_profile_examples,
        "shape_cluster_exemplars": shape_cluster_exemplars,
        "shape_cluster_stability": shape_cluster_stability,
        "apexline_summary": apexline_summary,
        "apexline_examples": apexline_examples,
        "figure_paths": figure_paths,
        "table_paths": table_paths,
        "report_path": report_path,
        "metadata_path": metadata_path,
        "summary_path": summary_path,
        "skrub_version": skrub.__version__,
    }


if __name__ == "__main__":
    result = run_analysis()
    classified = result["classified"]
    bad = int(classified["bad_lap_any_category"].sum())
    total = len(classified)
    print(f"skrub {result['skrub_version']}")
    print(f"2025 race sessions: {result['sessions']['session_id'].nunique()}")
    print(f"laps: {total:,}; any-category bad: {bad:,} ({bad / total * 100:.1f}%)")
    print(f"summary: {result['summary_path']}")
    print(f"skrub report: {result['report_path']}")
