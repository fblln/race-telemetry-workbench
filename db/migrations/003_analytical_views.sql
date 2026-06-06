CREATE OR REPLACE VIEW lap_summaries AS
SELECT
    l.session_id,
    l.driver_code,
    l.lap_number,
    l.stint_number,
    l.lap_start_utc,
    l.lap_end_utc,
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
    max(t.speed_kmh) AS max_speed_kmh,
    avg(t.speed_kmh) AS avg_speed_kmh,
    avg(t.throttle_pct) AS avg_throttle_pct,
    avg(t.brake_pct) AS avg_brake_pct,
    count(t.*) AS telemetry_samples
FROM laps l
LEFT JOIN telemetry_samples t
    ON t.session_id = l.session_id
    AND t.driver_code = l.driver_code
    AND t.lap_number = l.lap_number
GROUP BY
    l.session_id,
    l.driver_code,
    l.lap_number,
    l.stint_number,
    l.lap_start_utc,
    l.lap_end_utc,
    l.lap_time_ms,
    l.sector_1_ms,
    l.sector_2_ms,
    l.sector_3_ms,
    l.compound,
    l.tyre_life,
    l.is_pit_out_lap,
    l.is_pit_in_lap,
    l.is_deleted,
    l.is_accurate;

CREATE OR REPLACE VIEW driver_stint_summaries AS
SELECT
    session_id,
    driver_code,
    stint_number,
    compound,
    min(lap_number) AS first_lap_number,
    max(lap_number) AS last_lap_number,
    count(*) AS laps,
    min(tyre_life) AS min_tyre_life,
    max(tyre_life) AS max_tyre_life,
    avg(lap_time_ms) AS avg_lap_time_ms,
    min(lap_time_ms) AS best_lap_time_ms,
    max(lap_time_ms) AS worst_lap_time_ms
FROM laps
WHERE stint_number IS NOT NULL
GROUP BY session_id, driver_code, stint_number, compound;

CREATE OR REPLACE VIEW session_weather_summary AS
SELECT
    session_id,
    min(air_temp_c) AS min_air_temp_c,
    max(air_temp_c) AS max_air_temp_c,
    avg(air_temp_c) AS avg_air_temp_c,
    min(track_temp_c) AS min_track_temp_c,
    max(track_temp_c) AS max_track_temp_c,
    avg(track_temp_c) AS avg_track_temp_c,
    min(humidity_pct) AS min_humidity_pct,
    max(humidity_pct) AS max_humidity_pct,
    avg(humidity_pct) AS avg_humidity_pct,
    min(pressure_mbar) AS min_pressure_mbar,
    max(pressure_mbar) AS max_pressure_mbar,
    avg(pressure_mbar) AS avg_pressure_mbar,
    avg(wind_speed_mps) AS avg_wind_speed_mps,
    bool_or(coalesce(rainfall, false)) AS rainfall_observed
FROM weather_samples
GROUP BY session_id;

CREATE OR REPLACE VIEW track_status_periods AS
SELECT
    session_id,
    event_time_ms AS start_time_ms,
    lead(event_time_ms) OVER (
        PARTITION BY session_id
        ORDER BY event_time_ms
    ) AS end_time_ms,
    status_code,
    CASE status_code
        WHEN '1' THEN 'track_clear'
        WHEN '2' THEN 'yellow_flag'
        WHEN '4' THEN 'safety_car'
        WHEN '5' THEN 'red_flag'
        WHEN '6' THEN 'virtual_safety_car_deployed'
        WHEN '7' THEN 'virtual_safety_car_ending'
        ELSE 'unknown'
    END AS status_name,
    message,
    metadata
FROM track_status_events;

CREATE OR REPLACE VIEW race_control_event_index AS
SELECT
    race_control_message_id,
    session_id,
    message_time_utc,
    session_time_ms,
    category,
    flag,
    status,
    scope,
    sector,
    racing_number,
    lap_number,
    message,
    lower(concat_ws(' ', category, flag, status, scope, sector, message)) AS search_text,
    metadata
FROM race_control_messages;

CREATE OR REPLACE VIEW telemetry_event_candidates AS
SELECT
    sample_time_utc,
    session_id,
    driver_code,
    lap_number,
    session_time_ms,
    lap_time_ms,
    distance_m,
    speed_kmh,
    throttle_pct,
    brake_pct,
    drs,
    track_status,
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
