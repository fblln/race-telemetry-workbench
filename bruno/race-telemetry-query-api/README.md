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
6. Run the replay, comparison, and event requests.

For the imported 2025 Monza race, replay samples start around `3470000ms`.
If you switch `sessionId`, first run `Replay Metadata` and set `fromMs` near
the returned `replayStartMs`.
