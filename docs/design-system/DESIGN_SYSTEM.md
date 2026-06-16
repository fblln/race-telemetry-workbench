# Carbon Signal — Design System

**Product:** Race Telemetry Workbench (F1 Telemetry Visualizer)
**Owner:** Fabio
**Version:** 1.2.0
**Status:** Foundation release

Carbon Signal is the original visual language for the desktop telemetry workbench described in the [architecture spec](../../f1_telemetry_architecture_spec_focused.md). It satisfies §8.8 *Display Styling and Assets*: an original dark analysis theme, dense but legible, using only project-owned names, palettes, and generated assets.

This document is the authority for *what* the system is. The companion files carry the *values* and a live rendering:

- `design-tokens.css` — CSS custom properties (single source of truth)
- `design-tokens.json` — same tokens, machine-readable (generate the MAUI `ResourceDictionary` from this)
- `styleguide.html` — interactive styleguide rendering every token and component

---

## 1. Design direction

The base is **warm carbon** — graphite surfaces with a faint warm bias (`#14110E` rather than `#000`), which reads as an instrument enclosure rather than a void and reduces the harsh edge-glare of pure black during long analysis sessions. A single **signal amber** accent (`#FFA60D`) carries action, selection, focus, and the replay cursor, so the eye always knows where "now" and "this" are. Telemetry traces use an **original, colorblind-safe palette** (Okabe-Ito derived) that never borrows real team liveries and never competes with the amber cursor.

Three principles drive every decision:

**Density with legibility.** Every panel describes the same replay timestamp; the layout is information-dense by design. The primary target is a 15-inch MacBook Pro Retina workspace: 2880×1800 physical pixels, rendered as a 1440×900 logical-point window at 2x. Legibility is preserved through tabular numerics, generous contrast on text, and restraint in color — chrome stays quiet so data stays loud.

**One accent, one meaning.** Amber means *interactive or active*. It is never used for a data series. This single rule keeps a six-channel waveform readable.

**Real-time honesty.** The replay surfaces are live. The system forbids animating the render loop and forbids inventing data — gaps are shown as gaps, weather is stepped not interpolated.

---

## 2. Tokens

### 2.1 Color

Surfaces stack in four warm-graphite layers; depth comes from tint and borders rather than heavy shadow.

| Token | Hex | Use |
|---|---|---|
| `bg-canvas` | `#14110E` | App background, deepest layer |
| `bg-surface` | `#1C1815` | Panels, cards |
| `bg-raised` | `#241F1B` | Panel headers, popovers, raised rows |
| `bg-overlay` | `#2C2620` | Tooltips, menus, dialogs |
| `bg-inset` | `#100D0B` | Chart plot areas, wells |
| `bg-hover` | `#2A241F` | Row / control hover |
| `bg-selected` | `#43320F` | Selected row tint (amber-derived) |

Text and borders:

| Token | Hex | Use |
|---|---|---|
| `text-primary` | `#F4EEE6` | Headings, key values |
| `text-secondary` | `#BCB1A2` | Body, labels |
| `text-tertiary` | `#8A7F70` | Captions, axis ticks |
| `text-disabled` | `#5C544A` | Disabled controls |
| `border-default` | `#3A3128` | Panel borders, inputs |
| `border-strong` | `#524537` | Emphasized edges |
| `grid-line` / `grid-line-major` | `#2A2620` / `#3A342C` | Chart gridlines |

Accent — signal amber. Reserved for primary action, selection, focus, and the cursor.

| Token | Hex | Use |
|---|---|---|
| `accent` | `#FFA60D` | Primary button, cursor |
| `accent-hover` | `#FFC24D` | Hover |
| `accent-active` | `#E08A00` | Pressed |
| `accent-muted` | `#43320F` | Amber wash background |
| `cursor-ref` | `#7FA6C9` | Reference cursor (cool, secondary) |

Semantic feedback (UI state, distinct from motorsport flags): `success #27D98C`, `warning #FFB424`, `danger #FF5A5F`, `info #2E9BFF`, each with a matching low-luminance background token.

### 2.2 Motorsport status & flags

Two encodings live side by side. Flags carry a **marker color** (badges, timeline dots) and a **low-alpha shade** (chart backgrounds, timeline period bands). The status codes map to FastF1 track-status values from the schema.

| Status | Marker | Code |
|---|---|---|
| Track clear | `#27D98C` | 1 |
| Yellow flag | `#FFDA1F` | 2 |
| Safety car | `#FF8A1F` | 4 |
| Red flag | `#FF5A5F` | 5 |
| Virtual safety car | `#FFDB3D` | 6 / 7 |
| Rainfall | `#3E9BF5` | — |
| DRS enabled | `#1FE0CE` | — |

### 2.3 Telemetry channels

Original, colorblind-safe, no team liveries. Hue is **reinforced with a dash pattern** so traces stay distinguishable in grayscale or for users with color vision deficiency.

| Channel | Color | Dash |
|---|---|---|
| `speed_kmh` | `#22A7FF` sky blue | solid |
| `throttle_pct` | `#15D981` green | solid |
| `brake_pct` | `#FF7A22` vermillion | solid |
| `gear` | `#E86BE0` orchid | `4 3` |
| `rpm` | `#FFCE1A` gold | `6 3` |
| `drs` | `#1FE0CE` teal | `2 3` |

For lap comparison, driver A is `#22A7FF` (blue) and driver B is `#FF931A` (orange) — the strongest colorblind-safe pair. Delta sign uses `deltaPos #27D98C` / `deltaNeg #FF5A5F` plus a leading `+` / `−` glyph so the meaning never rests on color alone.

The load-map / density heatmap uses an on-brand **amber single-hue sequential ramp** (`#221A12 → #4A2F0C → #7A4E0B → #A86A0A → #E08A00 → #FFA60D → #FFDE7A`), replacing the legacy magenta-blue ramp.

### 2.4 Typography

Inter for all chrome and labels; JetBrains Mono for every number. Mono's tabular figures keep telemetry columns from shifting as values update during replay.

| Role | Size | Font / weight |
|---|---|---|
| Hero readout (timer) | 44px | mono 500 |
| Numeric lg (value cards) | 28px | mono 500 |
| Screen heading | 20px | sans 600 |
| Panel title | 17px | sans 600 |
| Body | 14px | sans 400 |
| Control text | 13px | sans 500 |
| Table cell / numeric | 12–13px | mono 400 |
| Axis tick / micro-label | 11px | mono 400 |

Line height: 1.2 for numerics and headings, 1.35 for dense rows, 1.55 for body. Sentence case everywhere; the rare uppercase micro-label uses `0.06em` tracking.

### 2.5 Spacing, radius, elevation, motion

Spacing is a 4px base scale tuned dense: `2, 4, 6, 8, 12, 16, 20, 24, 32, 40, 48`. Controls use 5×12px padding with a 30px target height, panels 12px, table cells 6×10px.

The default density profile is `macbook-pro-15-retina`. Treat sizes as logical CSS pixels / MAUI device-independent points, not physical pixels. The shell target is 1440×900 logical points: 16px page padding, 44px command bar, 42px HUD strip, 176px console rail, 12px panel padding. The launcher uses 132px circuit cards in a 430px scrolling grid with 14px gaps, 38px session chips, and 34px driver chips so the circuit → session → driver funnel fits the first viewport on a 15-inch MacBook Pro without scaling text down.

Radius is moderate — softer than the legacy 0px chrome, tighter than consumer cards: `sm 3 · md 5 (default control) · lg 8 (panel/card) · xl 12 · pill`.

Elevation is restrained on dark; layering leans on surface tint plus border. Shadow appears only on true overlays: `e1` panels, `e2` popovers/tooltips, `e3` modals. Focus is a 2px amber ring at 55% alpha.

Motion is chrome-only: `fast 120ms`, `base 180ms`, `slow 240ms`, standard easing `cubic-bezier(0.2,0,0,1)`. The replay render loop (track map, waveform, readouts) must **not** use CSS/animation transitions — it is data-driven and real-time. All durations collapse to 0 under `prefers-reduced-motion`.

---

## 2a. Application shell

The product wraps the analysis panels in a thin, keyboard-first shell. Patterns here are adapted from modern motorsport tools but kept in Carbon Signal styling — warm graphite and amber, never the reference's pure-black + Ferrari-red (a single accent is a load-bearing rule of this system, so the accent does not change per surface).

**Home & launcher.** The entry surface is a three-step funnel — circuit, then session, then drivers. Circuit cards carry a small national flag (a factual identifier, not a team livery); the selected card takes the amber-muted fill. Session is a chip row, drivers a compact multi-select, and one amber primary ("Open replay") commits the choice. National flags are permitted; team liveries are not.

**Command palette.** A global launcher on `⌘K` / `/` over a dimmed faux-viewport. Fuzzy-matches imported sessions, drivers, and quick actions; the highlighted row gets the amber left rail and an `↵` hint. Grouped headers (Sessions, Quick actions) use the uppercase micro-label.

**Session console.** Built for engineers, not casual viewers — and deliberately *not* the common horizontal-tabs + big-KPI-cards dashboard layout. The session context lives in a monospace **command bar**: a `/`-separated breadcrumb (`2025 / ITA / RACE / monza-r`) with a load indicator, an always-on search that doubles as the query input, and actions that each show their hotkey (`E`, `S`, `?`). Views switch from a **left vertical rail** driven by number keys `1`–`6` (icon + label + hotkey badge; the active item takes an amber left-border and muted fill). Session metrics read as a compact **instrument HUD** strip — small mono label-over-value cells divided by hairlines, sized like a status line rather than hero cards — so they inform without dominating. A persistent **keyboard map** (`?`) keeps every shortcut one keystroke away; nothing important hides in a menu. The rail, command bar, and HUD persist across every view so the engineer never loses context or keyboard focus.

**Driver multi-select.** A responsive grid of toggleable driver chips, each with a team-color-free categorical rail, a checkbox affordance (amber when checked), name, and finishing position. A live count plus select-all / clear sit in the header. Selection is local UI state and never mutates imported data. Driver identity uses an **original categorical palette** (LEC blue, HAM orange, NOR green, PIA gold, VER orchid, RUS teal, …) so it stays project-owned per §8.8; a team-livery mode can be offered later as an explicit opt-in.

## 2b. Race views

**Overview.** The session entry view leads with three compact result cards: Winner, Pole Position, and Fastest Lap. Each card uses a driver identity tile with the categorical color rail, a muted label, the driver name, and the key time/value. Below, a full classification table shows position, driver, team, grid, time/gap, fastest lap, stints, laps, points, and status. Fields not present in the imported Query API surface must render as explicit unavailable values (`--` or "not imported") rather than inferred facts; grid and pole require qualifying/grid metadata before they can be filled.

**Tire strategy gantt.** The Strategy tab: one row per driver, stints drawn as compound-colored bars on a shared lap axis with the stint length labelled and pit boundaries at the segment edges. Compounds use the tyre palette (soft red, medium gold, hard off-white, inter green, wet blue); labels flip to light text on the darker hard/inter fills for contrast.

**Position trace.** The Lap analysis tab: a race "spaghetti" chart of grid position over laps, one line per driver in the categorical palette. Line crossings read as overtakes and pit cycles. The Y axis is the full field (P1–P20); the legend doubles as a visibility toggle. Built from `lap_summaries`, not raw telemetry.

**Field view (all drivers).** The engineer's default situational-awareness screen — a dense **timing tower** showing every driver at once, sortable on any column: position, driver (team-free categorical rail + code + name), gap, interval, last lap (session-best in purple, personal-best in green), best, tyre + age, pit count, a five-lap **pace sparkline**, and a running/out status dot. View toggles (Tower / Grid / Gaps) and a `/` filter sit in the toolbar; clicking a row pins it to comparison and `D` adds it to replay. Rows virtualize for the full 20. Sparklines color green when the driver is trending toward a personal best. Built from `lap_summaries` and `driver_stint_summaries`.

**Track incidents & hard-braking.** Spatial situational awareness: incidents and hard-braking hotspots placed on the data-derived Monza outline and synced to an incident list. Flag zones, spins, and off-tracks get a colored glyph (SC / ! / R / spin); braking load shows as amber **heat dots** sized by intensity at each braking zone. The list carries timestamp, glyph, message, and lap/location; selecting either side highlights the other, and a small stat block summarizes hardest brake (g), incident count, and laps lost under SC. Composes `race_control_messages`, `track_status_events`, and the `telemetry_event_candidates` hard-braking helper view over `circuit_markers` for corner attribution.

---

## 3. Components

Each component is documented as *what it is*, its *variants/states*, and the *do/don't* that keeps it on-system. Live examples are in `styleguide.html`.

### 3.1 Button

The action primitive. At most one amber primary per view — usually Play or Open session.

| Variant | Use |
|---|---|
| Primary | The single most important action in the view (amber fill) |
| Secondary | Supporting actions (raised surface + strong border) |
| Ghost | Low-emphasis / repeated actions (reset zoom, refresh) |
| Danger | Destructive (delete import) — outlined, fills on hover |

States: default, hover, active, focus (amber ring), disabled. Sizes: `md` (default) and `sm`. Icon-only buttons are 32px square and require `aria-label`.

| ✅ Do | ❌ Don't |
|---|---|
| Keep exactly one primary per view | Stack two amber primaries together |
| Use ghost for toolbar repeats | Use danger styling for non-destructive actions |

### 3.2 Segmented control

A mutually-exclusive switch on an inset track — used for replay speed (`0.25×…20×`) and small mode toggles. The active segment takes the amber fill. Mono labels keep widths even.

### 3.3 Channel toggle chip

A pill with a color swatch that shows/hides a telemetry channel. Inactive chips drop to 42% opacity and desaturate their swatch. Toggling channel visibility is **local UI state** and must never mutate imported telemetry.

### 3.4 Inputs

Text fields, search (with leading magnifier), and selects share the inset background, default border, and amber focus ring. Used in the Session Browser (event search, session-type, driver filters) and event-timeline filtering. Placeholder text uses `text-disabled`.

### 3.5 Flags & tags

Status **badges** carry a leading LED dot and use the flag marker color on its low-alpha background. Tyre **compound tags** are a ringed mono tag matching the FastF1 vocabulary: SOFT (red ring), MEDIUM (gold), HARD (off-white), INTER (green), WET (blue).

### 3.6 Numeric readout

The Current Values panel. Mono tabular value with a quiet unit suffix and an uppercase micro-label. The dominant channel for the active driver may take the amber variant (`ro.accent`). Readouts never animate count-ups — they snap to the cursor value.

### 3.7 Data table

Dense, right-aligned numerics, mono figures, sticky header, hairline rows (no zebra). The selected row gets `bg-selected` plus a 2px amber left rail; the fastest value in a numeric column is amber. Rows virtualize for laps, events, and race-control messages per the performance requirements (§8.9).

### 3.8 Panel

The workbench container. A `raised` header bar with title (icon + label) and a tool cluster, over a `surface` or `inset` body. The **active** panel gains an amber border and a 2px amber rail on its header. Panels are independent components so the layout can later be saved, hidden, resized, or rearranged (§8.5).

### 3.9 Replay transport

The persistent playback bar: restart, play/pause (amber), a mono clock showing `session-time / duration`, a seek slider, and an inline speed segmented control. The seek **track previews flag periods** as colored slivers so a user can scrub straight to a safety-car or red-flag window. A buffered-range indicator shows how far chunks are loaded ahead.

### 3.10 Context strip & event timeline

The **context strip** compresses laps (tick dividers), flag/SC/VSC/red periods (shade bands), rain periods, and race-control markers into one seekable band with the shared amber cursor. The **event timeline** is a filterable list; each row shows timestamp (mono), a flag dot, the message, and lap number. Selecting a row seeks the shared cursor when the event has a timestamp.

### 3.11 Cursor system

Two cursors share every replay display. The **primary cursor** is the amber playhead, driven by playback, seek, chart click, event selection, or track-map selection. The **reference cursor** is a cool dashed line (`cursor-ref`) used in analysis views to show current-vs-reference deltas. Both are overlay-layer elements that never alter underlying data.

### 3.12 Track-map markers

Corners are small amber-ringed dots with a mono `T#` label; marshal lights and sectors reuse the marker convention at lower emphasis. The start/finish line is a short bright tick. Driver dots use the driver A/B colors with a canvas-colored halo; the cursor position adds an amber ring. The outline is always derived from imported position samples.

### 3.13 Tooltip

An `overlay`-surface card with `e2` shadow showing the hovered timestamp (mono) and per-driver values. Delta rows color the value by sign.

---

## 4. Patterns

**Replay workspace.** A fixed docked grid of independent panels — Track Map, Waveform, Current Values, Lap Summary, Event Timeline, Context Strip, Pit Summary — all locked to one session-relative timebase. The transport bar spans the bottom. Structure panels independently now so saved/rearrangeable layouts can land later without reshaping the UI.

**Lap comparison.** Two laps aligned by lap-relative time: driver A (blue) over driver B (orange) on overlaid charts, with a lap-time delta hero, three sector deltas, and a cursor tooltip — all under the `A − B` convention where negative means A was faster.

**Session browser.** A virtualized table (season, event, type, imported, drivers, laps, context flags) beside a detail property panel, filtered to race sessions by default.

**States.** Empty (no session / no imported context), loading (chunk fetch in flight — show the skeleton, never a blocking spinner over live controls), and error (validation/DB errors rendered from the API error shape with code, message, and details). Degrade gracefully on large sessions: reduce chart detail before dropping cursor interaction.

The track map is always **derived from imported `position_samples`** — the styleguide example is a real Autodromo Nazionale Monza lap extracted from the FastF1 position cache (≈5.79 km, 11 corners), never an external track asset.

---

## 4a. Extension modules

Four higher-value surfaces compose the existing MCP primitives and Query API contracts. Each reuses the panel, badge, delta, tyre, and flag vocabulary above rather than introducing new chrome.

**Strategy / pit-loss narrative.** Answers "why did X pit on lap Y?" by composing `analyze_driver_stints`, pit analytics, `track_status_periods`, and the race-control index into one structured story. The panel leads with a plain-language verdict (undercut/overcut, with the decisive driver and lap in amber), then a per-driver **stint timeline** — compound-colored bars with an amber pit marker and any SC/VSC window shaded behind — then three metric readouts: net undercut gain (delta-green/red), pit-lane loss vs field average, and laps of stop cost saved under a VSC. It is a composition of bounded aggregates, so it stays MCP-safe.

**Corner-level performance index.** Once `detect_telemetry_windows` attributes braking/throttle zones to corners via `circuit_markers`, each corner becomes a driver-vs-driver row. The left panel shows a single focused corner (the data-derived corner geometry with both drivers' brake points and the gap annotated in amber, e.g. "12 m later"); the right panel is the full **corner index table** — brake-point Δm, min-speed Δ, exit-speed Δ, and time Δ per turn, under the `A − B` convention with delta-colored cells and the fastest corner highlighted. Extends planned Phase 1 corner-matching.

**Session diff (multi-season).** The natural extension of the `compare/laps` contract: the same driver's best lap at the same circuit across two seasons. It reuses lap-relative alignment but pins circuit identity instead of session, and **recolors the pair** — neutral past-year grey (`text-tertiary`) against the amber current year — so it never reads as a live driver-vs-driver overlay. The summary rail shows lap-time delta and a top-speed development gain.

**MCP race debrief export.** Gives the read-only MCP server a tangible artifact beyond chat: a one-page markdown/PDF debrief per session built from race/lap story, weather summary, and race-control endpoints. The panel renders a **document preview** (headline, "what happened", gained/lost, key incidents with flag dots) on the canvas surface with `.md` and Export PDF actions in the header, plus a source-endpoint readiness list. Pure composition over bounded aggregates — no raw telemetry — so it stays within the read-only contract while producing a shareable file.

**Tire degradation & pit-window predictor.** An ML model over telemetry + lap times forecasts each compound's degradation curve and recommends the optimal pit window — turning the workbench from viewer into advisor. Measured lap-time rise is a **solid** line; the forecast continues **dashed** inside a confidence band, and the recommended window is an amber zone with dashed boundaries — so a modelled value can never be read as a measured one. Supporting readouts give the optimal window, predicted cliff lap, and undercut threat. Consumes the same bounded aggregates as `analyze_driver_stints`.

**Ghost-car overlay.** Extends two-lap comparison onto the track map: a live lap races an animated **ghost** of a reference lap (same driver another year, or a rival) along the data-derived outline, with a trailing opacity fade and a running gap badge. The live car uses the driver's categorical color plus the amber cursor ring; the ghost is rendered in neutral grey so it never competes. Reuses the replay clock and track-map renderer.

**Race story (natural language, MCP).** The AI Assistant Panel (§8.10). The MCP server chains telemetry, stint, and event queries and an LLM composes a narrative briefing rather than charts. Rendered as a chat surface: the user's question as an amber-muted bubble, the answer as a raised bubble with key facts emphasized, and the **tool calls shown as quiet mono pills** beneath so the reasoning stays auditable. Always answered from bounded analytical endpoints, never raw sample dumps.

**Incident × weather correlation.** A unified timeline correlating `race_control_messages`, `track_status_events`, and `weather_samples` on one shared axis — surfacing relationships like "VSC deployed two minutes after rain crossed threshold." Each source is a lane (track-status period bands, race-control marker dots, rainfall band, a track-temperature heat strip using the amber ramp); a shared cursor line ties an incident to the weather state at that moment, with a plain-language correlation callout. Weather stays stepped, never interpolated.

**Exportable session report.** One click turns a session's key findings — strategy gantt, lap comparison, incidents — into a shareable PDF/HTML report for content creators and fan analysts. It builds on the debrief aggregates, expanded to a multi-section sheet (result KPIs, tire strategy bar, key lap delta, incident list) centered on the canvas as a printable page with `.html` and Export PDF actions. The debrief and the session report share one renderer at two levels of detail.

---

## 5. Accessibility

Contrast targets WCAG AA on the graphite base: body text on surfaces exceeds 7:1, axis ticks and hints exceed 4.5:1. No information rests on color alone — channels add dash patterns, delta sign adds a `+`/`−` glyph, flags add an LED dot and a text label.

The channel and driver palettes are Okabe-Ito derived and verified distinguishable under deuteranopia and protanopia. Amber is never a data color, so cursor and traces never merge for any viewer.

All transport and cursor actions are keyboard-reachable with a visible amber focus ring; the playhead responds to arrow-key nudges and Home/End. A reduced-motion mode (honoring `prefers-reduced-motion`) freezes non-essential chrome transitions while keeping the real-time replay loop, which is already transition-free.

---

## 6. Implementation notes

`design-tokens.css` is the source of truth. Generate `Theme.Carbon.xaml` (a MAUI `ResourceDictionary`) from `design-tokens.json` so the desktop app and any web/doc surfaces never drift — e.g. `accent → <Color x:Key="Accent">#FFA60D</Color>`, `ch-speed → <Color x:Key="ChSpeed">#22A7FF</Color>`. SkiaSharp / MAUI Graphics rendering for the track map, waveform, and context strip should read the same channel, flag, and grid tokens rather than hardcoding hex. Keep these assets project-owned and original per §8.8.
