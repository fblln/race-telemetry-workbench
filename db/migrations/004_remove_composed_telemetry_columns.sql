DROP VIEW IF EXISTS telemetry_event_candidates;

ALTER TABLE telemetry_samples
    DROP COLUMN IF EXISTS distance_m,
    DROP COLUMN IF EXISTS relative_distance,
    DROP COLUMN IF EXISTS driver_ahead,
    DROP COLUMN IF EXISTS distance_to_driver_ahead_m,
    DROP COLUMN IF EXISTS track_status;

COMMENT ON TABLE telemetry_samples IS
'High-volume raw car telemetry samples from FastF1 session.car_data. Stores source car channels without derived distance, driver-ahead, or position-enriched fields.';

CREATE OR REPLACE VIEW telemetry_event_candidates AS
SELECT
    sample_time_utc,
    session_id,
    driver_code,
    lap_number,
    session_time_ms,
    lap_time_ms,
    speed_kmh,
    throttle_pct,
    brake_pct,
    drs,
    CASE
        WHEN brake_pct >= 80 THEN 'hard_braking'
        WHEN speed_kmh >= 300 THEN 'high_speed'
        WHEN drs IS NOT NULL AND drs > 0 THEN 'drs_active'
        WHEN throttle_pct <= 10 AND speed_kmh >= 150 THEN 'throttle_lift'
        ELSE NULL
    END AS event_type
FROM telemetry_samples
WHERE
    brake_pct >= 80
    OR speed_kmh >= 300
    OR (drs IS NOT NULL AND drs > 0)
    OR (throttle_pct <= 10 AND speed_kmh >= 150);

COMMENT ON VIEW telemetry_event_candidates IS
'Bounded event helper over telemetry_samples. Emits candidate hard braking, high speed, DRS active, and throttle lift events using simple thresholds intended for MCP and Query API filtering.';
