CREATE TABLE IF NOT EXISTS aligned_telemetry_10hz (
    sample_time_utc TIMESTAMPTZ NOT NULL,
    session_id TEXT NULL REFERENCES sessions(session_id) ON DELETE CASCADE,
    session_key INTEGER NOT NULL,
    driver_number INTEGER NOT NULL,
    driver_code TEXT NULL,
    lap_number INTEGER NULL,

    sample_index INTEGER NOT NULL,
    session_time_ms BIGINT NULL,
    lap_time_ms BIGINT NULL,

    speed DOUBLE PRECISION NULL,
    rpm DOUBLE PRECISION NULL,
    n_gear INTEGER NULL,
    throttle DOUBLE PRECISION NULL,
    brake DOUBLE PRECISION NULL,
    drs INTEGER NULL,

    x DOUBLE PRECISION NULL,
    y DOUBLE PRECISION NULL,
    z DOUBLE PRECISION NULL,
    location_status TEXT NULL,

    source_car_time TIMESTAMPTZ NULL,
    source_location_time TIMESTAMPTZ NULL,
    car_sample_age_ms INTEGER NULL,
    location_sample_age_ms INTEGER NULL,

    is_interpolated_car BOOLEAN NOT NULL DEFAULT TRUE,
    is_interpolated_location BOOLEAN NOT NULL DEFAULT TRUE,
    quality_flags TEXT[] NOT NULL DEFAULT ARRAY['OK'],
    alignment_version INTEGER NOT NULL DEFAULT 1,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),

    PRIMARY KEY (sample_time_utc, session_key, driver_number),
    CONSTRAINT ck_aligned_lap_number
        CHECK (lap_number IS NULL OR lap_number > 0),
    CONSTRAINT ck_aligned_percentages
        CHECK (
            (throttle IS NULL OR throttle BETWEEN 0 AND 100)
            AND (brake IS NULL OR brake BETWEEN 0 AND 100)
        ),
    CONSTRAINT ck_aligned_quality_flags
        CHECK (array_length(quality_flags, 1) IS NOT NULL)
);

SELECT create_hypertable('aligned_telemetry_10hz', 'sample_time_utc', if_not_exists => TRUE);

CREATE INDEX IF NOT EXISTS ix_aligned_telemetry_session_driver_time
ON aligned_telemetry_10hz (session_id, driver_code, session_time_ms);

CREATE INDEX IF NOT EXISTS ix_aligned_telemetry_driver_lap
ON aligned_telemetry_10hz (session_key, driver_number, lap_number, lap_time_ms);

CREATE INDEX IF NOT EXISTS ix_aligned_telemetry_session_lap
ON aligned_telemetry_10hz (session_key, lap_number, driver_number, lap_time_ms);

CREATE TABLE IF NOT EXISTS telemetry_ingestion_diagnostics (
    id BIGSERIAL PRIMARY KEY,
    session_id TEXT NULL REFERENCES sessions(session_id) ON DELETE CASCADE,
    session_key INTEGER NOT NULL,
    driver_number INTEGER NOT NULL,
    driver_code TEXT NULL,
    stream_name TEXT NOT NULL,

    sample_count INTEGER NOT NULL,
    start_time TIMESTAMPTZ NULL,
    end_time TIMESTAMPTZ NULL,

    min_delta_ms DOUBLE PRECISION NULL,
    median_delta_ms DOUBLE PRECISION NULL,
    p90_delta_ms DOUBLE PRECISION NULL,
    p99_delta_ms DOUBLE PRECISION NULL,
    max_delta_ms DOUBLE PRECISION NULL,

    estimated_frequency_hz DOUBLE PRECISION NULL,
    duplicate_count INTEGER NOT NULL DEFAULT 0,
    out_of_order_count INTEGER NOT NULL DEFAULT 0,

    warning_flags TEXT[] NOT NULL DEFAULT ARRAY[]::TEXT[],
    created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS ix_telemetry_ingestion_diagnostics_session
ON telemetry_ingestion_diagnostics (session_id, driver_code, stream_name);

COMMENT ON TABLE aligned_telemetry_10hz IS
'10Hz ingestion-time materialization that aligns raw car telemetry and position samples for UI replay.';

COMMENT ON COLUMN aligned_telemetry_10hz.quality_flags IS
'Alignment quality flags. OK means no detected issue; degraded rows omit OK and name the condition.';

COMMENT ON TABLE telemetry_ingestion_diagnostics IS
'Per-import source frequency and quality diagnostics for raw telemetry streams used by UI alignment.';
