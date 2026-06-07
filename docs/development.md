# Development Guide

This is the day-to-day command guide for working on Race Telemetry Workbench.
The repo uses a mixed stack:

- .NET 10, MSBuild, NuGet, and Aspire for the application layer.
- Python and FastF1 for data download/import.
- TimescaleDB/PostgreSQL for storage.

If you come from Java/Gradle, the rough mapping is:

| Java/Gradle | .NET/Aspire |
|---|---|
| `settings.gradle` | `RaceTelemetryWorkbench.slnx` |
| `build.gradle` | `*.csproj` |
| Gradle dependency | NuGet package reference |
| `./gradlew build` | `dotnet build` |
| `./gradlew test` | `dotnet test` |
| Spring Boot run config | `dotnet run` for one app, `aspire start` for the distributed app |
| Testcontainers/local compose | Aspire resources or `docker compose` |

## One-Time Shell Setup

On this machine the SDK is installed at `/usr/local/share/dotnet/dotnet`. Add it
to your shell path if `dotnet --info` does not work:

```bash
export PATH="/usr/local/share/dotnet:$HOME/.docker/bin:$PATH"
```

Useful checks:

```bash
dotnet --info
aspire --version
docker version
```

Python setup:

```bash
python3 -m venv .venv
.venv/bin/python -m pip install -r scripts/requirements.txt
```

## Normal .NET Inner Loop

Restore NuGet packages:

```bash
dotnet restore RaceTelemetryWorkbench.slnx
```

Build everything:

```bash
dotnet build RaceTelemetryWorkbench.slnx
```

Run all .NET tests once standard test projects are in place:

```bash
dotnet test RaceTelemetryWorkbench.slnx
```

Run the current console-based API integration checks:

```bash
dotnet run --project tests/RaceTelemetry.IntegrationTests/RaceTelemetry.IntegrationTests.csproj
```

Build a single project when you are working locally in one area:

```bash
dotnet build src/RaceTelemetry.QueryApi/RaceTelemetry.QueryApi.csproj
dotnet build src/RaceTelemetry.Data/RaceTelemetry.Data.csproj
```

## NuGet Package Commands

Add a package:

```bash
dotnet add src/RaceTelemetry.QueryApi/RaceTelemetry.QueryApi.csproj package Npgsql
```

List package references:

```bash
dotnet list RaceTelemetryWorkbench.slnx package
```

Check outdated packages:

```bash
dotnet list RaceTelemetryWorkbench.slnx package --outdated
```

Remove a package:

```bash
dotnet remove src/RaceTelemetry.QueryApi/RaceTelemetry.QueryApi.csproj package Npgsql
```

Package references live in the project files. Example:

```text
src/RaceTelemetry.QueryApi/RaceTelemetry.QueryApi.csproj
```

## Aspire App Loop

Use Aspire to run the distributed development app. Do not run the AppHost with
`dotnet run`; use Aspire so resources, endpoints, environment variables, and
telemetry are managed consistently.

Start the app:

```bash
aspire start
```

In automation or agent runs, prefer:

```bash
aspire start --non-interactive
```

See resources:

```bash
aspire ps
aspire ps --include-hidden
```

Describe the full app model:

```bash
aspire describe
aspire describe --include-hidden
```

Wait for the Query API resource:

```bash
aspire wait query-api
```

Stop the app:

```bash
aspire stop
```

Important: if a build fails with file-lock errors while Aspire is running, stop
Aspire first:

```bash
aspire stop
dotnet build RaceTelemetryWorkbench.slnx
```

The AppHost is:

```text
src/RaceTelemetry.AppHost/AppHost.cs
```

## Query API Checks

When Aspire is running, use the dashboard or `aspire ps` to find the Query API
HTTP endpoint. Then call:

```bash
curl http://127.0.0.1:{port}/api/
curl http://127.0.0.1:{port}/api/sessions
curl http://127.0.0.1:{port}/api/sessions/2025-italian-grand-prix-r/drivers
curl http://127.0.0.1:{port}/api/sessions/2025-italian-grand-prix-r/drivers/LEC/laps
curl http://127.0.0.1:{port}/api/sessions/2025-italian-grand-prix-r/replay/metadata
curl "http://127.0.0.1:{port}/api/sessions/2025-italian-grand-prix-r/replay/chunk?fromMs=60000&durationMs=30000&drivers=LEC,VER&sampleEvery=2"
curl "http://127.0.0.1:{port}/api/sessions/2025-italian-grand-prix-r/replay/context?fromMs=60000&durationMs=300000"
```

There is also an HTTP scratch file:

```text
src/RaceTelemetry.QueryApi/RaceTelemetry.QueryApi.http
```

Bruno users can open the collection at:

```text
bruno/race-telemetry-query-api
```

Set the collection `baseUrl` variable to the Query API endpoint shown by Aspire
or `dotnet run`.

## Database Loop

Start local TimescaleDB:

```bash
docker compose up -d timescaledb
```

Check status:

```bash
docker compose ps
docker compose logs -f timescaledb
```

Default database URL:

```text
postgresql://race_telemetry:race_telemetry@localhost:5432/race_telemetry
```

Set it explicitly when needed:

```bash
export RACE_TELEMETRY_DATABASE_URL="postgresql://race_telemetry:race_telemetry@localhost:5432/race_telemetry"
```

Stop the database container:

```bash
docker compose stop timescaledb
```

Remove the database volume only when you intentionally want a clean database:

```bash
docker compose down -v
```

## Python Test Loop

Run all Python tests:

```bash
.venv/bin/python -m unittest discover -s tests
```

Run database migration integration tests:

```bash
.venv/bin/python -m unittest tests.test_database_migrations
```

The database integration tests apply the real migrations in a temporary schema
and verify tables, hypertables, views, and representative data.

## Data Download And Import Loop

Warm the FastF1 cache and validate source data:

```bash
.venv/bin/python scripts/download_session.py --year 2024 --event Monza
```

Fast importer smoke test:

```bash
.venv/bin/python scripts/import_session.py \
  --year 2024 \
  --event Monza \
  --drivers LEC \
  --limit-laps 1 \
  --mode replace
```

Full Monza race import:

```bash
.venv/bin/python scripts/import_session.py \
  --year 2024 \
  --event Monza \
  --mode replace
```

Import multiple races:

```bash
.venv/bin/python scripts/import_sessions.py \
  --spec 2024:Monza:R \
  --spec 2025:Monza:R \
  --mode replace \
  --workers 2
```

Start bulk imports conservatively. Use `--workers 2` first, then increase only
after checking database CPU, disk I/O, and connection count.

## Recommended Daily Flow

For application work:

```bash
dotnet restore RaceTelemetryWorkbench.slnx
dotnet build RaceTelemetryWorkbench.slnx
dotnet run --project tests/RaceTelemetry.IntegrationTests/RaceTelemetry.IntegrationTests.csproj
aspire start
```

For importer/database work:

```bash
docker compose up -d timescaledb
.venv/bin/python -m unittest tests.test_database_migrations
.venv/bin/python scripts/import_session.py --year 2024 --event Monza --drivers LEC --limit-laps 1 --mode replace
```

For a full verification pass:

```bash
dotnet restore RaceTelemetryWorkbench.slnx
dotnet build RaceTelemetryWorkbench.slnx
dotnet run --project tests/RaceTelemetry.IntegrationTests/RaceTelemetry.IntegrationTests.csproj
.venv/bin/python -m unittest discover -s tests
```

## Troubleshooting

`localhost:0` dynamic port error:

Use `127.0.0.1:0` instead. Dynamic port binding does not work with
`localhost:0`.

NuGet vulnerability feed warning `NU1900`:

This means the project built, but NuGet could not reach the vulnerability-data
feed. It is usually a network/connectivity warning, not a compile failure.

Build fails with locked files:

```bash
aspire stop
dotnet build RaceTelemetryWorkbench.slnx
```

Docker command not found:

```bash
export PATH="$HOME/.docker/bin:$PATH"
docker version
```

Query API endpoint unknown:

```bash
aspire ps
```

Use the endpoint shown for `query-api`.
