# RaceTelemetry.Desktop

This folder is reserved for the Avalonia desktop replay app.

The first desktop slice should be created after the Query API exposes replay
metadata and chunk endpoints. Keep the desktop app as an Aspire-adjacent client:
it should consume the Query API over HTTP and avoid direct database access.

Planned shape:

- Avalonia `net10.0` application.
- MVVM view models for session selection, replay controls, track map, and charts.
- Typed HTTP client generated or wrapped around `RaceTelemetry.Contracts`.
- Integration tests that exercise the Query API contract before UI playback tests.
