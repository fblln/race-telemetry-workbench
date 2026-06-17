# Why Cross-Validate Circuit GPS Shapes?

Circuit outlines look simple: a list of latitude/longitude points, a line on a
map, maybe an encoded polyline string.

But small geometry errors matter:

- a chicane can be cut by an over-aggressive simplifier,
- a historical layout can be mixed with a modern one,
- a centerline can drift from the shape implied by telemetry,
- a line can be in the wrong direction or have a strange start point,
- a map overlay can look right zoomed out but wrong at turn-level zoom.

Apexline exists to make those problems measurable.

## The Key Constraint

FastF1 does not give car latitude/longitude.

FastF1 position data contains local circuit coordinates:

```text
X, Y, Z
```

Those coordinates are excellent for shape validation, but they are not GPS. That
means Apexline cannot say:

> This car was exactly here on Earth.

It can say:

> This repository GPS outline has the same shape as the FastF1 lap trace, after
> accounting for scale, rotation, translation, start offset, and direction.

That is still very useful.

## Method

For each circuit:

1. Load the GPS LineString from `bacinger/f1-circuits`.
2. Project it into local meter-space.
3. Load candidate FastF1 race laps.
4. Filter candidate laps by path length.
5. Resample GPS and FastF1 shapes by equal lap progress.
6. Search for the best start offset and direction.
7. Fit FastF1 to GPS with scale, rotation, and translation.
8. Report RMSE, median, p95, and max pointwise shape error.
9. Simplify the GPS line with a conservative meter tolerance.
10. Encode the simplified line as a Google encoded polyline.

## Why Similarity Fitting?

FastF1 local coordinates and GPS coordinates are in different coordinate
systems. Direct point-to-point comparison would be meaningless.

Similarity fitting allows:

- scale,
- rotation,
- translation.

It does not allow arbitrary warping. So if the circuit shape is genuinely wrong,
the error remains visible.

This is the right level of flexibility: normalize coordinate systems, but still
preserve shape differences.

## Why Lap Progress Resampling?

The repository line and FastF1 lap have different point counts and sampling
rates.

Instead of comparing raw indices, Apexline samples both shapes at equal progress
around the closed loop:

```text
0%, 0.14%, 0.28%, ... 99.86%
```

This makes the comparison independent of source sampling density.

## Why Encoded Polyline?

Encoded polyline is a compact text representation of a coordinate sequence.

It is useful because:

- it is much smaller than a JSON array of coordinates,
- it is already supported by mapping libraries,
- it is easy to store in databases or API responses,
- it is deterministic,
- it can be decoded client-side without custom geometry formats.

The trick is choosing a simplification tolerance that is small enough for racing
circuits. Apexline defaults to 1 m because higher tolerances can visibly cut
chicanes and hairpins.

![Polyline encoding explainer](assets/polyline-encoding-explainer.svg)

## Why Averaging Laps Is Complicated

Averaging sounds attractive: more laps should reduce noise.

In practice, FastF1 lap traces are not centerlines. They are driven racing
lines. Drivers use different:

- braking approaches,
- apex points,
- curb usage,
- corner exits,
- defensive/offline lines,
- early-lap or late-lap paths.

So averaging several good laps can either help or hurt.

In the 2025 run:

- averaging improved 10 circuits,
- averaging worsened 14 circuits.

That is a useful diagnostic result. It tells us averaged FastF1 is not a
universal replacement for a GPS centerline.

## When This Is Useful

This cross-validation is useful when you need to:

- trust a third-party circuit GPS repository,
- generate compact map overlays,
- detect layout mismatches,
- verify that simplification does not destroy geometry,
- compare public map data against telemetry-derived shapes,
- build API payloads that include track outlines,
- precompute circuit assets for dashboards or mobile clients.

## What To Investigate When Error Is High

High RMSE or p95 error can come from:

- wrong circuit layout,
- stale repository geometry,
- a FastF1 candidate lap with poor position slices,
- street-circuit racing lines far from the mapped centerline,
- start/progress mismatch in a complex loop,
- over-simplified source geometry,
- different treatment of pit entry/exit or alternate chicanes.

The script keeps enough intermediate data to inspect:

- selected FastF1 driver and lap,
- candidate laps used for averaging,
- repo GPS polyline,
- averaged FastF1 fitted polyline,
- simplification errors,
- fit direction and start offset.

## Practical Recommendation

For production map overlays:

1. Use the repository GPS polyline as the rendered track.
2. Validate it against FastF1 with best-lap RMSE/p95.
3. Keep 1 m simplification unless you have a visual QA step.
4. Treat averaged FastF1 as a diagnostic layer, not the default track shape.
5. Flag circuits above 15-20 m RMSE for manual review.

