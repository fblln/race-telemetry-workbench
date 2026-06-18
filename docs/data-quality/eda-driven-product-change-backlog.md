# EDA-Driven Product Change Backlog

This backlog translates the 2025 race database-surface EDA into backend and
desktop implementation work. The source analysis is
`docs/data-quality/2025-race-database-surface-eda-summary.md`, generated from
`notebooks/race_database_surface_eda.ipynb`.

Current product stance:

- All 24 imported 2025 race sessions are usable.
- All 24 sessions need UI labeling rather than a clean/green presentation.
- No 2025 session currently needs reimport or manual inspection from circuit
  marker QA.
- `session_end_utc` is missing for all 24 sessions.
- Raw position coverage windows are inferred from UTC offsets because
  `position_samples` does not store `session_time_ms`.
- Aligned replay quality should be surfaced as diagnostics first, not as a
  blocking error.

## Backend Changes

### 1. Expose Session Readiness

- [ ] Add a session readiness DTO in `RaceTelemetry.Contracts`.
- [ ] Include these fields:
  - `catalogReadiness`
  - `rawStreamReadiness`
  - `replayReadiness`
  - `contextReadiness`
  - `circuitContextReadiness`
  - `finalRecommendation`
  - `schemaImporterFollowUp`
  - `knownLimitations`
- [ ] Add readiness to session list/details responses, or expose a bounded
  `/api/sessions/{sessionId}/quality` endpoint.
- [ ] Keep readiness labels additive and string-tolerant for client
  compatibility.
- [ ] Add real-database tests over the 2025 imported session set or a fixture
  slice.

Acceptance:

- Session browser can show `ready_with_warnings`, `partial`, and follow-up
  states without inferring them client-side.

### 2. Add Replay Quality Summary

- [ ] Add replay-quality summary fields to `ReplayMetadata` or a companion
  endpoint:
  - aligned non-OK row percentage
  - degraded 30-second window count
  - severe 30-second window count
  - longest degraded segment in milliseconds
  - guidance: `diagnostics_only`, `show_replay_quality_overlay`,
    `warn_before_replay`, `no_action`
- [ ] Preserve per-sample `quality_flags` in replay chunks.
- [ ] Add backend tests for guidance thresholds.

Acceptance:

- Desktop can decide whether to show a diagnostics overlay without scanning all
  replay chunks first.

### 3. Fix Schema/Importer Follow-Ups

- [ ] Populate `sessions.session_end_utc` during import when FastF1/source data
  provides a reliable value.
- [ ] Add `session_time_ms` to `position_samples`.
- [ ] Backfill or reimport position samples so raw-position coverage checks no
  longer depend on UTC-offset approximation.
- [ ] Update migrations, importer, schema docs, and integration tests.
- [ ] Re-run surface EDA after schema/importer changes.

Acceptance:

- Raw-position coverage can be measured in session-relative time.
- The desktop can display official session end only when it exists; otherwise it
  keeps using derived replay duration.

### 4. Enrich Race-Control Timeline

- [ ] Add deterministic taxonomy to Query API race-control responses:
  - flags
  - safety car
  - VSC
  - DRS
  - penalties
  - investigations/noted
  - pit entry/exit
  - other
- [ ] Add duplicate/near-duplicate grouping for repeated sector, flag, and blue
  flag messages.
- [ ] Keep original race-control message text available.
- [ ] Consider exposing text-cluster IDs as developer/analysis metadata, not as
  primary user labels.

Acceptance:

- Desktop event timeline can filter and group race-control messages without
  hardcoding text rules.

### 5. Expose Circuit Marker QA

- [ ] Add marker QA fields to circuit context:
  - marker count by type
  - marker distance completeness
  - outside-position-bounds count
  - outside-core-trace-bounds count
  - circuit context readiness
  - recommendation
- [ ] Keep marker coordinates unchanged; QA fields are metadata only.
- [ ] Add real-database tests for a session with complete markers.

Acceptance:

- Track map can show circuit markers while also knowing when to label them as
  incomplete or suspicious.

### 6. Add Weather Trend Primitives

- [ ] Add a weather trend endpoint or extend existing weather trend contract
  with:
  - stepped samples
  - rainfall transitions
  - air/track temperature trend
  - humidity/pressure/wind jump flags
- [ ] Keep weather stepped; do not interpolate it like telemetry.

Acceptance:

- Desktop can render current weather at cursor and rainfall/temperature trend
  panels from bounded backend data.

## Desktop Changes

### 1. Session Browser Quality Labels

- [ ] Show a compact quality badge for each session.
- [ ] Use `label_in_ui` as the current primary state for all 2025 races.
- [ ] Show schema/importer follow-up only in details or developer diagnostics,
  not as a blocker.
- [ ] Render unavailable fields as `--` or `not imported`.

Acceptance:

- Users can open replay for 2025 races, but the UI does not imply the data is
  perfectly clean.

### 2. Replay Quality Diagnostics Overlay

- [ ] Add a toggleable replay-quality overlay.
- [ ] Visualize degraded windows on the timeline or waveform.
- [ ] Surface severe windows without blocking playback.
- [ ] Reserve modal/pre-open warnings for sustained severe degradation only.

Acceptance:

- Replay can explain stale/gap artifacts when they appear, without alarming the
  user for isolated rows.

### 3. Context Strip And Event Timeline

- [ ] Render flag, safety car, VSC, and red-flag periods as timeline bands.
- [ ] Render race-control messages as timestamped markers.
- [ ] Render rainfall periods as stepped bands.
- [ ] Add filters by race-control taxonomy.
- [ ] Collapse or group repeated messages.
- [ ] Selecting a timestamped event seeks the replay cursor.

Acceptance:

- The context strip uses imported data directly and does not claim continuous
  coverage for event-only surfaces.

### 4. Track Map And Circuit Context

- [ ] Keep the track outline derived from imported `position_samples`.
- [ ] Render corners, marshal lights, and marshal sectors from
  `circuit_markers`.
- [ ] Display circuit marker QA status in a quiet detail area.
- [ ] If marker QA ever flags a session, show markers as inspectable/limited
  rather than silently hiding them.

Acceptance:

- 2025 track maps can show circuit markers now; marker QA currently has zero
  outside-position-bounds issues.

### 5. Weather Panel

- [ ] Show current weather at the replay cursor using nearest/stepped samples.
- [ ] Show rainfall transitions in the context strip.
- [ ] Add a compact trend panel for air temperature, track temperature, and
  rainfall.
- [ ] Do not animate or interpolate weather between samples.

Acceptance:

- Weather behaves like low-frequency context data, not telemetry.

### 6. Race-Control Timeline UX

- [ ] Add taxonomy chips/filters.
- [ ] Add duplicate-group expansion.
- [ ] Keep original message text visible.
- [ ] Use cluster summaries only for analysis/debug affordances.

Acceptance:

- Blue/yellow/sector-message noise is manageable without losing source detail.

## Mark Not Feasible With Current Data

These design ideas should be shown as unavailable, approximate, or deferred
until the listed data gap is closed.

### 1. Exact Raw-Position Coverage Warnings

- [ ] Mark as not feasible until `position_samples.session_time_ms` exists.
- [ ] Do not show raw-position coverage warnings based only on UTC-offset
  approximation.

### 2. Official Session End-Time Display

- [ ] Mark official end time as unavailable while `session_end_utc` is missing.
- [ ] Use derived replay duration for transport and coverage only.

### 3. Qualifying/Grid-Derived Overview Fields

- [ ] Mark Pole Position as `not imported` unless qualifying/grid metadata is
  imported.
- [ ] Mark Grid Position as `not imported` unless grid metadata is imported.
- [ ] Do not infer qualifying results from race lap data.

### 4. Causal Incident And Weather Claims

- [ ] Support correlation/overlap language only.
- [ ] Avoid wording such as "rain caused VSC" or "incident caused replay
  degradation".
- [ ] Use "near", "overlaps", "followed by", or "same window".

### 5. Corner-Level Brake-Point Delta In Metres

- [ ] Mark as approximate/deferred until position-aware corner matching is
  improved.
- [ ] Do not present brake-point metres as exact when derived from current
  telemetry-window matching.

### 6. Hard-Braking Hotspots With Confident Corner Attribution

- [ ] Permit hard-braking event dots as telemetry-event candidates.
- [ ] Mark corner/location attribution as approximate until corner matching is
  improved.

### 7. Tire Degradation Or Pit-Window Predictor

- [ ] Keep measured stint summaries.
- [ ] Mark predictive pit-window recommendations as not feasible from current
  validated data.
- [ ] If added later, visually distinguish modelled values from measured values.

### 8. Official Live-Timing Style Gaps

- [ ] Avoid presenting derived gaps/intervals as official live timing unless the
  backend explicitly computes and qualifies them.
- [ ] Prefer lap-summary and replay-position-derived labels with clear wording.

## Suggested Implementation Order

1. Backend readiness/replay-quality contracts.
2. Desktop session-browser quality labels.
3. Replay quality overlay and context strip.
4. Race-control taxonomy/grouping endpoint and timeline filters.
5. Weather trend/cursor panel.
6. Circuit marker QA metadata in circuit context.
7. Schema/importer fix for `session_end_utc` and `position_samples.session_time_ms`.
8. Re-run EDA and tighten readiness labels after schema/importer changes.

