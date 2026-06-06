import re
import unittest
from pathlib import Path


MIGRATION_DIR = Path(__file__).resolve().parents[1] / "db" / "migrations"


def read_migration(name: str) -> str:
    return (MIGRATION_DIR / name).read_text(encoding="utf-8")


def normalized_sql(sql: str) -> str:
    return re.sub(r"\s+", " ", sql.lower())


class DatabaseMigrationContractTests(unittest.TestCase):
    def test_initial_schema_creates_required_tables(self):
        sql = normalized_sql(read_migration("001_initial_schema.sql"))

        for table_name in [
            "sessions",
            "session_drivers",
            "laps",
            "telemetry_samples",
            "position_samples",
            "circuit_metadata",
            "circuit_markers",
            "weather_samples",
            "track_status_events",
            "session_status_events",
            "race_control_messages",
        ]:
            self.assertIn(f"create table if not exists {table_name}", sql)

    def test_timescale_migration_creates_required_hypertables(self):
        sql = normalized_sql(read_migration("002_timescale_hypertables.sql"))

        self.assertIn("create extension if not exists timescaledb", sql)
        for table_name in ["telemetry_samples", "position_samples", "weather_samples"]:
            self.assertIn(f"create_hypertable('{table_name}', 'sample_time_utc'", sql)

    def test_analytical_views_cover_mcp_summary_contract(self):
        sql = normalized_sql(read_migration("003_analytical_views.sql"))

        for view_name in [
            "lap_summaries",
            "driver_stint_summaries",
            "session_weather_summary",
            "track_status_periods",
            "race_control_event_index",
            "telemetry_event_candidates",
        ]:
            self.assertIn(f"create or replace view {view_name}", sql)

    def test_weather_samples_store_absolute_and_session_time(self):
        sql = normalized_sql(read_migration("001_initial_schema.sql"))

        self.assertIn("sample_time_utc timestamptz not null", sql)
        self.assertIn("session_time_ms bigint not null", sql)
        self.assertIn("primary key (sample_time_utc, session_id)", sql)


if __name__ == "__main__":
    unittest.main()
