# Apexline Repository Migration Backlog

This document is the handoff plan for moving the Apexline work out of this
repository and into its own project.

## Decision

Move the current Apexline prototype from:

```text
standalone/apexline/
```

to a sibling repository:

```text
/Users/fabio/Workspace/apexline
```

Do not make `/Users/fabio/Workspace` a Git repository. Use one Codex
thread/workspace per repository for normal work, and use an editor multi-root
workspace when both projects need to be open at the same time.

## Project Scope

Apexline validates Formula 1 circuit reference geometry against driven FastF1
position traces, classifies laps suitable for geometry fitting, reports
shape/path-length diagnostics, and emits reusable per-lap and per-circuit
quality artifacts.

It should be treated as a research and validation tool first. It is good enough
to be its own project, but not yet a production library until the CLI, package
shape, schemas, and tests are tightened.

## Boundary With Race Telemetry Workbench

Apexline owns:

- FastF1 position-trace geometry validation.
- Circuit polyline fitting, simplification, and diagnostic metrics.
- Lap rejection reasons for geometry-reference suitability.
- Per-lap geometry diagnostics.
- Per-circuit validation summaries.
- Rejected-lap galleries and geometry QA documentation.
- Output schemas for Apexline artifacts.

Race Telemetry Workbench owns:

- Importing race telemetry into TimescaleDB.
- Query API, MCP, and desktop replay behavior.
- Joining Apexline outputs into the bad-lap EDA or future product data model.
- Product decisions about replay/comparison safety labels.
- Persisting or exposing quality flags through app-facing APIs.

The workbench should consume Apexline outputs through documented files, a CLI,
or a package interface. It should not keep copied Apexline implementation code
after the split.

## Migration Checklist

### 1. Create The New Repository

- [ ] Create `/Users/fabio/Workspace/apexline`.
- [ ] Initialize a new Git repository there.
- [ ] Move the contents of `standalone/apexline/` into the new repository root.
- [ ] Keep the existing `README.md` as the starting project README.
- [ ] Preserve the existing docs, scripts, data artifacts, and SVG assets during
  the first move so behavior can be verified before cleanup.
- [ ] Do not move unrelated Race Telemetry Workbench EDA artifacts.

### 2. Clean Generated And Local-Only Files

- [ ] Remove Python bytecode caches such as `scripts/__pycache__/`.
- [ ] Remove local matplotlib cache state such as `.matplotlib-cache/`.
- [ ] Review `.gitignore` so local FastF1 caches, virtualenvs, Python caches,
  OS files, and generated scratch outputs stay untracked.
- [ ] Decide which generated artifacts are intentional examples, fixtures, or
  release assets.
- [ ] Keep intentional docs assets under `docs/assets/`.
- [ ] Keep small sample data only if it is needed for reproducible tests or
  documentation.

### 3. Package And CLI Shape

- [ ] Add a `pyproject.toml`.
- [ ] Convert the main script into an importable Python package.
- [ ] Add a console entry point, for example `apexline`.
- [ ] Split reusable geometry, FastF1 loading, lap classification, plotting, and
  serialization code out of the large analysis script.
- [ ] Keep script wrappers for existing workflows if useful, but have them call
  package functions.
- [ ] Add CLI arguments for year, event/session scope, output directory,
  FastF1 cache directory, and overwrite/update behavior.
- [ ] Keep race sessions as the default scope unless explicitly overridden.

### 4. Artifact Schemas

- [ ] Define the per-lap diagnostics schema.
- [ ] Define the per-circuit polyline schema.
- [ ] Define the event/session summary schema.
- [ ] Define rejected-lap gallery metadata schema.
- [ ] Document stable IDs needed by downstream consumers:
  `year`, `round`, `event_name`, `session_type`, `driver`, `lap_number`, and any
  deterministic lap key.
- [ ] Document metric units and thresholds, including RMSE, p50, p95, max error,
  path length, path-ratio deviation, sample counts, and rejection reasons.
- [ ] Version output schemas so Race Telemetry Workbench can validate imports.

### 5. Tests

- [ ] Add unit tests for closed-path resampling and polyline simplification.
- [ ] Add unit tests for transform fitting, including direction and phase
  handling.
- [ ] Add tests for lap rejection reasons:
  `fastf1_inaccurate`, `pit_lap`, `missing_lap_time`,
  `too_few_position_samples`, `path_length_outlier`,
  `shape_rmse_over_threshold`, and `shape_p95_over_threshold`.
- [ ] Add schema validation tests for generated JSON/CSV artifacts.
- [ ] Add a small fixture-based smoke test that runs without downloading a full
  season.
- [ ] Add an optional integration test path for real FastF1 data, gated behind an
  explicit opt-in environment variable or test marker.

### 6. Documentation

- [ ] Keep the current README narrative and tighten it around the standalone
  project scope.
- [ ] Add a "Quickstart" section for setup, cache configuration, and one small
  validation run.
- [ ] Add a "Outputs" section with schema links and example paths.
- [ ] Add a "Downstream Consumption" section explaining how Race Telemetry
  Workbench should use Apexline outputs.
- [ ] Add a "Limitations" section covering FastF1 local X/Y coordinates, lack of
  raw GPS car telemetry, session data availability, and threshold sensitivity.
- [ ] Add a "License And Data Sources" section that distinguishes project code,
  generated artifacts, FastF1-derived data, and source circuit-line data.

### 7. Race Telemetry Workbench Follow-Up

- [ ] Replace `standalone/apexline/` with a short pointer document after the new
  repository is verified.
- [ ] Add a workbench-side importer or loader for Apexline per-lap diagnostics
  only after the Apexline output schema is stable.
- [ ] Join Apexline per-lap diagnostics into the bad-lap EDA classified-lap
  table.
- [ ] Add geometry-specific quality categories derived from Apexline outputs.
- [ ] Compare imported position path metrics against Apexline FastF1-derived
  path metrics.
- [ ] Keep workbench product labels aligned with Carbon Signal terminology.

## Suggested First Codex Session

Use `/Users/fabio/Workspace/apexline` as the workspace for the new session and
ask Codex to:

1. Move the current `standalone/apexline` contents into the new repository.
2. Initialize Git.
3. Remove local-only cache files.
4. Add `pyproject.toml`.
5. Convert the current scripts into a minimal importable package plus CLI while
   preserving current generated outputs.
6. Run a smoke validation against the existing checked-in artifacts.

Keep the first session focused on extraction and reproducibility. Deeper
refactoring, package polish, and Race Telemetry Workbench integration should
come after the new repository can reproduce the current Apexline outputs.

