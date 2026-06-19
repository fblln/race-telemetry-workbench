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


def figure(path: str, title: str, note: str):
    return markdown(
        f"""
        ### {title}

        {note}

        ![{title}]({path})
        """
    )


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

            This notebook audits the imported 2025 race-session database
            surfaces in Race Telemetry Workbench and presents them as a product
            story instead of a long QA appendix.

            The guiding rule stays explicit:

            - raw telemetry answers what was observed
            - aligned replay answers when it happened
            - distance-domain gained/lost analysis is a separate projection

            That distinction matters because replay-quality warnings and raw
            ingest coverage are related, but they are not the same thing.
            """
        ),
        markdown(
            """
            ## Scope And Runtime

            The notebook is restricted to `year = 2025` and `session_type = 'R'`
            and should only make season-wide claims when the expected 24 race
            sessions are present.
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
            ## Run The Surface Audit

            The support module builds one row per 2025 race session plus the
            aligned replay, context, weather, marker, and readiness tables used
            below.
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
            """
        ),
        code(
            """
            import pandas as pd
            from IPython.display import HTML, Markdown, display

            def show_table(df, columns=None, sort_by=None, ascending=False, limit=10, title=None):
                table = df.copy()
                if sort_by and sort_by in table.columns:
                    table = table.sort_values(sort_by, ascending=ascending)
                if columns:
                    keep = [column for column in columns if column in table.columns]
                    if keep:
                        table = table[keep]
                if title:
                    display(Markdown(f"### {title}"))
                display(table.head(limit))

            def metric_row(cards):
                html_cards = []
                for label, value, detail in cards:
                    html_cards.append(
                        f'''
                        <div style="flex:1; min-width:180px; border:1px solid #d6dde6; border-radius:14px; padding:14px 16px; background:linear-gradient(180deg,#fbfdff 0%,#f2f6fa 100%);">
                          <div style="font-size:12px; text-transform:uppercase; letter-spacing:0.08em; color:#5b677a; margin-bottom:6px;">{label}</div>
                          <div style="font-size:28px; font-weight:700; color:#1f2933; line-height:1.1;">{value}</div>
                          <div style="font-size:13px; color:#52606d; margin-top:6px;">{detail}</div>
                        </div>
                        '''
                    )
                display(
                    HTML(
                        '<div style="display:flex; gap:12px; flex-wrap:wrap; margin:10px 0 18px 0;">'
                        + "".join(html_cards)
                        + "</div>"
                    )
                )

            def callout(title: str, body: str, accent: str = "#1f6feb"):
                display(
                    HTML(
                        f'''
                        <div style="border-left:5px solid {accent}; background:#f7fafc; padding:12px 16px; margin:10px 0 16px 0; border-radius:10px;">
                          <div style="font-weight:700; color:#102a43; margin-bottom:4px;">{title}</div>
                          <div style="color:#334e68; line-height:1.45;">{body}</div>
                        </div>
                        '''
                    )
                )
            """
        ),
        markdown(
            """
            ## Quick Read

            This notebook starts with a product question rather than a database
            question: if a human opened the imported 2025 season today, which
            surfaces look broadly trustworthy and which ones need visible caveats?
            """
        ),
        code(
            """
            session_count = len(classified)
            sessions_with_issues = int(classified["has_surface_issue"].sum())
            issue_pct = sessions_with_issues / session_count * 100 if session_count else 0
            worst_surface = flag_summary.sort_values("sessions", ascending=False).iloc[0]
            top_recommendation = recommendation_summary.sort_values("sessions", ascending=False).iloc[0]

            metric_row([
                ("Race sessions", f"{session_count:,}", result["scope"].label),
                ("Sessions with issues", f"{sessions_with_issues:,}", f"{issue_pct:.1f}% carry at least one surface warning"),
                ("Most common flag", worst_surface["surface_flag"], f"{int(worst_surface['sessions'])} sessions"),
                ("Main recommendation", top_recommendation["final_recommendation"], f"{int(top_recommendation['sessions'])} sessions"),
            ])

            callout(
                "Read this notebook by domain",
                "Raw ingest coverage, aligned replay quality, and product readiness are separated on purpose. Treating them as the same thing usually leads to misleading conclusions.",
            )
            """
        ),
        figure(
            "../artifacts/2025-race-database-surface-eda/figures/surface_availability_heatmap.svg",
            "Coverage Across The Imported Surfaces",
            "This is the fastest season-wide view of what exists before we judge quality or product risk.",
        ),
        figure(
            "../artifacts/2025-race-database-surface-eda/figures/surface_issue_counts.svg",
            "Which Surfaces Drive The Warnings",
            "A session can be replay-safe and still show a surface issue worth documenting or labeling.",
        ),
        code(
            """
            show_table(
                classified,
                columns=[
                    "year",
                    "event_name",
                    "driver_count",
                    "lap_rows",
                    "telemetry_samples",
                    "position_samples",
                    "aligned_samples",
                    "surface_issue_count",
                ],
                sort_by="surface_issue_count",
                ascending=False,
                limit=8,
                title="Sessions With The Most Surface Warnings",
            )
            """
        ),
        markdown(
            """
            ## Coverage First, Then Quality

            A human EDA usually asks whether the low-level surfaces exist before
            it asks whether the higher-level replay model is trustworthy. This
            section keeps that order explicit.
            """
        ),
        figure(
            "../artifacts/2025-race-database-surface-eda/figures/ingestion_frequency_by_stream.svg",
            "Raw Ingestion Cadence",
            "The point here is not a perfect nominal frequency, but whether certain streams or races drift enough to explain later replay degradation.",
        ),
        figure(
            "../artifacts/2025-race-database-surface-eda/figures/surface_active_coverage_heatmap.svg",
            "Active Replay Coverage By Surface",
            "This chart narrows the focus from whole-session presence to the windows that matter most for playback.",
        ),
        figure(
            "../artifacts/2025-race-database-surface-eda/figures/surface_coverage_windows.svg",
            "Coverage Windows Across The Session",
            "The temporal spread matters because some sessions have coverage that is technically present but poorly placed for live replay use.",
        ),
        code(
            """
            show_table(
                diagnostics,
                columns=[
                    "event_name",
                    "driver_code",
                    "stream_name",
                    "estimated_frequency_hz",
                    "sample_count",
                ],
                sort_by="estimated_frequency_hz",
                ascending=True,
                limit=12,
                title="Selected Ingestion Diagnostics",
            )
            show_table(
                coverage_summary,
                limit=len(coverage_summary),
                title="Coverage Summary By Surface",
            )
            """
        ),
        markdown(
            """
            ## Replay Quality Is Its Own Story

            `aligned_telemetry_10hz` is a replay-oriented derived surface. It is
            useful, but it should never be mistaken for raw truth. The next few
            charts focus on how replay degrades, where it degrades, and whether
            those degradations line up with obvious race context.
            """
        ),
        code(
            """
            callout(
                "Time-domain rule",
                "Replay-quality warnings are time-domain diagnostics. They answer when the replay model was forced to interpolate, age out, or degrade, not where a driver gained or lost time.",
                accent="#b54708",
            )
            """
        ),
        figure(
            "../artifacts/2025-race-database-surface-eda/figures/aligned_driver_non_ok_heatmap.svg",
            "Replay Quality By Driver And Race",
            "This is the season map for non-OK aligned rows.",
        ),
        figure(
            "../artifacts/2025-race-database-surface-eda/figures/aligned_quality_replay_strips.svg",
            "What Degraded Replay Windows Look Like",
            "The strip view is closer to how the desktop player experiences replay problems than an aggregate percentage table.",
        ),
        figure(
            "../artifacts/2025-race-database-surface-eda/figures/aligned_context_overlap.svg",
            "Do Degraded Windows Coincide With Context-Heavy Moments?",
            "This correlation is descriptive, not causal, but it is useful for deciding whether warnings should mention incidents, pit phases, or neutralization periods.",
        ),
        code(
            """
            show_table(
                aligned_races,
                columns=["event_name", "aligned_rows", "non_ok_rows", "non_ok_pct"],
                sort_by="non_ok_pct",
                ascending=False,
                limit=10,
                title="Races With The Highest Non-OK Replay Share",
            )
            show_table(
                desktop_watchlist,
                columns=[
                    "event_name",
                    "driver_code",
                    "window_start_ms",
                    "window_end_ms",
                    "non_ok_pct",
                    "dominant_family",
                ],
                sort_by="non_ok_pct",
                ascending=False,
                limit=12,
                title="Replay Windows Worth Watching In Product QA",
            )
            """
        ),
        markdown(
            """
            ## Context Surfaces Should Tell A Story Too

            Race control, status, and weather are not just auxiliary tables.
            They are the narrative layer that explains why certain replay windows
            deserve labels, chips, or richer UI treatment.
            """
        ),
        figure(
            "../artifacts/2025-race-database-surface-eda/figures/race_control_category_mix.svg",
            "Race-Control Message Mix",
            "This is the category balance check before building incident stories on top of the text feed.",
        ),
        figure(
            "../artifacts/2025-race-database-surface-eda/figures/race_control_taxonomy_mix.svg",
            "Taxonomy Depth",
            "The deterministic taxonomy should cover the common phrase families before clustering is allowed to explain the leftovers.",
        ),
        figure(
            "../artifacts/2025-race-database-surface-eda/figures/status_timeline_strips.svg",
            "Status Timeline Strips",
            "Status intervals are inherently temporal, so a strip chart communicates them better than a flat table.",
        ),
        figure(
            "../artifacts/2025-race-database-surface-eda/figures/context_timeline_density.svg",
            "Where The Session Context Peaks",
            "This reveals whether context density is concentrated in a few violent stretches or spread across the race.",
        ),
        code(
            """
            show_table(
                race_control_taxonomy_summary,
                columns=["taxonomy", "messages", "sessions"],
                sort_by="messages",
                ascending=False,
                limit=10,
                title="Top Race-Control Taxonomy Buckets",
            )
            show_table(
                context_replay_correlation,
                limit=len(context_replay_correlation),
                title="Context / Replay Correlation Summary",
            )
            show_table(
                race_control_duplicates,
                columns=["event_name", "taxonomy", "message", "duplicates"],
                sort_by="duplicates",
                ascending=False,
                limit=10,
                title="Repeated Race-Control Messages",
            )
            """
        ),
        markdown(
            """
            ## Weather And Marker Geometry

            These surfaces are visually rich and easy to misunderstand in plain
            tables, so they benefit the most from being chart-led.
            """
        ),
        figure(
            "../artifacts/2025-race-database-surface-eda/figures/weather_cadence_jumps.svg",
            "Weather Cadence And Change Intensity",
            "This separates sparse weather feeds from races that actually experienced meaningful transitions.",
        ),
        figure(
            "../artifacts/2025-race-database-surface-eda/figures/weather_trend_panels.svg",
            "Weather Panels For High-Change Sessions",
            "The panels make it obvious whether the imported weather stream would support a believable timeline overlay.",
        ),
        figure(
            "../artifacts/2025-race-database-surface-eda/figures/circuit_marker_quality_summary.svg",
            "Circuit Marker Quality",
            "Marker counts alone are not enough; we also need to know whether coordinates are plausible against imported traces.",
        ),
        figure(
            "../artifacts/2025-race-database-surface-eda/figures/circuit_marker_overlay_examples.svg",
            "Marker Overlay Examples",
            "This is the geometry sanity check before the product treats corners or marshal lights as trustworthy annotation anchors.",
        ),
        code(
            """
            show_table(
                weather_summary,
                columns=[
                    "event_name",
                    "weather_samples",
                    "max_gap_ms",
                    "large_gap_flag",
                    "rainfall_transitions",
                ],
                sort_by="max_gap_ms",
                ascending=False,
                limit=10,
                title="Weather Sessions To Inspect",
            )
            show_table(
                marker_quality[marker_quality["marker_coordinate_issue"] != "none"],
                columns=[
                    "event_name",
                    "marker_type",
                    "marker_number",
                    "marker_letter",
                    "marker_coordinate_issue",
                ],
                sort_by="event_name",
                ascending=True,
                limit=12,
                title="Marker Coordinate Exceptions",
            )
            """
        ),
        markdown(
            """
            ## Product Readiness

            After coverage, replay, context, and geometry, the notebook can
            finally translate QA evidence into product-facing recommendations.
            """
        ),
        figure(
            "../artifacts/2025-race-database-surface-eda/figures/product_readiness_dashboard.svg",
            "Readiness Across Product Lenses",
            "The dashboard keeps catalog, raw-stream, replay, context, and circuit-context readiness separate.",
        ),
        figure(
            "../artifacts/2025-race-database-surface-eda/figures/product_recommendation_summary.svg",
            "Recommendation Mix",
            "This is the action view: no action, UI label, inspect, reimport, or schema/importer work.",
        ),
        code(
            """
            show_table(
                product_readiness,
                columns=[
                    "event_name",
                    "catalog_readiness",
                    "raw_stream_readiness",
                    "replay_readiness",
                    "context_readiness",
                    "circuit_context_readiness",
                    "final_recommendation",
                ],
                sort_by="final_recommendation",
                ascending=True,
                limit=12,
                title="Session-Level Product Readiness",
            )
            show_table(
                recommendation_summary,
                limit=len(recommendation_summary),
                title="Recommendation Totals",
            )
            """
        ),
        markdown(
            """
            ## Appendix: Text Clusters And Supporting Tables

            These remain useful, but they belong after the main visual argument.
            """
        ),
        figure(
            "../artifacts/2025-race-database-surface-eda/figures/race_control_text_clusters.svg",
            "Race-Control Text Clusters",
            "Clustering is a supplement to the deterministic taxonomy, not a replacement for it.",
        ),
        code(
            """
            show_table(
                race_control_cluster_summary,
                columns=["text_cluster", "cluster_terms", "messages"],
                sort_by="messages",
                ascending=False,
                limit=12,
                title="Largest Text Clusters",
            )
            show_table(
                year_summary,
                limit=len(year_summary),
                title="Year-Level Coverage Check",
            )
            """
        ),
        markdown(
            """
            ## Outputs

            Primary outputs:

            - `docs/data-quality/2025-race-database-surface-eda-summary.md`
            - `artifacts/2025-race-database-surface-eda/skrub_2025_race_database_surface_report.html`
            - `artifacts/2025-race-database-surface-eda/tables/*.csv`
            - `artifacts/2025-race-database-surface-eda/tables/*.parquet`
            - `artifacts/2025-race-database-surface-eda/figures/*.svg`

            The next useful slice remains explicit: add the distance-domain
            projection and keep any “where time was gained or lost” analysis out
            of this notebook until that surface exists.
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
