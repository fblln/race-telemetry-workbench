ALTER TABLE race_control_messages
    ADD COLUMN IF NOT EXISTS text_cluster INT NULL,
    ADD COLUMN IF NOT EXISTS cluster_terms TEXT NULL;

COMMENT ON COLUMN race_control_messages.text_cluster IS
'Cluster id assigned by scripts/cluster_race_control.py (TF-IDF + KMeans over all messages). Null until clustered.';

COMMENT ON COLUMN race_control_messages.cluster_terms IS
'Top terms describing the message cluster, for grouping/labelling race-control families. Null until clustered.';

-- Re-project the cluster columns through the search view used by the query API.
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
    metadata,
    text_cluster,
    cluster_terms
FROM race_control_messages;
