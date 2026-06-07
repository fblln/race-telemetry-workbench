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
    speed_kmh DOUBLE PRECISION NULL,
    throttle_pct DOUBLE PRECISION NULL,
    brake_pct DOUBLE PRECISION NULL,
    gear INT NULL,
    rpm DOUBLE PRECISION NULL,
    drs INT NULL,
    sample_source TEXT NULL,
    source_sample_index BIGINT NULL,
    metadata JSONB NOT NULL DEFAULT '{}'::jsonb,
    PRIMARY KEY (sample_time_utc, session_id, driver_code),
    FOREIGN KEY (session_id, driver_code)
        REFERENCES session_drivers(session_id, driver_code)
        ON DELETE CASCADE,
    CONSTRAINT ck_telemetry_lap_number
        CHECK (lap_number IS NULL OR lap_number > 0),
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

COMMENT ON TABLE sessions IS
'One row per imported FastF1 session. Race sessions are the default product scope, while other session types are explicit opt-ins.';

COMMENT ON COLUMN sessions.session_id IS
'Stable project identifier, for example 2024-italian-grand-prix-r.';
COMMENT ON COLUMN sessions.session_type IS
'FastF1 session code: FP1, FP2, FP3, Q, SQ, S, or R.';
COMMENT ON COLUMN sessions.metadata IS
'Importer-owned JSON for source values that are useful to retain but not query-critical.';

COMMENT ON TABLE session_drivers IS
'Drivers available in one imported session, normalized to driver abbreviations such as VER, HAM, or LEC.';

COMMENT ON COLUMN session_drivers.driver_number IS
'FastF1 may identify drivers by racing number; this keeps the source number next to the normalized driver code.';

COMMENT ON TABLE laps IS
'Lap-level timing, tyre, stint, pit, and quality metadata for one driver in one session.';

COMMENT ON COLUMN laps.is_accurate IS
'FastF1 lap accuracy flag where available. NULL means the source did not provide an accuracy value.';
COMMENT ON COLUMN laps.stint_number IS
'Driver stint number where available from FastF1. Used by driver_stint_summaries.';

COMMENT ON TABLE telemetry_samples IS
'High-volume raw car telemetry samples from FastF1 lap.get_car_data(). Stores source car channels without derived distance, driver-ahead, or position-enriched fields.';

COMMENT ON COLUMN telemetry_samples.sample_time_utc IS
'Absolute sample timestamp. This is the Timescale hypertable time dimension.';
COMMENT ON COLUMN telemetry_samples.session_time_ms IS
'Milliseconds from FastF1 session start. Use for replay window queries and race timeline alignment.';
COMMENT ON COLUMN telemetry_samples.lap_time_ms IS
'Milliseconds from lap start. Use for per-lap charting and comparison.';
COMMENT ON COLUMN telemetry_samples.brake_pct IS
'Brake value normalized to 0-100. Boolean FastF1 brake values should be converted to 0 or 100 by the importer.';
COMMENT ON COLUMN telemetry_samples.sample_source IS
'FastF1 car-data source marker, usually car.';

COMMENT ON TABLE position_samples IS
'High-volume raw position samples from FastF1 lap.get_pos_data(). Used for replay positions and deriving the track outline.';

COMMENT ON COLUMN position_samples.sample_time_utc IS
'Absolute sample timestamp. This is the Timescale hypertable time dimension.';
COMMENT ON COLUMN position_samples.x IS
'FastF1 track-map x coordinate in source units.';
COMMENT ON COLUMN position_samples.y IS
'FastF1 track-map y coordinate in source units.';
COMMENT ON COLUMN position_samples.z IS
'FastF1 track-map z coordinate in source units.';

COMMENT ON TABLE circuit_metadata IS
'Session-level circuit metadata from FastF1 session.get_circuit_info(), such as map rotation.';

COMMENT ON TABLE circuit_markers IS
'Circuit annotations from FastF1 session.get_circuit_info(): corners, marshal lights, and marshal sectors. These annotate the data-derived track outline.';

COMMENT ON COLUMN circuit_markers.marker_type IS
'Marker category: corner, marshal_light, or marshal_sector.';
COMMENT ON COLUMN circuit_markers.distance_m IS
'Approximate marker distance along the lap where FastF1 provides it.';

COMMENT ON TABLE weather_samples IS
'Low-frequency FastF1 weather samples, usually around one row per minute. Useful for context, overlays, and MCP summary answers.';

COMMENT ON COLUMN weather_samples.sample_time_utc IS
'Absolute sample timestamp. This is the Timescale hypertable time dimension.';
COMMENT ON COLUMN weather_samples.session_time_ms IS
'Milliseconds from FastF1 session start, derived from the FastF1 weather Time value.';
COMMENT ON COLUMN weather_samples.rainfall IS
'Whether rain was reported at the sample timestamp.';

COMMENT ON TABLE track_status_events IS
'Compact race-control status timeline from FastF1 track_status. Codes include clear, yellow, safety car, red flag, and virtual safety car states.';

COMMENT ON COLUMN track_status_events.status_code IS
'FastF1 status code: 1 clear, 2 yellow, 4 safety car, 5 red flag, 6 VSC deployed, 7 VSC ending.';

COMMENT ON TABLE session_status_events IS
'Session lifecycle timeline from FastF1 session_status, such as started, suspended, resumed, or finished.';

COMMENT ON TABLE race_control_messages IS
'Verbose race-control messages such as DRS, investigations, flags, pit-exit messages, sector messages, and driver-specific notices.';
