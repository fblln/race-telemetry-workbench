# Parameter Guide

Apexline has two jobs:

1. Validate a GPS circuit shape against FastF1 local telemetry shape.
2. Encode the GPS shape compactly without losing important geometry.

The defaults are intentionally conservative.

## Recommended Defaults

```bash
--validation-samples 720
--validation-offset-step 4
--polyline-tolerance-m 1.0
--polyline-precision 5
--max-shape-candidates 12
--average-laps 5
--average-samples 720
```

## Validation Samples

`--validation-samples` controls how many equal-progress points are compared
between the repository GPS line and FastF1 lap shape.

Recommended: `720`

Why:

- It is dense enough to catch chicanes and tight corner errors.
- It keeps the fit stable across circuits from Monaco to Spa.
- It is still cheap enough for a full-season run.

Lower values can hide localized errors. Higher values usually add runtime more
than insight.

## Validation Offset Step

`--validation-offset-step` controls the circular start-offset search.

Recommended: `4`

FastF1 lap samples and repository GPS LineStrings rarely start at exactly the
same physical point. The script searches different phase offsets around the
closed loop. Step `4` at `720` samples means offsets are tested every 0.56% of a
lap.

Use a smaller value only when debugging a single circuit.

## Polyline Tolerance

`--polyline-tolerance-m` controls Ramer-Douglas-Peucker simplification in meters.

Recommended: `1.0`

Why not higher?

- F1 circuits have chicanes, hairpins, and tight street-circuit geometry.
- A 5-10 m tolerance can visibly cut corners.
- The encoded strings are already small enough at 1 m tolerance.

In the 2025 run, a 1 m tolerance produced:

- average of about 105 simplified points per circuit,
- average of about 309 encoded characters,
- max simplification error under 1 m for every circuit.

That is a good tradeoff for map overlays.

## Polyline Precision

`--polyline-precision` controls decimal precision for Google encoded polyline.

Recommended: `5`

Precision 5 is the common web-map default. It represents roughly meter-level
coordinate precision. Since the simplification tolerance is already 1 m, using a
higher encoding precision usually increases string size without materially
improving the visible line.

## Candidate Lap Selection

`--max-shape-candidates` controls how many FastF1 laps receive full shape
fitting after a cheap path-length filter.

Recommended: `12`

Why this exists:

FastF1's `IsAccurate` flag is useful, but not sufficient for circuit-shape
validation. Some early laps can include odd position slices while still being
marked accurate.

The script now:

1. collects clean non-pit laps,
2. rejects laps whose FastF1 path length is too far from the GPS circuit length,
3. ranks remaining laps by path-length closeness,
4. runs expensive shape fitting on the top candidates,
5. picks the lowest RMSE fit.

This fixed the Canadian GP false outlier: the first accurate lap looked terrible
against the repository shape, but a later representative lap matched at about
5 m RMSE.

## Averaging Laps

`--average-laps` controls how many fitted FastF1 laps are averaged into a
secondary diagnostic line.

Recommended: `5`

Important: averaging is not always better.

FastF1 traces are driven racing lines. The repository GPS shape is usually a
centerline. Multiple good laps can use different corner entries, exits, and
curb usage. Averaging those racing lines can move the result away from the
centerline.

Use averaged output as a diagnostic:

- If averaging improves RMSE, the single best lap may still have local noise.
- If averaging worsens RMSE, the circuit likely has meaningful racing-line
  variation relative to the mapped centerline.

## Interpreting RMSE

These metrics do not mean "the car GPS was wrong by 7 meters." FastF1 has no
native GPS channel here.

They mean:

> After allowing scale, rotation, translation, direction, and start offset, how
> closely does the FastF1 local track shape match the repository GPS shape?

Useful thresholds:

| RMSE | Interpretation |
|---:|---|
| 0-5 m | Excellent shape agreement. |
| 5-10 m | Good agreement for display and validation. |
| 10-20 m | Investigate localized differences, especially street circuits. |
| 20+ m | Likely wrong lap, wrong layout, or source geometry mismatch. |

