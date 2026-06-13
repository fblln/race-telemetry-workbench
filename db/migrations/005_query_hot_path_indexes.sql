CREATE INDEX IF NOT EXISTS ix_telemetry_session_session_time_driver
ON telemetry_samples (session_id, session_time_ms, driver_code)
WHERE session_time_ms IS NOT NULL;

CREATE INDEX IF NOT EXISTS ix_telemetry_session_driver_lap_sample_index
ON telemetry_samples (session_id, driver_code, lap_number, source_sample_index)
WHERE source_sample_index IS NOT NULL;

CREATE INDEX IF NOT EXISTS ix_position_session_driver_sample_time_cover
ON position_samples (session_id, driver_code, sample_time_utc)
INCLUDE (x, y, z);

CREATE INDEX IF NOT EXISTS ix_telemetry_event_hard_braking
ON telemetry_samples (session_id, session_time_ms, driver_code)
WHERE brake_pct >= 80 AND session_time_ms IS NOT NULL;

CREATE INDEX IF NOT EXISTS ix_telemetry_event_high_speed
ON telemetry_samples (session_id, session_time_ms, driver_code)
WHERE speed_kmh >= 300 AND session_time_ms IS NOT NULL;

CREATE INDEX IF NOT EXISTS ix_telemetry_event_drs_active
ON telemetry_samples (session_id, session_time_ms, driver_code)
WHERE drs IS NOT NULL AND drs > 0 AND session_time_ms IS NOT NULL;

CREATE INDEX IF NOT EXISTS ix_telemetry_event_throttle_lift
ON telemetry_samples (session_id, session_time_ms, driver_code)
WHERE throttle_pct <= 10 AND speed_kmh >= 150 AND session_time_ms IS NOT NULL;
