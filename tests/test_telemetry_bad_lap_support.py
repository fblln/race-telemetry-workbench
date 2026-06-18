"""Unit tests for telemetry bad-lap EDA classification helpers."""

import unittest

import pandas as pd

from notebooks import telemetry_bad_lap_support as eda


def base_lap(**overrides):
    row = {
        "car_samples": 120,
        "position_samples": 180,
        "lap_time_ms": 90_000.0,
        "lap_start_utc": pd.Timestamp("2025-01-01T00:00:00Z"),
        "lap_end_utc": pd.Timestamp("2025-01-01T00:01:30Z"),
        "car_coverage_ratio": 0.99,
        "min_speed_kmh": 10.0,
        "max_speed_kmh": 330.0,
        "min_rpm": 1_000.0,
        "max_rpm": 12_000.0,
        "min_gear": 1,
        "max_gear": 8,
        "min_throttle_pct": 0.0,
        "max_throttle_pct": 100.0,
        "min_brake_pct": 0.0,
        "max_brake_pct": 100.0,
        "speed_profile_bins_present": 20,
        "speed_shape_rms_kmh": 8.0,
        "speed_shape_limit_kmh": 35.0,
        "speed_shape_session_median_kmh": 7.0,
        "speed_shape_iqr_kmh": 5.0,
        "position_path_ratio": 1.0,
        "position_segment_limit_units": 120.0,
        "position_max_segment_units": 80.0,
        "is_pit_in_lap": False,
        "is_pit_out_lap": False,
        "safety_car_periods": 0,
        "virtual_safety_car_periods": 0,
        "red_flag_periods": 0,
        "lap_start_session_ms": 100_000.0,
        "lap_end_session_ms": 190_000.0,
        "car_out_of_order_steps": 0,
        "car_lap_time_negative_steps": 0,
        "car_p95_gap_ms": 120.0,
        "car_max_gap_ms": 600.0,
        "is_accurate": True,
        "is_deleted": False,
        "telemetry_null_rate": 0.0,
        "position_null_rate": 0.0,
    }
    row.update(overrides)
    return row


class TelemetryBadLapClassificationTests(unittest.TestCase):
    def classify_one(self, **overrides):
        return eda.classify_laps(pd.DataFrame([base_lap(**overrides)])).iloc[0]

    def test_clean_lap_is_safe_and_kept(self):
        row = self.classify_one()

        self.assertFalse(row["bad_lap_any_category"])
        self.assertTrue(row["safe_for_replay"])
        self.assertTrue(row["safe_for_lap_comparison"])
        self.assertTrue(row["safe_for_geometry_reference"])
        self.assertEqual(row["product_recommendation"], "keep")
        self.assertEqual(row["reason_set"], "clean")

    def test_context_lap_is_replay_safe_but_not_comparison_safe(self):
        row = self.classify_one(is_pit_in_lap=True)

        self.assertTrue(row["pit_lane_or_safety_car_influenced"])
        self.assertTrue(row["race_context_flag"])
        self.assertTrue(row["safe_for_replay"])
        self.assertFalse(row["safe_for_lap_comparison"])
        self.assertEqual(row["product_recommendation"], "keep_with_context_label")

    def test_timing_artifact_takes_primary_priority_over_source_anomaly(self):
        row = self.classify_one(
            car_max_gap_ms=3_500.0,
            is_accurate=False,
        )

        self.assertTrue(row["timing_session_boundary_artifact"])
        self.assertTrue(row["import_or_source_data_anomaly"])
        self.assertEqual(row["primary_category"], "timing_session_boundary_artifact")
        self.assertGreaterEqual(row["reason_count"], 2)
        self.assertFalse(row["safe_for_replay"])
        self.assertEqual(row["product_recommendation"], "exclude")

    def test_shape_only_outlier_is_manual_review_not_replay_exclusion(self):
        row = self.classify_one(
            speed_shape_rms_kmh=80.0,
            speed_shape_limit_kmh=35.0,
        )

        self.assertTrue(row["atypical_speed_profile"])
        self.assertTrue(row["unknown_needs_inspection"])
        self.assertTrue(row["needs_manual_review"])
        self.assertTrue(row["safe_for_replay"])
        self.assertFalse(row["safe_for_lap_comparison"])
        self.assertEqual(row["product_recommendation"], "manual_review")

    def test_distance_checks_remain_explicitly_unavailable(self):
        row = self.classify_one()

        self.assertFalse(row["distance_reset_or_non_monotonic_distance"])
        self.assertNotIn("distance_reset_or_non_monotonic_distance", row["reason_set"])


if __name__ == "__main__":
    unittest.main()
