# 2025 lap compliance summary

Good laps are laps that pass Apexline's geometry-reference checks.
Bad laps are laps rejected for at least one reason such as FastF1
`IsAccurate == False`, pit in/out, missing time, too few position
samples, path-length outlier, or shape residual over threshold.

Reason counts are not mutually exclusive: one bad lap can be both a
pit lap and FastF1-inaccurate.

## Season totals

| Metric | Value |
|---|---:|
| Circuits | 24 |
| Total laps inspected | 26,689 |
| Fitted laps | 23,002 |
| Good laps | 22,899 (85.8%) |
| Bad laps | 3,790 (14.2%) |
| Shape-threshold bad laps | 103 |

## Rejection signals

| Reason | Count |
|---|---:|
| `fastf1_inaccurate` | 3,646 |
| `pit_lap` | 1,625 |
| `missing_lap_time` | 352 |
| `shape_rmse_over_threshold` | 100 |
| `path_length_outlier` | 93 |
| `too_few_position_samples` | 19 |
| `shape_p95_over_threshold` | 18 |

## Circuit table

| Round | Event | Good | Bad | Total | Good % | Shape Bad | Top rejection signals |
|---:|---|---:|---:|---:|---:|---:|---|
| 1 | Australian Grand Prix | 568 | 359 | 927 | 61.3% | 0 | `fastf1_inaccurate` 359, `pit_lap` 132, `missing_lap_time` 69 |
| 2 | Chinese Grand Prix | 995 | 70 | 1,065 | 93.4% | 0 | `fastf1_inaccurate` 70, `pit_lap` 52 |
| 3 | Japanese Grand Prix | 997 | 62 | 1,059 | 94.1% | 0 | `fastf1_inaccurate` 62, `pit_lap` 42 |
| 4 | Bahrain Grand Prix | 952 | 176 | 1,128 | 84.4% | 0 | `fastf1_inaccurate` 176, `pit_lap` 84, `missing_lap_time` 13 |
| 5 | Saudi Arabian Grand Prix | 809 | 89 | 898 | 90.1% | 1 | `fastf1_inaccurate` 88, `pit_lap` 39, `missing_lap_time` 37 |
| 6 | Miami Grand Prix | 861 | 144 | 1,005 | 85.7% | 0 | `fastf1_inaccurate` 144, `pit_lap` 38, `path_length_outlier` 4 |
| 7 | Emilia Romagna Grand Prix | 956 | 251 | 1,207 | 79.2% | 0 | `fastf1_inaccurate` 251, `pit_lap` 75, `missing_lap_time` 2 |
| 8 | Monaco Grand Prix | 1,271 | 154 | 1,425 | 89.2% | 0 | `fastf1_inaccurate` 154, `pit_lap` 81, `path_length_outlier` 9 |
| 9 | Spanish Grand Prix | 990 | 213 | 1,203 | 82.3% | 0 | `fastf1_inaccurate` 213, `pit_lap` 109, `missing_lap_time` 1 |
| 10 | Canadian Grand Prix | 1,157 | 192 | 1,349 | 85.8% | 0 | `fastf1_inaccurate` 152, `pit_lap` 131, `path_length_outlier` 42 |
| 11 | Austrian Grand Prix | 1,010 | 116 | 1,126 | 89.7% | 0 | `fastf1_inaccurate` 116, `pit_lap` 65, `missing_lap_time` 2 |
| 12 | British Grand Prix | 495 | 330 | 825 | 60.0% | 1 | `fastf1_inaccurate` 329, `missing_lap_time` 91, `pit_lap` 76 |
| 13 | Belgian Grand Prix | 672 | 207 | 879 | 76.5% | 75 | `fastf1_inaccurate` 132, `shape_rmse_over_threshold` 75, `pit_lap` 72 |
| 14 | Hungarian Grand Prix | 1,288 | 80 | 1,368 | 94.2% | 0 | `fastf1_inaccurate` 79, `pit_lap` 60, `path_length_outlier` 1 |
| 15 | Dutch Grand Prix | 1,041 | 323 | 1,364 | 76.3% | 0 | `fastf1_inaccurate` 323, `pit_lap` 80, `missing_lap_time` 2 |
| 16 | Italian Grand Prix | 912 | 62 | 974 | 93.6% | 4 | `fastf1_inaccurate` 58, `pit_lap` 41, `shape_p95_over_threshold` 4 |
| 17 | Azerbaijan Grand Prix | 853 | 115 | 968 | 88.1% | 2 | `fastf1_inaccurate` 113, `missing_lap_time` 58, `pit_lap` 41 |
| 18 | Singapore Grand Prix | 1,158 | 71 | 1,229 | 94.2% | 5 | `fastf1_inaccurate` 66, `pit_lap` 48, `shape_rmse_over_threshold` 5 |
| 19 | United States Grand Prix | 961 | 106 | 1,067 | 90.1% | 2 | `fastf1_inaccurate` 104, `pit_lap` 42, `shape_p95_over_threshold` 2 |
| 20 | Mexico City Grand Prix | 1,153 | 110 | 1,263 | 91.3% | 0 | `fastf1_inaccurate` 110, `pit_lap` 57, `missing_lap_time` 1 |
| 21 | São Paulo Grand Prix | 1,049 | 202 | 1,251 | 83.9% | 0 | `fastf1_inaccurate` 202, `pit_lap` 77, `missing_lap_time` 2 |
| 22 | Las Vegas Grand Prix | 758 | 128 | 886 | 85.6% | 2 | `fastf1_inaccurate` 126, `pit_lap` 45, `missing_lap_time` 2 |
| 23 | Qatar Grand Prix | 920 | 147 | 1,067 | 86.2% | 2 | `fastf1_inaccurate` 145, `pit_lap` 84, `missing_lap_time` 3 |
| 24 | Abu Dhabi Grand Prix | 1,073 | 83 | 1,156 | 92.8% | 9 | `fastf1_inaccurate` 74, `pit_lap` 54, `shape_rmse_over_threshold` 9 |
