ALTER TABLE aligned_telemetry_10hz
ADD COLUMN IF NOT EXISTS alignment_method TEXT NOT NULL DEFAULT 'time_grid_linear_ffill_v1';

CREATE TABLE IF NOT EXISTS lap_telemetry_by_distance (
    session_id TEXT NOT NULL REFERENCES sessions(session_id) ON DELETE CASCADE,
    session_key INTEGER NOT NULL,
    driver_number INTEGER NOT NULL,
    driver_code TEXT NULL,
    lap_number INTEGER NOT NULL,
    distance_m DOUBLE PRECISION NOT NULL,
    normalized_track_progress DOUBLE PRECISION NOT NULL,
    lap_elapsed_time_ms BIGINT NULL,
    session_time_ms BIGINT NULL,
    speed_kmh DOUBLE PRECISION NULL,
    throttle_pct DOUBLE PRECISION NULL,
    brake_pct DOUBLE PRECISION NULL,
    gear INTEGER NULL,
    rpm DOUBLE PRECISION NULL,
    drs INTEGER NULL,
    x DOUBLE PRECISION NULL,
    y DOUBLE PRECISION NULL,
    z DOUBLE PRECISION NULL,
    source_sample_before_time_utc TIMESTAMPTZ NULL,
    source_sample_after_time_utc TIMESTAMPTZ NULL,
    interpolated BOOLEAN NOT NULL DEFAULT TRUE,
    quality_flags TEXT[] NOT NULL DEFAULT ARRAY['OK'],
    alignment_version INTEGER NOT NULL DEFAULT 1,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),

    PRIMARY KEY (session_id, driver_number, lap_number, distance_m),
    CONSTRAINT ck_lap_telemetry_by_distance_lap_number
        CHECK (lap_number > 0),
    CONSTRAINT ck_lap_telemetry_by_distance_distance
        CHECK (distance_m >= 0),
    CONSTRAINT ck_lap_telemetry_by_distance_progress
        CHECK (normalized_track_progress >= 0 AND normalized_track_progress <= 1),
    CONSTRAINT ck_lap_telemetry_by_distance_quality_flags
        CHECK (array_length(quality_flags, 1) IS NOT NULL)
);

CREATE INDEX IF NOT EXISTS ix_lap_telemetry_by_distance_session_driver_lap
ON lap_telemetry_by_distance (session_id, driver_number, lap_number);

CREATE INDEX IF NOT EXISTS ix_lap_telemetry_by_distance_session_lap_distance
ON lap_telemetry_by_distance (session_id, lap_number, distance_m);

CREATE TABLE IF NOT EXISTS lap_telemetry_quality (
    session_id TEXT NOT NULL REFERENCES sessions(session_id) ON DELETE CASCADE,
    driver_number INTEGER NOT NULL,
    lap_number INTEGER NOT NULL,
    official_lap_duration_ms BIGINT NULL,
    telemetry_covered_duration_ms BIGINT NULL,
    first_sample_offset_ms BIGINT NULL,
    last_sample_offset_ms BIGINT NULL,
    maximum_car_data_gap_ms BIGINT NULL,
    maximum_position_gap_ms BIGINT NULL,
    final_integrated_distance_m DOUBLE PRECISION NULL,
    interpolated_car_data_percentage DOUBLE PRECISION NULL,
    interpolated_position_percentage DOUBLE PRECISION NULL,
    stale_sample_percentage DOUBLE PRECISION NULL,
    distance_delta_validation_ms BIGINT NULL,
    quality_status TEXT NOT NULL,
    quality_messages TEXT[] NOT NULL DEFAULT ARRAY[]::TEXT[],
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),

    PRIMARY KEY (session_id, driver_number, lap_number),
    CONSTRAINT ck_lap_telemetry_quality_lap_number
        CHECK (lap_number > 0),
    CONSTRAINT ck_lap_telemetry_quality_percentages
        CHECK (
            (interpolated_car_data_percentage IS NULL OR interpolated_car_data_percentage BETWEEN 0 AND 100)
            AND (interpolated_position_percentage IS NULL OR interpolated_position_percentage BETWEEN 0 AND 100)
            AND (stale_sample_percentage IS NULL OR stale_sample_percentage BETWEEN 0 AND 100)
        )
);

CREATE INDEX IF NOT EXISTS ix_lap_telemetry_quality_session_status
ON lap_telemetry_quality (session_id, quality_status);

COMMENT ON COLUMN aligned_telemetry_10hz.alignment_method IS
'Named replay-alignment strategy used to build the derived time-domain sample.';

COMMENT ON TABLE lap_telemetry_by_distance IS
'Distance-domain lap projection used for where-performance-was-gained analysis. distance_m is derived analytical lap distance, not surveyed circuit distance.';

COMMENT ON TABLE lap_telemetry_quality IS
'Objective per-lap distance-alignment quality metrics and validation results.';
