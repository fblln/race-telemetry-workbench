# RaceTelemetry.Desktop

The high-performance .NET MAUI desktop application — the primary product surface
(spec §8). It consumes the Query API over HTTP and never touches the database
directly (§8.1). The UI implements the project-owned **Carbon Signal** design
system (`docs/design-system/`, §8.8).

## What this scaffold contains

```
RaceTelemetry.Desktop/
  RaceTelemetry.Desktop.csproj   # net10.0 MAUI, desktop targets (Mac Catalyst + Windows)
  MauiProgram.cs                 # DI: HttpClient → QueryApiClient, view models, pages, fonts
  App.xaml(.cs)                  # merges Theme.Carbon + Styles; opens a workbench-sized window
  AppShell.xaml(.cs)             # Shell host; root = Launcher, route "console"
  Converters.cs                  # IsNotNull / DashIfEmpty value converters
  Controls/
    TrackMapDrawable.cs          # data-derived Monza outline IDrawable (§7.3)
  Services/
    QueryApiClient.cs            # typed read-only client over Query API contracts (§5,§6)
    ApiModels.cs                 # local DTOs for §6.11–6.16 until they land in Contracts
    SessionPrefetchService.cs    # eager in-memory snapshot cache (§8.9) — see below
  ViewModels/                    # CommunityToolkit.Mvvm view models
    LauncherViewModel.cs
    SessionConsoleViewModel.cs   # breadcrumb, view rail, HUD (§8.11)
    FieldViewViewModel.cs        # timing tower (§8.13)
    TrackIncidentsViewModel.cs   # incidents + hard-braking (§8.14)
    StubViewModels.cs            # Replay / Lap comparison placeholders
  Views/
    LauncherPage.xaml(.cs)       # Home / launcher (§8.11)
    SessionConsolePage.xaml(.cs) # console shell: command bar + view rail + content host
    FieldView.xaml(.cs)          # all-driver timing tower
    TrackIncidentsView.xaml(.cs) # Monza map + incident list
    ReplayWorkspaceView / LapComparisonView  # scaffold placeholders
    PlaceholderView.cs           # generic placeholder for not-yet-built views
  Resources/
    Styles/Theme.Carbon.xaml     # GENERATED from design-tokens.json — do not hand-edit
    Styles/Styles.xaml           # component styles built on the tokens
    Fonts/README.md              # add Inter + JetBrains Mono TTFs here
    AppIcon/ Splash/ Images/     # placeholder app art
  Platforms/
    MacCatalyst/ Windows/        # platform heads
```

## Prerequisites

- .NET SDK 10 (see `global.json`).
- MAUI workload: `dotnet workload install maui`.
- On macOS, Xcode for the Mac Catalyst target.

## Before first build

1. **Fonts.** Drop the TTFs listed in `Resources/Fonts/README.md` (Inter +
   JetBrains Mono, both SIL OFL). Until then, either add them or comment out the
   `AddFont` lines in `MauiProgram.cs`.
2. **Query API.** Start the backend so the app has data to read:
   ```bash
   dotnet run --project src/RaceTelemetry.AppHost
   ```
   The Query API is exposed on `http://localhost:5120` (§11). Override with the
   `RACE_TELEMETRY_QUERY_API_BASEURL` environment variable.

## Run

```bash
# Mac Catalyst
dotnet build src/RaceTelemetry.Desktop -t:Run -f net10.0-maccatalyst

# Windows
dotnet build src/RaceTelemetry.Desktop -t:Run -f net10.0-windows10.0.19041.0
```

## DevFlow

Debug builds register the experimental .NET MAUI DevFlow agent. Install the
matching prerelease CLI once, run the app, then inspect it from another shell:

```bash
dotnet tool install -g Microsoft.Maui.Cli --prerelease

maui devflow ui tree
maui devflow ui screenshot --output screenshot.png --overwrite
maui devflow mcp
```

## Prefetch / snappiness (§8.9)

`SessionPrefetchService` makes view switching instant. When a session row is
**selected** in the launcher it starts warming a `SessionSnapshot` in the
background; by the time the user opens the session and starts switching views,
drivers, replay metadata, standings, incidents, positions, and per-driver laps
are already in memory.

Key behaviours:

- One shared `Task<SessionSnapshot>` per session — priming on selection and
  awaiting on open never double-fetch (`ConcurrentDictionary.GetOrAdd`).
- All session-scoped calls run in parallel (`Task.WhenAll`); per-driver lap
  fetches are bounded by a `SemaphoreSlim` (6 at a time).
- Prefetch uses `CancellationToken.None` on purpose, so a quick view switch can
  never cancel a warm another view is about to await.
- A failed sub-fetch leaves the rest of the snapshot usable (`Safe` wrapper).

View models (`FieldViewViewModel`, `TrackIncidentsViewModel`,
`SessionConsoleViewModel`) read from the snapshot rather than calling the API
directly. High-volume replay chunks are **not** part of the snapshot — they stay
streamed/windowed on demand per §8.9.

## Regenerating the theme

`Resources/Styles/Theme.Carbon.xaml` is generated from
`docs/design-system/design-tokens.json` (the single source of truth, §8.8). When
the tokens change, regenerate the dictionary rather than editing it by hand so
the app and the design system never drift.

## Scaffold status

Implemented end-to-end (UI → view model → typed client): Launcher, Session
console shell, Field view, Track incidents. Placeholders wired into the rail:
Overview, Replay, Strategy, Lap analysis, Head to head, Telemetry. Each builds on
the same Query API contracts and the linked timebase (§7.7); see §13 phases 7–8
for the build-out order.
