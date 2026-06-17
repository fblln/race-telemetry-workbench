# 2025 Race Database Surface EDA Backlog

This backlog turns the first imported database-surface audit into a 2025-only
iteration plan. Future EDA work in this track should use 2025 race sessions
(`session_type = 'R'`) as the analysis scope unless another season/session type
is explicitly requested and documented as a comparison baseline.

The prior `race_database_surface_eda.ipynb` inventoried every imported race
session in the local database, including partial 2024 and 2026 imports. That was
useful for discovering what exists locally. The next iterations should narrow
the analytical story to the complete 2025 race season.

## Target Standard

The finished 2025 database-surface EDA should answer these questions:

1. Is every 2025 race session complete enough for launcher/session browsing?
2. Are raw telemetry and position streams complete enough for bounded Query API
   and MCP use?
3. Is aligned 10 Hz replay data reliable enough for desktop replay?
4. Are weather, track status, session status, race-control, and circuit marker
   surfaces good enough for context-aware storytelling?
5. Which defects require reimport, schema changes, importer fixes, or UI/API
   warnings?

The notebook should distinguish inventory, severity, and product impact. A
session can be populated but still risky for a specific feature.

## Scope Rules

- [ ] Filter all primary queries to `year = 2025` and `session_type = 'R'`.
- [ ] Keep 2024/2026 out of plots, summaries, and conclusions unless explicitly
  shown in a clearly labeled comparison appendix.
- [ ] Preserve race-default behavior. Do not include FP, Q, SQ, S, FP1, FP2, or
  FP3 unless a future task explicitly requests them.
- [ ] Make the notebook title, summary, artifact directory, and metadata say
  `2025 race sessions`.
- [ ] Add a guardrail assertion that exactly 24 2025 race sessions are present
  before season-level conclusions are generated.

## Iteration 1 - Reframe The Surface Quality Model

- [ ] Split surface QA into product-readiness lenses:
  - `catalog_readiness`: session metadata, event identity, country/circuit,
    driver list
  - `raw_stream_readiness`: car telemetry and position stream coverage
  - `replay_readiness`: aligned 10 Hz rows, interpolation, stale/gap flags
  - `context_readiness`: weather, track/session status, race control
  - `circuit_context_readiness`: circuit metadata and markers
- [ ] Add labels:
  - `ready`
  - `ready_with_warnings`
  - `partial`
  - `needs_reimport`
  - `needs_manual_review`
- [ ] Add severity scores instead of relying only on booleans:
  - affected drivers
  - affected laps or windows
  - affected rows
  - percent of session time affected
  - product impact: launcher, replay, lap comparison, context panels
- [ ] Separate systematic known limitations from surprising defects. Example:
  missing `session_end_utc` is a schema/import limitation, not necessarily a
  session defect if duration can be derived from samples.

## Iteration 2 - Aligned Replay Quality Deep Dive

This is the highest-value next notebook because desktop replay depends on
`aligned_telemetry_10hz`, not just raw telemetry.

- [ ] Aggregate aligned quality by race, driver, lap, and session-time window.
- [ ] Decode `quality_flags` into stable families:
  - `OK`
  - car gap too large
  - car sample too old
  - location gap too large
  - location sample too old
  - other/unknown
- [ ] Compute:
  - non-OK rows per race
  - non-OK percentage per driver
  - longest consecutive non-OK segment
  - number of affected replay windows
  - car-related vs location-related quality mix
- [ ] Distinguish isolated stale rows from long degraded bursts.
- [ ] Join aligned quality to lap metadata and context:
  - pit in/out
  - SC/VSC/red flag
  - missing lap time
  - FastF1 inaccurate
- [ ] Add representative replay-quality strips:
  - one row per driver
  - x-axis session time
  - color by aligned quality family
  - markers for SC/VSC/red flag and pit windows where available
- [ ] Identify races/drivers where replay is likely to appear jumpy, stale, or
  incomplete.

## Iteration 3 - Raw Stream Coverage And Ingestion Diagnostics

- [ ] Convert ingestion diagnostic warnings from session-level booleans into
  severity:
  - warning type
  - stream name
  - affected driver count
  - affected sample count
  - median/p95/p99/max delta
  - duplicate and out-of-order counts
- [ ] Compare raw car and position stream coverage by race and driver.
- [ ] Add stream-frequency distributions per race:
  - car telemetry estimated Hz
  - position estimated Hz
  - p95/p99/max sample gaps
- [ ] Highlight driver-stream outliers within each race, not just season totals.
- [ ] Add a table of streams that should be inspected before replay demos.
- [ ] Explain whether ingestion warnings are normal FastF1 cadence behavior,
  importer artifacts, or likely source-data problems.

## Iteration 4 - Context Surface EDA

### Weather

- [ ] Check weather sample cadence by race.
- [ ] Flag large weather sampling gaps.
- [ ] Detect suspicious jumps in:
  - air temperature
  - track temperature
  - pressure
  - humidity
  - wind direction/speed
- [ ] Identify rainfall transitions and align them with track status and race
  control.
- [ ] Add per-race weather trend panels for races with rainfall or large value
  shifts.

### Track And Session Status

- [ ] Convert status events into intervals with derived duration.
- [ ] Compare status intervals against race-control messages.
- [ ] Detect sessions with status timelines that begin late, end early, or have
  ambiguous transitions.
- [ ] Add status timeline strips for every 2025 race.

### Race Control

- [ ] Build a race-control message taxonomy:
  - flags
  - safety car
  - VSC
  - DRS
  - investigations/noted incidents
  - penalties
  - pit entry/exit
  - other
- [ ] Add duplicate and near-duplicate detection.
- [ ] Cluster message text using a skrub/scikit-learn-compatible text pipeline.
- [ ] Quantify missing timing, missing lap scope, and missing driver scope by
  category.
- [ ] Add incident density timelines by race.
- [ ] Preserve example messages for each taxonomy bucket.

## Iteration 5 - Circuit Marker And Track Context QA

- [ ] Audit circuit metadata and markers for all 24 2025 races:
  - circuit metadata row present
  - rotation present
  - corner count
  - marshal light count
  - marshal sector count
  - marker distance completeness
- [ ] Compare marker coordinates with imported position trace bounds.
- [ ] Flag markers outside plausible position bounds.
- [ ] Add marker-over-track visual examples for selected circuits.
- [ ] Validate marker count against expected circuit complexity where a reliable
  project-owned reference exists.
- [ ] Identify whether missing/low marker counts are FastF1 source limitations
  or importer issues.

## Iteration 6 - Session Duration And Coverage Windows

- [ ] Derive `session_duration_ms` from:
  - max raw telemetry `session_time_ms`
  - max aligned `session_time_ms`
  - max weather/status/race-control session time
  - session status finished event, if available
- [ ] Use derived duration to measure surface coverage:
  - weather coverage ratio
  - status coverage ratio
  - race-control time span
  - raw telemetry span
  - aligned telemetry span
- [ ] Treat missing `session_end_utc` as a known limitation, then show whether
  derived duration is sufficient for replay/context QA.
- [ ] Add a coverage-window chart per race showing which surfaces cover which
  parts of the session timeline.

## Iteration 7 - Stronger Visual Storytelling

- [ ] Replace broad availability heatmaps with 2025-only product-readiness
  visuals.
- [ ] Add a readiness dashboard:
  - rows: 2025 races
  - columns: catalog, raw streams, aligned replay, context, circuit markers
  - color: ready/warning/partial/review
- [ ] Add severity waterfalls:
  - 24 races
  - catalog ready
  - raw streams ready
  - aligned replay ready
  - context ready
  - circuit context ready
- [ ] Add aligned-quality driver/race heatmap.
- [ ] Add race-control/status timeline strips.
- [ ] Add weather cadence and jump plots.
- [ ] Add circuit-marker sanity panels.
- [ ] Add a final recommendation table: no action, label in UI, inspect, reimport,
  schema/importer change.

## Iteration 8 - Technical Reproducibility

- [ ] Add a CLI for the surface EDA support module:
  - `--year 2025`
  - `--session R`
  - `--output-dir`
  - `--refresh-cache`
  - `--skip-skrub-report`
- [ ] Cache expensive aggregates as Parquet.
- [ ] Write metadata JSON with:
  - year
  - session type
  - session count
  - table row counts
  - threshold values
  - package versions
  - generation timestamp
- [ ] Save all major tables as Parquet and CSV.
- [ ] Keep generated cache folders ignored, but preserve figures, summary,
  metadata, tables, and `skrub` report.
- [ ] Add a clear command in the notebook and docs for rerunning the full EDA.

## Iteration 9 - Tests And Quality Gates

- [ ] Add unit tests for surface readiness classification.
- [ ] Add tests for aligned quality flag decoding.
- [ ] Add tests for status interval derivation.
- [ ] Add tests for session duration derivation.
- [ ] Add smoke test against a small Monza 2025 DB slice.
- [ ] Validate generated tables have expected columns.
- [ ] Validate generated SVGs and HTML reports are non-empty.
- [ ] Add a notebook execution check that can run locally when the DB is
  available.

## Iteration 10 - Product And API Decisions

- [ ] Decide whether session-level quality should remain offline diagnostics or
  become persisted database state.
- [ ] If persisted, design a table such as `session_surface_quality` with:
  - `session_id`
  - `quality_version`
  - readiness labels
  - severity scores
  - issue JSON
  - generated timestamp
- [ ] Decide Query API exposure:
  - session list quality badges
  - replay metadata quality warnings
  - circuit context completeness flags
  - context surface availability
- [ ] Decide desktop behavior:
  - show partial-context warnings in launcher
  - warn before opening degraded replay sessions
  - show aligned-quality overlays in replay diagnostics
  - distinguish missing context from clean zero-event context
- [ ] Align user-facing language with Carbon Signal before surfacing quality
  labels in the MAUI app.

## Near-Term Recommended Order

1. Refactor the current broad surface notebook into a 2025-only notebook and
   artifact directory.
2. Build aligned replay quality aggregation by race, driver, lap, and time
   window.
3. Add aligned-quality timeline strips and driver/race heatmaps.
4. Derive session duration and coverage windows from imported samples/events.
5. Add race-control taxonomy and timeline density.
6. Add weather cadence/jump checks.
7. Add circuit-marker sanity visuals.
8. Add tests for readiness labels and quality-flag decoding.

## Open Questions

- What percentage of aligned non-OK rows is acceptable for normal replay?
- Should replay warn on isolated stale samples, or only sustained degraded
  windows?
- Should context surfaces be required for a session to be "ready", or should
  they be labeled as optional enrichments?
- Should race-control messages be deduplicated during import or only in analysis
  views?
- Should session duration be stored during import once derived robustly?
- Which quality labels should be visible in the desktop app versus reserved for
  developer diagnostics?
