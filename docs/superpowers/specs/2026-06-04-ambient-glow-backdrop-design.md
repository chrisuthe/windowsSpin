# Ambient Glow Reactive Backdrop — Design

**Date:** 2026-06-04
**Status:** Approved (design)
**Depends on:** Sendspin.SDK **9.0.0** — uses the new `visualizer@v1` and `color@v1` roles. (Originally designed against the dev branch / PR #37 as "8.1.0 prep"; the breaking changes — artwork event signatures, sync-offset removal, repeat/shuffle relocation — warranted a major bump, so it shipped as **9.0.0**, now on NuGet and pinned. The app builds locally; the dev-source-CI workaround is no longer needed.)

---

## 1. Summary

Replace the static blurred-album-art background with an **always-on reactive backdrop**: soft color blobs derived from the audio `color@v1` palette, **breathing** with `loudness` and **pulsing** on `beat`. This is the first mode of a future native visualizer suite, but this design covers **only the backdrop**.

The backdrop is rendered in **pure WPF** (compositor-driven, no new dependency), implemented behind a **swappable renderer seam** so later modes can use a different surface (e.g. SkiaSharp/GPU) without rework.

### Goals
- A subtle, "never distracting" living backdrop that reflects the music's color and energy.
- Zero new runtime dependencies; power-efficient enough to run for hours during playback.
- Cleanly isolated so it doesn't bloat `MainViewModel` and can be swapped/extended later.

### Non-goals (explicitly deferred)
- Other visualizer modes (spectrum bars, radial ring, spectrogram).
- View switching (album art ↔ visualizer), pop-out window, fullscreen.
- MilkDrop / Winamp plugin support (Butterchurn/projectM) — a separate future project.
- Intensity/sensitivity slider, album-art color extraction, oscilloscope/raw-PCM tap.

---

## 2. Background: why this is independent of the rest of 8.1.0

The Winamp/MilkDrop ambition was set aside (it needs raw PCM + a 32-bit out-of-process plugin host or a preset engine like Butterchurn/projectM — its own project). This feature instead consumes the SDK's server-derived feature roles:

- **`color@v1`** → `ColorPalette` (6 nullable swatches: `background_dark`, `background_light`, `primary`, `accent`, `on_dark`, `on_light`). Delivered via the `ColorChanged` event and `GroupState.Colors`. Already in the dev default roles.
- **`visualizer@v1`** → `VisualizerFrame` (one feature per frame). We request only `loudness` and `beat`. Delivered via the `VisualizationReceived` event.

Both events are exposed on **both** connection sources: `SendSpinHostService` (server-initiated) and `ISendspinClient` (client-initiated/manual).

---

## 3. Architecture & components

```
server ──(color/visualizer frames)──► SDK events
   │  ColorChanged(ColorPalette)
   │  VisualizationReceived(VisualizerFrame{loudness,beat})
   ▼
MainViewModel  (thin forwarders on both _hostService and _manualClient,
   │            mirroring the existing artwork wiring)
   ▼
AmbientBackdropViewModel   (target state + pure mapping logic; Dispatcher-marshaled)
   ▼
AmbientBackdropView        (UserControl: WPF blobs + CompositionTarget.Rendering easing loop)
   ▼
screen
```

### 3.1 `AmbientBackdropViewModel` (new — `src/SendspinClient/ViewModels/`)
- Holds **target** state: 3 blob colors + base color (from `ColorPalette`), target energy (0–1 from loudness), and a beat trigger/counter.
- Thread-safe; updated on the UI thread via `Dispatcher` from the SDK events (which fire on background threads).
- Contains the **pure mapping logic** (loudness→energy, palette→colors, beat→pulse parameters) as testable functions/helpers.
- Exposes whether a real palette is currently active (drives blobs-vs-fallback).

### 3.2 `AmbientBackdropView` (new — `src/SendspinClient/Views/`, `UserControl`)
- Visual tree: `background_dark` base fill; **3 `RadialGradientBrush` ellipses** (color→transparent = soft glow, **no `BlurEffect`**) with `RenderTransform`s; the existing **blurred-art `ImageBrush`** as the fallback layer.
- A single **`CompositionTarget.Rendering`** easing loop reads the VM targets and glides displayed scale/translate/opacity/colors toward them, applies the **decaying beat-pulse envelope**, and runs the **idle drift** motion.
- This control is the concrete implementation behind the renderer seam: the contract is "(palette, energy, beat) in → visuals out." A future SkiaSharp renderer is an alternate control consuming the same VM.

### 3.3 `MainViewModel` (modified)
- Add thin handlers `OnColorChanged` and `OnVisualizationReceived`, wired/unwired on **both** `_hostService` and `_manualClient` exactly like the existing `ArtworkReceived`/`ArtworkCleared` handlers. They forward into `AmbientBackdropViewModel`.

### 3.4 `App.xaml.cs` / `ClientCapabilities` (modified)
- Add `"visualizer@v1"` to `Roles`.
- Set `VisualizerSupport { Types = ["loudness", "beat"], RateMax = 30, BufferCapacity = <small> }`.
- `color@v1` is already in the dev default roles.
- **Role advertising policy (decided):** advertise `visualizer@v1`(loudness,beat) + `color@v1` **always** (negligible bandwidth). The Settings toggle controls the **visual only** (immediate, no reconnect required).

### 3.5 `MainWindow.xaml` (modified)
- Host `AmbientBackdropView` as the background layer where the blurred-art `ImageBrush` lives today (around the existing background region). The fallback layer preserves current behavior.

---

## 4. Reactivity mapping

| Input | Source | Drives |
|---|---|---|
| `loudness` (0–65535 → 0–1) | `VisualizerFrame.Loudness` | target **energy** → blob scale (~0.85–1.15), opacity (~0.5–0.9), drift speed; eased attack/decay = breathing |
| `beat` | `VisualizerFrame.IsDownbeat` (beat frame) | additive **pulse envelope** (~250–400 ms decay); **downbeat** → larger bump (if `stream/start` reports `tracks_downbeats`) |
| palette | `ColorPalette` | `primary`/`accent`/`on_dark` = 3 blob colors (cross-fade on change); `background_dark` = base fill; null swatches → theme defaults |
| (none) | — | **idle drift**: slow continuous sinusoidal blob motion so the scene is alive at low energy |

Smooth motion between sparse feature frames comes from the easing loop, not from the frame rate.

---

## 5. Degradation & edge cases

| Condition | Behavior |
|---|---|
| No `color@v1` palette (older server, between tracks) | Static blurred-art fallback (chosen behavior) |
| `visualizer@v1` unsupported by server | SDK delivers no frames (fails gracefully) → never animate → fallback art |
| Paused / stopped | Energy eases to a calm baseline; blobs idle-drift on last palette |
| Disconnected | Revert to blurred art |
| Feature disabled in Settings | Current static blurred-art background, no animation (immediate) |

---

## 6. Settings & configuration

- New Settings toggle: **"Reactive backdrop (Ambient Glow)"**, persisted via the existing user `appsettings.json` override mechanism.
- **Default: ON.**
- New top-level `Visualizer` config section (grouping the feature alongside the existing `Audio`/`Player`/etc. sections):
  - `Visualizer:Enabled` (bool, default `true`) — backs the toggle.
  - `Visualizer:RateMax` (int, default `30`) — requested max feature frame rate.
  - `Visualizer:BufferCapacity` (int, default `4096`) — `VisualizerSupport` buffer capacity in bytes.
- Toggle is visual-only and immediate (see §3.4).

---

## 7. Threading

- SDK events fire on background threads → marshal to the UI thread via `Dispatcher` to update VM target state (matching the existing artwork handlers).
- The easing loop runs on the UI thread via `CompositionTarget.Rendering`.

---

## 8. Testing

- **TDD on pure functions:** loudness→energy curve, beat-pulse envelope decay, palette→color mapping, easing step. Keep these in testable helpers so the View stays thin.
- **VM tests:** feed fake `ColorChanged`/`VisualizationReceived` events, assert target state.
- **Compile/verify against dev SDK:** dispatch `ci-sdk-dev.yml` (ProjectReference against the SDK dev branch) — local builds restore stable NuGet 8.0.0 and cannot compile 8.1.0-only APIs by design.
- **Manual visual check** against a live server emitting color + visualizer frames.
- **Open item:** no test project currently exists for the Windows client — planning will decide whether to add one or place the pure logic in a testable class library.

---

## 9. Files touched (anticipated)

| File | Change |
|---|---|
| `src/SendspinClient/ViewModels/AmbientBackdropViewModel.cs` | **new** — target state + mapping |
| `src/SendspinClient/Views/AmbientBackdropView.xaml(.cs)` | **new** — WPF renderer + easing loop |
| `src/SendspinClient/MainWindow.xaml` | host backdrop as background layer; keep blurred-art fallback |
| `src/SendspinClient/ViewModels/MainViewModel.cs` | thin `OnColorChanged`/`OnVisualizationReceived` forwarders on both connection sources |
| `src/SendspinClient/App.xaml.cs` | `ClientCapabilities`: add `visualizer@v1` role + `VisualizerSupport` |
| Settings view + config model | toggle + persisted `Visualizer:Enabled` |
| `appsettings.json` | new `Visualizer` section defaults (enabled, rate, buffer) |
| test project | mapping/envelope/easing tests (planning decides: add a test project, or place pure logic in a testable class) |

---

## 10. Release sequencing

This feature compiles only against the new SDK APIs, now released as **9.0.0** and pinned in both csproj files. The app builds and the Services tests run locally, so verification no longer depends on `ci-sdk-dev.yml`. The branch is free to merge to `master` once implementation completes (the artwork-event migration in this branch is part of the same 9.0.0 cutover).
