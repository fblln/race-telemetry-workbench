# Canada 2025 fastest-lap EDA

This note explains why the first Canada 2025 lap-diagnostics run incorrectly
marked George Russell's fastest lap as shape non-compliant.

## Finding

The FastF1 data for Russell lap 63 is not the problem. The issue was the phase
search used by the diagnostics path.

Russell lap 63, using a fine offset search:

| Metric | Value |
|---|---:|
| Driver | RUS |
| Lap | 63 |
| Lap time | 74.119 s |
| Position samples | 283 |
| FastF1 closed path length | 4311.97 m |
| Repository GPS length | 4365.04 m |
| Length error | -53.07 m (-1.22%) |
| Fit RMSE | 3.89 m |
| Fit p95 | 6.58 m |
| Fit max | 8.19 m |
| Best offset at 720 samples | 37 |

The previous diagnostics run used 240 validation samples with an offset step of
8. At that resolution, the correct offset is 12, but the coarse grid only tried
offsets 8 and 16 around it. Those two offsets are bad local alignments:

| 240-sample offset | RMSE | p95 |
|---:|---:|---:|
| 8 | 68.0 m | 89.2 m |
| 12 | 5.5 m | 8.5 m |
| 16 | 60.2 m | 81.0 m |

That is why the original output reported approximately 60 m RMSE for the
fastest lap. It skipped the correct phase.

## Fix

`validate_shape` now does a coarse phase search and then refines locally around
the best coarse offset. This keeps diagnostics reasonably fast while avoiding
false shape failures on phase-sensitive tracks.

The lap length gate was also tightened from 15% to 5%. With the old 15% gate,
early race laps with about 5.0 km measured paths could still be shape-fitted on
a 4.365 km circuit. Those are better classified as path-length outliers before
shape validation.

## Final Canada 2025 diagnostics

| Metric | Value |
|---|---:|
| Total laps | 1349 |
| Fitted laps | 1157 |
| Compliant laps | 1157 |
| Non-compliant laps | 192 |
| Shape non-compliant laps | 0 |

Reason counts:

| Reason | Count |
|---|---:|
| compliant | 1157 |
| fastf1_inaccurate | 152 |
| pit_lap | 131 |
| path_length_outlier | 42 |
| missing_lap_time | 3 |
| too_few_position_samples | 1 |

After the fix, Russell lap 63 is the fastest compliant lap:

| Metric | Value |
|---|---:|
| Fit RMSE | 5.54 m |
| Fit p50 | 5.00 m |
| Fit p95 | 8.51 m |
| Fit max | 10.61 m |
| Diagnostics offset | 12 / 240 samples |

The remaining non-compliant laps are not shape failures. They are excluded
because FastF1 marks them inaccurate, they are pit laps, their path length is a
clear outlier, their lap time is missing, or they have too few position samples.
