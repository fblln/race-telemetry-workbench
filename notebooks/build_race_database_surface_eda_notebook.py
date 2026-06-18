#!/usr/bin/env python3
"""Build the 2025 race-session database surface EDA notebook."""

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
            # 2025 Race Database-Surface EDA

            This notebook audits the imported 2025 race-session data surfaces in
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

            Race sessions are the default project scope. This notebook is limited
            to `year = 2025` and `session_type = 'R'` and refuses to generate
            season-level conclusions unless exactly 24 races are present.
            """
        ),
        code(
            """
            from pathlib import Path
            import sys

            REPO_ROOT = Path.cwd()
            if not (REPO_ROOT / "notebooks").exists():
                REPO_ROOT = REPO_ROOT.parent
            if str(REPO_ROOT) not in sys.path:
                sys.path.insert(0, str(REPO_ROOT))

            from notebooks import database_surface_quality_support as eda
            print(f"scope: {eda.SurfaceScope().label}")
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

            The support module builds one row per 2025 race session and
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
            aligned_races = result["aligned_races"]
            aligned_drivers = result["aligned_drivers"]
            aligned_laps = result["aligned_laps"]
            degraded_segments = result["degraded_segments"]
            aligned_windows = result["aligned_windows"]
            aligned_context_overlap = result["aligned_context_overlap"]
            aligned_lap_context = result["aligned_lap_context"]
            desktop_watchlist = result["desktop_watchlist"]
            session_duration_coverage = result["session_duration_coverage"]
            coverage_windows = result["coverage_windows"]
            coverage_summary = result["coverage_summary"]
            race_control_messages = result["race_control_messages"]
            race_control_taxonomy_summary = result["race_control_taxonomy_summary"]
            race_control_duplicates = result["race_control_duplicates"]
            race_control_examples = result["race_control_examples"]
            status_intervals = result["status_intervals"]
            status_race_control_overlap = result["status_race_control_overlap"]
            weather_summary = result["weather_summary"]
            weather_transitions = result["weather_transitions"]
            context_timeline_bins = result["context_timeline_bins"]
            context_replay_correlation = result["context_replay_correlation"]
            product_readiness = result["product_readiness"]
            recommendation_summary = result["recommendation_summary"]
            marker_quality = result["marker_quality"]
            marker_summary = result["marker_summary"]
            marker_position_examples = result["marker_position_examples"]
            race_control_clustered = result["race_control_clustered"]
            race_control_cluster_summary = result["race_control_cluster_summary"]

            print(f"scope: {result['scope'].label}")
            print(f"race sessions inspected: {len(classified)}")
            print(f"sessions with at least one surface issue: {classified['has_surface_issue'].sum()}")
            print(f"summary: {result['summary_path']}")
            print(f"skrub report: {result['report_path']}")
            """
        ),
        markdown(
            """
            ## Year-Level Coverage

            This table is intentionally single-year. Partial 2024 and 2026 local
            imports are excluded from the primary EDA scope.
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
            ## Aligned Replay Quality Deep Dive

            These tables decode aligned `quality_flags` into stable families and
            aggregate them by race, driver, lap, consecutive degraded segment,
            and 30-second replay window. The window table is intentionally close
            to desktop replay chunks so it can guide warning and overlay design.
            """
        ),
        code(
            """
            display(aligned_races.sort_values('non_ok_pct', ascending=False).head(12))
            display(desktop_watchlist.head(20))
            display(degraded_segments.head(20))
            """
        ),
        markdown(
            """
            ## Context Correlation Checks

            This pass checks whether degraded aligned windows overlap
            race-control messages or active yellow/SC/VSC/red status intervals.
            The result is not causal, but it highlights whether replay-quality
            warnings should be explained as source/cadence behavior or as
            context-heavy race moments.
            """
        ),
        code(
            """
            display(aligned_context_overlap)
            display(aligned_lap_context)

            high_context = aligned_windows[
                (aligned_windows['non_ok_pct'] >= result['thresholds'].degraded_window_min_non_ok_pct)
                & aligned_windows['has_incident_context']
            ].sort_values(['non_ok_pct', 'race_control_messages'], ascending=False)
            display(high_context.head(25))
            """
        ),
        markdown(
            """
            ## Session Duration And Coverage Windows

            `session_end_utc` is still missing in the imported session metadata,
            so this section derives duration from the imported sample and status
            surfaces. It also separates the full session timeline from the active
            replay window that matters most to desktop playback.
            """
        ),
        code(
            """
            display(session_duration_coverage[[
                'event_name',
                'derived_session_duration_ms',
                'duration_source',
                'active_replay_start_ms',
                'active_replay_end_ms',
                'active_replay_duration_ms',
                'finished_to_derived_end_gap_ms',
            ]])
            display(coverage_summary)
            display(coverage_windows.sort_values(['active_coverage_ratio', 'surface']).head(25))
            """
        ),
        markdown(
            """
            ## Race-Control, Status, And Weather Context

            This pass turns imported context rows into deterministic buckets that
            can drive desktop timeline chips and filters: race-control taxonomy,
            repeated message groups, track-status intervals, weather cadence and
            rainfall transitions, plus a 5-minute context-density table.
            """
        ),
        code(
            """
            display(race_control_taxonomy_summary)
            display(race_control_duplicates.head(20))
            display(race_control_examples)
            display(status_race_control_overlap.sort_values(['incident_messages', 'race_control_messages'], ascending=False).head(25))
            display(weather_summary.sort_values(['large_gap_flag', 'rainfall_transitions', 'max_gap_ms'], ascending=[False, False, False]).head(20))
            display(weather_transitions)
            display(context_replay_correlation)
            display(context_timeline_bins.sort_values(['degraded_window_rate', 'incident_messages'], ascending=False).head(25))
            """
        ),
        markdown(
            """
            ## Product Readiness And Recommendations

            The EDA now turns inventory and severity signals into five product
            lenses: catalog, raw streams, replay, context, and circuit context.
            The final recommendation column maps analysis into API/desktop
            decisions: no action, label in UI, inspect, reimport, or
            schema/importer change.
            """
        ),
        code(
            """
            display(recommendation_summary)
            display(product_readiness[[
                'event_name',
                'catalog_readiness',
                'raw_stream_readiness',
                'replay_readiness',
                'context_readiness',
                'circuit_context_readiness',
                'final_recommendation',
                'schema_importer_follow_up',
                'affected_drivers',
                'affected_replay_windows',
                'marker_coordinate_issues',
                'product_impact',
                'systematic_known_limitations',
            ]])
            """
        ),
        markdown(
            """
            ## Circuit Marker Geometry QA

            Marker coordinates are compared against imported position trace
            bounds. Markers outside padded min/max bounds are treated as
            implausible; markers outside the core percentile trace are marked
            for inspection before desktop callouts rely on them.
            """
        ),
        code(
            """
            display(marker_summary)
            display(marker_quality[marker_quality['marker_coordinate_issue'] != 'none'][[
                'event_name',
                'marker_type',
                'marker_number',
                'marker_letter',
                'x',
                'y',
                'marker_coordinate_issue',
                'outside_minmax_bounds',
                'outside_core_bounds',
            ]].head(40))
            display(marker_position_examples.groupby(['event_name', 'driver_code']).size().reset_index(name='sampled_trace_points'))
            """
        ),
        markdown(
            """
            ## Race-Control Text Clusters And Weather Trend Panels

            Text clustering complements the deterministic taxonomy by surfacing
            repeated phrase families. Weather trend panels show races with
            rainfall or large shifts so the desktop context strip can be tested
            against real changes.
            """
        ),
        code(
            """
            display(race_control_cluster_summary)
            display(race_control_clustered[[
                'event_name',
                'session_time_ms',
                'taxonomy',
                'text_cluster',
                'cluster_terms',
                'message',
            ]].head(40))
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

            - `docs/data-quality/2025-race-database-surface-eda-summary.md`
            - `artifacts/2025-race-database-surface-eda/skrub_2025_race_database_surface_report.html`
            - `artifacts/2025-race-database-surface-eda/tables/*.csv`
            - `artifacts/2025-race-database-surface-eda/tables/session_surface_quality.parquet`
            - `artifacts/2025-race-database-surface-eda/tables/aligned_quality_windows_30s.parquet`
            - `artifacts/2025-race-database-surface-eda/tables/desktop_replay_quality_watchlist.parquet`
            - `artifacts/2025-race-database-surface-eda/tables/session_duration_coverage.parquet`
            - `artifacts/2025-race-database-surface-eda/tables/session_surface_coverage_windows.parquet`
            - `artifacts/2025-race-database-surface-eda/tables/race_control_messages_classified.parquet`
            - `artifacts/2025-race-database-surface-eda/tables/context_timeline_bins_5min.parquet`
            - `artifacts/2025-race-database-surface-eda/tables/product_readiness.parquet`
            - `artifacts/2025-race-database-surface-eda/tables/circuit_marker_quality.csv`
            - `artifacts/2025-race-database-surface-eda/tables/race_control_text_cluster_summary.csv`
            - `artifacts/2025-race-database-surface-eda/figures/*.svg`

            Command-line rerun:

            ```bash
            .venv/bin/python notebooks/database_surface_quality_support.py
            ```

            The next useful iteration is raw stream diagnostic severity and
            deciding whether these offline readiness labels should be persisted
            or only surfaced as generated QA artifacts.
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
