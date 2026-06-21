-- Starting grid position per driver (FastF1 results.GridPosition). NULL when unknown;
-- 0 represents a pit-lane start. Enables true grid->finish position-change deltas instead
-- of the order-after-lap-1 proxy.
ALTER TABLE session_drivers
    ADD COLUMN IF NOT EXISTS grid_position INT NULL;

ALTER TABLE session_drivers
    DROP CONSTRAINT IF EXISTS ck_session_drivers_grid_position;
ALTER TABLE session_drivers
    ADD CONSTRAINT ck_session_drivers_grid_position
        CHECK (grid_position IS NULL OR grid_position >= 0);
