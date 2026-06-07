CREATE EXTENSION IF NOT EXISTS timescaledb;

SELECT create_hypertable('telemetry_samples', 'sample_time_utc', if_not_exists => TRUE);
SELECT create_hypertable('position_samples', 'sample_time_utc', if_not_exists => TRUE);
SELECT create_hypertable('weather_samples', 'sample_time_utc', if_not_exists => TRUE);

CREATE INDEX IF NOT EXISTS ix_sessions_year_event_session
ON sessions (year, event_name, session_type);

CREATE INDEX IF NOT EXISTS ix_session_drivers_session_team
ON session_drivers (session_id, team_name);

CREATE INDEX IF NOT EXISTS ix_laps_session_driver_lap
ON laps (session_id, driver_code, lap_number);

CREATE INDEX IF NOT EXISTS ix_laps_session_driver_stint
ON laps (session_id, driver_code, stint_number)
WHERE stint_number IS NOT NULL;

CREATE INDEX IF NOT EXISTS ix_telemetry_session_driver_lap_time
ON telemetry_samples (session_id, driver_code, lap_number, lap_time_ms);

CREATE INDEX IF NOT EXISTS ix_telemetry_session_time
ON telemetry_samples (session_id, sample_time_utc);

CREATE INDEX IF NOT EXISTS ix_telemetry_session_driver_session_time
ON telemetry_samples (session_id, driver_code, session_time_ms);

CREATE INDEX IF NOT EXISTS ix_position_session_driver_lap
ON position_samples (session_id, driver_code, lap_number);

CREATE INDEX IF NOT EXISTS ix_position_session_driver_time
ON position_samples (session_id, driver_code, sample_time_utc);

CREATE INDEX IF NOT EXISTS ix_circuit_markers_session_type
ON circuit_markers (session_id, marker_type);

CREATE INDEX IF NOT EXISTS ix_weather_samples_session_time
ON weather_samples (session_id, session_time_ms);

CREATE INDEX IF NOT EXISTS ix_track_status_events_session_time
ON track_status_events (session_id, event_time_ms);

CREATE INDEX IF NOT EXISTS ix_session_status_events_session_time
ON session_status_events (session_id, event_time_ms);

CREATE INDEX IF NOT EXISTS ix_race_control_messages_session_time
ON race_control_messages (session_id, session_time_ms);

CREATE INDEX IF NOT EXISTS ix_race_control_messages_session_lap
ON race_control_messages (session_id, lap_number);

CREATE INDEX IF NOT EXISTS ix_race_control_messages_session_category
ON race_control_messages (session_id, category);
