"""Support code for the 2025 telemetry bad-lap EDA notebook.

The notebook is intentionally narrative-first. This module keeps the bounded
database queries, tunable quality rules, clustering, and artifact generation in
one reusable place so the analysis can be rerun from a clean kernel.
"""

from __future__ import annotations

import json
import math
import os
from dataclasses import dataclass
from pathlib import Path
from typing import Any


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
import numpy as np
import pandas as pd
import seaborn as sns
import skrub
from sklearn.cluster import KMeans
from sklearn.decomposition import PCA
from sklearn.impute import SimpleImputer
from sklearn.metrics import silhouette_score
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
    return result


def summarize_categories(classified: pd.DataFrame) -> pd.DataFrame:
    total_laps = len(classified)
    rows = []
    for category in CATEGORY_COLUMNS:
        count = int(classified[category].sum())
        rows.append(
            {
                "category": category,
                "laps": count,
                "pct_of_all_laps": count / total_laps * 100 if total_laps else 0.0,
            }
        )
    return pd.DataFrame(rows).sort_values(["laps", "category"], ascending=[False, True])


def summarize_primary(classified: pd.DataFrame) -> pd.DataFrame:
    total_laps = len(classified)
    summary = (
        classified.groupby("primary_category", dropna=False)
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
            "primary_category",
            "lap_time_ms",
            "car_samples",
            "position_samples",
            *score_cols,
        ]
        sample = subset.head(per_category)[keep_cols].copy()
        sample.insert(0, "category", category)
        rows.append(sample)
    if not rows:
        return pd.DataFrame()
    return pd.concat(rows, ignore_index=True)


def compute_shape_clusters(classified: pd.DataFrame, n_clusters: int = 7) -> tuple[pd.DataFrame, pd.DataFrame]:
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
    matrix = preprocessing.fit_transform(cluster_source)
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


def load_apexline_summary() -> tuple[pd.DataFrame, pd.DataFrame]:
    path = REPO_ROOT / "standalone" / "apexline" / "data" / "lap-diagnostics-2025.json"
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
    category_summary: pd.DataFrame,
    primary_summary: pd.DataFrame,
    race_summary: pd.DataFrame,
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
        "- `distance_reset_or_non_monotonic_distance`: unavailable in the imported schema because raw FastF1 `Distance` is not stored; this is kept explicit instead of inferred.",
        "- `implausible_channel_values`: speed, RPM, gear, throttle, or brake outside configured physical/source bounds.",
        "- `shape_mismatch_against_comparable_laps`: equal-lap-time speed profile is a robust outlier versus clean laps from the same race.",
        "- `position_trace_discontinuity`: position path length or segment jumps are inconsistent with clean same-race laps.",
        "- `pit_lane_or_safety_car_influenced`: pit-in/out, safety car, VSC, or red-flag context overlaps the lap.",
        "- `timing_session_boundary_artifact`: missing session-relative timing, raw sample ordering issues, lap-time reset, or large raw telemetry gaps.",
        "- `import_or_source_data_anomaly`: FastF1 inaccurate/deleted lap marker.",
        "- `unknown_needs_inspection`: speed-shape outlier with no stronger explanatory signal.",
        "",
        "Reason columns are not mutually exclusive. The `primary_category` table assigns each lap to the first applicable category in a deterministic priority order.",
        "",
        "## Category counts",
        "",
        category_summary.to_markdown(index=False, floatfmt=".2f"),
        "",
        "## Primary categories",
        "",
        primary_summary.to_markdown(index=False, floatfmt=".2f"),
        "",
        "## Most affected races",
        "",
        race_summary.head(10).to_markdown(index=False, floatfmt=".2f"),
        "",
        "## Most affected drivers",
        "",
        driver_summary.head(12).to_markdown(index=False, floatfmt=".2f"),
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

    category_summary = summarize_categories(clustered)
    primary_summary = summarize_primary(clustered)
    race_summary = summarize_by_race(clustered)
    driver_summary = summarize_by_driver(clustered)
    examples = representative_examples(clustered)
    apexline_summary, apexline_examples = load_apexline_summary()

    report_path = write_skrub_report(clustered)
    figure_paths = [
        plot_category_bar(category_summary),
        plot_race_comparison(race_summary),
        plot_missingness_heatmap(clustered),
        plot_shape_clusters(clustered),
        plot_representative_speed_traces(clustered),
    ]

    if write_outputs:
        save_table(clustered, "classified_laps_2025.csv")
        save_table(category_summary, "category_summary_2025.csv")
        save_table(primary_summary, "primary_category_summary_2025.csv")
        save_table(race_summary, "race_summary_2025.csv")
        save_table(driver_summary, "driver_summary_2025.csv")
        save_table(examples, "representative_examples_2025.csv")
        save_table(cluster_profile, "shape_cluster_profile_2025.csv")
        if not apexline_summary.empty:
            save_table(apexline_summary, "apexline_event_summary_2025.csv")
        if not apexline_examples.empty:
            save_table(apexline_examples, "apexline_shape_examples_2025.csv")

        summary = build_markdown_summary(
            clustered,
            category_summary,
            primary_summary,
            race_summary,
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
        "category_summary": category_summary,
        "primary_summary": primary_summary,
        "race_summary": race_summary,
        "driver_summary": driver_summary,
        "examples": examples,
        "cluster_profile": cluster_profile,
        "apexline_summary": apexline_summary,
        "apexline_examples": apexline_examples,
        "figure_paths": figure_paths,
        "report_path": report_path,
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
