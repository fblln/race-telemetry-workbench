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
            category_summary = result["category_summary"]
            primary_summary = result["primary_summary"]
            race_summary = result["race_summary"]
            driver_summary = result["driver_summary"]
            examples = result["examples"]
            cluster_profile = result["cluster_profile"]

            print(f"skrub version used: {result['skrub_version']}")
            print(f"laps inspected: {len(classified):,}")
            print(f"any-category bad laps: {classified['bad_lap_any_category'].sum():,}")
            print(f"summary path: {result['summary_path']}")
            print(f"skrub report path: {result['report_path']}")
            """
        ),
        markdown(
            """
            ## Taxonomy Counts

            Reason columns are intentionally not mutually exclusive. For example,
            a pit lap can also be FastF1-inaccurate and have an incomplete
            telemetry window.
            """
        ),
        code("display(category_summary)\ndisplay(primary_summary)"),
        markdown(
            """
            ## Race and Driver Concentration

            These tables separate "how much bad data exists" from "where it is
            concentrated." The category columns are counts; `bad_pct` is the
            percentage of laps with at least one category flag.
            """
        ),
        code("display(race_summary.head(12))\ndisplay(driver_summary.head(15))"),
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
