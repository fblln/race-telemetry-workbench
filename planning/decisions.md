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
