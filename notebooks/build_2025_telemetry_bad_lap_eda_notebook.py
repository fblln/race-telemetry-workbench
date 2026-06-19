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
            Race Telemetry Workbench import and rewrites the analysis as a
            narrative walkthrough instead of a table dump.

            It stays explicit about telemetry domains:

            - raw telemetry explains what was actually observed
            - time-aligned telemetry explains when something happened
            - distance alignment is not inferred here when the source data does not support it

            The notebook therefore treats its shape checks as a raw/time-domain
            quality aid, not as authoritative gained/lost truth.
            """
        ),
        markdown(
            """
            ## Runtime Setup

            `skrub 0.9.0` needs `SKB_DATA_DIRECTORY`, Matplotlib must use a
            noninteractive backend here, and all notebook artifacts are written
            under `artifacts/2025-telemetry-bad-lap-eda/`.
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
            ## Run The Analysis Once

            `run_analysis()` performs the bounded 2025 race queries, classifies
            lap-quality signals, writes figures and tables, and returns the data
            used by the story below.
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
            apexline_summary = result["apexline_summary"]
            apexline_examples = result["apexline_examples"]

            print(f"skrub version used: {result['skrub_version']}")
            print(f"laps inspected: {len(classified):,}")
            print(f"summary path: {result['summary_path']}")
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

            Start with the seasonal shape before diving into lap-level evidence.
            The point here is to answer three questions quickly:

            1. How much telemetry is being flagged at all?
            2. Are those flags driven by a few races or spread across the season?
            3. Are we mostly seeing integrity problems, context-heavy laps, or exploratory shape outliers?
            """
        ),
        code(
            """
            total_laps = len(classified)
            bad_laps = int(classified["bad_lap_any_category"].sum())
            bad_pct = bad_laps / total_laps * 100 if total_laps else 0
            top_race = race_summary.sort_values("bad_pct", ascending=False).iloc[0]
            top_category = category_summary.sort_values("laps", ascending=False).iloc[0]

            metric_row([
                ("Laps inspected", f"{total_laps:,}", "2025 race sessions only"),
                ("Flagged laps", f"{bad_laps:,}", f"{bad_pct:.1f}% with at least one category flag"),
                ("Most affected race", top_race["event_name"], f"{top_race['bad_pct']:.1f}% flagged"),
                ("Largest category", top_category["category"], f"{int(top_category['laps']):,} laps"),
            ])

            callout(
                "Interpretation rule",
                "These counts are deliberately non-mutually-exclusive. A pit lap can also be sparse, context-heavy, or shape-atypical, so the figures below are about evidence layers rather than a single defect code.",
            )
            """
        ),
        figure(
            "../artifacts/2025-telemetry-bad-lap-eda/figures/category_counts.svg",
            "Which Signals Show Up Most Often",
            "The first chart answers what types of evidence are driving the season-wide flagged-lap count.",
        ),
        figure(
            "../artifacts/2025-telemetry-bad-lap-eda/figures/race_bad_lap_rates.svg",
            "How Uneven The Season Really Is",
            "The second chart asks whether the issue is systemic or concentrated in a handful of race weekends.",
        ),
        code(
            """
            show_table(
                race_summary,
                columns=["event_round", "event_name", "laps", "bad_laps", "bad_pct"],
                sort_by="bad_pct",
                ascending=False,
                limit=8,
                title="Races With The Highest Flagged-Lap Rate",
            )
            """
        ),
        markdown(
            """
            ## What Is Actually Driving The Flags

            After the headline counts, the next question is whether the flagged
            set is dominated by cleanly separable categories or by multi-reason
            laps that need a more careful review path.
            """
        ),
        figure(
            "../artifacts/2025-telemetry-bad-lap-eda/figures/category_intersections.svg",
            "Common Reason Overlaps",
            "This matters because a product rule built on a single flag can hide the real cause when certain combinations repeat.",
        ),
        figure(
            "../artifacts/2025-telemetry-bad-lap-eda/figures/decision_waterfall.svg",
            "Deterministic Primary-Category Assignment",
            "The waterfall is the reporting layer: it assigns one primary category after preserving the richer multi-flag evidence.",
        ),
        figure(
            "../artifacts/2025-telemetry-bad-lap-eda/figures/recommendation_summary.svg",
            "What The Current Rules Want The Product To Do",
            "Recommendations stay separate from raw flags so notebook outputs do not hard-code product behavior into the schema.",
        ),
        code(
            """
            show_table(
                primary_summary,
                columns=["primary_category", "laps", "pct_laps"],
                sort_by="laps",
                ascending=False,
                limit=10,
                title="Primary Categories",
            )
            show_table(
                recommendation_summary,
                columns=["product_recommendation", "laps", "pct_laps"],
                sort_by="laps",
                ascending=False,
                limit=10,
                title="Recommendation Split",
            )
            """
        ),
        markdown(
            """
            ## Where The Season Concentrates

            This is the part that should feel most like a human EDA: not just
            how many rows are bad, but where the story visibly clusters by race,
            lap progression, and driver/race combinations.
            """
        ),
        figure(
            "../artifacts/2025-telemetry-bad-lap-eda/figures/race_primary_category_decomposition.svg",
            "Race-By-Race Decomposition",
            "Some races are noisy for one dominant reason, while others are broad multi-surface problems.",
        ),
        figure(
            "../artifacts/2025-telemetry-bad-lap-eda/figures/lap_number_primary_category_heatmap.svg",
            "Where In A Race The Flags Show Up",
            "A lap-number heatmap is often more revealing than another ranked table because it surfaces whether issues arrive at starts, pit cycles, or closing laps.",
        ),
        figure(
            "../artifacts/2025-telemetry-bad-lap-eda/figures/driver_race_quality_matrix.svg",
            "Driver/Race Concentration",
            "This is the fastest way to see whether we have isolated driver streams or race-wide telemetry conditions.",
        ),
        code(
            """
            show_table(
                driver_summary,
                columns=["driver_code", "laps", "bad_laps", "bad_pct"],
                sort_by="bad_pct",
                ascending=False,
                limit=10,
                title="Most Affected Drivers",
            )
            show_table(
                race_drilldowns[race_drilldowns["flagged_laps"] > 0],
                columns=["event_name", "lap_number", "primary_category", "flagged_laps"],
                sort_by="flagged_laps",
                ascending=False,
                limit=12,
                title="Selected Race Drilldowns",
            )
            """
        ),
        markdown(
            """
            ## How Stable Are The Labels

            A useful EDA should admit where the labels are fragile. This section
            asks which races and laps move when the thresholds are nudged rather
            than pretending the chosen cutoffs are magically exact.
            """
        ),
        figure(
            "../artifacts/2025-telemetry-bad-lap-eda/figures/threshold_sensitivity.svg",
            "Scenario Sensitivity",
            "Positive deltas add flagged laps versus the baseline, negative deltas remove them.",
        ),
        figure(
            "../artifacts/2025-telemetry-bad-lap-eda/figures/borderline_laps_by_race.svg",
            "Where Borderline Decisions Accumulate",
            "If a race keeps showing up here, it deserves manual review before downstream tooling treats the labels as settled.",
        ),
        code(
            """
            show_table(
                threshold_summary,
                columns=[
                    "scenario",
                    "bad_laps",
                    "bad_laps_delta_vs_baseline",
                    "exclude_laps",
                    "context_label_laps",
                ],
                sort_by="bad_laps_delta_vs_baseline",
                ascending=False,
                limit=12,
                title="Threshold Scenarios",
            )
            show_table(
                borderline_laps,
                columns=[
                    "event_name",
                    "driver_code",
                    "lap_number",
                    "baseline_primary_category",
                    "baseline_product_recommendation",
                ],
                sort_by="event_name",
                ascending=True,
                limit=12,
                title="Borderline Laps To Review Manually",
            )
            """
        ),
        markdown(
            """
            ## What The Shape Outliers Look Like

            This is the most important caveat in the notebook: shape analysis is
            still equal-time and exploratory here. It can point to suspicious lap
            families, but it is not a substitute for the future distance-domain
            projection.
            """
        ),
        code(
            """
            callout(
                "Domain caution",
                "Equal-time profile comparisons can show that two laps look different, but they do not yet tell us where time was gained or lost. That requires the distance-domain rollout.",
                accent="#b54708",
            )
            """
        ),
        figure(
            "../artifacts/2025-telemetry-bad-lap-eda/figures/shape_clusters.svg",
            "Cluster Map Of Speed-Shape Families",
            "This is the exploratory lens: it groups laps with similar equal-time profiles and quality traits.",
        ),
        figure(
            "../artifacts/2025-telemetry-bad-lap-eda/figures/representative_speed_shapes.svg",
            "Representative Shape Examples",
            "These traces are the visual answer to 'what does an outlier actually look like?'",
        ),
        figure(
            "../artifacts/2025-telemetry-bad-lap-eda/figures/shape_profile_quantile_bands.svg",
            "Examples Against Same-Race Bands",
            "The median plus 10th-90th bands make the outliers easier to trust than a raw RMS number alone.",
        ),
        figure(
            "../artifacts/2025-telemetry-bad-lap-eda/figures/shape_cluster_stability.svg",
            "Cluster Stability Check",
            "The clusters are only useful if they are reasonably stable under reruns and sampling variation.",
        ),
        code(
            """
            show_table(
                shape_profile_examples,
                columns=[
                    "event_name",
                    "driver_code",
                    "lap_number",
                    "comparison_group",
                    "speed_profile_rms",
                ],
                sort_by="speed_profile_rms",
                ascending=False,
                limit=10,
                title="Most Extreme Shape Examples",
            )
            show_table(
                shape_cluster_stability,
                columns=list(shape_cluster_stability.columns),
                limit=10,
                title="Stability Metrics",
            )
            """
        ),
        markdown(
            """
            ## Evidence Tables, Not Evidence Dumps

            The notebook still needs concrete rows for auditability, but the
            point is to keep them short and positioned after the chart that made
            them interesting.
            """
        ),
        code(
            """
            show_table(
                examples,
                columns=[
                    "event_name",
                    "driver_code",
                    "lap_number",
                    "primary_category",
                    "product_recommendation",
                ],
                sort_by="event_name",
                ascending=True,
                limit=15,
                title="Representative Manual-Review Examples",
            )
            show_table(
                primary_audit,
                columns=[
                    "event_name",
                    "driver_code",
                    "lap_number",
                    "primary_category",
                    "reason_set",
                ],
                sort_by="event_name",
                ascending=True,
                limit=12,
                title="Primary Category Audit Samples",
            )
            """
        ),
        markdown(
            """
            ## Geometry Cross-Check

            Apexline remains the stronger geometry-reference audit. This notebook
            uses it as a cross-check, not as a hidden replacement for the local
            database-surface analysis.
            """
        ),
        code(
            """
            if apexline_summary.empty:
                callout(
                    "Apexline cross-check unavailable",
                    "No Apexline summary was loaded in this environment, so geometry-reference comparison is not shown here.",
                    accent="#8d2b0b",
                )
            else:
                show_table(
                    apexline_summary,
                    columns=[
                        "event_name",
                        "apexline_total_laps",
                        "apexline_bad_laps",
                        "apexline_shape_bad_laps",
                    ],
                    sort_by="apexline_bad_laps",
                    ascending=False,
                    limit=10,
                    title="Apexline Event Summary",
                )
                if not apexline_examples.empty:
                    show_table(
                        apexline_examples,
                        columns=["event_name", "driver_code", "lap_number"],
                        limit=10,
                        title="Apexline Example Laps",
                    )
            """
        ),
        markdown(
            """
            ## Appendix: Thresholds And Supporting Lenses

            The detailed thresholds still matter, but they belong after the
            visual argument rather than before it.
            """
        ),
        code(
            """
            show_table(thresholds_df, limit=len(thresholds_df), title="Threshold Definitions")
            show_table(lens_summary, limit=len(lens_summary), title="Quality Lenses")
            show_table(safety_summary, limit=len(safety_summary), title="Safety Lenses")
            """
        ),
        markdown(
            """
            ## Outputs

            Primary written artifacts:

            - `docs/data-quality/2025-telemetry-bad-lap-eda-summary.md`
            - `artifacts/2025-telemetry-bad-lap-eda/skrub_lap_quality_table_report.html`
            - `artifacts/2025-telemetry-bad-lap-eda/tables/*.csv`
            - `artifacts/2025-telemetry-bad-lap-eda/tables/classified_laps_2025.parquet`
            - `artifacts/2025-telemetry-bad-lap-eda/metadata.json`
            - `artifacts/2025-telemetry-bad-lap-eda/figures/*.svg`

            The explicit limitation remains unchanged: raw FastF1 `Distance` is
            not part of the imported schema, so distance reset and non-monotonic
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
