# 2025 race database-surface EDA summary

Scope: `2025` race sessions (`session_type = 'R'`) in the local TimescaleDB.

Guardrail: this summary is generated only when exactly 24 2025 race sessions are present.

## Data availability

- Race sessions inspected: 24
- Sessions with at least one surface quality flag: 24 (100.0%)
- Raw telemetry samples: 9,113,457
- Raw position samples: 9,314,091
- Aligned 10 Hz samples: 24,477,559
- Aligned rows without `OK`: 458,158 (1.87%)
- Ingestion diagnostic streams with warnings: 945
- Skrub report: `artifacts/2025-race-database-surface-eda/skrub_2025_race_database_surface_report.html`

## Year summary

|    year |   sessions |   sessions_with_issue |   median_surface_issue_count |   telemetry_samples |   position_samples |   aligned_samples |   weather_samples |   race_control_messages |   issue_pct |
|--------:|-----------:|----------------------:|-----------------------------:|--------------------:|-------------------:|------------------:|------------------:|------------------------:|------------:|
| 2025.00 |      24.00 |                 24.00 |                         2.00 |          9113457.00 |         9314091.00 |       24477559.00 |           3781.00 |                 2178.00 |      100.00 |

## Surface quality flags

| surface_flag                   |   sessions |   pct_sessions |
|:-------------------------------|-----------:|---------------:|
| ingestion_diagnostic_warning   |         24 |         100.00 |
| lap_metadata_incomplete        |         18 |          75.00 |
| race_control_sparse_or_untimed |         14 |          58.33 |
| aligned_replay_quality_issue   |          0 |           0.00 |
| circuit_annotation_issue       |          0 |           0.00 |
| driver_metadata_sparse         |          0 |           0.00 |
| raw_position_coverage_issue    |          0 |           0.00 |
| raw_telemetry_coverage_issue   |          0 |           0.00 |
| session_metadata_incomplete    |          0 |           0.00 |
| status_timeline_sparse         |          0 |           0.00 |
| weather_surface_issue          |          0 |           0.00 |

## Highest-issue sessions

|   year | event_name                |   surface_issue_count |   driver_count |   lap_rows |   telemetry_driver_count |   position_driver_count |   aligned_driver_count |   weather_samples |   race_control_messages |   corner_markers |
|-------:|:--------------------------|----------------------:|---------------:|-----------:|-------------------------:|------------------------:|-----------------------:|------------------:|------------------------:|-----------------:|
|   2025 | Australian Grand Prix     |                     3 |             20 |        927 |                       20 |                      20 |                     20 |               178 |                     113 |               14 |
|   2025 | Austrian Grand Prix       |                     3 |             20 |       1126 |                       19 |                      19 |                     19 |               161 |                     117 |               10 |
|   2025 | Azerbaijan Grand Prix     |                     3 |             20 |        968 |                       20 |                      20 |                     20 |               156 |                      71 |               20 |
|   2025 | Belgian Grand Prix        |                     3 |             20 |        879 |                       20 |                      20 |                     20 |               223 |                     116 |               19 |
|   2025 | British Grand Prix        |                     3 |             20 |        825 |                       19 |                      19 |                     19 |               155 |                     158 |               18 |
|   2025 | Dutch Grand Prix          |                     3 |             20 |       1364 |                       20 |                      20 |                     20 |               159 |                     108 |               14 |
|   2025 | Las Vegas Grand Prix      |                     3 |             20 |        886 |                       20 |                      20 |                     20 |               141 |                      61 |               17 |
|   2025 | Miami Grand Prix          |                     3 |             20 |       1005 |                       20 |                      20 |                     20 |               149 |                      77 |               19 |
|   2025 | São Paulo Grand Prix      |                     3 |             20 |       1251 |                       20 |                      20 |                     20 |               152 |                      56 |               15 |
|   2025 | Bahrain Grand Prix        |                     2 |             20 |       1128 |                       20 |                      20 |                     20 |               158 |                      91 |               15 |
|   2025 | Canadian Grand Prix       |                     2 |             20 |       1349 |                       20 |                      20 |                     20 |               167 |                      97 |               14 |
|   2025 | Chinese Grand Prix        |                     2 |             20 |       1065 |                       20 |                      20 |                     20 |               154 |                      56 |               16 |
|   2025 | Emilia Romagna Grand Prix |                     2 |             20 |       1207 |                       20 |                      20 |                     20 |               150 |                      44 |               19 |
|   2025 | Hungarian Grand Prix      |                     2 |             20 |       1368 |                       20 |                      20 |                     20 |               157 |                      96 |               16 |
|   2025 | Italian Grand Prix        |                     2 |             20 |        974 |                       19 |                      19 |                     19 |               144 |                      71 |               11 |

## Top aligned quality flags

| quality_flag            |     rows |
|:------------------------|---------:|
| OK                      | 24019401 |
| CAR_GAP_TOO_LARGE       |   262141 |
| LOCATION_GAP_TOO_LARGE  |   231468 |
| CAR_SAMPLE_TOO_OLD      |   140883 |
| LOCATION_SAMPLE_TOO_OLD |   125959 |

## Aligned replay quality by race

| event_name             |   aligned_rows |   non_ok_rows |   non_ok_pct |   car_related_pct_of_non_ok |   location_related_pct_of_non_ok |
|:-----------------------|---------------:|--------------:|-------------:|----------------------------:|---------------------------------:|
| Dutch Grand Prix       |        1119107 |         31300 |         2.80 |                       56.59 |                            44.49 |
| Austrian Grand Prix    |         821801 |         22295 |         2.71 |                       55.56 |                            44.97 |
| British Grand Prix     |         943107 |         23768 |         2.52 |                       51.68 |                            48.71 |
| Hungarian Grand Prix   |        1134521 |         27436 |         2.42 |                       54.75 |                            45.40 |
| Belgian Grand Prix     |        1035545 |         24193 |         2.34 |                       49.06 |                            52.09 |
| Monaco Grand Prix      |        1131575 |         23043 |         2.04 |                       75.68 |                            65.31 |
| Mexico City Grand Prix |        1059385 |         20890 |         1.97 |                       81.31 |                            60.51 |
| Italian Grand Prix     |         820417 |         15478 |         1.89 |                       74.60 |                            68.50 |
| Abu Dhabi Grand Prix   |        1043228 |         19164 |         1.84 |                       68.11 |                            73.14 |
| Miami Grand Prix       |         955079 |         17542 |         1.84 |                       71.63 |                            67.87 |
| Canadian Grand Prix    |        1069986 |         19435 |         1.82 |                       73.48 |                            72.32 |
| Qatar Grand Prix       |         964250 |         17152 |         1.78 |                       70.76 |                            69.79 |

## Highest-risk replay driver pairs

| event_name          | driver_code   |   non_ok_pct |   max_window_non_ok_pct |   longest_segment_ms |   degraded_windows |   severe_windows | desktop_guidance            |
|:--------------------|:--------------|-------------:|------------------------:|---------------------:|-------------------:|-----------------:|:----------------------------|
| Italian Grand Prix  | NOR           |         1.90 |                   37.14 |                 1400 |                 62 |                1 | diagnostics_only            |
| Austrian Grand Prix | LAW           |         2.72 |                   30.77 |                 1500 |                111 |                1 | diagnostics_only            |
| Spanish Grand Prix  | ALO           |         1.44 |                   28.00 |                 1400 |                 70 |                1 | diagnostics_only            |
| Italian Grand Prix  | PIA           |         1.90 |                   23.21 |                 1400 |                 62 |                1 | diagnostics_only            |
| Dutch Grand Prix    | STR           |         2.82 |                   12.00 |                 2000 |                123 |                3 | show_replay_quality_overlay |
| Dutch Grand Prix    | ALO           |         2.82 |                   12.00 |                 2000 |                123 |                3 | show_replay_quality_overlay |
| Dutch Grand Prix    | TSU           |         2.81 |                   12.00 |                 2000 |                123 |                3 | show_replay_quality_overlay |
| Dutch Grand Prix    | OCO           |         2.81 |                   12.00 |                 2000 |                123 |                3 | show_replay_quality_overlay |
| Dutch Grand Prix    | COL           |         2.81 |                   12.00 |                 2000 |                123 |                3 | show_replay_quality_overlay |
| Dutch Grand Prix    | LAW           |         2.81 |                   12.00 |                 2000 |                123 |                3 | show_replay_quality_overlay |
| Dutch Grand Prix    | SAI           |         2.81 |                   12.00 |                 2000 |                123 |                3 | show_replay_quality_overlay |
| Dutch Grand Prix    | HUL           |         2.81 |                   12.00 |                 2000 |                123 |                3 | show_replay_quality_overlay |
| Dutch Grand Prix    | BOR           |         2.81 |                   12.00 |                 2000 |                123 |                3 | show_replay_quality_overlay |
| Dutch Grand Prix    | GAS           |         2.81 |                   12.00 |                 2000 |                123 |                3 | show_replay_quality_overlay |
| Dutch Grand Prix    | PIA           |         2.80 |                   12.00 |                 2000 |                122 |                3 | show_replay_quality_overlay |

## Aligned replay window/context overlap

| window_bucket    |   windows |   pct_with_race_control |   pct_with_status |   pct_with_pit_context |   pct_with_incident_context |   median_non_ok_pct |   p95_non_ok_pct |
|:-----------------|----------:|------------------------:|------------------:|-----------------------:|----------------------------:|--------------------:|-----------------:|
| all_windows      |     82056 |                   13.96 |             11.52 |                   8.17 |                       21.81 |                0.67 |             5.33 |
| degraded_windows |     37793 |                   14.33 |             13.00 |                   8.63 |                       23.72 |                4.00 |             7.67 |
| severe_windows   |       133 |                    0.00 |             27.82 |                   6.77 |                       27.82 |               11.33 |            13.43 |

## Lap context and aligned replay quality

| lap_bucket             |   laps |   laps_with_any_non_ok |   laps_with_severe_non_ok |   median_non_ok_pct |   p95_non_ok_pct |   non_ok_rows |
|:-----------------------|-------:|-----------------------:|--------------------------:|--------------------:|-----------------:|--------------:|
| all_laps               |  26689 |                  25759 |                         0 |                1.66 |             4.24 |        458158 |
| pit_laps               |   1625 |                   1583 |                         0 |                1.84 |             4.25 |         35474 |
| fastf1_inaccurate_laps |   3646 |                   3577 |                         0 |                1.94 |             4.31 |         85375 |
| missing_lap_time_laps  |    352 |                    352 |                         0 |                2.20 |             4.37 |         13057 |
| ordinary_laps          |  23043 |                  22182 |                         0 |                1.63 |             4.23 |        372783 |

## Session duration and surface coverage

| event_name                |   derived_session_duration_ms | duration_source         |   active_replay_start_ms |   active_replay_end_ms |   active_replay_duration_ms |   finished_to_derived_end_gap_ms |
|:--------------------------|------------------------------:|:------------------------|-------------------------:|-----------------------:|----------------------------:|---------------------------------:|
| Australian Grand Prix     |                      10632748 | session_status_finished |                  4260581 |               10426981 |                     6166400 |                                0 |
| Chinese Grand Prix        |                       9243467 | session_status_finished |                  3543065 |                9079065 |                     5536000 |                                0 |
| Japanese Grand Prix       |                       8495802 | session_status_finished |                  3366970 |                8377670 |                     5010700 |                                0 |
| Bahrain Grand Prix        |                       9913997 | session_status_finished |                  3334933 |                9141033 |                     5806100 |                                0 |
| Saudi Arabian Grand Prix  |                       8490361 | session_status_finished |                  3417386 |                8383386 |                     4966000 |                                0 |
| Miami Grand Prix          |                       8957990 | session_status_finished |                  3435459 |                8857359 |                     5421900 |                                0 |
| Emilia Romagna Grand Prix |                       9001103 | session_status_finished |                  3359385 |                8891085 |                     5531700 |                                0 |
| Monaco Grand Prix         |                       9560014 | session_status_finished |                  3368914 |                9474614 |                     6105700 |                                0 |
| Spanish Grand Prix        |                       9494399 | session_status_finished |                  3553802 |                9163002 |                     5609200 |                                0 |
| Canadian Grand Prix       |                      10394423 | session_status_finished |                  3479645 |                9007545 |                     5527900 |                                0 |
| Austrian Grand Prix       |                       9680006 | session_status_finished |                  4268607 |                9363807 |                     5095200 |                                0 |
| British Grand Prix        |                       9434243 | session_status_finished |                  3369287 |                9289287 |                     5920000 |                                0 |
| Belgian Grand Prix        |                      13458265 | session_status_finished |                  8072330 |               13289930 |                     5217600 |                                0 |
| Hungarian Grand Prix      |                       9412940 | session_status_finished |                  3468106 |                9261406 |                     5793300 |                                0 |
| Dutch Grand Prix          |                      10002761 | session_status_finished |                  3394287 |                9327487 |                     5933200 |                                0 |
| Italian Grand Prix        |                       8652942 | session_status_finished |                  3470087 |                7956587 |                     4486500 |                                0 |
| Azerbaijan Grand Prix     |                       9389504 | session_status_finished |                  3447717 |                9150217 |                     5702500 |                                0 |
| Singapore Grand Prix      |                       9642852 | session_status_finished |                  3297937 |                9413637 |                     6115700 |                                0 |
| United States Grand Prix  |                       9289288 | session_status_finished |                  3404707 |                9137207 |                     5732500 |                                0 |
| Mexico City Grand Prix    |                       9707743 | session_status_finished |                  3427825 |                9384725 |                     5956900 |                                0 |
| São Paulo Grand Prix      |                       9158888 | session_status_finished |                  3414801 |                9005301 |                     5590500 |                                0 |
| Las Vegas Grand Prix      |                       8571060 | session_status_finished |                  3462049 |                8421749 |                     4959700 |                                0 |
| Qatar Grand Prix          |                       8985803 | session_status_finished |                  3521625 |                8684625 |                     5163000 |                                0 |
| Abu Dhabi Grand Prix      |                       9280200 | session_status_finished |                  3501037 |                8753137 |                     5252100 |                                0 |

## Surface coverage over active replay windows

| surface        |   sessions |   median_coverage_ratio |   median_active_coverage_ratio |   min_active_coverage_ratio |   sessions_starting_after_active |   sessions_ending_before_active_end |
|:---------------|-----------:|------------------------:|-------------------------------:|----------------------------:|---------------------------------:|------------------------------------:|
| raw telemetry  |         24 |                    0.59 |                           1.00 |                        1.00 |                                0 |                                   0 |
| raw position   |         24 |                    0.59 |                           0.42 |                        0.27 |                                0 |                                  24 |
| aligned replay |         24 |                    0.59 |                           1.00 |                        1.00 |                                0 |                                   0 |
| weather        |         24 |                    0.99 |                           1.00 |                        1.00 |                                0 |                                   0 |
| track status   |         24 |                    0.74 |                           0.62 |                        0.00 |                                0 |                                  20 |
| session status |         24 |                    1.00 |                           1.00 |                        1.00 |                                0 |                                   0 |
| race control   |         24 |                    0.63 |                           0.45 |                        0.32 |                                0 |                                  24 |

## Top race-control categories

| category   |   messages |
|:-----------|-----------:|
| Flag       |       1080 |
| Other      |        971 |
| Drs        |         67 |
| SafetyCar  |         60 |

## Race-control taxonomy

| taxonomy             |   messages |   missing_session_time |   missing_lap_scope |   missing_driver_scope |   driver_scoped |   lap_scoped |   sessions |   missing_session_time_pct |   missing_lap_scope_pct |   missing_driver_scope_pct |   driver_scoped_pct |   lap_scoped_pct |
|:---------------------|-----------:|-----------------------:|--------------------:|-----------------------:|----------------:|-------------:|-----------:|---------------------------:|------------------------:|---------------------------:|--------------------:|-----------------:|
| flags                |       1008 |                    146 |                   0 |                    501 |             507 |         1008 |         24 |                      14.48 |                    0.00 |                      49.70 |               50.30 |           100.00 |
| other                |        431 |                     47 |                   0 |                    431 |               0 |          431 |         24 |                      10.90 |                    0.00 |                     100.00 |                0.00 |           100.00 |
| investigations_noted |        318 |                     25 |                   0 |                    318 |               0 |          318 |         23 |                       7.86 |                    0.00 |                     100.00 |                0.00 |           100.00 |
| drs                  |        139 |                     68 |                   0 |                    139 |               0 |          139 |         24 |                      48.92 |                    0.00 |                     100.00 |                0.00 |           100.00 |
| safety_car           |         92 |                      2 |                   0 |                     92 |               0 |           92 |         18 |                       2.17 |                    0.00 |                     100.00 |                0.00 |           100.00 |
| pit_entry_exit       |         89 |                     48 |                   0 |                     89 |               0 |           89 |         24 |                      53.93 |                    0.00 |                     100.00 |                0.00 |           100.00 |
| penalties            |         76 |                      0 |                   0 |                     76 |               0 |           76 |         19 |                       0.00 |                    0.00 |                     100.00 |                0.00 |           100.00 |
| red_flag             |         25 |                      0 |                   0 |                     25 |               0 |           25 |         24 |                       0.00 |                    0.00 |                     100.00 |                0.00 |           100.00 |

## Race-control duplicate groups

| event_name            |   messages |   span_ms | taxonomy   | example_message                                    |
|:----------------------|-----------:|----------:|:-----------|:---------------------------------------------------|
| Belgian Grand Prix    |         39 |   6810000 | flags      | CLEAR IN TRACK SECTOR 4                            |
| British Grand Prix    |         37 |   5195000 | flags      | CLEAR IN TRACK SECTOR 5                            |
| British Grand Prix    |         32 |   5028000 | flags      | YELLOW IN TRACK SECTOR 5                           |
| Belgian Grand Prix    |         22 |   6793000 | flags      | DOUBLE YELLOW IN TRACK SECTOR 4                    |
| Australian Grand Prix |         20 |   6103000 | flags      | CLEAR IN TRACK SECTOR 2                            |
| Monaco Grand Prix     |         18 |   2872000 | flags      | WAVED BLUE FLAG FOR CAR 27 (HUL) TIMED AT 15:40:23 |
| Dutch Grand Prix      |         18 |   4903000 | flags      | DOUBLE YELLOW IN TRACK SECTOR 5                    |
| Monaco Grand Prix     |         15 |   3335000 | flags      | WAVED BLUE FLAG FOR CAR 87 (BEA) TIMED AT 15:37:55 |
| Azerbaijan Grand Prix |         14 |   5539000 | flags      | CLEAR IN TRACK SECTOR 6                            |
| Azerbaijan Grand Prix |         14 |   5860000 | flags      | DOUBLE YELLOW IN TRACK SECTOR 7                    |
| Australian Grand Prix |         14 |   6001000 | flags      | DOUBLE YELLOW IN TRACK SECTOR 2                    |
| Monaco Grand Prix     |         13 |   2162000 | flags      | WAVED BLUE FLAG FOR CAR 18 (STR) TIMED AT 15:38:50 |
| Monaco Grand Prix     |         13 |   2933000 | flags      | WAVED BLUE FLAG FOR CAR 5 (BOR) TIMED AT 15:40:39  |
| Miami Grand Prix      |         12 |    288000 | flags      | CLEAR IN TRACK SECTOR 2                            |
| Miami Grand Prix      |         12 |    295000 | flags      | DOUBLE YELLOW IN TRACK SECTOR 2                    |

## Status/race-control overlap

| event_name                | status_label   |   start_ms |   end_ms |   duration_ms |   race_control_messages |   incident_messages | taxonomies                                                                        |
|:--------------------------|:---------------|-----------:|---------:|--------------:|------------------------:|--------------------:|:----------------------------------------------------------------------------------|
| Spanish Grand Prix        | clear          |     712805 |  7962315 |       7249510 |                      92 |                  78 | drs,flags,investigations_noted,other,penalties,red_flag,safety_car                |
| Canadian Grand Prix       | clear          |     392909 |  7158892 |       6765983 |                      89 |                  75 | drs,flags,investigations_noted,other,penalties,pit_entry_exit,red_flag,safety_car |
| Singapore Grand Prix      | clear          |    3982255 |  7613749 |       3631494 |                      87 |                  64 | drs,flags,investigations_noted,other,pit_entry_exit,red_flag                      |
| Monaco Grand Prix         | clear          |    1475108 |  3420403 |       1945295 |                      66 |                  64 | flags,other                                                                       |
| Monaco Grand Prix         | clear          |    4298677 |  6354608 |       2055931 |                      70 |                  63 | flags,investigations_noted,other,penalties,red_flag                               |
| Hungarian Grand Prix      | clear          |    2547920 |  9412940 |       6865020 |                      66 |                  56 | flags,investigations_noted,other,penalties,pit_entry_exit,red_flag                |
| Belgian Grand Prix        | red_flag       |    3390244 |  6562639 |       3172395 |                      62 |                  49 | drs,flags,other,pit_entry_exit,safety_car                                         |
| Austrian Grand Prix       | clear          |    4569488 |  6509248 |       1939760 |                      54 |                  46 | drs,flags,investigations_noted,other,penalties,pit_entry_exit,red_flag            |
| Bahrain Grand Prix        | clear          |          0 |  6475160 |       6475160 |                      83 |                  39 | drs,flags,investigations_noted,other,penalties,pit_entry_exit,red_flag,safety_car |
| Australian Grand Prix     | clear          |    5270163 |  7646655 |       2376492 |                      46 |                  37 | drs,flags,investigations_noted,other,penalties,red_flag,safety_car                |
| Abu Dhabi Grand Prix      | clear          |    2812088 |  9280200 |       6468112 |                      51 |                  32 | flags,investigations_noted,other,penalties,pit_entry_exit,red_flag                |
| Qatar Grand Prix          | clear          |          0 |  4079807 |       4079807 |                      50 |                  32 | drs,flags,investigations_noted,other,penalties,pit_entry_exit,safety_car          |
| Emilia Romagna Grand Prix | clear          |          0 |  5677627 |       5677627 |                      38 |                  30 | drs,flags,investigations_noted,other,pit_entry_exit,safety_car                    |
| Mexico City Grand Prix    | clear          |    3884818 |  9125543 |       5240725 |                      34 |                  29 | drs,flags,investigations_noted,other,penalties,pit_entry_exit,red_flag,safety_car |
| São Paulo Grand Prix      | clear          |          0 |  2792143 |       2792143 |                      35 |                  28 | drs,flags,investigations_noted,other,penalties,pit_entry_exit,safety_car          |
| Dutch Grand Prix          | clear          |    2669238 |  5099711 |       2430473 |                      34 |                  28 | drs,flags,investigations_noted,other,penalties,safety_car                         |
| Saudi Arabian Grand Prix  | clear          |       8569 |  3446539 |       3437970 |                      41 |                  24 | drs,flags,investigations_noted,other,penalties,pit_entry_exit,safety_car          |
| Mexico City Grand Prix    | clear          |          0 |  3639516 |       3639516 |                      39 |                  24 | drs,flags,investigations_noted,other,penalties,pit_entry_exit                     |
| Dutch Grand Prix          | yellow         |     503726 |  2669238 |       2165512 |                      30 |                  24 | drs,flags,investigations_noted,other,safety_car                                   |
| Abu Dhabi Grand Prix      | clear          |     376112 |  2799231 |       2423119 |                      45 |                  23 | flags,investigations_noted,other,penalties                                        |

## Weather cadence and jumps

| event_name            |   samples |   median_gap_ms |   max_gap_ms |   max_track_temp_delta_c |   max_humidity_delta_pct |   rainfall_samples |   rainfall_transitions | large_gap_flag   | temperature_jump_flag   | wind_jump_flag   |
|:----------------------|----------:|----------------:|-------------:|-------------------------:|-------------------------:|-------------------:|-----------------------:|:-----------------|:------------------------|:-----------------|
| Australian Grand Prix |       178 |        60004.00 |     60092.00 |                     0.50 |                     6.00 |                 58 |                     17 | False            | False                   | True             |
| Belgian Grand Prix    |       223 |        60005.00 |     60200.00 |                     1.10 |                     4.00 |                102 |                     12 | False            | False                   | True             |
| British Grand Prix    |       155 |        60003.00 |     60220.00 |                     3.30 |                     4.00 |                 28 |                      4 | False            | False                   | True             |
| Miami Grand Prix      |       149 |        60006.00 |     60046.00 |                     2.80 |                     4.00 |                  5 |                      4 | False            | False                   | True             |
| Singapore Grand Prix  |       161 |        60007.50 |     60040.00 |                     4.20 |                     6.00 |                  6 |                      4 | False            | False                   | True             |
| Canadian Grand Prix   |       167 |        60001.00 |     60032.00 |                     0.90 |                     4.00 |                  1 |                      2 | False            | False                   | True             |
| Abu Dhabi Grand Prix  |       154 |        60003.00 |     60224.00 |                     1.00 |                     1.00 |                  0 |                      0 | False            | False                   | True             |
| Las Vegas Grand Prix  |       141 |        60003.00 |     60140.00 |                     5.90 |                     1.00 |                  0 |                      0 | False            | False                   | True             |
| Bahrain Grand Prix    |       158 |        60004.00 |     60137.00 |                     1.80 |                     1.00 |                  0 |                      0 | False            | False                   | True             |
| São Paulo Grand Prix  |       152 |        60003.00 |     60127.00 |                     0.60 |                     2.00 |                  0 |                      0 | False            | False                   | True             |
| Japanese Grand Prix   |       140 |        60001.00 |     60082.00 |                     0.70 |                     2.00 |                  0 |                      0 | False            | False                   | True             |
| Spanish Grand Prix    |       155 |        60003.00 |     60082.00 |                     1.40 |                     2.00 |                  0 |                      0 | False            | False                   | True             |
| Austrian Grand Prix   |       161 |        60002.00 |     60081.00 |                     2.00 |                     2.00 |                  0 |                      0 | False            | False                   | True             |
| Azerbaijan Grand Prix |       156 |        60001.00 |     60072.00 |                     0.80 |                     2.00 |                  0 |                      0 | False            | False                   | True             |
| Chinese Grand Prix    |       154 |        60003.00 |     60066.00 |                     1.80 |                     1.00 |                  0 |                      0 | False            | False                   | True             |

## Context timeline/replay correlation

| bin_bucket                 |   bins |   median_degraded_window_rate |   p95_degraded_window_rate |   median_max_non_ok_pct |   p95_max_non_ok_pct |   severe_driver_windows |
|:---------------------------|-------:|------------------------------:|---------------------------:|------------------------:|---------------------:|------------------------:|
| all_bins                   |    466 |                         50.00 |                      78.52 |                    4.67 |                 9.00 |                     133 |
| context_event_bins         |    225 |                         50.00 |                      80.00 |                    5.00 |                 9.00 |                      97 |
| no_context_event_bins      |    241 |                         40.88 |                      70.00 |                    4.67 |                 9.00 |                      36 |
| race_control_incident_bins |    168 |                         50.00 |                      80.00 |                    5.00 |                 8.67 |                      58 |
| status_bins                |     96 |                         50.00 |                      82.50 |                    5.00 |                 9.83 |                      78 |
| rainfall_transition_bins   |     15 |                         40.00 |                      72.00 |                    6.67 |                 8.97 |                       0 |

## Product-readiness recommendations

| final_recommendation   |   sessions |   max_readiness_score |   affected_drivers |   affected_rows |   severe_replay_windows |   marker_coordinate_issues |   schema_importer_follow_up_sessions |
|:-----------------------|-----------:|----------------------:|-------------------:|----------------:|------------------------:|---------------------------:|-------------------------------------:|
| no_action              |          0 |                     0 |                  0 |               0 |                       0 |                          0 |                                    0 |
| label_in_ui            |         24 |                     2 |                476 |          458158 |                     133 |                          0 |                                   24 |
| inspect                |          0 |                     0 |                  0 |               0 |                       0 |                          0 |                                    0 |
| reimport               |          0 |                     0 |                  0 |               0 |                       0 |                          0 |                                    0 |
| schema_importer_change |          0 |                     0 |                  0 |               0 |                       0 |                          0 |                                    0 |

| event_name                | catalog_readiness   | raw_stream_readiness   | replay_readiness    | context_readiness   | circuit_context_readiness   | final_recommendation   | schema_importer_follow_up   | product_impact                                                | systematic_known_limitations                                             |
|:--------------------------|:--------------------|:-----------------------|:--------------------|:--------------------|:----------------------------|:-----------------------|:----------------------------|:--------------------------------------------------------------|:-------------------------------------------------------------------------|
| Australian Grand Prix     | ready_with_warnings | ready_with_warnings    | ready_with_warnings | ready_with_warnings | ready                       | label_in_ui            | True                        | launcher; bounded raw API/MCP; desktop replay; context panels | session_end_utc missing; position coverage approximated from UTC offsets |
| Chinese Grand Prix        | ready_with_warnings | ready_with_warnings    | ready_with_warnings | ready_with_warnings | ready                       | label_in_ui            | True                        | launcher; bounded raw API/MCP; desktop replay; context panels | session_end_utc missing; position coverage approximated from UTC offsets |
| Japanese Grand Prix       | ready_with_warnings | ready_with_warnings    | ready_with_warnings | ready_with_warnings | ready                       | label_in_ui            | True                        | launcher; bounded raw API/MCP; desktop replay; context panels | session_end_utc missing; position coverage approximated from UTC offsets |
| Bahrain Grand Prix        | ready_with_warnings | ready_with_warnings    | ready_with_warnings | ready               | ready                       | label_in_ui            | True                        | launcher; bounded raw API/MCP; desktop replay                 | session_end_utc missing; position coverage approximated from UTC offsets |
| Saudi Arabian Grand Prix  | ready_with_warnings | ready_with_warnings    | partial             | ready               | ready                       | label_in_ui            | True                        | launcher; bounded raw API/MCP; desktop replay                 | session_end_utc missing; position coverage approximated from UTC offsets |
| Miami Grand Prix          | ready_with_warnings | ready_with_warnings    | partial             | ready_with_warnings | ready                       | label_in_ui            | True                        | launcher; bounded raw API/MCP; desktop replay; context panels | session_end_utc missing; position coverage approximated from UTC offsets |
| Emilia Romagna Grand Prix | ready_with_warnings | ready_with_warnings    | ready_with_warnings | ready               | ready                       | label_in_ui            | True                        | launcher; bounded raw API/MCP; desktop replay                 | session_end_utc missing; position coverage approximated from UTC offsets |
| Monaco Grand Prix         | ready_with_warnings | ready_with_warnings    | ready_with_warnings | ready               | ready                       | label_in_ui            | True                        | launcher; bounded raw API/MCP; desktop replay                 | session_end_utc missing; position coverage approximated from UTC offsets |
| Spanish Grand Prix        | ready_with_warnings | ready_with_warnings    | ready_with_warnings | ready               | ready                       | label_in_ui            | True                        | launcher; bounded raw API/MCP; desktop replay                 | session_end_utc missing; position coverage approximated from UTC offsets |
| Canadian Grand Prix       | ready_with_warnings | ready_with_warnings    | ready_with_warnings | ready               | ready                       | label_in_ui            | True                        | launcher; bounded raw API/MCP; desktop replay                 | session_end_utc missing; position coverage approximated from UTC offsets |
| Austrian Grand Prix       | ready_with_warnings | ready_with_warnings    | ready_with_warnings | ready_with_warnings | ready                       | label_in_ui            | True                        | launcher; bounded raw API/MCP; desktop replay; context panels | session_end_utc missing; position coverage approximated from UTC offsets |
| British Grand Prix        | ready_with_warnings | ready_with_warnings    | partial             | ready_with_warnings | ready                       | label_in_ui            | True                        | launcher; bounded raw API/MCP; desktop replay; context panels | session_end_utc missing; position coverage approximated from UTC offsets |
| Belgian Grand Prix        | ready_with_warnings | ready_with_warnings    | ready_with_warnings | ready_with_warnings | ready                       | label_in_ui            | True                        | launcher; bounded raw API/MCP; desktop replay; context panels | session_end_utc missing; position coverage approximated from UTC offsets |
| Hungarian Grand Prix      | ready_with_warnings | ready_with_warnings    | ready_with_warnings | ready_with_warnings | ready                       | label_in_ui            | True                        | launcher; bounded raw API/MCP; desktop replay; context panels | session_end_utc missing; position coverage approximated from UTC offsets |
| Dutch Grand Prix          | ready_with_warnings | ready_with_warnings    | partial             | ready_with_warnings | ready                       | label_in_ui            | True                        | launcher; bounded raw API/MCP; desktop replay; context panels | session_end_utc missing; position coverage approximated from UTC offsets |
| Italian Grand Prix        | ready_with_warnings | ready_with_warnings    | ready_with_warnings | ready_with_warnings | ready                       | label_in_ui            | True                        | launcher; bounded raw API/MCP; desktop replay; context panels | session_end_utc missing; position coverage approximated from UTC offsets |
| Azerbaijan Grand Prix     | ready_with_warnings | ready_with_warnings    | ready_with_warnings | ready_with_warnings | ready                       | label_in_ui            | True                        | launcher; bounded raw API/MCP; desktop replay; context panels | session_end_utc missing; position coverage approximated from UTC offsets |
| Singapore Grand Prix      | ready_with_warnings | ready_with_warnings    | ready_with_warnings | ready_with_warnings | ready                       | label_in_ui            | True                        | launcher; bounded raw API/MCP; desktop replay; context panels | session_end_utc missing; position coverage approximated from UTC offsets |
| United States Grand Prix  | ready_with_warnings | ready_with_warnings    | ready_with_warnings | ready               | ready                       | label_in_ui            | True                        | launcher; bounded raw API/MCP; desktop replay                 | session_end_utc missing; position coverage approximated from UTC offsets |
| Mexico City Grand Prix    | ready_with_warnings | ready_with_warnings    | ready_with_warnings | ready               | ready                       | label_in_ui            | True                        | launcher; bounded raw API/MCP; desktop replay                 | session_end_utc missing; position coverage approximated from UTC offsets |
| São Paulo Grand Prix      | ready_with_warnings | ready_with_warnings    | ready_with_warnings | ready_with_warnings | ready                       | label_in_ui            | True                        | launcher; bounded raw API/MCP; desktop replay; context panels | session_end_utc missing; position coverage approximated from UTC offsets |
| Las Vegas Grand Prix      | ready_with_warnings | ready_with_warnings    | ready_with_warnings | ready_with_warnings | ready                       | label_in_ui            | True                        | launcher; bounded raw API/MCP; desktop replay; context panels | session_end_utc missing; position coverage approximated from UTC offsets |
| Qatar Grand Prix          | ready_with_warnings | ready_with_warnings    | ready_with_warnings | ready               | ready                       | label_in_ui            | True                        | launcher; bounded raw API/MCP; desktop replay                 | session_end_utc missing; position coverage approximated from UTC offsets |
| Abu Dhabi Grand Prix      | ready_with_warnings | ready_with_warnings    | ready_with_warnings | ready               | ready                       | label_in_ui            | True                        | launcher; bounded raw API/MCP; desktop replay                 | session_end_utc missing; position coverage approximated from UTC offsets |

## Circuit marker geometry QA

| event_name                |   marker_count |   corner_markers |   marshal_light_markers |   marshal_sector_markers |   marker_distance_nulls |   markers_outside_position_bounds |   markers_outside_core_bounds | circuit_context_readiness   | circuit_context_recommendation   |
|:--------------------------|---------------:|-----------------:|------------------------:|-------------------------:|------------------------:|----------------------------------:|------------------------------:|:----------------------------|:---------------------------------|
| Australian Grand Prix     |             54 |               14 |                      20 |                       20 |                       0 |                                 0 |                             0 | ready                       | no_action                        |
| Chinese Grand Prix        |             56 |               16 |                      20 |                       20 |                       0 |                                 0 |                             0 | ready                       | no_action                        |
| Japanese Grand Prix       |             62 |               18 |                      22 |                       22 |                       0 |                                 0 |                             0 | ready                       | no_action                        |
| Bahrain Grand Prix        |             51 |               15 |                      18 |                       18 |                       0 |                                 0 |                             0 | ready                       | no_action                        |
| Saudi Arabian Grand Prix  |             69 |               27 |                      21 |                       21 |                       0 |                                 0 |                             0 | ready                       | no_action                        |
| Miami Grand Prix          |             58 |               19 |                      19 |                       20 |                       0 |                                 0 |                             0 | ready                       | no_action                        |
| Emilia Romagna Grand Prix |             57 |               19 |                      19 |                       19 |                       0 |                                 0 |                             0 | ready                       | no_action                        |
| Monaco Grand Prix         |             57 |               19 |                      19 |                       19 |                       0 |                                 0 |                             0 | ready                       | no_action                        |
| Spanish Grand Prix        |             46 |               14 |                      16 |                       16 |                       0 |                                 0 |                             0 | ready                       | no_action                        |
| Canadian Grand Prix       |             44 |               14 |                      15 |                       15 |                       0 |                                 0 |                             0 | ready                       | no_action                        |
| Austrian Grand Prix       |             42 |               10 |                      16 |                       16 |                       0 |                                 0 |                             0 | ready                       | no_action                        |
| British Grand Prix        |             51 |               18 |                      17 |                       16 |                       0 |                                 0 |                             0 | ready                       | no_action                        |
| Belgian Grand Prix        |             61 |               19 |                      21 |                       21 |                       0 |                                 0 |                             0 | ready                       | no_action                        |
| Hungarian Grand Prix      |             54 |               16 |                      19 |                       19 |                       0 |                                 0 |                             0 | ready                       | no_action                        |
| Dutch Grand Prix          |             52 |               14 |                      19 |                       19 |                       0 |                                 0 |                             0 | ready                       | no_action                        |
| Italian Grand Prix        |             45 |               11 |                      17 |                       17 |                       0 |                                 0 |                             0 | ready                       | no_action                        |
| Azerbaijan Grand Prix     |             62 |               20 |                      21 |                       21 |                       0 |                                 0 |                             0 | ready                       | no_action                        |
| Singapore Grand Prix      |             51 |               19 |                      16 |                       16 |                       0 |                                 0 |                             0 | ready                       | no_action                        |
| United States Grand Prix  |             60 |               20 |                      20 |                       20 |                       0 |                                 0 |                             0 | ready                       | no_action                        |
| Mexico City Grand Prix    |             49 |               17 |                      16 |                       16 |                       0 |                                 0 |                             0 | ready                       | no_action                        |
| São Paulo Grand Prix      |             47 |               15 |                      16 |                       16 |                       0 |                                 0 |                             0 | ready                       | no_action                        |
| Las Vegas Grand Prix      |             59 |               17 |                      21 |                       21 |                       0 |                                 0 |                             0 | ready                       | no_action                        |
| Qatar Grand Prix          |             56 |               16 |                      20 |                       20 |                       0 |                                 0 |                             0 | ready                       | no_action                        |
| Abu Dhabi Grand Prix      |             56 |               16 |                      20 |                       20 |                       0 |                                 0 |                             0 | ready                       | no_action                        |

## Race-control text clusters

|   text_cluster |   messages |   sessions | cluster_terms                                                                | taxonomy_mix                                                                      | example_message                                                             |
|---------------:|-----------:|-----------:|:-----------------------------------------------------------------------------|:----------------------------------------------------------------------------------|:----------------------------------------------------------------------------|
|              3 |        480 |         23 | blue flag, waved, timed, waved blue, blue, flag car                          | flags,red_flag                                                                    | WAVED BLUE FLAG FOR CAR 87 (BEA) TIMED AT 16:07:48                          |
|              0 |        405 |         24 | car, safety car, safety, involving car, penalty, fia                         | drs,flags,investigations_noted,other,penalties,pit_entry_exit,red_flag,safety_car | ABORTED START                                                               |
|              4 |        324 |         23 | lap, turn lap, deleted track, deleted, limits turn, time deleted             | other                                                                             | CAR 12 (ANT) TIME 1:37.326 DELETED - TRACK LIMITS AT TURN 4 LAP 16 15:48:48 |
|              1 |        255 |         23 | clear, clear track, sector, track sector, track, track clear                 | flags                                                                             | CLEAR IN TRACK SECTOR 2                                                     |
|              5 |        245 |         23 | yellow track, yellow, sector, track sector, double yellow, double            | flags                                                                             | DOUBLE YELLOW IN TRACK SECTOR 2                                             |
|              7 |        217 |         23 | turn incident, involving cars, cars, incident, incident involving, involving | investigations_noted,safety_car                                                   | INCIDENT INVOLVING CARS 5 (BOR) AND 30 (LAW) NOTED - UNSAFE RELEASE         |
|              2 |         74 |         24 | drs enabled, enabled, drs, enabled zone, zone, drs disabled                  | drs                                                                               | DRS ENABLED                                                                 |
|              6 |         72 |         24 | pit exit, exit, pit, green light, green, light pit                           | pit_entry_exit                                                                    | GREEN LIGHT - PIT EXIT OPEN                                                 |
|              8 |         63 |         24 | drs disabled, disabled, drs, disabled zone, zone, entry incident             | drs                                                                               | DRS DISABLED IN ZONE 3                                                      |
|              9 |         43 |         13 | slippery track, slippery, surface slippery, surface, track surface, track    | other                                                                             | TRACK SURFACE SLIPPERY IN TRACK SECTOR 15                                   |

## Visual artifacts

- `artifacts/2025-race-database-surface-eda/figures/surface_availability_heatmap.svg`
- `artifacts/2025-race-database-surface-eda/figures/surface_issue_counts.svg`
- `artifacts/2025-race-database-surface-eda/figures/context_density_by_session.svg`
- `artifacts/2025-race-database-surface-eda/figures/ingestion_frequency_by_stream.svg`
- `artifacts/2025-race-database-surface-eda/figures/race_control_category_mix.svg`
- `artifacts/2025-race-database-surface-eda/figures/aligned_driver_non_ok_heatmap.svg`
- `artifacts/2025-race-database-surface-eda/figures/aligned_quality_replay_strips.svg`
- `artifacts/2025-race-database-surface-eda/figures/aligned_context_overlap.svg`
- `artifacts/2025-race-database-surface-eda/figures/surface_active_coverage_heatmap.svg`
- `artifacts/2025-race-database-surface-eda/figures/surface_coverage_windows.svg`
- `artifacts/2025-race-database-surface-eda/figures/race_control_taxonomy_mix.svg`
- `artifacts/2025-race-database-surface-eda/figures/status_timeline_strips.svg`
- `artifacts/2025-race-database-surface-eda/figures/weather_cadence_jumps.svg`
- `artifacts/2025-race-database-surface-eda/figures/context_timeline_density.svg`
- `artifacts/2025-race-database-surface-eda/figures/product_readiness_dashboard.svg`
- `artifacts/2025-race-database-surface-eda/figures/product_recommendation_summary.svg`
- `artifacts/2025-race-database-surface-eda/figures/circuit_marker_quality_summary.svg`
- `artifacts/2025-race-database-surface-eda/figures/circuit_marker_overlay_examples.svg`
- `artifacts/2025-race-database-surface-eda/figures/weather_trend_panels.svg`
- `artifacts/2025-race-database-surface-eda/figures/race_control_text_clusters.svg`

## Findings to carry forward

- `session_end_utc` is absent for imported sessions. This does not block replay because session-relative times exist, but it limits session-duration QA without deriving an end from samples/status.
- The 2025 race season is complete locally and is the only scope represented in these outputs.
- The non-lap context surfaces are populated enough for replay storytelling: weather, status timelines, race control, and circuit markers are present across the 2025 races.
- Aligned 10 Hz telemetry should be audited separately from raw telemetry because replay quality depends on interpolation and quality flags, not just raw sample counts.
- `session_end_utc` is missing for 24 sessions, but session duration can be derived from imported samples and status events for all 24 races.
- Weather covers at least 99.5% of each active replay window; race-control messages cover a median 45.5% time span and should be treated as event markers, not continuous context.
- Raw position coverage in this duration table is approximated from UTC offsets because `position_samples` does not store `session_time_ms`; use this as a schema/importer follow-up signal before surfacing user-facing position warnings.
- Race-control taxonomy produced 8 deterministic buckets and 308 repeated-message groups for deduplication review.
- Race-control text clustering produced 10 clusters for incident/noise review.
- Weather cadence is mostly regular; rainfall transitions found: 43. Weather transitions should be available as timeline markers in the desktop context strip.
- Circuit-marker geometry QA flagged 0 markers outside padded imported position bounds; flagged markers should be inspected before using them as desktop track callouts.
- Primary final recommendations: no action=0, label in UI=24, inspect=0, reimport=0, schema/importer change=0. Supporting schema/importer follow-up is present for 24 sessions.
- Median degraded replay-window rate is 9.12 percentage points different between context-event and no-context bins; current evidence does not justify treating context events as the main cause of aligned-quality degradation.
- 30-second replay windows with at least 1% non-OK aligned rows: 37,793; windows with at least 10% non-OK rows: 133.
- Longest consecutive degraded aligned segment found: 2,300 ms.
- The desktop app should treat aligned quality as a diagnostics overlay first, with warnings reserved for sustained or repeated degraded windows rather than isolated stale rows.

## Recommended next analyses

- Add raw stream ingestion severity tables that separate normal FastF1 cadence from importer/source problems.
- Decide whether the readiness labels should be persisted in the database or remain offline diagnostics.
- Decide whether session-level quality summaries should be persisted or remain offline notebook diagnostics.
