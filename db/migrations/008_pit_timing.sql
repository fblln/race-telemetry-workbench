ALTER TABLE laps
    ADD COLUMN IF NOT EXISTS pit_in_session_time_ms BIGINT NULL,
    ADD COLUMN IF NOT EXISTS pit_out_session_time_ms BIGINT NULL;

ALTER TABLE laps
    DROP CONSTRAINT IF EXISTS ck_laps_non_negative_pit_times;

ALTER TABLE laps
    ADD CONSTRAINT ck_laps_non_negative_pit_times
    CHECK (
        (pit_in_session_time_ms IS NULL OR pit_in_session_time_ms >= 0)
        AND (pit_out_session_time_ms IS NULL OR pit_out_session_time_ms >= 0)
    );

CREATE INDEX IF NOT EXISTS ix_laps_session_pit_timing
ON laps (session_id, driver_code, lap_number)
WHERE pit_in_session_time_ms IS NOT NULL OR pit_out_session_time_ms IS NOT NULL;

COMMENT ON COLUMN laps.pit_in_session_time_ms IS
'FastF1 PitInTime expressed as session-relative milliseconds. Null when the source does not identify a pit entry.';

COMMENT ON COLUMN laps.pit_out_session_time_ms IS
'FastF1 PitOutTime expressed as session-relative milliseconds. Null when the source does not identify a pit exit.';
