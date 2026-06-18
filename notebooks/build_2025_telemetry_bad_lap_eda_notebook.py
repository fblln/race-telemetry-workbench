#!/usr/bin/env python3
"""Build the 2025 telemetry bad-lap EDA notebook."""

from __future__ import annotations

from pathlib import Path
from textwrap import dedent

import nbformat as nbf


REPO_ROOT = Path(__file__).resolve().parents[1]
NOTEBOOK_PATH = REPO_ROOT / "notebooks" / "2025_telemetry_bad_lap_eda.ipynb"


def markdown(source: str):
    return nbf.v4.new_markdown_cell(dedent(source).strip() + "\n")


def code(source: str):
    return nbf.v4.new_code_cell(dedent(source).strip() + "\n")


def build_notebook():
    nb = nbf.v4.new_notebook()
    nb["metadata"] = {
        "kernelspec": {
            "display_name": "Python (.venv)",
            "language": "python",
            "name": "python3",
        },
        "language_info": {"name": "python", "pygments_lexer": "ipython3"},
    }

    nb.cells = [
        markdown(
            """
            # 2025 Race Telemetry Bad-Lap EDA

            This notebook audits 2025 race-session telemetry quality in the local
            Race Telemetry Workbench TimescaleDB import. It deliberately excludes
            FP, qualifying, sprint qualifying, and sprint sessions.

            The goal is not to fabricate a single "bad" score. The notebook builds
            a transparent taxonomy of failure signals, keeps reason flags
            non-mutually-exclusive, assigns a deterministic primary category for
            reporting, and uses `skrub` plus scikit-learn-compatible tooling for
            dataframe inspection and exploratory shape clustering.
            """
        ),
        markdown(
            """
            ## Runtime Contract

            `skrub 0.9.0` uses `SKB_DATA_DIRECTORY` for its writable data directory.
            Matplotlib must use a noninteractive backend in this sandbox. The
            support module sets these paths before importing `skrub` or
            Matplotlib, and all generated artifacts are written under
            `artifacts/2025-telemetry-bad-lap-eda/`.
            """
        ),
        code(
            """
            from pathlib import Path
            import sys

            REPO_ROOT = Path.cwd()
            if not (REPO_ROOT / "notebooks").exists() and REPO_ROOT.name == "notebooks":
                REPO_ROOT = REPO_ROOT.parent
            if str(REPO_ROOT) not in sys.path:
                sys.path.insert(0, str(REPO_ROOT))

            from notebooks import telemetry_bad_lap_support as eda

            print(f"artifact dir: {eda.ARTIFACT_DIR}")
            print(f"skrub data dir: {eda.SKRUB_DATA_DIR}")
            """
        ),
        markdown(
            """
            ## Confirm Imported 2025 Race Coverage

            Before classification, confirm the local database contains 2025 race
            sessions. If this table is empty in another environment, import a
            conservative validation slice first:

            ```bash
            docker compose up -d timescaledb
            .venv/bin/python scripts/download_session.py --year 2025 --event Monza
            .venv/bin/python scripts/import_session.py --year 2025 --event Monza --mode upsert
            ```

            For a full season import, keep concurrency conservative:

            ```bash
            .venv/bin/python scripts/import_sessions.py --year 2025 --workers 2 --mode upsert
            ```
            """
        ),
        code(
            """
            sessions = eda.load_session_inventory()
            display(sessions)
            print(f"2025 race sessions imported: {sessions['session_id'].nunique()}")
            """
        ),
        markdown(
            """
            ## Skrub API Check

            The installed package is `skrub` and this notebook uses the installed
            `TableReport`, `Cleaner`, and `tabular_pipeline`-compatible API surface.
            `tabular_learner` is not present in `skrub 0.9.0`, so the analysis
            does not depend on that older/nonexistent symbol.
            """
        ),
        code(
            """
            import inspect
            import skrub

            print("skrub version:", skrub.__version__)
            for name in ["TableReport", "Cleaner", "tabular_pipeline", "GapEncoder", "StringEncoder"]:
                obj = getattr(skrub, name, None)
                print(name, "available:", obj is not None, "signature:", inspect.signature(obj) if obj else None)
            """
        ),
        markdown(
            """
            ## Load, Classify, Cluster, and Write Artifacts

            `run_analysis()` performs bounded all-race queries:

            - one row per imported 2025 race lap,
            - raw car telemetry sample counts, gaps, null rates, and channel bounds,
            - raw position sample counts, path length, and segment discontinuity metrics,
            - track-status and race-control context,
            - 20 equal-lap-time speed bins used for shape residuals and clustering.

            It then writes CSV tables, SVG figures, a `skrub.TableReport`, and the
            markdown summary.
            """
        ),
        code(
            """
            result = eda.run_analysis(write_outputs=True)

            classified = result["classified"]
            thresholds_df = result["thresholds_df"]
            category_summary = result["category_summary"]
            primary_summary = result["primary_summary"]
            lens_summary = result["lens_summary"]
            safety_summary = result["safety_summary"]
            recommendation_summary = result["recommendation_summary"]
            intersections = result["intersections"]
            waterfall = result["waterfall"]
            race_summary = result["race_summary"]
            primary_by_race = result["primary_by_race"]
            race_drilldowns = result["race_drilldowns"]
            primary_audit = result["primary_audit"]
            driver_race_matrix = result["driver_race_matrix"]
            threshold_summary = result["threshold_summary"]
            threshold_by_race = result["threshold_by_race"]
            threshold_by_driver = result["threshold_by_driver"]
            borderline_laps = result["borderline_laps"]
            speed_profile_baselines = result["speed_profile_baselines"]
            shape_profile_examples = result["shape_profile_examples"]
            shape_cluster_exemplars = result["shape_cluster_exemplars"]
            shape_cluster_stability = result["shape_cluster_stability"]
            driver_summary = result["driver_summary"]
            examples = result["examples"]
            cluster_profile = result["cluster_profile"]

            print(f"skrub version used: {result['skrub_version']}")
            print(f"laps inspected: {len(classified):,}")
            print(f"any-category bad laps: {classified['bad_lap_any_category'].sum():,}")
            print(f"summary path: {result['summary_path']}")
            print(f"skrub report path: {result['report_path']}")
            print(f"metadata path: {result['metadata_path']}")
            """
        ),
        markdown(
            """
            ## Thresholds And Quality Lenses

            Thresholds are explicit because each flag should map to a known
            failure mode. The derived quality lenses separate data-integrity
            failures from race context and exploratory speed-shape outliers.
            """
        ),
        code(
            "display(thresholds_df)\n"
            "display(lens_summary)\n"
            "display(safety_summary)\n"
            "display(recommendation_summary)"
        ),
        markdown(
            """
            ## Taxonomy Counts

            Reason columns are intentionally not mutually exclusive. For example,
            a pit lap can also be FastF1-inaccurate and have an incomplete
            telemetry window. The speed-shape outlier category is reported as
            `atypical_speed_profile`; the compatibility source column remains
            `shape_mismatch_against_comparable_laps`.
            """
        ),
        code("display(category_summary)\ndisplay(primary_summary)"),
        markdown(
            """
            ## Category Overlap And Waterfall

            `reason_count` and `reason_set` are persisted on the classified-lap
            table. The intersection table shows the most common overlapping
            reason sets, while the waterfall assigns laps once in a deterministic
            product-review order.
            """
        ),
        code("display(intersections.head(20))\ndisplay(waterfall)"),
        markdown(
            """
            ## Race and Driver Concentration

            These tables separate "how much bad data exists" from "where it is
            concentrated." The category columns are counts; `bad_pct` is the
            percentage of laps with at least one category flag. The primary-by-race
            table powers the stacked race-decomposition figure.
            """
        ),
        code(
            "display(race_summary.head(12))\n"
            "display(primary_by_race[primary_by_race['primary_category'] != 'clean'].head(80))\n"
            "display(driver_summary.head(15))"
        ),
        markdown(
            """
            ## Selected Race Drilldowns

            British, Belgian, Australian, Dutch, and Sao Paulo are expanded because
            they dominate the first-pass bad-rate table. Rows are grouped by race,
            lap number, and deterministic primary category.
            """
        ),
        code("display(race_drilldowns[race_drilldowns['flagged_laps'] > 0].head(100))"),
        markdown(
            """
            ## Driver/Race Matrix And Primary-Category Audit

            The driver/race matrix separates all flags from integrity-only and
            context-only rates. The audit table surfaces multi-reason laps where
            the primary category may hide a useful secondary explanation.
            """
        ),
        code("display(driver_race_matrix.head(80))\ndisplay(primary_audit.head(80))"),
        markdown(
            """
            ## Threshold Sensitivity And Borderline Laps

            Sensitivity scenarios loosen and tighten the main sample-count,
            coverage, gap, path-ratio, and speed-shape thresholds. Borderline
            laps are the laps whose bad flag, recommendation, safety, or primary
            category changes under at least one scenario.
            """
        ),
        code(
            "display(threshold_summary)\n"
            "display(threshold_by_race.head(12))\n"
            "display(threshold_by_driver.head(12))\n"
            "display(borderline_laps.head(80))"
        ),
        markdown(
            """
            ## Speed-Shape Baselines And Cluster Stability

            Shape examples are compared against clean green-flag same-race median
            profiles with 10th-90th percentile bands. Cluster exemplars and
            stability checks keep the exploratory clustering from being treated
            as stronger evidence than it deserves.
            """
        ),
        code(
            "display(speed_profile_baselines.head(40))\n"
            "display(shape_profile_examples.head(20))\n"
            "display(shape_cluster_exemplars.head(40))\n"
            "display(shape_cluster_stability)"
        ),
        markdown(
            """
            ## Representative Examples

            The examples table is selected by category-specific severity: speed
            profile RMS for shape mismatch, path deviation or segment jump for
            position discontinuity, low coverage for incomplete windows, and large
            sample gaps for timing/session artifacts.
            """
        ),
        code("display(examples)"),
        markdown(
            """
            ## Shape Clusters

            The clustering is exploratory. It uses speed-profile bins and compact
            telemetry/position quality features to surface shape families and
            outlier regions. It is not used as the sole source of truth for source
            defects.
            """
        ),
        code("display(cluster_profile)"),
        markdown(
            """
            ## Cross-Check With Standalone Apexline Work

            Apexline's existing 2025 diagnostics are geometry-reference checks
            against GPS circuit outlines. This notebook uses the imported database
            surface for all-lap telemetry quality, while Apexline remains the
            stronger reference for GPS-shape validation.
            """
        ),
        code(
            """
            display(result["apexline_summary"].head(24))
            display(result["apexline_examples"].head(20))
            """
        ),
        markdown(
            """
            ## Visual Artifacts

            The notebook writes professional static artifacts so the analysis can
            be reviewed without re-running the notebook.
            """
        ),
        code(
            """
            from IPython.display import SVG, display

            for path in result["figure_paths"]:
                print(path.relative_to(REPO_ROOT))
                display(SVG(filename=str(path)))
            """
        ),
        markdown(
            """
            ## Outputs

            The primary written outputs are:

            - `docs/data-quality/2025-telemetry-bad-lap-eda-summary.md`
            - `artifacts/2025-telemetry-bad-lap-eda/skrub_lap_quality_table_report.html`
            - `artifacts/2025-telemetry-bad-lap-eda/tables/*.csv`
            - `artifacts/2025-telemetry-bad-lap-eda/tables/classified_laps_2025.parquet`
            - `artifacts/2025-telemetry-bad-lap-eda/metadata.json`
            - `artifacts/2025-telemetry-bad-lap-eda/tables/threshold_sensitivity_2025.csv`
            - `artifacts/2025-telemetry-bad-lap-eda/tables/borderline_laps_2025.csv`
            - `artifacts/2025-telemetry-bad-lap-eda/tables/speed_profile_baselines_2025.csv`
            - `artifacts/2025-telemetry-bad-lap-eda/tables/shape_cluster_stability_2025.csv`
            - `artifacts/2025-telemetry-bad-lap-eda/figures/*.svg`

            The most important limitation is explicit: raw FastF1 `Distance` is
            not part of the imported schema, so distance reset/non-monotonic
            distance checks are reported as unavailable rather than guessed.
            """
        ),
    ]
    return nb


def main() -> None:
    NOTEBOOK_PATH.parent.mkdir(parents=True, exist_ok=True)
    nbf.write(build_notebook(), NOTEBOOK_PATH)
    print(f"Wrote {NOTEBOOK_PATH}")


if __name__ == "__main__":
    main()
