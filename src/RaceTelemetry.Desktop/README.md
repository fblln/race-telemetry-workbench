# RaceTelemetry.Desktop

This folder is reserved for the high-performance .NET MAUI desktop replay app.

The first desktop slice can be created against the existing Query API replay
metadata, replay chunk, replay context, and lap-comparison endpoints. Keep the
desktop app as an Aspire-adjacent client: it should consume the Query API over
HTTP and avoid direct database access.

Planned shape:

- .NET MAUI desktop-first application.
- MVVM view models for session selection, replay controls, track map, and charts.
- Typed HTTP client generated or wrapped around `RaceTelemetry.Contracts`.
- Viewport-aware drawing for dense track, waveform, and timeline surfaces.
- Virtualized rows for laps, events, race-control messages, and telemetry-event candidates.
- Integration tests that exercise the Query API contract before UI playback tests.
