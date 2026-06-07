import argparse
import tempfile
import unittest
from pathlib import Path

from scripts.download_session import (
    DriverDownloadSummary,
    SessionDownloadSummary,
    build_manifest_stem,
    build_session_id,
    parse_driver_filter,
    prepare_fastf1_cache,
    select_driver_codes,
    slugify,
    validate_summary,
    build_parser,
)
from scripts.estimate_storage import estimate_storage, format_bytes
from scripts.estimate_storage import manifest_count
from scripts.import_session import (
    DEFAULT_BATCH_SIZE,
    DriverLapWindow,
    brake_to_pct,
    iter_lap_assignments,
    percentage_or_none,
    race_control_time_fields,
    session_start,
    timestamp_or_none,
    timedelta_to_ms,
)
from scripts.import_session import build_parser as build_import_parser
from scripts.import_sessions import build_parser as build_bulk_import_parser
from scripts.import_sessions import build_tasks as build_bulk_import_tasks


class DownloadSessionHelperTests(unittest.TestCase):
    def test_parser_defaults_to_race_session(self):
        args = build_parser().parse_args(["--year", "2024", "--event", "Monza"])

        self.assertEqual(args.session, "R")

    def test_import_parser_defaults_to_race_and_fail_mode(self):
        args = build_import_parser().parse_args(["--year", "2024", "--event", "Monza"])

        self.assertEqual(args.session, "R")
        self.assertEqual(args.mode, "fail")
        self.assertTrue(args.include_telemetry)
        self.assertTrue(args.include_position)
        self.assertTrue(args.include_context)
        self.assertGreaterEqual(args.telemetry_workers, 1)
        self.assertEqual(args.sample_write_method, "copy")
        self.assertEqual(args.batch_size, DEFAULT_BATCH_SIZE)
        self.assertTrue(args.parallel_sample_copy)

    def test_import_parser_accepts_parallel_sample_copy(self):
        args = build_import_parser().parse_args(
            ["--year", "2024", "--event", "Monza", "--parallel-sample-copy"]
        )

        self.assertTrue(args.parallel_sample_copy)

    def test_import_parser_can_disable_parallel_sample_copy(self):
        args = build_import_parser().parse_args(
            ["--year", "2024", "--event", "Monza", "--no-parallel-sample-copy"]
        )

        self.assertFalse(args.parallel_sample_copy)

    def test_bulk_import_parser_defaults_to_full_context_session_imports(self):
        parser = build_bulk_import_parser()
        args = parser.parse_args(["--year", "2024", "--events", "Monza,Spa", "--sessions", "R,Q"])

        tasks = build_bulk_import_tasks(args)

        self.assertEqual([task.label for task in tasks], ["2024 Monza R", "2024 Monza Q", "2024 Spa R", "2024 Spa Q"])
        self.assertEqual(args.workers, 2)
        self.assertTrue(args.parallel_sample_copy)
        self.assertFalse(hasattr(args, "include_context"))

    def test_bulk_import_parser_accepts_explicit_specs_across_years(self):
        parser = build_bulk_import_parser()
        args = parser.parse_args(["--spec", "2024:Monza:R", "--spec", "2025:Monza"])

        tasks = build_bulk_import_tasks(args)

        self.assertEqual([task.label for task in tasks], ["2024 Monza R", "2025 Monza R"])

    def test_manifest_stem_keeps_full_session_canonical(self):
        self.assertEqual(build_manifest_stem("2024-monza-r", None, None), "2024-monza-r")

    def test_manifest_stem_prevents_subset_runs_from_overwriting_full_manifest(self):
        self.assertEqual(
            build_manifest_stem("2024-monza-r", {"LEC", "VER"}, 3),
            "2024-monza-r-subset-lec-ver-first-3-laps",
        )

    def test_prepare_fastf1_cache_creates_and_enables_cache_directory(self):
        class FakeCache:
            enabled_path = None

            @classmethod
            def enable_cache(cls, path):
                cls.enabled_path = path

        class FakeFastF1:
            Cache = FakeCache

        with tempfile.TemporaryDirectory() as temp_dir:
            cache_dir = Path(temp_dir) / "nested" / "cache"

            resolved = prepare_fastf1_cache(FakeFastF1, cache_dir)

            self.assertTrue(resolved.exists())
            self.assertEqual(FakeCache.enabled_path, str(resolved))

    def test_storage_estimate_projects_from_observed_session_average(self):
        estimate = estimate_storage(
            cache_bytes=200,
            downloaded_sessions=2,
            events_per_year=24,
            sessions_per_event=5,
        )

        self.assertEqual(estimate.average_bytes_per_session, 100)
        self.assertEqual(estimate.race_only_season_bytes, 2400)
        self.assertEqual(estimate.typical_weekend_season_bytes, 12000)

    def test_format_bytes_uses_binary_units(self):
        self.assertEqual(format_bytes(1024 * 1024), "1.0 MB")

    def test_manifest_count_ignores_subset_manifests_for_storage_estimates(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            manifest_dir = Path(temp_dir)
            (manifest_dir / "2024-monza-r.json").write_text("{}")
            (manifest_dir / "2024-monza-r-subset-lec-first-3-laps.json").write_text("{}")

            self.assertEqual(manifest_count(manifest_dir), 1)

    def test_select_driver_codes_maps_fastf1_numbers_to_abbreviations(self):
        class FakeDriverInfo(dict):
            pass

        class FakeSession:
            drivers = ["16", "1"]

            def get_driver(self, driver_ref):
                return FakeDriverInfo({"16": {"Abbreviation": "LEC"}, "1": {"Abbreviation": "VER"}}[driver_ref])

        self.assertEqual(select_driver_codes(FakeSession(), None), ["LEC", "VER"])

    def test_slugify_normalizes_event_names(self):
        self.assertEqual(slugify("Italian Grand Prix"), "italian-grand-prix")
        self.assertEqual(slugify("São Paulo GP"), "s-o-paulo-gp")

    def test_build_session_id_matches_project_contract(self):
        self.assertEqual(
            build_session_id(2024, "Italian Grand Prix", "R"),
            "2024-italian-grand-prix-r",
        )

    def test_parse_driver_filter_uppercases_and_trims_codes(self):
        self.assertEqual(parse_driver_filter(" ver, HAM,lec "), {"VER", "HAM", "LEC"})

    def test_parse_driver_filter_rejects_empty_value(self):
        with self.assertRaises(argparse.ArgumentTypeError):
            parse_driver_filter(" , ")

    def test_validate_summary_rejects_missing_sample_data(self):
        summary = SessionDownloadSummary(
            session_id="2024-monza-r",
            year=2024,
            event="Monza",
            official_event_name="Italian Grand Prix",
            circuit_name="Monza",
            country="Italy",
            session="R",
            downloaded_at_utc="2026-06-06T00:00:00+00:00",
            cache_dir="data/fastf1-cache",
            drivers=[
                DriverDownloadSummary(
                    driver_code="VER",
                    laps=1,
                    telemetry_samples=0,
                    position_samples=10,
                    laps_without_telemetry=[1],
                    laps_without_position=[],
                )
            ],
            elapsed_seconds=1.2,
        )

        with self.assertRaisesRegex(ValueError, "No telemetry samples"):
            validate_summary(summary)

    def test_importer_converts_fastf1_timedeltas_to_milliseconds(self):
        import pandas as pd

        self.assertEqual(timedelta_to_ms(pd.Timedelta(seconds=1.234)), 1234)

    def test_importer_normalizes_boolean_brake_to_percent(self):
        self.assertEqual(brake_to_pct(True), 100.0)
        self.assertEqual(brake_to_pct(False), 0.0)
        self.assertEqual(brake_to_pct(37), 37.0)

    def test_importer_clamps_percent_channels_to_schema_range(self):
        self.assertEqual(percentage_or_none(104), 100.0)
        self.assertEqual(percentage_or_none(-1), 0.0)
        self.assertEqual(percentage_or_none(37), 37.0)

    def test_importer_parses_utc_timestamp_strings(self):
        parsed = timestamp_or_none("2024-09-01T13:00:00Z")

        self.assertEqual(parsed.isoformat(), "2024-09-01T13:00:00+00:00")

    def test_importer_prefers_resolved_session_date_for_session_start(self):
        from datetime import datetime

        class FakeEvent(dict):
            pass

        class FakeSession:
            date = datetime(2024, 9, 1, 13, 0)
            event = FakeEvent({"Session1DateUtc": "2024-08-30T11:30:00Z"})
            laps = None

        self.assertEqual(session_start(FakeSession()).isoformat(), "2024-09-01T13:00:00+00:00")

    def test_importer_parses_race_control_absolute_times(self):
        from datetime import UTC, datetime

        start = datetime(2024, 9, 1, 13, 0, tzinfo=UTC)

        pre_race_time, pre_race_ms = race_control_time_fields(start, "2024-09-01T12:20:01Z")
        race_time, race_ms = race_control_time_fields(start, "2024-09-01T13:00:05Z")

        self.assertEqual(pre_race_time.isoformat(), "2024-09-01T12:20:01+00:00")
        self.assertIsNone(pre_race_ms)
        self.assertEqual(race_time.isoformat(), "2024-09-01T13:00:05+00:00")
        self.assertEqual(race_ms, 5000)

    def test_iter_lap_assignments_maps_driver_stream_samples_into_lap_windows(self):
        import pandas as pd

        assignments = list(
            iter_lap_assignments(
                [
                    pd.Timedelta(milliseconds=900),
                    pd.Timedelta(milliseconds=1000),
                    pd.Timedelta(milliseconds=1100),
                    pd.Timedelta(milliseconds=2050),
                    pd.Timedelta(milliseconds=2200),
                ],
                [
                    DriverLapWindow(lap_number=1, start_ms=1000, end_ms=1999),
                    DriverLapWindow(lap_number=2, start_ms=2000, end_ms=2199),
                ],
            )
        )

        self.assertEqual(
            assignments,
            [
                (1, 1, 1000, 0),
                (2, 1, 1100, 100),
                (3, 2, 2050, 50),
            ],
        )

    def test_iter_lap_assignments_skips_gaps_between_laps(self):
        import pandas as pd

        assignments = list(
            iter_lap_assignments(
                [
                    pd.Timedelta(milliseconds=1500),
                    pd.Timedelta(milliseconds=2500),
                    pd.Timedelta(milliseconds=3200),
                ],
                [
                    DriverLapWindow(lap_number=1, start_ms=1000, end_ms=1999),
                    DriverLapWindow(lap_number=2, start_ms=3000, end_ms=3999),
                ],
            )
        )

        self.assertEqual(assignments, [(0, 1, 1500, 500), (2, 2, 3200, 200)])


if __name__ == "__main__":
    unittest.main()
