# Handoff Spec — Launcher ↔ Shell Integration (Option A)

**Product:** Race Telemetry Workbench · **System:** Carbon Signal v1.2.0
**Stack:** .NET MAUI (net10.0-maccatalyst), MVVM, code-behind view swapping
**Goal:** Stop treating the launcher as a separate screen. Make it the **pre-session state of the one persistent shell**, so opening a session is a state transition, not a page navigation.

Artifacts in this folder:

- `shell-integration-mockup.html` — interactive visual; toggle Pre-session ⇄ In-session, click the funnel and watch the breadcrumb/HUD/rail fill in. This is the ground truth.
- `proposed-xaml/ConsoleShellPage.xaml` — the unified shell (replaces the two-page split).
- `proposed-xaml/LauncherView.xaml` — today's launcher reduced to a chrome-less funnel that lives in the shell's content host.

---

## 1. Overview

Today you have two `ContentPage`s with **different frames**:

| | `LauncherPage.xaml` | `SessionConsolePage.xaml` |
|---|---|---|
| Top | "Open a session" + big search box | 44px mono command bar (breadcrumb + search + actions) |
| Left | 220px **SELECTION** summary column | 176px numbered **view rail** |
| Metrics | bottom footer summary line | 42px **HUD** strip |
| Primary | "Open replay" bottom-right | — |
| Palette | its own ⌘K | shared ⌘K |

`AppShell.xaml` boots the launcher; `OpenCommand` navigates to the console. Because the two frames share nothing, the launcher reads as a different app — your stated problem.

**Option A** mounts **one** frame (command bar + HUD + rail) that never unmounts. A `ShellState` enum decides how that frame looks and what fills the content host:

```
PreSession (Home / state 0)        SessionOpen (states 1–8)
├ command bar: building breadcrumb  ├ command bar: full breadcrumb + live actions
│   "2025 / ITA / RACE"  (amber)    │   "2025 / ITA / RACE / monza-r"
├ HUD: placeholder cells ("--")     ├ HUD: live metrics (20 drivers, 01:14:46…)
├ rail: Home active, 1–8 locked     ├ rail: active view amber, all unlocked
└ content host: LauncherView        └ content host: active session view
```

---

## 2. Layout

Single root `Grid RowDefinitions="Auto,Auto,*"` — unchanged from `SessionConsolePage`:

| Row | Region | Height | Persistent? |
|---|---|---|---|
| 0 | Command bar | `CommandBarHeight` (44) | Always mounted |
| 1 | HUD strip | `HudHeight` (42) | Always mounted |
| 2 | Body = `ColumnDefinitions="176,*"` → rail + `ContentHost` | fill | Always mounted |

Target viewport 1440×900 logical points (`macbook-pro-15-retina`). The launcher funnel keeps its density tokens (`LauncherCircuitGridHeight` 430, `LauncherCircuitCardHeight` 132, `LauncherSessionChipHeight` 38, `LauncherDriverChipHeight` 34).

---

## 3. The breadcrumb (builds as you select)

Replace the long-name breadcrumb with the spec's short-code form (§2a) and render empty segments as muted placeholders so the user sees the funnel filling:

| Selection so far | Breadcrumb (PreSession, amber = chosen) |
|---|---|
| nothing | `2025 / — / —` |
| circuit | `2025 / ITA / —` |
| + session | `2025 / ITA / RACE` |
| session opened | `2025 / ITA / RACE / monza-r` (all `TextSecondary`, no amber) |

`ColorPlaceholder = TextDisabled (#5C544A)`, `ColorChosen = Accent (#FFA60D)` while PreSession, `TextSecondary (#BCB1A2)` once SessionOpen. Build the string in the VM (`Breadcrumb` get-only, raised on selection changes).

> **Fix the naming drift:** the launcher says **Monza**, the console breadcrumb says **Italian Grand Prix**. Pick one canonical pair per circuit — a display name (`Monza`) and a short code (`ITA`) — and store both on `CircuitChoice`. Breadcrumb uses the code; cards/HUD use the display name.

---

## 4. Design tokens used

| Token (resource key) | Value | Usage in this feature |
|---|---|---|
| `BgInset` | `#100D0B` | command-bar background |
| `BgRaised` | `#241F1B` | HUD strip, action pills, unselected chips/cards |
| `BgSurface` | `#1C1815` | view rail, launcher sticky footer |
| `BgCanvas` | `#14110E` | content host |
| `BgSelected` / `AccentMuted` | `#43320F` | active rail item, selected card/chip fill |
| `Accent` | `#FFA60D` | active rail bar, chosen breadcrumb segs, Open replay, focus ring |
| `AccentBorder` | `#7D5E12` | selected driver-chip stroke |
| `TextPrimary/Secondary/Tertiary/Disabled` | `#F4EEE6 / #BCB1A2 / #8A7F70 / #5C544A` | hierarchy + placeholders |
| `BorderDefault` / `BorderSubtle` | `#3A3128` / `#2A241F` | borders, hairlines |
| `CommandBarHeight` / `HudHeight` / `ConsoleRailWidth` | 44 / 42 / 176 | persistent frame |
| `FontMono` (JetBrains Mono) | — | breadcrumb, HUD, hotkeys, codes, all numerics |
| `FontSans` (Inter) | — | labels, names, headings |

No new tokens required. The launcher was already on-palette; this work is structural.

---

## 5. States and interactions

| Element | State | Behavior |
|---|---|---|
| Back `‹` | PreSession | Disabled, opacity 0.35 |
| Back `‹` | SessionOpen | Enabled → `GoHomeCommand` (⌘[) |
| Search field | PreSession | Placeholder "Search circuits, years, drivers — or ⌘K"; filters the funnel |
| Search field | SessionOpen | Placeholder "Search / query"; queries the session |
| ⌘K | both | Raises the **one** `CommandPalette`. No second ⌘K target. |
| Export / Save view / ? | PreSession | Disabled, opacity 0.40 (nothing to act on yet) |
| HUD cell | PreSession | Value `--` in `TextDisabled` (placeholder) |
| HUD cell | SessionOpen | Live value in `TextPrimary` |
| Rail: Home (0) | PreSession | Active (amber bar + muted fill) |
| Rail: views 1–8 | PreSession | Locked: opacity 0.34, `InputTransparent`, number keys no-op |
| Rail: views 1–8 | SessionOpen | Enabled; active item amber; `1`–`8` switch |
| Year/Circuit/Session chip | selected | `AccentMuted` fill + `Accent` stroke (already correct) |
| Driver chip | checked | `AccentMuted` fill, amber `✓`, categorical color rail |
| Open replay | `CanOpen` false | Disabled, opacity 0.4 (needs circuit + session + ≥1 driver) |
| Open replay | tap | `OpenCommand` → load session → `ShellState = SessionOpen`, `ActiveView = Overview` |

### Edge cases
- **No imported sessions:** circuit grid shows `BindableLayout.EmptyView` — "No sessions. Start the Query API and press Retry." Rail stays locked; HUD all `--`.
- **Loading a session:** keep the shell mounted; show a skeleton in the content host (never a blocking spinner over the chrome — Patterns §4 "States").
- **Open with 0 drivers:** impossible — `CanOpen` gates it.
- **Long circuit names:** cards `TailTruncation` (already set); breadcrumb uses the short code so it never wraps.
- **Back to Home mid-session:** allowed; preserves the last selection so reopening is one click. Does not unload imported data.

---

## 6. MAUI implementation — step by step

**6.1 ViewModel.** Merge `LauncherViewModel` + `SessionConsoleViewModel` into `ConsoleShellViewModel` (or compose: shell VM owns a `Launcher` child VM). Add:

```csharp
public enum ShellState { PreSession, SessionOpen }

public ShellState State { get; private set; } = ShellState.PreSession;
public bool IsPreSession => State == ShellState.PreSession;
public bool IsSessionOpen => State == ShellState.SessionOpen;

public string Breadcrumb   => BuildBreadcrumb();          // §3
public string SearchPlaceholder => IsPreSession
    ? "Search circuits, years, drivers — or ⌘K" : "Search / query";
public string SelectionHint => $"{SelectedYear?.Year} · {SelectedCircuit?.CircuitName ?? "—"} · " +
                               $"{SessionType(SelectedSession)} · {SelectedDriverCount} drivers";

public ObservableCollection<ConsoleView> Views { get; }   // index 0 = Home, 1..8 = session views
public ObservableCollection<HudMetric> Hud { get; }        // §6.3

[RelayCommand] void GoHome()  { State = PreSession; ActiveView = Views[0]; RaiseShellChanged(); }
[RelayCommand] async Task Open() {
    await LoadSessionAsync();
    State = SessionOpen; ActiveView = Views[1]; RaiseShellChanged();
}
void RaiseShellChanged() => OnPropertyChanged(new[]{
    nameof(IsPreSession), nameof(IsSessionOpen), nameof(Breadcrumb),
    nameof(SearchPlaceholder), nameof(Hud) });
```

**6.2 `ConsoleView` gains lock state:**

```csharp
public sealed partial class ConsoleView : ObservableObject {
    public string Title { get; init; }
    public string Hotkey { get; init; }            // "0".."8"
    [ObservableProperty] bool isActive;
    public bool IsLocked => Index > 0 && shell.IsPreSession;   // Home (0) never locks
}
```

**6.3 `HudMetric` gains `IsPlaceholder`:**

```csharp
public sealed record HudMetric(string Label, string Value, bool IsPlaceholder = false);
// PreSession seed: new("drivers", SelectedDriverCount>0? SelectedDriverCount.ToString():"--", true) ...
// On Open(): swap to real metrics with IsPlaceholder=false.
```

**6.4 Page.** Replace `SessionConsolePage.xaml` with `proposed-xaml/ConsoleShellPage.xaml`. In code-behind, swap the content host on `ActiveView`/`State`:

```csharp
void OnShellStateChanged() {
    ContentHost.Content = vm.IsPreSession
        ? _launcherView ??= new LauncherView { BindingContext = vm }
        : ResolveSessionView(vm.ActiveView);   // existing Overview/Replay/… resolver
}
```

**6.5 Launcher.** Convert `LauncherPage.xaml` → `proposed-xaml/LauncherView.xaml` (a `ContentView`). Delete its header search box, SELECTION column, footer summary, and its `CommandPalette` instance — the shell owns all of those now. Paste the four item `DataTemplate`s back verbatim. Keep `OpenCommand`/`CanOpen`/driver logic.

**6.6 AppShell.** Point the root at the shell instead of the launcher:

```xml
<ShellContent Route="console" ContentTemplate="{DataTemplate views:ConsoleShellPage}" />
```

The app now boots directly into the shell in `PreSession`. No launcher→console navigation remains — delete `BackToLauncherCommand`'s navigation and replace with `GoHomeCommand` (state flip).

**6.7 Converters.** You already use `BoolColor`. Add two trivial ones if missing:
- `BoolToOpacity` (param `"trueVal;falseVal"`, e.g. `"1;0.35"`).
- (Optional) fold opacity into existing converters; the XAML uses `BoolToOpacity` for back button, actions, and locked rail items.

---

## 7. Accessibility

- **Focus order PreSession:** search → year chips → circuit grid → session chips → driver chips → Open replay. Locked rail items are skipped (`InputTransparent` + remove from tab order).
- **Focus order SessionOpen:** rail (1–8) → command-bar search → actions → view content.
- Keep the visible 2px amber focus ring (system default) on every interactive element.
- HUD placeholder cells: announce as "drivers, not available" rather than "drivers, dash dash" — set `SemanticProperties.Description` to "not available" when `IsPlaceholder`.
- Locked rail items: `SemanticProperties.Hint = "Opens once a session is loaded"`.
- ⌘K, number keys, and ⌘[ are all `KeyboardAccelerator`s — no mouse-only paths.

---

## 8. Motion

Chrome-only, per §2.5. The state flip is a content-host swap, not the live render loop, so it may animate:

| Element | Trigger | Animation | Duration | Easing |
|---|---|---|---|---|
| Content host (launcher ⇄ session) | Open / Home | cross-fade opacity 0→1 | 180ms (`base`) | `cubic-bezier(0.2,0,0,1)` |
| Rail items 1–8 | state flip | opacity 0.34⇄1 | 120ms (`fast`) | standard |
| HUD values | placeholder→live | no transition (snap) | 0 | — |

All collapse to 0 under `prefers-reduced-motion`. Never animate the replay loop.

---

## 9. Definition of done

1. App boots into the shell in `PreSession`; no separate launcher page in the nav stack.
2. Selecting circuit/session updates the breadcrumb (short codes) live; driver count updates the HUD.
3. Rail shows Home active + 1–8 locked until Open; after Open, 1–8 work and Overview is active.
4. Exactly one ⌘K, one search field, one command palette across both states.
5. Back/⌘[ returns to Home without unloading the selection.
6. Circuit naming is consistent (display name on cards/HUD, short code in breadcrumb).
