# 2025 Telemetry Bad-Lap EDA Backlog

This backlog turns the first 2025 race-only telemetry quality pass into an
iteration plan. The goal is to make the EDA defensible enough to guide importer
changes, API behavior, replay defaults, and future persisted quality flags.

Current baseline:

- Notebook: `notebooks/2025_telemetry_bad_lap_eda.ipynb`
- Support code: `notebooks/telemetry_bad_lap_support.py`
- Summary: `docs/data-quality/2025-telemetry-bad-lap-eda-summary.md`
- Artifacts: `artifacts/2025-telemetry-bad-lap-eda/`
- Scope: 2025 race sessions only, 24 sessions, 26,689 laps
- First-pass result: 5,497 laps flagged with at least one quality/context/source
  signal, 20.6% of inspected laps

## Target Standard

The finished work should answer four product/data questions clearly:

1. Do we have complete and trustworthy 2025 race coverage?
2. Which laps are objectively unsafe for replay or API analysis?
3. Which laps are valid telemetry but distorted by race context?
4. Which anomalies remain unexplained and deserve importer, schema, or source
   investigation?

The analysis should avoid treating every unusual lap as bad telemetry. It should
separate data-integrity failures from race-context outliers and exploratory
shape anomalies.

## Iteration 1 - Clarify The Quality Model

- [ ] Split the current single taxonomy into three independent lenses:
  - data integrity: missing windows, sample gaps, nulls, impossible values,
    position discontinuities, FastF1 inaccurate/deleted
  - race context: pit laps, safety car, VSC, red flag, local yellows, start and
    restart artifacts, rain/weather shifts
  - analytical shape: speed-profile, position-shape, and timing-shape outliers
    versus comparable laps
- [ ] Add explicit derived booleans for intended use:
  - `safe_for_replay`
  - `safe_for_lap_comparison`
  - `safe_for_geometry_reference`
  - `needs_manual_review`
- [ ] Rename or document `shape_mismatch_against_comparable_laps` as
  `atypical_speed_profile` unless another integrity signal supports a telemetry
  defect.
- [ ] Add severity scores next to boolean flags:
  - coverage deficit
  - max/p95 sample gap excess
  - path-ratio deviation
  - max-segment jump excess
  - speed-shape robust z-score
  - null-rate severity
- [ ] Make threshold configuration visible in the notebook and markdown summary,
  including rationale and expected failure mode.

## Iteration 2 - Improve Category Overlap And Attribution

- [ ] Add an UpSet-style category-intersection table or plot. Current category
  bars are useful but hide overlap.
- [ ] Add a decision waterfall:
  - total laps
  - source/timing excluded
  - missing/incomplete telemetry
  - position discontinuity
  - race-context influenced
  - shape-only outliers
  - clean
- [ ] Add per-race stacked category decomposition so high bad-lap rates explain
  themselves immediately.
- [ ] Add a primary-category audit table with examples where the primary
  category may hide a more interesting secondary reason.
- [ ] Add `reason_count` and `reason_set` columns to the classified-lap table for
  easier filtering and grouping.

## Iteration 3 - Stronger Visual Storytelling

- [ ] Add a lap-number heatmap:
  - rows: race
  - columns: lap number
  - color: primary quality class
  - overlay or mark SC/VSC/red-flag periods where available
- [ ] Add a driver-by-race matrix for bad-lap rate with minimum-lap-count
  filtering and separate views for integrity-only and context-only flags.
- [ ] Replace or complement the race bad-rate bar with stacked bars showing the
  dominant causes per race.
- [ ] Add selected race drilldowns for British, Belgian, Australian, Dutch, and
  São Paulo because they dominate the first-pass bad-rate table.
- [ ] Add small multiples for top races:
  - lap number vs primary category
  - lap time vs shape severity
  - sample gap severity vs lap number
  - pit/SC context shading
- [ ] Add a final recommendation figure: keep, keep-with-context-label, exclude,
  manual-review.

## Iteration 4 - Better Shape Analysis

- [ ] Compare each lap against a clean same-race median speed profile with
  quantile bands, not only a residual score.
- [ ] For representative traces, show:
  - clean median profile
  - 10th-90th percentile band
  - selected anomalous lap
  - contextual markers for pit/SC/VSC/red flag where available
- [ ] Stratify speed-shape baselines by at least one of:
  - compound
  - stint number
  - early/mid/late race phase
  - clean green-flag laps only
- [ ] Evaluate whether equal-time bins over-flag legitimate slow laps. Compare
  against lap-progress bins if distance becomes available.
- [ ] Add shape-cluster exemplars:
  - nearest-to-centroid lap
  - highest-severity lap
  - most common primary category per cluster
- [ ] Add cluster stability checks across `k` values and random seeds before
  treating clusters as meaningful.

## Iteration 5 - Position And Geometry Integration

- [ ] Persist per-lap Apexline diagnostics, not only event-level summaries and
  selected worst examples.
- [ ] Join Apexline per-lap diagnostics to the notebook classified-lap table by
  session/event, driver, and lap number.
- [ ] Add geometry-specific categories:
  - no position data
  - too few position samples
  - path-length outlier
  - GPS-shape RMSE over threshold
  - GPS-shape p95 over threshold
- [ ] Add side-by-side position-trace examples:
  - clean lap
  - pit lap
  - path-length outlier
  - segment discontinuity
  - geometry shape reject
- [ ] Compare imported `position_samples` path metrics against Apexline
  FastF1-derived path metrics to detect importer-induced differences.
- [ ] Document whether zero path-length laps are source issues, import issues,
  or session boundary artifacts.

## Iteration 6 - Missing Distance And Importer Follow-Up

- [ ] Decide whether raw FastF1 `Distance` should be imported or derived during
  import.
- [ ] If distance is added, implement checks for:
  - distance reset inside a lap
  - non-monotonic distance
  - implausible distance delta
  - distance coverage versus expected circuit length
- [ ] Add migration/import planning for distance fields if approved.
- [ ] Add real-database importer tests for distance monotonicity on a limited
  Monza 2025 slice.
- [ ] Keep nulls distinct from true zeroes in all distance-derived outputs.

## Iteration 7 - Threshold Sensitivity And Robustness

- [ ] Add threshold sensitivity sweeps for:
  - minimum car samples
  - minimum position samples
  - lap coverage ratio
  - p95/max telemetry gap
  - path-length tolerance
  - speed-shape RMS threshold
- [ ] Show which races/drivers/categories are stable versus threshold-sensitive.
- [ ] Add bootstrap or resampling checks for speed-profile baseline bands.
- [ ] Record the selected threshold set in a machine-readable artifact.
- [ ] Add a "borderline laps" table for manual review.

## Iteration 8 - Data Products And Reproducibility

- [ ] Add a lightweight CLI entry point for the EDA support module:
  - `--year`
  - `--session R`
  - `--event`
  - `--drivers`
  - `--output-dir`
  - `--skip-skrub-report`
- [ ] Add optional caching for expensive feature tables so notebook iteration
  does not always rescan all telemetry samples.
- [ ] Save classified laps as Parquet as well as CSV for type preservation.
- [ ] Add a JSON metadata file with:
  - database URL host/db only, not credentials
  - session count
  - lap count
  - package versions
  - threshold values
  - generated artifact paths
- [ ] Keep artifact caches ignored while preserving figures, tables, markdown,
  and the `skrub` report.

## Iteration 9 - Tests And Engineering Hardening

- [ ] Add unit tests for classification rules using small synthetic lap rows.
- [ ] Add tests that verify mutually exclusive and non-mutually-exclusive
  behavior:
  - reason flags can overlap
  - primary category follows documented priority
  - unavailable distance checks remain false with a clear limitation
- [ ] Add a smoke test that loads a small Monza 2025 slice from the real DB and
  runs feature derivation plus classification.
- [ ] Add validation that every generated table has expected columns.
- [ ] Add validation that every generated SVG exists and is non-empty.
- [ ] Add a notebook execution check that can run in CI or local dev when a DB is
  available.

## Iteration 10 - Product Decisions

- [ ] Decide whether quality flags remain offline diagnostics or become
  database/API concepts.
- [ ] If persisted, design a table such as `lap_quality_flags` with:
  - `session_id`
  - `driver_code`
  - `lap_number`
  - `quality_version`
  - boolean flags
  - severity metrics
  - reason JSON
  - generated timestamp
- [ ] Decide how the Query API should expose quality:
  - lap list quality badges
  - replay metadata warnings
  - lap-comparison exclusion hints
  - analytical endpoint filters
- [ ] Decide desktop behavior:
  - hide bad laps by default
  - show context-labeled laps
  - allow manual override
  - surface manual-review laps in the UI
- [ ] Align any UI labels with Carbon Signal language before exposing them in
  the MAUI app.

## Near-Term Recommended Order

1. Add category overlap, waterfall, and stacked race-decomposition visuals.
2. Rename and reframe speed-shape mismatch as atypical speed profile.
3. Add race drilldowns for British, Belgian, and Australian.
4. Persist/join per-lap Apexline diagnostics.
5. Add threshold sensitivity and borderline-lap review.
6. Decide whether distance import is worth adding.
7. Add tests for helper code and a small real-DB smoke path.

## Open Questions

- Should a FastF1 inaccurate lap automatically be excluded from replay, or only
  from performance analysis and geometry references?
- Should pit laps be "bad" or simply context-labeled for most product views?
- Should safety-car and VSC laps be excluded from lap comparison by default?
- Is equal-time speed-profile comparison sufficient until distance is imported?
- Which quality labels should be user-facing in the desktop app versus retained
  as internal diagnostics?
