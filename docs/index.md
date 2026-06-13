# Race Telemetry Workbench

Race Telemetry Workbench is a local Formula 1 telemetry analysis project. It
imports public FastF1 race data into TimescaleDB, exposes bounded query surfaces
through a .NET REST API and MCP server, and is evolving toward a desktop replay
workbench.

The documentation site starts with the local development workflow, the data
path, and the Query API/MCP contract. The checked-in OpenAPI document is
intended to be the stable input for generated API reference pages and future
documentation agents.

## Core Areas

- [API and MCP](api.md): REST endpoints, MCP tools, validation, observability,
  and manual checks.
- [OpenAPI reference](api/openapi.md): rendered API contract backed by the
  checked-in schema.
- [Development guide](development.md): day-to-day commands for the local app.
- [Data](data.md): FastF1 source data, download validation, imports, and
  storage estimates.
- [Database schema](database-schema.md): imported storage model and analytical
  views.
