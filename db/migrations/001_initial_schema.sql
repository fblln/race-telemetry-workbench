CREATE TABLE IF NOT EXISTS sessions (
    session_id TEXT PRIMARY KEY,
    year INT NOT NULL,
    event_name TEXT NOT NULL,
    circuit_name TEXT NULL,
    country TEXT NULL,
    session_type TEXT NOT NULL,
    session_start_utc TIMESTAMPTZ NULL,
    session_end_utc TIMESTAMPTZ NULL,
    source TEXT NOT NULL,
    imported_at_utc TIMESTAMPTZ NOT NULL DEFAULT now(),
    metadata JSONB NOT NULL DEFAULT '{}'::jsonb,
    CONSTRAINT ck_sessions_session_type
        CHECK (session_type IN ('FP1', 'FP2', 'FP3', 'Q', 'SQ', 'S', 'R')),
    CONSTRAINT ck_sessions_year
        CHECK (year >= 1950)
);

CREATE TABLE IF NOT EXISTS session_drivers (
    session_id TEXT NOT NULL REFERENCES sessions(session_id) ON DELETE CASCADE,
    driver_code TEXT NOT NULL,
    driver_number INT NULL,
    full_name TEXT NULL,
    team_name TEXT NULL,
    metadata JSONB NOT NULL DEFAULT '{}'::jsonb,
    PRIMARY KEY (session_id, driver_code),
    CONSTRAINT ck_session_drivers_driver_code
        CHECK (driver_code = upper(driver_code) AND char_length(driver_code) BETWEEN 2 AND 4),
    CONSTRAINT ck_session_drivers_driver_number
        CHECK (driver_number IS NULL OR driver_number > 0)
);

CREATE TABLE IF NOT EXISTS laps (
    lap_id TEXT PRIMARY KEY,
    session_id TEXT NOT NULL REFERENCES sessions(session_id) ON DELETE CASCADE,
    driver_code TEXT NOT NULL,
    lap_number INT NOT NULL,
    stint_number INT NULL,
    lap_start_utc TIMESTAMPTZ NULL,
    lap_end_utc TIMESTAMPTZ NULL,
    lap_time_ms INT NULL,
    sector_1_ms INT NULL,
    sector_2_ms INT NULL,
    sector_3_ms INT NULL,
    compound TEXT NULL,
    tyre_life INT NULL,
    is_pit_out_lap BOOLEAN NOT NULL DEFAULT false,
    is_pit_in_lap BOOLEAN NOT NULL DEFAULT false,
    is_deleted BOOLEAN NOT NULL DEFAULT false,
    is_accurate BOOLEAN NULL,
    metadata JSONB NOT NULL DEFAULT '{}'::jsonb,
    UNIQUE (session_id, driver_code, lap_number),
    FOREIGN KEY (session_id, driver_code)
        REFERENCES session_drivers(session_id, driver_code)
        ON DELETE CASCADE,
    CONSTRAINT ck_laps_lap_number
        CHECK (lap_number > 0),
    CONSTRAINT ck_laps_stint_number
        CHECK (stint_number IS NULL OR stint_number > 0),
    CONSTRAINT ck_laps_non_negative_times
        CHECK (
            (lap_time_ms IS NULL OR lap_time_ms >= 0)
            AND (sector_1_ms IS NULL OR sector_1_ms >= 0)
            AND (sector_2_ms IS NULL OR sector_2_ms >= 0)
            AND (sector_3_ms IS NULL OR sector_3_ms >= 0)
        )
);

CREATE TABLE IF NOT EXISTS telemetry_samples (
    sample_time_utc TIMESTAMPTZ NOT NULL,
    session_id TEXT NOT NULL,
    driver_code TEXT NOT NULL,
    lap_number INT NULL,
    session_time_ms BIGINT NULL,
    lap_time_ms BIGINT NULL,
    distance_m DOUBLE PRECISION NULL,
    relative_distance DOUBLE PRECISION NULL,
    speed_kmh DOUBLE PRECISION NULL,
    throttle_pct DOUBLE PRECISION NULL,
    brake_pct DOUBLE PRECISION NULL,
    gear INT NULL,
    rpm DOUBLE PRECISION NULL,
    drs INT NULL,
    driver_ahead TEXT NULL,
    distance_to_driver_ahead_m DOUBLE PRECISION NULL,
    track_status TEXT NULL,
    sample_source TEXT NULL,
    source_sample_index BIGINT NULL,
    metadata JSONB NOT NULL DEFAULT '{}'::jsonb,
    PRIMARY KEY (sample_time_utc, session_id, driver_code),
    FOREIGN KEY (session_id, driver_code)
        REFERENCES session_drivers(session_id, driver_code)
        ON DELETE CASCADE,
    CONSTRAINT ck_telemetry_lap_number
        CHECK (lap_number IS NULL OR lap_number > 0),
    CONSTRAINT ck_telemetry_relative_distance
        CHECK (relative_distance IS NULL OR relative_distance BETWEEN 0 AND 1),
    CONSTRAINT ck_telemetry_percentages
        CHECK (
            (throttle_pct IS NULL OR throttle_pct BETWEEN 0 AND 100)
            AND (brake_pct IS NULL OR brake_pct BETWEEN 0 AND 100)
        )
);

CREATE TABLE IF NOT EXISTS position_samples (
    sample_time_utc TIMESTAMPTZ NOT NULL,
    session_id TEXT NOT NULL,
    driver_code TEXT NOT NULL,
    lap_number INT NULL,
    x DOUBLE PRECISION NULL,
    y DOUBLE PRECISION NULL,
    z DOUBLE PRECISION NULL,
    track_status TEXT NULL,
    sample_source TEXT NULL,
    source_sample_index BIGINT NULL,
    metadata JSONB NOT NULL DEFAULT '{}'::jsonb,
    PRIMARY KEY (sample_time_utc, session_id, driver_code),
    FOREIGN KEY (session_id, driver_code)
        REFERENCES session_drivers(session_id, driver_code)
        ON DELETE CASCADE,
    CONSTRAINT ck_position_lap_number
        CHECK (lap_number IS NULL OR lap_number > 0)
);

CREATE TABLE IF NOT EXISTS circuit_metadata (
    session_id TEXT PRIMARY KEY REFERENCES sessions(session_id) ON DELETE CASCADE,
    rotation_degrees DOUBLE PRECISION NULL,
    source TEXT NOT NULL,
    metadata JSONB NOT NULL DEFAULT '{}'::jsonb
);

CREATE TABLE IF NOT EXISTS circuit_markers (
    circuit_marker_id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    session_id TEXT NOT NULL REFERENCES sessions(session_id) ON DELETE CASCADE,
    marker_type TEXT NOT NULL,
    marker_number INT NULL,
    marker_letter TEXT NULL,
    x DOUBLE PRECISION NOT NULL,
    y DOUBLE PRECISION NOT NULL,
    angle_degrees DOUBLE PRECISION NULL,
    distance_m DOUBLE PRECISION NULL,
    metadata JSONB NOT NULL DEFAULT '{}'::jsonb,
    CONSTRAINT ck_circuit_markers_marker_type
        CHECK (marker_type IN ('corner', 'marshal_light', 'marshal_sector'))
);

CREATE TABLE IF NOT EXISTS weather_samples (
    session_id TEXT NOT NULL REFERENCES sessions(session_id) ON DELETE CASCADE,
    sample_time_utc TIMESTAMPTZ NOT NULL,
    session_time_ms BIGINT NOT NULL,
    air_temp_c DOUBLE PRECISION NULL,
    track_temp_c DOUBLE PRECISION NULL,
    humidity_pct DOUBLE PRECISION NULL,
    pressure_mbar DOUBLE PRECISION NULL,
    rainfall BOOLEAN NULL,
    wind_direction_deg INT NULL,
    wind_speed_mps DOUBLE PRECISION NULL,
    metadata JSONB NOT NULL DEFAULT '{}'::jsonb,
    PRIMARY KEY (sample_time_utc, session_id),
    CONSTRAINT ck_weather_session_time
        CHECK (session_time_ms >= 0),
    CONSTRAINT ck_weather_humidity
        CHECK (humidity_pct IS NULL OR humidity_pct BETWEEN 0 AND 100)
);

CREATE TABLE IF NOT EXISTS track_status_events (
    session_id TEXT NOT NULL REFERENCES sessions(session_id) ON DELETE CASCADE,
    event_time_ms BIGINT NOT NULL,
    status_code TEXT NOT NULL,
    message TEXT NULL,
    metadata JSONB NOT NULL DEFAULT '{}'::jsonb,
    PRIMARY KEY (session_id, event_time_ms, status_code),
    CONSTRAINT ck_track_status_events_time
        CHECK (event_time_ms >= 0),
    CONSTRAINT ck_track_status_events_status_code
        CHECK (status_code IN ('1', '2', '4', '5', '6', '7'))
);

CREATE TABLE IF NOT EXISTS session_status_events (
    session_id TEXT NOT NULL REFERENCES sessions(session_id) ON DELETE CASCADE,
    event_time_ms BIGINT NOT NULL,
    status TEXT NOT NULL,
    metadata JSONB NOT NULL DEFAULT '{}'::jsonb,
    PRIMARY KEY (session_id, event_time_ms, status),
    CONSTRAINT ck_session_status_events_time
        CHECK (event_time_ms >= 0)
);

CREATE TABLE IF NOT EXISTS race_control_messages (
    race_control_message_id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    session_id TEXT NOT NULL REFERENCES sessions(session_id) ON DELETE CASCADE,
    message_time_utc TIMESTAMPTZ NULL,
    session_time_ms BIGINT NULL,
    category TEXT NULL,
    message TEXT NOT NULL,
    status TEXT NULL,
    flag TEXT NULL,
    scope TEXT NULL,
    sector TEXT NULL,
    racing_number INT NULL,
    lap_number INT NULL,
    metadata JSONB NOT NULL DEFAULT '{}'::jsonb,
    CONSTRAINT ck_race_control_messages_session_time
        CHECK (session_time_ms IS NULL OR session_time_ms >= 0),
    CONSTRAINT ck_race_control_messages_lap_number
        CHECK (lap_number IS NULL OR lap_number > 0)
);
