# 2025 race telemetry bad-lap EDA summary

Scope: 2025 race sessions (`session_type = 'R'`) imported in the local TimescaleDB. No FP, qualifying, sprint qualifying, or sprint sessions are included.

## Data availability

- Race sessions inspected: 24
- Laps inspected: 26,689
- Laps with at least one quality/category flag: 5,497 (20.6%)
- Laps with no quality/category flag: 21,192 (79.4%)
- Skrub report: `artifacts/2025-telemetry-bad-lap-eda/skrub_lap_quality_table_report.html`

## Taxonomy

- `missing_or_sparse_telemetry`: too few car or position samples for a defensible lap-level trace.
- `incomplete_lap_window`: missing timing or less than the configured lap-window coverage in raw car telemetry.
- `distance_reset_or_non_monotonic_distance`: unavailable in the imported schema because raw FastF1 `Distance` is not stored; this is kept explicit instead of inferred.
- `implausible_channel_values`: speed, RPM, gear, throttle, or brake outside configured physical/source bounds.
- `shape_mismatch_against_comparable_laps`: equal-lap-time speed profile is a robust outlier versus clean laps from the same race.
- `position_trace_discontinuity`: position path length or segment jumps are inconsistent with clean same-race laps.
- `pit_lane_or_safety_car_influenced`: pit-in/out, safety car, VSC, or red-flag context overlaps the lap.
- `timing_session_boundary_artifact`: missing session-relative timing, raw sample ordering issues, lap-time reset, or large raw telemetry gaps.
- `import_or_source_data_anomaly`: FastF1 inaccurate/deleted lap marker.
- `unknown_needs_inspection`: speed-shape outlier with no stronger explanatory signal.

Reason columns are not mutually exclusive. The `primary_category` table assigns each lap to the first applicable category in a deterministic priority order.

## Category counts

| category                                 |   laps |   pct_of_all_laps |
|:-----------------------------------------|-------:|------------------:|
| import_or_source_data_anomaly            |   3929 |             14.72 |
| shape_mismatch_against_comparable_laps   |   3224 |             12.08 |
| pit_lane_or_safety_car_influenced        |   3199 |             11.99 |
| timing_session_boundary_artifact         |    369 |              1.38 |
| incomplete_lap_window                    |    352 |              1.32 |
| position_trace_discontinuity             |    142 |              0.53 |
| unknown_needs_inspection                 |     54 |              0.20 |
| implausible_channel_values               |      4 |              0.01 |
| distance_reset_or_non_monotonic_distance |      0 |              0.00 |
| missing_or_sparse_telemetry              |      0 |              0.00 |

## Primary categories

| primary_category                       |   laps |   pct_of_all_laps |
|:---------------------------------------|-------:|------------------:|
| clean                                  |  21192 |             79.40 |
| pit_lane_or_safety_car_influenced      |   3087 |             11.57 |
| shape_mismatch_against_comparable_laps |   1559 |              5.84 |
| import_or_source_data_anomaly          |    374 |              1.40 |
| timing_session_boundary_artifact       |    369 |              1.38 |
| position_trace_discontinuity           |    107 |              0.40 |
| implausible_channel_values             |      1 |              0.00 |

## Most affected races

|   event_round | event_name                |   total_laps |   bad_laps |   source_anomaly |   pit_sc_context |   sparse |   incomplete |   position |   shape |   timing |   bad_pct |
|--------------:|:--------------------------|-------------:|-----------:|-----------------:|-----------------:|---------:|-------------:|-----------:|--------:|---------:|----------:|
|            12 | British Grand Prix        |          825 |        601 |              336 |              344 |        0 |           91 |         10 |     145 |       91 |     72.85 |
|            13 | Belgian Grand Prix        |          879 |        396 |              145 |              332 |        0 |           60 |          0 |     168 |       60 |     45.05 |
|             1 | Australian Grand Prix     |          927 |        410 |              364 |              235 |        0 |           69 |          7 |     295 |       69 |     44.23 |
|            15 | Dutch Grand Prix          |         1364 |        432 |              330 |              213 |        0 |            2 |          3 |     316 |        2 |     31.67 |
|            21 | São Paulo Grand Prix      |         1251 |        359 |              203 |              233 |        0 |            2 |          4 |     200 |        2 |     28.70 |
|            23 | Qatar Grand Prix          |         1067 |        272 |              163 |              194 |        0 |            3 |          3 |     142 |        3 |     25.49 |
|            22 | Las Vegas Grand Prix      |          886 |        219 |              126 |              124 |        0 |            2 |          1 |     115 |       19 |     24.72 |
|            17 | Azerbaijan Grand Prix     |          968 |        233 |              114 |              152 |        0 |           58 |          3 |      63 |       58 |     24.07 |
|             7 | Emilia Romagna Grand Prix |         1207 |        272 |              254 |               93 |        0 |            2 |          3 |     242 |        2 |     22.54 |
|             5 | Saudi Arabian Grand Prix  |          898 |        179 |              100 |              117 |        0 |           37 |          1 |      52 |       37 |     19.93 |

## Most affected drivers

| driver_code   |   total_laps |   bad_laps |   source_anomaly |   pit_sc_context |   sparse |   incomplete |   position |   shape |   timing |   bad_pct |
|:--------------|-------------:|-----------:|-----------------:|-----------------:|---------:|-------------:|-----------:|--------:|---------:|----------:|
| RUS           |         1442 |        317 |              227 |              179 |        0 |           18 |         18 |     186 |       19 |     21.98 |
| TSU           |         1386 |        294 |              208 |              181 |        0 |           16 |          6 |     171 |       17 |     21.21 |
| BEA           |         1373 |        291 |              212 |              164 |        0 |           18 |          8 |     168 |       19 |     21.19 |
| VER           |         1375 |        290 |              202 |              172 |        0 |           19 |          7 |     163 |       20 |     21.09 |
| NOR           |         1434 |        290 |              202 |              164 |        0 |           21 |          8 |     160 |       22 |     20.22 |
| GAS           |         1315 |        289 |              207 |              167 |        0 |           16 |         10 |     171 |       17 |     21.98 |
| ALB           |         1307 |        288 |              198 |              166 |        0 |           18 |          5 |     173 |       19 |     22.04 |
| STR           |         1317 |        285 |              198 |              171 |        0 |           19 |          5 |     170 |       19 |     21.64 |
| PIA           |         1394 |        282 |              204 |              167 |        0 |           18 |          6 |     164 |       19 |     20.23 |
| HUL           |         1287 |        280 |              203 |              157 |        0 |           18 |          5 |     165 |       19 |     21.76 |
| OCO           |         1402 |        277 |              184 |              168 |        0 |           19 |          5 |     160 |       20 |     19.76 |
| HAM           |         1360 |        276 |              200 |              162 |        0 |           19 |          5 |     162 |       20 |     20.29 |

## Representative examples

| category                               |   event_round | event_name            | driver_code   |   lap_number | primary_category                       |   lap_time_ms |   car_samples |   position_samples |   speed_shape_rms_kmh |   position_path_ratio |   position_max_segment_units |   car_max_gap_ms |   car_coverage_ratio |
|:---------------------------------------|--------------:|:----------------------|:--------------|-------------:|:---------------------------------------|--------------:|--------------:|-------------------:|----------------------:|----------------------:|-----------------------------:|-----------------:|---------------------:|
| incomplete_lap_window                  |             1 | Australian Grand Prix | ALB           |            2 | timing_session_boundary_artifact       |       nan     |           616 |                634 |               nan     |                 1.000 |                      594.839 |         1359.000 |              nan     |
| incomplete_lap_window                  |             1 | Australian Grand Prix | ALB           |            3 | timing_session_boundary_artifact       |       nan     |           601 |                631 |               nan     |                 1.002 |                      638.421 |         1280.000 |              nan     |
| incomplete_lap_window                  |             1 | Australian Grand Prix | ALB           |            4 | timing_session_boundary_artifact       |       nan     |           568 |                588 |               nan     |                 1.004 |                      449.662 |          961.000 |              nan     |
| incomplete_lap_window                  |             1 | Australian Grand Prix | ALO           |            2 | timing_session_boundary_artifact       |       nan     |           618 |                637 |               nan     |                 1.003 |                      616.623 |         1359.000 |              nan     |
| incomplete_lap_window                  |             1 | Australian Grand Prix | ALO           |            3 | timing_session_boundary_artifact       |       nan     |           600 |                628 |               nan     |                 1.004 |                      568.919 |         1280.000 |              nan     |
| implausible_channel_values             |             1 | Australian Grand Prix | PIA           |           44 | timing_session_boundary_artifact       |       nan     |           719 |                739 |               nan     |                 1.127 |                     2859.845 |         1201.000 |              nan     |
| implausible_channel_values             |             8 | Monaco Grand Prix     | BOR           |            1 | implausible_channel_values             |    145564.000 |           534 |                546 |                87.684 |                 1.051 |                      573.248 |         1041.000 |                1.001 |
| implausible_channel_values             |            21 | São Paulo Grand Prix  | BOR           |            1 | timing_session_boundary_artifact       |       nan     |           295 |                308 |               nan     |                 0.672 |                      362.604 |          960.000 |              nan     |
| implausible_channel_values             |            23 | Qatar Grand Prix      | HUL           |            7 | timing_session_boundary_artifact       |       nan     |           563 |                564 |               nan     |                 0.183 |                      646.877 |         1160.000 |              nan     |
| shape_mismatch_against_comparable_laps |            16 | Italian Grand Prix    | ALB           |            4 | shape_mismatch_against_comparable_laps |     85427.000 |           319 |                320 |               256.237 |                 1.000 |                     1099.027 |         1280.000 |                0.995 |
| shape_mismatch_against_comparable_laps |            16 | Italian Grand Prix    | ALB           |           10 | shape_mismatch_against_comparable_laps |     84453.000 |           318 |                331 |               256.237 |                 1.002 |                      677.432 |          960.000 |                0.997 |
| shape_mismatch_against_comparable_laps |            16 | Italian Grand Prix    | ALB           |            3 | shape_mismatch_against_comparable_laps |     85238.000 |           314 |                320 |               256.237 |                 1.000 |                      587.487 |         1160.000 |                0.998 |
| shape_mismatch_against_comparable_laps |            16 | Italian Grand Prix    | ALB           |            2 | shape_mismatch_against_comparable_laps |     85762.000 |           315 |                312 |               256.237 |                 0.999 |                     1181.326 |         1201.000 |                0.999 |
| shape_mismatch_against_comparable_laps |            16 | Italian Grand Prix    | ALB           |            6 | shape_mismatch_against_comparable_laps |     84743.000 |           325 |                330 |               256.237 |                 1.003 |                      516.036 |          640.000 |                0.995 |
| position_trace_discontinuity           |             1 | Australian Grand Prix | HAD           |            1 | timing_session_boundary_artifact       |       nan     |           442 |                418 |               nan     |                 0.000 |                        0.000 |         1080.000 |              nan     |
| position_trace_discontinuity           |             4 | Bahrain Grand Prix    | RUS           |           48 | position_trace_discontinuity           |     96746.000 |           350 |                363 |                74.667 |                 0.000 |                        0.000 |         1000.000 |                0.994 |
| position_trace_discontinuity           |             4 | Bahrain Grand Prix    | RUS           |           49 | position_trace_discontinuity           |     97315.000 |           352 |                366 |                74.667 |                 0.000 |                        0.000 |         1120.000 |                0.996 |
| position_trace_discontinuity           |             4 | Bahrain Grand Prix    | RUS           |           50 | position_trace_discontinuity           |     96973.000 |           355 |                375 |                74.667 |                 0.000 |                        0.000 |          920.000 |                0.999 |
| position_trace_discontinuity           |             4 | Bahrain Grand Prix    | RUS           |           51 | position_trace_discontinuity           |     97377.000 |           352 |                370 |                74.667 |                 0.000 |                        0.000 |         1001.000 |                0.997 |
| pit_lane_or_safety_car_influenced      |             1 | Australian Grand Prix | ALB           |            2 | timing_session_boundary_artifact       |       nan     |           616 |                634 |               nan     |                 1.000 |                      594.839 |         1359.000 |              nan     |
| pit_lane_or_safety_car_influenced      |             1 | Australian Grand Prix | ALB           |            3 | timing_session_boundary_artifact       |       nan     |           601 |                631 |               nan     |                 1.002 |                      638.421 |         1280.000 |              nan     |
| pit_lane_or_safety_car_influenced      |             1 | Australian Grand Prix | ALB           |            4 | timing_session_boundary_artifact       |       nan     |           568 |                588 |               nan     |                 1.004 |                      449.662 |          961.000 |              nan     |
| pit_lane_or_safety_car_influenced      |             1 | Australian Grand Prix | ALB           |            5 | pit_lane_or_safety_car_influenced      |    142084.000 |           522 |                538 |                86.694 |                 1.004 |                      840.372 |         1200.000 |                0.998 |
| pit_lane_or_safety_car_influenced      |             1 | Australian Grand Prix | ALB           |           31 | pit_lane_or_safety_car_influenced      |     90053.000 |           340 |                345 |                 6.086 |                 0.997 |                      965.298 |         1241.000 |                0.997 |
| timing_session_boundary_artifact       |            22 | Las Vegas Grand Prix  | HUL           |           22 | timing_session_boundary_artifact       |     96180.000 |           347 |                359 |                 7.467 |                 1.001 |                     1183.856 |         2040.000 |                0.995 |
| timing_session_boundary_artifact       |            22 | Las Vegas Grand Prix  | HAD           |           22 | timing_session_boundary_artifact       |     96407.000 |           350 |                368 |                 6.708 |                 1.002 |                     1040.099 |         2040.000 |                0.998 |
| timing_session_boundary_artifact       |            22 | Las Vegas Grand Prix  | SAI           |           22 | timing_session_boundary_artifact       |    102420.000 |           373 |                386 |                69.987 |                 1.005 |                      938.077 |         2040.000 |                0.996 |
| timing_session_boundary_artifact       |            22 | Las Vegas Grand Prix  | PIA           |           22 | timing_session_boundary_artifact       |    111292.000 |           405 |                421 |               104.257 |                 1.002 |                     1371.185 |         2040.000 |                0.997 |
| timing_session_boundary_artifact       |            22 | Las Vegas Grand Prix  | TSU           |           22 | timing_session_boundary_artifact       |     98734.000 |           360 |                374 |                16.200 |                 0.999 |                      945.386 |         2040.000 |                0.997 |
| import_or_source_data_anomaly          |             1 | Australian Grand Prix | ALB           |            1 | shape_mismatch_against_comparable_laps |    132195.000 |           498 |                477 |                86.170 |                 1.012 |                      698.733 |         1080.000 |                1.001 |
| import_or_source_data_anomaly          |             1 | Australian Grand Prix | ALB           |            2 | timing_session_boundary_artifact       |       nan     |           616 |                634 |               nan     |                 1.000 |                      594.839 |         1359.000 |              nan     |
| import_or_source_data_anomaly          |             1 | Australian Grand Prix | ALB           |            3 | timing_session_boundary_artifact       |       nan     |           601 |                631 |               nan     |                 1.002 |                      638.421 |         1280.000 |              nan     |
| import_or_source_data_anomaly          |             1 | Australian Grand Prix | ALB           |            4 | timing_session_boundary_artifact       |       nan     |           568 |                588 |               nan     |                 1.004 |                      449.662 |          961.000 |              nan     |
| import_or_source_data_anomaly          |             1 | Australian Grand Prix | ALB           |            5 | pit_lane_or_safety_car_influenced      |    142084.000 |           522 |                538 |                86.694 |                 1.004 |                      840.372 |         1200.000 |                0.998 |
| unknown_needs_inspection               |             1 | Australian Grand Prix | GAS           |           45 | shape_mismatch_against_comparable_laps |     92969.000 |           355 |                371 |                35.527 |                 0.997 |                      403.610 |          920.000 |                0.997 |
| unknown_needs_inspection               |             1 | Australian Grand Prix | HAM           |           44 | shape_mismatch_against_comparable_laps |     89454.000 |           329 |                336 |                33.326 |                 1.004 |                      460.371 |         1201.000 |                0.996 |
| unknown_needs_inspection               |             1 | Australian Grand Prix | HAM           |           45 | shape_mismatch_against_comparable_laps |     93045.000 |           357 |                370 |                47.740 |                 0.999 |                      407.303 |          920.000 |                0.999 |
| unknown_needs_inspection               |             1 | Australian Grand Prix | HAM           |           46 | shape_mismatch_against_comparable_laps |     96714.000 |           360 |                364 |                50.005 |                 1.002 |                     1008.467 |         1001.000 |                0.993 |
| unknown_needs_inspection               |             1 | Australian Grand Prix | LAW           |           45 | shape_mismatch_against_comparable_laps |     97844.000 |           376 |                388 |                56.907 |                 0.996 |                      626.139 |          920.000 |                0.998 |

## Visual artifacts

- `artifacts/2025-telemetry-bad-lap-eda/figures/category_counts.svg`
- `artifacts/2025-telemetry-bad-lap-eda/figures/race_bad_lap_rates.svg`
- `artifacts/2025-telemetry-bad-lap-eda/figures/missingness_by_race.svg`
- `artifacts/2025-telemetry-bad-lap-eda/figures/shape_clusters.svg`
- `artifacts/2025-telemetry-bad-lap-eda/figures/representative_speed_shapes.svg`

## Cross-check with standalone Apexline geometry diagnostics

- Apexline inspected 26,689 2025 race laps and rejected 3,790 (14.2%).
- Apexline geometry shape-threshold rejects: 103.
- This notebook uses the imported DB surface for all-lap telemetry/timing/position quality and uses Apexline as a geometry-reference cross-check.

## Limitations

- Raw FastF1 `Distance` is not imported, so distance reset/non-monotonic checks are marked unavailable.
- Speed-profile shape outliers are exploratory and should be reviewed against video/race context before treating them as source-data faults.
- Safety-car and flag context uses imported status periods and race-control messages; ambiguous local yellows may need sector-level review.
- Geometry-reference diagnostics from Apexline are event-level plus selected worst-lap examples unless that standalone pipeline is extended to persist every per-lap record.

## Recommended next analyses

- Persist per-lap geometry diagnostics from Apexline so GPS-shape categories can be grouped by race, driver, and lap without rerunning FastF1.
- Add importer support for raw/derived distance if distance reset checks become a first-class quality gate.
- Add focused real-database Query API tests around bad-lap and sparse-window behavior before exposing these quality flags in API contracts.
