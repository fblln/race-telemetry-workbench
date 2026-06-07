"""Database integration tests for the TimescaleDB schema.

These tests intentionally run against a real PostgreSQL/TimescaleDB database
instead of parsing SQL files as text. The setup mirrors the product's first
database slice:

1. Connect to the local Docker Compose database, or to RACE_TELEMETRY_DATABASE_URL.
2. Create a unique temporary schema so the test run is isolated.
3. Apply the real migration files in order.
4. Insert a tiny Monza 2024 race-shaped fixture.
5. Verify that the real tables, hypertables, comments, and analytical views
   behave as the Query API and MCP server will expect.

The fixture is deliberately small but semantically rich: two drivers, multiple
laps, telemetry samples that trigger event candidates, weather with rainfall,
track-status periods including a safety car, and searchable race-control
messages.
"""

import os
import unittest
from pathlib import Path
from uuid import uuid4


MIGRATION_DIR = Path(__file__).resolve().parents[1] / "db" / "migrations"
REPO_ROOT = Path(__file__).resolve().parents[1]
DEFAULT_DATABASE_URL = "postgresql://race_telemetry:race_telemetry@localhost:5432/race_telemetry"
FIXTURE_SESSION_ID = "2024-italian-grand-prix-r"
FIXTURE_SCHEMA_PREFIX = "test_schema_"
EXPECTED_HYPERTABLES = {"telemetry_samples", "position_samples", "weather_samples"}
EXPECTED_BASE_TABLES = {
    "sessions",
    "session_drivers",
    "laps",
    "telemetry_samples",
    "position_samples",
    "weather_samples",
    "track_status_events",
    "session_status_events",
    "race_control_messages",
}
EXPECTED_TRACK_STATUS_PERIODS = [
    (0, 210000, "track_clear"),
    (210000, 260000, "safety_car"),
    (260000, None, "track_clear"),
]
EXPECTED_LEC_EVENT_CANDIDATES = [
    (1, 190000, "high_speed"),
    (1, 200000, "hard_braking"),
    (2, 275000, "high_speed"),
]
EXPECTED_TELEMETRY_COLUMNS = {
    "sample_time_utc",
    "session_id",
    "driver_code",
    "lap_number",
    "session_time_ms",
    "lap_time_ms",
    "speed_kmh",
    "throttle_pct",
    "brake_pct",
    "gear",
    "rpm",
    "drs",
    "sample_source",
    "source_sample_index",
    "metadata",
}
REMOVED_COMPOSED_TELEMETRY_COLUMNS = {
    "distance_m",
    "relative_distance",
    "driver_ahead",
    "distance_to_driver_ahead_m",
    "track_status",
}


def database_url() -> str:
    return os.environ.get("RACE_TELEMETRY_DATABASE_URL", DEFAULT_DATABASE_URL)


def read_migration(name: str) -> str:
    return (MIGRATION_DIR / name).read_text(encoding="utf-8")


def require_psycopg():
    try:
        import psycopg
        from psycopg import sql
    except ModuleNotFoundError as exc:
        raise unittest.SkipTest(
            "psycopg is required for database integration tests. "
            "Run `.venv/bin/python -m pip install -r scripts/requirements.txt`."
        ) from exc
    return psycopg, sql


class DatabaseIntegrationTests(unittest.TestCase):
    """Apply migrations to a real database and verify race-shaped data."""

    @classmethod
    def setUpClass(cls):
        cls.psycopg, cls.sql = require_psycopg()
        cls.schema_name = f"{FIXTURE_SCHEMA_PREFIX}{uuid4().hex}"

        try:
            cls.connection = cls.psycopg.connect(database_url(), autocommit=True)
        except cls.psycopg.OperationalError as exc:
            raise unittest.SkipTest(
                "Database integration tests require a running TimescaleDB instance. "
                "Start it with `docker compose up -d timescaledb` or set "
                "`RACE_TELEMETRY_DATABASE_URL`."
            ) from exc

        cls.apply_migrations()
        cls.seed_monza_2024_fixture()

    @classmethod
    def tearDownClass(cls):
        connection = getattr(cls, "connection", None)
        schema_name = getattr(cls, "schema_name", None)
        if connection is None or schema_name is None:
            return

        with connection.cursor() as cursor:
            cursor.execute(
                cls.sql.SQL("DROP SCHEMA IF EXISTS {} CASCADE").format(
                    cls.sql.Identifier(schema_name)
                )
            )
        connection.close()

    @classmethod
    def execute(cls, query, parameters=None):
        with cls.connection.cursor() as cursor:
            cursor.execute(query, parameters)
            if cursor.description is None:
                return None
            return cursor.fetchall()

    @classmethod
    def apply_migrations(cls):
        """Create an isolated schema and apply the production migrations."""

        with cls.connection.cursor() as cursor:
            cursor.execute(
                cls.sql.SQL("CREATE SCHEMA {}").format(
                    cls.sql.Identifier(cls.schema_name)
                )
            )
            cursor.execute(
                cls.sql.SQL("SET search_path TO {}, public").format(
                    cls.sql.Identifier(cls.schema_name)
                )
            )

            for migration_path in sorted(MIGRATION_DIR.glob("*.sql")):
                cursor.execute(migration_path.read_text(encoding="utf-8"))

    @classmethod
    def seed_monza_2024_fixture(cls):
        """Insert the compact race fixture used by every view assertion."""

        cls.seed_session()
        cls.seed_drivers()
        cls.seed_laps()
        cls.seed_telemetry_samples()
        cls.seed_position_samples()
        cls.seed_circuit_info()
        cls.seed_weather_samples()
        cls.seed_status_events()
        cls.seed_race_control_messages()

    @classmethod
    def seed_session(cls):
        cls.execute(
            """
            INSERT INTO sessions (
                session_id,
                year,
                event_name,
                circuit_name,
                country,
                session_type,
                session_start_utc,
                session_end_utc,
                source,
                metadata
            )
            VALUES
                (
                    %s,
                    2024,
                    'Italian Grand Prix',
                    'Monza',
                    'Italy',
                    'R',
                    '2024-09-01T13:00:00Z',
                    '2024-09-01T15:00:00Z',
                    'fastf1',
                    '{"fixture": "monza-2024"}'
                )
            """,
            (FIXTURE_SESSION_ID,),
        )

    @classmethod
    def seed_drivers(cls):
        cls.execute(
            """
            INSERT INTO session_drivers (
                session_id,
                driver_code,
                driver_number,
                full_name,
                team_name
            )
            VALUES
                (%s, 'LEC', 16, 'Charles Leclerc', 'Ferrari'),
                (%s, 'HAM', 44, 'Lewis Hamilton', 'Mercedes')
            """,
            (FIXTURE_SESSION_ID, FIXTURE_SESSION_ID),
        )

    @classmethod
    def seed_laps(cls):
        cls.execute(
            """
            INSERT INTO laps (
                lap_id,
                session_id,
                driver_code,
                lap_number,
                stint_number,
                lap_start_utc,
                lap_end_utc,
                lap_time_ms,
                sector_1_ms,
                sector_2_ms,
                sector_3_ms,
                compound,
                tyre_life,
                is_accurate
            )
            VALUES
                (
                    '2024-italian-grand-prix-r-lec-1',
                    %s,
                    'LEC',
                    1,
                    1,
                    '2024-09-01T13:03:00Z',
                    '2024-09-01T13:04:22Z',
                    82000,
                    28000,
                    27000,
                    27000,
                    'MEDIUM',
                    1,
                    true
                ),
                (
                    '2024-italian-grand-prix-r-lec-2',
                    %s,
                    'LEC',
                    2,
                    1,
                    '2024-09-01T13:04:22Z',
                    '2024-09-01T13:05:43Z',
                    81000,
                    27500,
                    26800,
                    26700,
                    'MEDIUM',
                    2,
                    true
                ),
                (
                    '2024-italian-grand-prix-r-ham-1',
                    %s,
                    'HAM',
                    1,
                    1,
                    '2024-09-01T13:03:02Z',
                    '2024-09-01T13:04:25Z',
                    83000,
                    28200,
                    27400,
                    27400,
                    'HARD',
                    1,
                    true
                )
            """,
            (FIXTURE_SESSION_ID, FIXTURE_SESSION_ID, FIXTURE_SESSION_ID),
        )

    @classmethod
    def seed_telemetry_samples(cls):
        cls.execute(
            """
            INSERT INTO telemetry_samples (
                sample_time_utc,
                session_id,
                driver_code,
                lap_number,
                session_time_ms,
                lap_time_ms,
                speed_kmh,
                throttle_pct,
                brake_pct,
                gear,
                rpm,
                drs,
                sample_source
            )
            VALUES
                (
                    '2024-09-01T13:03:10Z',
                    %s,
                    'LEC',
                    1,
                    190000,
                    10000,
                    312.0,
                    100.0,
                    0.0,
                    8,
                    11750.0,
                    10,
                    'car'
                ),
                (
                    '2024-09-01T13:03:20Z',
                    %s,
                    'LEC',
                    1,
                    200000,
                    20000,
                    184.0,
                    5.0,
                    92.0,
                    4,
                    10400.0,
                    0,
                    'car'
                ),
                (
                    '2024-09-01T13:04:35Z',
                    %s,
                    'LEC',
                    2,
                    275000,
                    13000,
                    301.0,
                    100.0,
                    0.0,
                    8,
                    11800.0,
                    12,
                    'car'
                ),
                (
                    '2024-09-01T13:03:12Z',
                    %s,
                    'HAM',
                    1,
                    192000,
                    10000,
                    295.0,
                    95.0,
                    0.0,
                    8,
                    11600.0,
                    0,
                    'car'
                )
            """,
            (
                FIXTURE_SESSION_ID,
                FIXTURE_SESSION_ID,
                FIXTURE_SESSION_ID,
                FIXTURE_SESSION_ID,
            ),
        )

    @classmethod
    def seed_position_samples(cls):
        cls.execute(
            """
            INSERT INTO position_samples (
                sample_time_utc,
                session_id,
                driver_code,
                lap_number,
                x,
                y,
                z,
                track_status,
                sample_source
            )
            VALUES
                ('2024-09-01T13:03:10Z', %s, 'LEC', 1, 100.0, 200.0, 0.0, 'OnTrack', 'pos'),
                ('2024-09-01T13:03:12Z', %s, 'HAM', 1, 98.0, 198.0, 0.0, 'OnTrack', 'pos')
            """,
            (FIXTURE_SESSION_ID, FIXTURE_SESSION_ID),
        )

    @classmethod
    def seed_circuit_info(cls):
        cls.execute(
            """
            INSERT INTO circuit_metadata (
                session_id,
                rotation_degrees,
                source
            )
            VALUES (%s, 336.0, 'fastf1')
            """,
            (FIXTURE_SESSION_ID,),
        )
        cls.execute(
            """
            INSERT INTO circuit_markers (
                session_id,
                marker_type,
                marker_number,
                x,
                y,
                angle_degrees,
                distance_m
            )
            VALUES
                (%s, 'corner', 1, 120.0, 220.0, 90.0, 650.0),
                (%s, 'marshal_sector', 1, 180.0, 300.0, NULL, 1100.0)
            """,
            (FIXTURE_SESSION_ID, FIXTURE_SESSION_ID),
        )

    @classmethod
    def seed_weather_samples(cls):
        cls.execute(
            """
            INSERT INTO weather_samples (
                session_id,
                sample_time_utc,
                session_time_ms,
                air_temp_c,
                track_temp_c,
                humidity_pct,
                pressure_mbar,
                rainfall,
                wind_direction_deg,
                wind_speed_mps
            )
            VALUES
                (%s, '2024-09-01T13:00:00Z', 0, 29.0, 45.0, 52.0, 1008.0, false, 120, 1.8),
                (%s, '2024-09-01T13:01:00Z', 60000, 29.4, 46.0, 51.0, 1008.2, true, 125, 2.2)
            """,
            (FIXTURE_SESSION_ID, FIXTURE_SESSION_ID),
        )

    @classmethod
    def seed_status_events(cls):
        cls.execute(
            """
            INSERT INTO track_status_events (
                session_id,
                event_time_ms,
                status_code,
                message
            )
            VALUES
                (%s, 0, '1', 'Track clear'),
                (%s, 210000, '4', 'Safety car'),
                (%s, 260000, '1', 'Track clear')
            """,
            (FIXTURE_SESSION_ID, FIXTURE_SESSION_ID, FIXTURE_SESSION_ID),
        )
        cls.execute(
            """
            INSERT INTO session_status_events (
                session_id,
                event_time_ms,
                status
            )
            VALUES
                (%s, 0, 'Started'),
                (%s, 7200000, 'Finished')
            """,
            (FIXTURE_SESSION_ID, FIXTURE_SESSION_ID),
        )

    @classmethod
    def seed_race_control_messages(cls):
        cls.execute(
            """
            INSERT INTO race_control_messages (
                session_id,
                message_time_utc,
                session_time_ms,
                category,
                message,
                status,
                flag,
                scope,
                sector,
                racing_number,
                lap_number
            )
            VALUES
                (
                    %s,
                    '2024-09-01T13:03:30Z',
                    210000,
                    'Flag',
                    'SAFETY CAR DEPLOYED',
                    'Deployed',
                    'SC',
                    'Track',
                    NULL,
                    NULL,
                    1
                ),
                (
                    %s,
                    '2024-09-01T13:04:00Z',
                    240000,
                    'Drs',
                    'DRS ENABLED',
                    'Enabled',
                    NULL,
                    'All',
                    NULL,
                    NULL,
                    2
                )
            """,
            (FIXTURE_SESSION_ID, FIXTURE_SESSION_ID),
        )

    def test_migrations_create_real_tables_and_timescale_hypertables(self):
        """The migrations create real tables and Timescale hypertables."""

        tables = self.execute(
            """
            SELECT table_name
            FROM information_schema.tables
            WHERE table_schema = %s
              AND table_type = 'BASE TABLE'
            """,
            (self.schema_name,),
        )
        table_names = {row[0] for row in tables}

        self.assertTrue(EXPECTED_BASE_TABLES.issubset(table_names))

        hypertables = self.execute(
            """
            SELECT hypertable_name
            FROM timescaledb_information.hypertables
            WHERE hypertable_schema = %s
            """,
            (self.schema_name,),
        )

        self.assertEqual({row[0] for row in hypertables}, EXPECTED_HYPERTABLES)

    def test_telemetry_schema_contains_only_raw_car_sample_columns(self):
        """telemetry_samples should not keep obsolete composed telemetry fields."""

        rows = self.execute(
            """
            SELECT column_name
            FROM information_schema.columns
            WHERE table_schema = %s
              AND table_name = 'telemetry_samples'
            """,
            (self.schema_name,),
        )
        columns = {row[0] for row in rows}

        self.assertTrue(EXPECTED_TELEMETRY_COLUMNS.issubset(columns))
        self.assertFalse(REMOVED_COMPOSED_TELEMETRY_COLUMNS.intersection(columns))

    def test_lap_summaries_aggregate_real_telemetry_rows(self):
        """lap_summaries keeps laps visible and aggregates telemetry rows."""

        rows = self.execute(
            """
            SELECT
                lap_number,
                lap_time_ms,
                max_speed_kmh,
                avg_brake_pct,
                telemetry_samples
            FROM lap_summaries
            WHERE session_id = %s
              AND driver_code = 'LEC'
            ORDER BY lap_number
            """,
            (FIXTURE_SESSION_ID,),
        )

        self.assertEqual(len(rows), 2)
        self.assertEqual(rows[0][0], 1)
        self.assertEqual(rows[0][1], 82000)
        self.assertEqual(float(rows[0][2]), 312.0)
        self.assertEqual(float(rows[0][3]), 46.0)
        self.assertEqual(rows[0][4], 2)

    def test_driver_stint_summaries_group_laps_by_stint_and_compound(self):
        """driver_stint_summaries groups lap timing by driver stint."""

        rows = self.execute(
            """
            SELECT
                first_lap_number,
                last_lap_number,
                laps,
                min_tyre_life,
                max_tyre_life,
                best_lap_time_ms
            FROM driver_stint_summaries
            WHERE session_id = %s
              AND driver_code = 'LEC'
              AND stint_number = 1
              AND compound = 'MEDIUM'
            """,
            (FIXTURE_SESSION_ID,),
        )

        self.assertEqual(rows, [(1, 2, 2, 1, 2, 81000)])

    def test_session_weather_summary_aggregates_real_weather_rows(self):
        """session_weather_summary condenses low-frequency weather samples."""

        rows = self.execute(
            """
            SELECT
                min_air_temp_c,
                max_air_temp_c,
                avg_track_temp_c,
                rainfall_observed
            FROM session_weather_summary
            WHERE session_id = %s
            """,
            (FIXTURE_SESSION_ID,),
        )

        self.assertEqual(float(rows[0][0]), 29.0)
        self.assertEqual(float(rows[0][1]), 29.4)
        self.assertEqual(float(rows[0][2]), 45.5)
        self.assertTrue(rows[0][3])

    def test_track_status_periods_turn_events_into_real_periods(self):
        """track_status_periods derives status windows with lead(event_time_ms)."""

        rows = self.execute(
            """
            SELECT start_time_ms, end_time_ms, status_name
            FROM track_status_periods
            WHERE session_id = %s
            ORDER BY start_time_ms
            """,
            (FIXTURE_SESSION_ID,),
        )

        self.assertEqual(rows, EXPECTED_TRACK_STATUS_PERIODS)

    def test_race_control_event_index_searches_real_messages(self):
        """race_control_event_index exposes normalized searchable text."""

        rows = self.execute(
            """
            SELECT lap_number, category, message
            FROM race_control_event_index
            WHERE session_id = %s
              AND search_text LIKE %s
            """,
            (FIXTURE_SESSION_ID, "%drs%"),
        )

        self.assertEqual(rows, [(2, "Drs", "DRS ENABLED")])

    def test_telemetry_event_candidates_labels_real_threshold_matches(self):
        """telemetry_event_candidates labels rows that match event thresholds."""

        rows = self.execute(
            """
            SELECT lap_number, session_time_ms, event_type
            FROM telemetry_event_candidates
            WHERE session_id = %s
              AND driver_code = 'LEC'
            ORDER BY session_time_ms, event_type
            """,
            (FIXTURE_SESSION_ID,),
        )

        self.assertEqual(rows, EXPECTED_LEC_EVENT_CANDIDATES)

    def test_database_comments_are_available_from_postgres_catalogs(self):
        """Object comments survive real migration execution."""

        rows = self.execute(
            """
            SELECT obj_description(%s::regclass, 'pg_class')
            """,
            (f"{self.schema_name}.telemetry_samples",),
        )
        self.assertIn("High-volume raw car telemetry", rows[0][0])

        rows = self.execute(
            """
            SELECT obj_description(%s::regclass, 'pg_class')
            """,
            (f"{self.schema_name}.track_status_periods",),
        )
        self.assertIn("lead(event_time_ms)", rows[0][0])

    def test_docker_compose_mounts_migrations_for_first_run_init(self):
        """Docker Compose initializes fresh databases from db/migrations."""

        compose = (REPO_ROOT / "docker-compose.yml").read_text(encoding="utf-8")

        self.assertIn("timescale/timescaledb", compose)
        self.assertIn("POSTGRES_DB: race_telemetry", compose)
        self.assertIn("./db/migrations:/docker-entrypoint-initdb.d:ro", compose)
        self.assertIn("timescaledb-data:/var/lib/postgresql", compose)

    def test_dbml_er_model_includes_core_tables_and_relationships(self):
        """The visual ER artifact covers the same core schema concepts."""

        dbml = (REPO_ROOT / "db" / "schema.dbml").read_text(encoding="utf-8")

        for table_name in EXPECTED_BASE_TABLES:
            self.assertIn(f"Table {table_name}", dbml)
        self.assertIn("Ref: session_drivers.session_id > sessions.session_id", dbml)
        self.assertIn("Ref: telemetry_samples.(session_id, driver_code)", dbml)


if __name__ == "__main__":
    unittest.main()
