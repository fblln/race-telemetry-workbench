# Database

The database target is TimescaleDB, running PostgreSQL with the TimescaleDB
extension enabled.

## Local Docker

Start the local database:

```bash
docker compose up -d timescaledb
```

Check container health:

```bash
docker compose ps timescaledb
```

Open `psql` inside the container:

```bash
docker compose exec timescaledb psql -U race_telemetry -d race_telemetry
```

Connection details:

| Setting | Value |
|---|---|
| Host | `localhost` |
| Port | `5432` |
| Database | `race_telemetry` |
| User | `race_telemetry` |
| Password | `race_telemetry` |

The container stores data in the `timescaledb-data` Docker volume. On first
startup, PostgreSQL runs all SQL files mounted from `db/migrations` into
`/docker-entrypoint-initdb.d`.

Docker only runs `/docker-entrypoint-initdb.d` scripts when the database volume
is created for the first time. If migrations are added after the container has
already initialized, either apply the new SQL manually with `psql` or recreate
the local volume.

To recreate the database from scratch:

```bash
docker compose down -v
docker compose up -d timescaledb
```

That deletes the local database volume.

## Migrations

Apply migrations in filename order:

1. `001_initial_schema.sql`
2. `002_timescale_hypertables.sql`
3. `003_analytical_views.sql`
4. `004_remove_composed_telemetry_columns.sql`

The initial schema creates PostgreSQL relational tables for session metadata,
laps, circuit annotations, weather, and race-control context. High-volume sample
tables are created as normal tables first, then converted to Timescale
hypertables in the second migration.

Analytical views are intentionally bounded summary surfaces for the Query API
and MCP server. Raw telemetry remains available for replay and lap comparison,
but MCP-facing analytical questions should prefer these views or API endpoints
derived from them.

The fourth migration removes composed telemetry columns from existing local
databases after the product switched to raw FastF1 car and position streams as
the canonical import shape.

Database comments live in the same migration as the objects they describe:
table comments are in `001_initial_schema.sql`, and view comments are in
`003_analytical_views.sql`.

For the full schema explanation, table meanings, view logic, visual ER model,
and example queries, read `db/schema.md`.

## Integration Tests

Database tests run against a real TimescaleDB database. By default they connect
to the Docker Compose database:

```text
postgresql://race_telemetry:race_telemetry@localhost:5432/race_telemetry
```

To use a different database, set `RACE_TELEMETRY_DATABASE_URL`.

Run the tests:

```bash
docker compose up -d timescaledb
.venv/bin/python -m unittest tests.test_database_migrations
```

The test suite creates a unique temporary schema, applies the migrations, inserts
race-shaped Monza fixture data, verifies the real tables and analytical views,
then drops the schema.
