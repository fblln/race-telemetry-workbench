# Race Telemetry Query API Bruno Collection

Open this folder in Bruno:

```text
bruno/race-telemetry-query-api
```

Select an environment:

| Environment | Use when |
|---|---|
| `Local` | Query API is running on `http://127.0.0.1:5120`. |
| `Rider` | Query API is running from Rider on the printed HTTP port. Update `baseUrl` if Rider prints a different port. |

Aspire is configured to expose stable local ports:

| Resource | URL |
|---|---|
| Query API HTTP | `http://127.0.0.1:5120` |
| Query API HTTPS | `https://127.0.0.1:5121` |
| Aspire Dashboard HTTPS | `https://127.0.0.1:18888` |
| Aspire Dashboard HTTP | `http://127.0.0.1:18889` |

Required variables:

| Variable | Meaning |
|---|---|
| `baseUrl` | Query API root URL, without a trailing slash. |
| `sessionId` | Imported session id, for example `2025-italian-grand-prix-r`. |
| `driverA`, `driverB` | Driver codes used by lap, comparison, replay, and event requests. |
| `lapA`, `lapB` | Lap numbers used by telemetry and comparison requests. |
| `fromMs` | Replay/context window start in session-relative milliseconds. |
| `replayDurationMs` | Replay chunk duration. |
| `contextDurationMs` | Replay context duration. |

Recommended first run:

1. `API Info`
2. `List Sessions`
3. Update `sessionId` if needed.
4. `List Drivers`
5. Update `driverA` and `driverB` if needed.
6. Run `Race Story`, `Lap Story`, `Lap Braking Zones`, and `Compare Laps Story`.
7. Drill into replay, raw comparison, telemetry, and event requests when needed.

For the imported 2025 Monza race, replay samples start around `3470000ms`.
If you switch `sessionId`, first run `Replay Metadata` and set `fromMs` near
the returned `replayStartMs`.

Analysis requests:

| Request | Use when |
|---|---|
| `Race Story` | Get race-level weather, tyre stints, pit markers, track-status periods, race-control messages, and insights. |
| `Lap Story` | Get a compact lap summary with sectors, tyre context, telemetry aggregates, and insights. |
| `Lap Braking Zones` | Detect contiguous braking windows and nearest corner labels when position/circuit data aligns. |
| `Compare Laps Story` | Compare two laps with total delta, sector deltas, coarse lap segments, and talking points. |
| `Telemetry Aggregate` | Summarize speed, braking time, DRS-active time, lift count, and high-speed time by driver/lap/stint/compound/status/time bucket without returning raw samples. |
| `Telemetry Windows` | Detect contiguous DRS, hard-braking, throttle-lift, or high-speed windows across many laps without downloading full telemetry. |
| `Stint Analysis` | Compare stint lap-time trends and tyre-degradation signals by driver and compound. |
| `Pit Stop Analysis` | Estimate pit-lap loss against nearby non-pit laps. |
| `Weather Trend` | Summarize weather deltas and rainfall for a selected time window. |
| `Race Control Timeline` | Search and bucket race-control messages without fetching replay context windows. |
| `Circuit Context` | Inspect imported circuit corners and marshal markers. |
