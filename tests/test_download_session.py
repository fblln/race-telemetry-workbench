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


class DownloadSessionHelperTests(unittest.TestCase):
    def test_parser_defaults_to_race_session(self):
        args = build_parser().parse_args(["--year", "2024", "--event", "Monza"])

        self.assertEqual(args.session, "R")

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


if __name__ == "__main__":
    unittest.main()
