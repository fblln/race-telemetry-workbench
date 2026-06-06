# Data Download Script

`scripts/download_session.py` downloads Formula 1 race data through FastF1 and
validates that the source data needed by the later database importer is present.
It does not write to TimescaleDB yet; it warms the FastF1 cache and writes a JSON
manifest with row counts and data-quality warnings.

Race sessions are the default project scope. Practice, qualifying, sprint
qualifying, and sprint sessions can still be downloaded explicitly with
`--session`, but year/event commands default to `R`.

Related docs:

- [FastF1 raw data notes](fastf1-raw-data.md)
- [Storage estimates](storage-estimates.md)

## Install Dependencies

```bash
python3 -m pip install -r scripts/requirements.txt
```

## Basic Usage

```bash
python3 scripts/download_session.py --year 2024 --event "Monza"
```

Useful development options:

```bash
python3 scripts/download_session.py --year 2024 --event "Monza" --drivers VER,LEC
python3 scripts/download_session.py --year 2025 --event "Monza" --limit-laps 3
python3 scripts/download_session.py --year 2024 --event "Silverstone" --session Q --log-level DEBUG
```

## CLI Options

| Option | Required | Default | Description |
|---|---:|---|---|
| `--year` | Yes | | Championship year, for example `2024`. |
| `--event` | Yes | | Event, circuit, or Grand Prix name accepted by FastF1, for example `Monza`. |
| `--session` | No | `R` | Session identifier: `FP1`, `FP2`, `FP3`, `Q`, `SQ`, `S`, or `R`. Non-race sessions are opt-in. |
| `--drivers` | No | all drivers | Comma-separated driver code subset such as `VER,HAM,LEC`. |
| `--limit-laps` | No | no limit | Validates only the first N laps per selected driver. Useful for smoke tests. |
| `--cache-dir` | No | `data/fastf1-cache` | FastF1 cache directory. |
| `--manifest-dir` | No | `data/download-manifests` | Output directory for JSON manifests. |
| `--log-level` | No | `INFO` | Logging verbosity. |

## Outputs

The script writes one manifest per session:

```text
data/download-manifests/{session_id}.json
```

The manifest includes:

- stable `session_id`, using `{year}-{event-slug}-{session-lower}`;
- official FastF1 event metadata when available;
- cache directory used for the source download;
- per-driver lap, telemetry, and position sample counts;
- laps where car telemetry or position data could not be loaded;
- aggregate totals.

Full-session runs write `{session_id}.json`. Filtered or limited development runs
write a subset manifest such as:

```text
data/download-manifests/2024-italian-grand-prix-r-subset-lec-first-3-laps.json
```

This keeps smoke tests from overwriting the canonical full-session manifest.

A successful run prints:

```text
Download completed successfully.
Session: 2024 Monza R
Session ID: 2024-italian-grand-prix-r
Drivers: 20
Laps: 1062
Telemetry samples: 3,481,220
Position samples: 3,481,220
Cache: /absolute/path/data/fastf1-cache
Manifest: /absolute/path/data/download-manifests/2024-italian-grand-prix-r.json
Elapsed: 0:02:41
```

## Test Commands

Fast local unit tests:

```bash
python3 -m unittest discover -s tests
```

Storage estimate from the current cache and manifests:

```bash
python3 scripts/estimate_storage.py
```

End-to-end download checks requested for this project:

```bash
python3 scripts/download_session.py --year 2024 --event "Monza" --session R
python3 scripts/download_session.py --year 2025 --event "Monza" --session R
```

These end-to-end checks need internet access on the first run. Later runs reuse
`data/fastf1-cache`.

## Verified Sessions

The script has been tested against the requested Monza race sessions:

| Session | Drivers | Laps | Telemetry samples | Position samples | Missing telemetry laps | Missing position laps |
|---|---:|---:|---:|---:|---:|---:|
| 2024 Monza Race | 20 | 1,008 | 324,546 | 333,505 | 0 | 0 |
| 2025 Monza Race | 20 | 974 | 305,177 | 312,768 | 0 | 0 |

FastF1 emitted a lap-accuracy warning for driver number `27` during the 2025 race load.
The downloaded lap, telemetry, and position data still validated with no missing sample laps.

## Relationship To The Importer

This script is intentionally the fetch/validation half of the future
`scripts/import_session.py`. The importer should reuse the same session resolution,
driver filtering, cache location, stable ID generation, and data-quality expectations,
then add TimescaleDB normalization and write modes (`fail`, `upsert`, `replace`).
