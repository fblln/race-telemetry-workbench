#!/usr/bin/env python3
"""Build the imported race-session database surface EDA notebook."""

from __future__ import annotations

from pathlib import Path
from textwrap import dedent

import nbformat as nbf


REPO_ROOT = Path(__file__).resolve().parents[1]
NOTEBOOK_PATH = REPO_ROOT / "notebooks" / "race_database_surface_eda.ipynb"


def markdown(source: str):
    return nbf.v4.new_markdown_cell(dedent(source).strip() + "\n")


def code(source: str):
    return nbf.v4.new_code_cell(dedent(source).strip() + "\n")


def build_notebook():
    nb = nbf.v4.new_notebook()
    nb["metadata"] = {
        "kernelspec": {"display_name": "Python (.venv)", "language": "python", "name": "python3"},
        "language_info": {"name": "python", "pygments_lexer": "ipython3"},
    }
    nb.cells = [
        markdown(
            """
            # Imported Race-Session Database Surface EDA

            This notebook audits the other imported race-session data surfaces in
            Race Telemetry Workbench: session metadata, drivers, weather,
            track/session status, race control, circuit markers, raw telemetry
            coverage, raw position coverage, ingestion diagnostics, and aligned
            10 Hz replay data.

            It complements `2025_telemetry_bad_lap_eda.ipynb`, which focuses on
            lap-level telemetry shape and bad-lap taxonomy.
            """
        ),
        markdown(
            """
            ## Scope

            Race sessions are the default project scope. This notebook includes
            all imported race sessions in the local database and clearly separates
            complete-season 2025 coverage from partial 2024 and 2026 imports.
            """
        ),
        code(
            """
            from pathlib import Path
            import sys

            REPO_ROOT = Path.cwd()
            if str(REPO_ROOT) not in sys.path:
                sys.path.insert(0, str(REPO_ROOT))

            from notebooks import database_surface_quality_support as eda
            print(f"artifact dir: {eda.ARTIFACT_DIR}")
            """
        ),
        markdown(
            """
            ## Installed Skrub API

            The EDA uses `skrub.TableReport` for a compact dataframe profile.
            `SKB_DATA_DIRECTORY` and the Matplotlib `Agg` backend are configured
            by the support module before importing `skrub`.
            """
        ),
        code(
            """
            import inspect
            import skrub

            print("skrub version:", skrub.__version__)
            print("TableReport:", inspect.signature(skrub.TableReport))
            """
        ),
        markdown(
            """
            ## Run The Surface Audit

            The support module builds one row per imported race session and
            supporting tables for ingestion diagnostics, aligned quality flags,
            and race-control category mix.
            """
        ),
        code(
            """
            result = eda.run_analysis(write_outputs=True)

            classified = result["classified"]
            flag_summary = result["flag_summary"]
            year_summary = result["year_summary"]
            diagnostics = result["diagnostics"]
            aligned_flags = result["aligned_flags"]
            race_control_categories = result["race_control_categories"]

            print(f"race sessions inspected: {len(classified)}")
            print(f"sessions with at least one surface issue: {classified['has_surface_issue'].sum()}")
            print(f"summary: {result['summary_path']}")
            print(f"skrub report: {result['report_path']}")
            """
        ),
        markdown(
            """
            ## Year-Level Coverage

            2025 is the complete-season slice. 2024 and 2026 are partial imports
            and should not be interpreted as complete season coverage.
            """
        ),
        code("display(year_summary)\ndisplay(classified[['year','event_name','driver_count','lap_rows','telemetry_samples','position_samples','aligned_samples','weather_samples','race_control_messages','corner_markers','surface_issue_count']])"),
        markdown(
            """
            ## Surface Quality Flags

            These are session-level flags, not per-lap flags. A session can be
            useful for replay and still have a surface flag that deserves
            documentation or follow-up.
            """
        ),
        code("display(flag_summary)"),
        markdown(
            """
            ## Ingestion Diagnostics And Aligned Replay Quality

            Raw telemetry coverage and aligned replay quality are different
            surfaces. The aligned table can carry quality flags even when raw
            streams are present.
            """
        ),
        code(
            """
            display(diagnostics.head(25))
            display(aligned_flags.groupby('quality_flag', as_index=False)['rows'].sum().sort_values('rows', ascending=False))
            """
        ),
        markdown(
            """
            ## Race-Control Message Mix

            Race-control messages are a storytelling surface. This table checks
            category/scope distribution and timing completeness before any
            incident taxonomy work.
            """
        ),
        code("display(race_control_categories.head(50))"),
        markdown(
            """
            ## Visual Artifacts
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

            Primary outputs:

            - `docs/data-quality/race-database-surface-eda-summary.md`
            - `artifacts/race-database-surface-eda/skrub_database_surface_report.html`
            - `artifacts/race-database-surface-eda/tables/*.csv`
            - `artifacts/race-database-surface-eda/tables/session_surface_quality.parquet`
            - `artifacts/race-database-surface-eda/figures/*.svg`

            The next useful iteration is to decode aligned `quality_flags` by
            session, driver, lap, and time window, then connect those findings
            back to replay behavior.
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
