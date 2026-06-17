# Imported race-session database surface EDA summary

Scope: all imported race sessions (`session_type = 'R'`) in the local TimescaleDB. The 2025 season is the only complete imported season in this database; 2024 and 2026 are partial imports.

## Data availability

- Race sessions inspected: 32
- Sessions with at least one surface quality flag: 32 (100.0%)
- Raw telemetry samples: 12,264,313
- Raw position samples: 11,977,829
- Aligned 10 Hz samples: 31,184,406
- Aligned rows without `OK`: 1,166,814 (3.74%)
- Ingestion diagnostic streams with warnings: 1,239
- Skrub report: `artifacts/race-database-surface-eda/skrub_database_surface_report.html`

## Year summary

|    year |   sessions |   sessions_with_issue |   median_surface_issue_count |   telemetry_samples |   position_samples |   aligned_samples |   weather_samples |   race_control_messages |   issue_pct |
|--------:|-----------:|----------------------:|-----------------------------:|--------------------:|-------------------:|------------------:|------------------:|------------------------:|------------:|
| 2024.00 |       1.00 |                  1.00 |                         1.00 |           324545.00 |          333502.00 |              0.00 |            133.00 |                   59.00 |      100.00 |
| 2025.00 |      24.00 |                 24.00 |                         2.00 |          9113457.00 |         9314091.00 |       24477559.00 |           3781.00 |                 2178.00 |      100.00 |
| 2026.00 |       7.00 |                  7.00 |                         2.00 |          2826311.00 |         2330236.00 |        6706847.00 |           1142.00 |                 1205.00 |      100.00 |

## Surface quality flags

| surface_flag                   |   sessions |   pct_sessions |
|:-------------------------------|-----------:|---------------:|
| ingestion_diagnostic_warning   |         31 |          96.88 |
| lap_metadata_incomplete        |         25 |          78.12 |
| race_control_sparse_or_untimed |         16 |          50.00 |
| aligned_replay_quality_issue   |          2 |           6.25 |
| circuit_annotation_issue       |          1 |           3.12 |
| driver_metadata_sparse         |          0 |           0.00 |
| raw_position_coverage_issue    |          0 |           0.00 |
| raw_telemetry_coverage_issue   |          0 |           0.00 |
| session_metadata_incomplete    |          0 |           0.00 |
| status_timeline_sparse         |          0 |           0.00 |
| weather_surface_issue          |          0 |           0.00 |

## Highest-issue sessions

|   year | event_name            |   surface_issue_count |   driver_count |   lap_rows |   telemetry_driver_count |   position_driver_count |   aligned_driver_count |   weather_samples |   race_control_messages |   corner_markers |
|-------:|:----------------------|----------------------:|---------------:|-----------:|-------------------------:|------------------------:|-----------------------:|------------------:|------------------------:|-----------------:|
|   2026 | Monaco Grand Prix     |                     4 |             22 |       1452 |                       22 |                      22 |                     22 |               208 |                     265 |                0 |
|   2025 | Australian Grand Prix |                     3 |             20 |        927 |                       20 |                      20 |                     20 |               178 |                     113 |               14 |
|   2025 | Austrian Grand Prix   |                     3 |             20 |       1126 |                       19 |                      19 |                     19 |               161 |                     117 |               10 |
|   2025 | Azerbaijan Grand Prix |                     3 |             20 |        968 |                       20 |                      20 |                     20 |               156 |                      71 |               20 |
|   2025 | Belgian Grand Prix    |                     3 |             20 |        879 |                       20 |                      20 |                     20 |               223 |                     116 |               19 |
|   2025 | British Grand Prix    |                     3 |             20 |        825 |                       19 |                      19 |                     19 |               155 |                     158 |               18 |
|   2025 | Dutch Grand Prix      |                     3 |             20 |       1364 |                       20 |                      20 |                     20 |               159 |                     108 |               14 |
|   2025 | Las Vegas Grand Prix  |                     3 |             20 |        886 |                       20 |                      20 |                     20 |               141 |                      61 |               17 |
|   2025 | Miami Grand Prix      |                     3 |             20 |       1005 |                       20 |                      20 |                     20 |               149 |                      77 |               19 |
|   2025 | São Paulo Grand Prix  |                     3 |             20 |       1251 |                       20 |                      20 |                     20 |               152 |                      56 |               15 |
|   2026 | Australian Grand Prix |                     3 |             22 |       1006 |                       20 |                      20 |                     20 |               148 |                     167 |               14 |
|   2026 | Japanese Grand Prix   |                     3 |             22 |       1107 |                       22 |                      22 |                     22 |               156 |                      47 |               18 |
|   2025 | Bahrain Grand Prix    |                     2 |             20 |       1128 |                       20 |                      20 |                     20 |               158 |                      91 |               15 |
|   2025 | Canadian Grand Prix   |                     2 |             20 |       1349 |                       20 |                      20 |                     20 |               167 |                      97 |               14 |
|   2025 | Chinese Grand Prix    |                     2 |             20 |       1065 |                       20 |                      20 |                     20 |               154 |                      56 |               16 |

## Top aligned quality flags

| quality_flag            |     rows |
|:------------------------|---------:|
| OK                      | 30017592 |
| LOCATION_GAP_TOO_LARGE  |   862777 |
| LOCATION_SAMPLE_TOO_OLD |   732254 |
| CAR_GAP_TOO_LARGE       |   343959 |
| CAR_SAMPLE_TOO_OLD      |   183357 |

## Top race-control categories

| category   |   messages |
|:-----------|-----------:|
| Flag       |       1876 |
| Other      |       1412 |
| SafetyCar  |         85 |
| Drs        |         69 |

## Visual artifacts

- `artifacts/race-database-surface-eda/figures/surface_availability_heatmap.svg`
- `artifacts/race-database-surface-eda/figures/surface_issue_counts.svg`
- `artifacts/race-database-surface-eda/figures/context_density_by_session.svg`
- `artifacts/race-database-surface-eda/figures/ingestion_frequency_by_stream.svg`
- `artifacts/race-database-surface-eda/figures/race_control_category_mix.svg`

## Findings to carry forward

- `session_end_utc` is absent for imported sessions. This does not block replay because session-relative times exist, but it limits session-duration QA without deriving an end from samples/status.
- The 2025 race season is complete locally; 2024 and 2026 are partial imports and should not be interpreted as season-level coverage.
- The non-lap context surfaces are populated enough for replay storytelling: weather, status timelines, race control, and circuit markers are present across imported race sessions.
- Aligned 10 Hz telemetry should be audited separately from raw telemetry because replay quality depends on interpolation and quality flags, not just raw sample counts.

## Recommended next analyses

- Add per-session duration derivation from raw samples and status events, then flag context surfaces that do not cover the derived session window.
- Decode aligned 10 Hz `quality_flags` into stable categories and track them by session, driver, lap, and time window.
- Add race-control message text clustering for incident taxonomy and duplicate/noise detection.
- Add weather trend anomaly checks by comparing observed sampling cadence and value jumps within each session.
- Decide whether session-level quality summaries should be persisted or remain offline notebook diagnostics.
