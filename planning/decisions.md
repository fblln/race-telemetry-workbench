# Decisions

## 2026-06-06 - Track Architecture Spec In Git

The architecture spec is tracked in git so product, data, database, API, replay,
MCP, and licensing decisions can be reviewed directly on GitHub.

Planning files in this folder should continue to capture actionable
implementation state derived from the spec.

## 2026-06-06 - Initial Work Order

Follow the implementation phases from the spec:

1. Database and import.
2. Query API.
3. Desktop replay.
4. Lap comparison.
5. MCP query server.
6. Optional AI assistant panel.

## 2026-06-06 - GNU GPLv3 License

The project license was changed from MIT to GNU General Public License version 3.

GPLv3 is a copyleft free-software license. It permits use, copying,
modification, distribution, and commercial use under the license terms, while
requiring distributed derivative works to preserve the same freedoms.

## 2026-06-06 - TimescaleDB Primary Storage

TimescaleDB is the primary storage target for the product because the expected
scope includes multiple years, multiple sessions, replay queries, lap
comparison, weather/context overlays, and in-database analytics for MCP-backed
questions.

Use ordinary PostgreSQL tables for bounded relational/event metadata, and
Timescale hypertables for high-volume or time-windowed sample data such as
telemetry, position, and weather samples.

DuckDB, ClickHouse, QuestDB, and plain PostgreSQL are not the primary
implementation path. They may be reconsidered later for export/offline analysis
or scale-specific secondary analytics.

## 2026-06-07 - Aspire Stable Port Model

The Query API should be exposed through a stable Aspire/DCP external HTTP port
for manual testing and Bruno. The project process itself must not bind the same
external port.

Use this AppHost pattern:

```csharp
builder.AddProject<Projects.RaceTelemetry_QueryApi>("query-api")
    .WithEnvironment("RACE_TELEMETRY_DATABASE_URL", databaseUrl)
    .WithHttpEndpoint(port: 5120, env: "ASPNETCORE_HTTP_PORTS")
    .WithExternalHttpEndpoints();
```

Avoid:

- Hard-coding `ASPNETCORE_URLS` to `http://127.0.0.1:5120` under Aspire.
- Setting identical `targetPort` and `port` on a proxied project resource.
- Starting a second AppHost or Rider-launched Query API on the stable port.

If Aspire Dashboard is running but `query-api` is `Finished`, first inspect
`aspire logs query-api --non-interactive`. The most common failure is a port
collision between Kestrel and the DCP proxy.

## 2026-06-07 - Natural-Language Analysis Shape

MCP and Query API should expose compact, structured analytical tools in addition
to raw sample retrieval.

Natural-language clients should start with race/lap story tools that return
bounded facts, summaries, and deterministic insight labels:

- race story: weather, stints, pit markers, track status, race-control context;
- lap story: lap time, sectors, tyre context, speed/throttle/brake aggregates;
- braking zones: contiguous brake windows plus nearest corner labels where
  source data aligns;
- comparison story: total delta, sector deltas, coarse segment differences.

Raw telemetry, replay chunks, and bucketed comparisons remain available for
drill-down, charting, and validation. They should not be the first tool a
language model needs to call for broad "what happened in this race?" questions.

## 2026-06-08 - Query API And MCP Analytical Parity

MCP analytical capabilities should stay in sync with the Query API. The default
rule is:

- first add or expose a shared contract and Query API route;
- then expose an MCP tool as a thin, read-only adapter over that same bounded
  capability;
- only allow MCP-only analytical tools when a decision record documents why the
  capability is not useful over REST.

For complex natural-language questions, prefer generic analytical primitives
instead of fetching raw telemetry or adding one tool per question:

- `aggregate_telemetry`: grouped metrics such as DRS active time, brake time,
  average speed, max speed, sample count, and throttle-lift count;
- `detect_telemetry_windows`: contiguous intervals for DRS active, hard
  braking, throttle lift, high speed, and similar events;
- `analyze_driver_stints`: tyre degradation, stint lap-time slope, best/worst
  lap, tyre-life range, and compound strategy summaries.

Raw telemetry endpoints remain bounded drill-down tools after an aggregate,
window, or stint query identifies a specific lap or time range.
