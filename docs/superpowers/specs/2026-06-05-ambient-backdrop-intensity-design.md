# Ambient Backdrop Intensity Slider — Design

**Date:** 2026-06-05
**Status:** Approved (pending spec review)

## Goal

Add a user-facing **intensity** slider for the Reactive Backdrop (Ambient Glow) feature
that scales the whole effect up and down: how strongly blobs react to the music
(reactivity), how visible the backdrop is even when quiet (presence), and how fast the
blobs drift (motion). One knob drives all three for a single "calm ↔ energetic" feel.

## Decisions

| Question | Decision |
|----------|----------|
| What does intensity scale? | **Everything** — reactivity, presence, and motion speed together. |
| Range / default | **0–200%, default 100%.** 100% reproduces today's shipped look exactly. |
| Behavior at 0% | **Floored to a faint minimum** (~15%): a subtle, slow, dim backdrop. The slider never reaches fully-off; the existing enable toggle remains the only way to fully disable. |

## Architecture

A single scalar `intensity` (effective range `[IntensityFloor, 2.0]`, where `1.0` is the
shipped default) is threaded into the three places that produce visible output. The value
flows along the same path as the existing enable toggle:

```
appsettings.json (Visualizer:Intensity)
   → MainViewModel.SettingsAmbientBackdropIntensity   (raw 0..2, bound to slider, persisted)
   → AmbientBackdropViewModel.SetIntensity(raw)        (stores raw, clamps to [0,2])
   → AmbientBackdropViewModel.Intensity (getter)       (raw floored to IntensityFloor)
   → AmbientBackdropView render loop / AmbientMath     (consumes effective intensity)
```

The render loop already polls the ViewModel every frame (the same way it reads
`TargetEnergy`), so no change-notification event is needed for the visuals.

## Component changes

### 1. `AmbientMath.cs` (pure math)

Add a new tuning constant and an optional `intensity` parameter to the two mapping
functions. The parameter defaults to `1.0`, so existing callers and tests are unaffected
and `intensity = 1.0` reproduces today's output exactly.

```csharp
/// <summary>Minimum effective intensity. The 0% slider position floors to this so the
/// backdrop stays faintly alive; use the enable toggle to disable entirely.</summary>
public const double IntensityFloor = 0.15;

// Reactivity: ScaleMin is unscaled so blobs never collapse geometrically.
public static double BlobScale(double energy, double pulse, double intensity = 1.0)
    => ScaleMin + (Math.Max(0.0, intensity) * ((e * ScaleEnergySpan) + (p * ScalePulseSpan)));

// Presence: whole opacity scales, clamped to [0,1].
public static double BlobOpacity(double energy, double intensity = 1.0)
    => Math.Clamp(Math.Max(0.0, intensity) * (OpacityMin + (e * OpacityEnergySpan)), 0.0, 1.0);
```

(`e` and `p` are the existing `[0,1]`-clamped energy/pulse.)

- `intensity = 0` → scale floors at `ScaleMin`, opacity → 0.
- `intensity = 1` → unchanged from today.
- `intensity = 2` → reactivity span doubles; opacity saturates toward 1.

`IntensityFloor` lives in `AmbientMath` (the home for tuning constants) but is **applied**
at the ViewModel boundary, not inside these functions — the functions stay honest
multipliers so they're easy to test at any intensity.

### 2. `AmbientBackdropView.xaml.cs` (motion + render)

- Read `var intensity = _vm.Intensity;` once per frame.
- Pass it to `AmbientMath.BlobScale(...)` and `AmbientMath.BlobOpacity(...)`.
- **Drift speed:** replace the raw monotonic clock (`Stopwatch.GetTimestamp() / freq`) with
  an accumulated phase field:

  ```csharp
  private double _driftPhase;
  ...
  _driftPhase += dt * intensity;   // intensity scales drift SPEED only
  var t = _driftPhase;             // used in the existing sin/cos terms unchanged
  ```

  Accumulation (rather than multiplying raw time) keeps the drift phase continuous when the
  slider moves and across Loaded/Unloaded cycles — preserving the existing "no snap on
  re-show" behavior. `_driftPhase` persists for the life of the view instance.
- **Amplitude is left unscaled.** The blob-center offsets are deliberately kept in the
  window interior so edges only ever see the faded part of each gradient; scaling amplitude
  could push a bright core to an edge. Only speed scales.

### 3. `AmbientBackdropViewModel.cs`

```csharp
private double _intensity = 1.0;

/// <summary>Effective intensity (IntensityFloor..2.0) consumed by the renderer.
/// The raw 0 setting is floored so the backdrop never goes fully dark via the slider.</summary>
public double Intensity => Math.Max(AmbientMath.IntensityFloor, _intensity);

/// <summary>Sets the raw intensity (0..2) from the settings slider.</summary>
public void SetIntensity(double raw) => _intensity = Math.Clamp(raw, 0.0, 2.0);
```

No effect on `IsActive` (intensity and enable are independent), so this does not call
`UpdateActive()`.

### 4. `MainViewModel.cs`

Exact parallel to `SettingsEnableAmbientBackdrop`:

- `[ObservableProperty] private double _settingsAmbientBackdropIntensity = 1.0;`
- `partial void OnSettingsAmbientBackdropIntensityChanged(double value) => _ambient.SetIntensity(value);`
- **Load:** `SettingsAmbientBackdropIntensity = _configuration.GetValue<double>("Visualizer:Intensity", 1.0);`
  followed by `_ambient.SetIntensity(SettingsAmbientBackdropIntensity);` (alongside the
  existing `SetEnabled` load).
- **Save:** `visualizerSection["Intensity"] = SettingsAmbientBackdropIntensity;` in the
  existing Visualizer section block.

### 5. `MainWindow.xaml`

Below the existing "Reactive backdrop (Ambient Glow)" checkbox row, add a slider row:

- A `Slider` with `Minimum="0"`, `Maximum="2"`, default value bound to
  `SettingsAmbientBackdropIntensity` (two-way), a sensible `TickFrequency` (e.g. `0.25`).
- A percentage readout `TextBlock` bound with `StringFormat={}{0:P0}` (1.0 → "100%").
- The slider's `IsEnabled` binds to `SettingsEnableAmbientBackdrop` so it greys out when the
  backdrop is toggled off.
- Follow the existing settings-row layout (label + caption in a `StackPanel`, control in the
  second column), matching surrounding styles (`BodyText`, `CaptionText`, `TextMutedBrush`).

### 6. `appsettings.json`

Add `"Intensity": 1.0` to the existing `Visualizer` section.

### 7. Tests — `AmbientMathTests.cs`

Add theory cases covering the new parameter; existing no-intensity cases stay valid (they
assert the `intensity = 1.0` default):

- `BlobScale` with `intensity` 0 / 1 / 2: floor at `ScaleMin` at 0, identity at 1, doubled
  span at 2.
- `BlobOpacity` with `intensity` 0 / 1 / 2: 0 at intensity 0, identity at 1, clamp to ≤1 at 2.
- Negative intensity clamped to 0 (defensive).

## Out of scope

- No new animation modes or color controls.
- No per-axis sliders (single combined knob only).
- Amplitude of drift is not scaled (speed only).

## Verification

- `dotnet build` clean.
- `dotnet test` — new and existing `AmbientMathTests` pass.
- Manual: launch the app, play audio, drag the slider 0%→200% and confirm the backdrop
  visibly calms/intensifies; confirm 0% is faint-but-alive, not dead; confirm the slider
  greys out when the backdrop toggle is off; confirm the value persists across restart.
