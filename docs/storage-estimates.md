# Storage Estimates

Storage has two layers. Project planning uses race sessions as the default scope:

- FastF1 raw cache: HTTP responses stored by FastF1 in
  `data/fastf1-cache/fastf1_http_cache.sqlite`.
- Future database storage: normalized TimescaleDB rows plus indexes.

Only the raw FastF1 cache exists right now. Non-race sessions are opt-in and
should be estimated separately when we choose to support them as first-class data.

## Current Observed Data

After downloading the requested Monza race sessions:

| Downloaded sessions | Raw cache size | Manifest size |
|---:|---:|---:|
| 2 race sessions | 169 MB | 16 KB |

The two manifests contain:

| Session | Telemetry samples | Position samples |
|---|---:|---:|
| 2024 Monza Race | 324,546 | 333,505 |
| 2025 Monza Race | 305,177 | 312,768 |

That gives a rough raw-cache average of about `84.5 MB` per race session.

## Estimator

Run:

```bash
python3 scripts/estimate_storage.py
```

With the current Monza-only cache, the estimator projects:

| Scope | Assumption | Estimated raw FastF1 cache |
|---|---|---:|
| Default race-only season | 24 race sessions | about 2.0 GB |
| Opt-in full weekend season | 24 events x 5 sessions | about 9.9 GB |

This is a planning estimate, not a quota guarantee. Some sessions are shorter,
some races have more laps, sprint weekends have different session shapes, and
FastF1's cache includes shared schedule/session metadata.

## Database Planning Reserve

TimescaleDB storage will be larger than the raw cache because we will store
normalized rows and indexes for query speed. Until the importer exists and we
measure it directly, plan conservatively:

| Scope | Suggested local reserve |
|---|---:|
| One race session imported to DB | 250 MB to 750 MB |
| Race-only season imported to DB | 8 GB to 18 GB |
| Full-weekend season imported to DB | 25 GB to 60 GB |

The first real database importer task should measure:

- bytes per telemetry row;
- bytes per position row;
- index size for replay and lap-comparison queries;
- compression savings if TimescaleDB compression is enabled later.
