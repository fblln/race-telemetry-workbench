# Database Migrations

The database target is TimescaleDB. Apply migrations in filename order:

1. `001_initial_schema.sql`
2. `002_timescale_hypertables.sql`
3. `003_analytical_views.sql`

The initial schema creates PostgreSQL relational tables for session metadata,
laps, circuit annotations, weather, and race-control context. High-volume sample
tables are created as normal tables first, then converted to Timescale
hypertables in the second migration.

Analytical views are intentionally bounded summary surfaces for the Query API
and MCP server. Raw telemetry remains available for replay and lap comparison,
but MCP-facing analytical questions should prefer these views or API endpoints
derived from them.
