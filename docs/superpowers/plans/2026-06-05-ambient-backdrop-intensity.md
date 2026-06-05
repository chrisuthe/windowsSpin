# Ambient Backdrop Intensity Slider Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a settings slider (0–200%, default 100%) that scales the Reactive Backdrop's reactivity, presence, and drift speed together, with a faint floor at 0%.

**Architecture:** A single `intensity` scalar flows from `appsettings.json` → `MainViewModel` (bound to the slider, persisted) → `AmbientBackdropViewModel` (stores raw, exposes a floored effective value) → the `AmbientBackdropView` render loop and `AmbientMath` mapping functions. Mirrors the existing enable-toggle path exactly.

**Tech Stack:** .NET 10, WPF, CommunityToolkit.Mvvm (source-generated `[ObservableProperty]`), xUnit.

**Spec:** `docs/superpowers/specs/2026-06-05-ambient-backdrop-intensity-design.md`

---

## File Structure

- **Modify** `src/SendspinClient.Services/Visualization/AmbientMath.cs` — add `IntensityFloor` constant and optional `intensity` param to `BlobScale`/`BlobOpacity`.
- **Modify** `tests/SendspinClient.Services.Tests/Visualization/AmbientMathTests.cs` — add intensity theory cases.
- **Modify** `src/SendspinClient/ViewModels/AmbientBackdropViewModel.cs` — add `Intensity` getter (floored) + `SetIntensity(raw)`.
- **Modify** `src/SendspinClient/Views/AmbientBackdropView.xaml.cs` — consume intensity in scale/opacity, accumulate drift phase.
- **Modify** `src/SendspinClient/ViewModels/MainViewModel.cs` — `SettingsAmbientBackdropIntensity` property, change handler, load, save.
- **Modify** `src/SendspinClient/MainWindow.xaml` — slider + percentage readout row under the existing checkbox.
- **Modify** `src/SendspinClient/appsettings.json` — add `Visualizer:Intensity`.

---

## Task 1: Math — intensity in `BlobScale` / `BlobOpacity`

**Files:**
- Modify: `src/SendspinClient.Services/Visualization/AmbientMath.cs`
- Test: `tests/SendspinClient.Services.Tests/Visualization/AmbientMathTests.cs`

- [ ] **Step 1: Write the failing tests**

Add these to `AmbientMathTests.cs` (inside the `AmbientMathTests` class):

```csharp
[Theory]
[InlineData(1.0, 0.0, 0.0, 0.82)]   // intensity 0 -> floor at ScaleMin
[InlineData(1.0, 0.0, 1.0, 1.32)]   // intensity 1 -> identity (ScaleMin + 0.50)
[InlineData(1.0, 0.0, 2.0, 1.82)]   // intensity 2 -> doubled energy span (ScaleMin + 1.00)
[InlineData(1.0, 1.0, 2.0, 2.52)]   // intensity 2 with full pulse: 0.82 + 2*(0.50+0.35)
public void BlobScale_ScalesReactivityByIntensity(double energy, double pulse, double intensity, double expected)
{
    Assert.Equal(expected, AmbientMath.BlobScale(energy, pulse, intensity), 0.0001);
}

[Theory]
[InlineData(0.0, 0.0, 0.0)]    // intensity 0 -> invisible
[InlineData(0.0, 1.0, 0.55)]   // intensity 1 -> identity
[InlineData(0.0, 2.0, 1.0)]    // intensity 2 -> 2*0.55 = 1.10 clamped to 1.0
[InlineData(1.0, 2.0, 1.0)]    // energy 1, intensity 2 -> clamped to 1.0
public void BlobOpacity_ScalesPresenceByIntensity(double energy, double intensity, double expected)
{
    Assert.Equal(expected, AmbientMath.BlobOpacity(energy, intensity), 0.0001);
}

[Fact]
public void BlobScale_NegativeIntensity_ClampsToZeroReactivity()
{
    Assert.Equal(0.82, AmbientMath.BlobScale(1.0, 1.0, -5.0), 0.0001);
}

[Fact]
public void IntensityFloor_IsFaintButNonZero()
{
    Assert.InRange(AmbientMath.IntensityFloor, 0.05, 0.30);
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/SendspinClient.Services.Tests/SendspinClient.Services.Tests.csproj --filter "FullyQualifiedName~AmbientMathTests"`
Expected: FAIL — compile error (`BlobScale` has no 3-arg overload, `IntensityFloor` undefined).

- [ ] **Step 3: Add the constant and intensity parameters**

In `AmbientMath.cs`, add near the other scale/opacity constants (after `OpacityEnergySpan`):

```csharp
    /// <summary>
    /// Minimum effective intensity. The 0% slider position floors to this so the backdrop
    /// stays faintly alive (subtle, slow, dim) rather than going fully dark. Use the enable
    /// toggle to disable the effect entirely. Applied at the ViewModel boundary, not here.
    /// </summary>
    public const double IntensityFloor = 0.15;
```

Replace `BlobScale` with:

```csharp
    /// <summary>
    /// Blob render scale from eased energy and the current beat pulse, scaled by
    /// <paramref name="intensity"/> (1.0 = default). Energy and pulse are clamped to [0,1];
    /// intensity is clamped to be non-negative. <c>ScaleMin</c> is never scaled, so blobs keep
    /// their minimum size at intensity 0.
    /// </summary>
    public static double BlobScale(double energy, double pulse, double intensity = 1.0)
    {
        var e = Math.Clamp(energy, 0.0, 1.0);
        var p = Math.Clamp(pulse, 0.0, 1.0);
        var i = Math.Max(0.0, intensity);
        return ScaleMin + (i * ((e * ScaleEnergySpan) + (p * ScalePulseSpan)));
    }
```

Replace `BlobOpacity` with:

```csharp
    /// <summary>
    /// Blob opacity from eased energy, scaled by <paramref name="intensity"/> (1.0 = default).
    /// The whole opacity scales, so intensity 0 is invisible; the result is clamped to [0,1].
    /// </summary>
    public static double BlobOpacity(double energy, double intensity = 1.0)
    {
        var e = Math.Clamp(energy, 0.0, 1.0);
        var i = Math.Max(0.0, intensity);
        return Math.Clamp(i * (OpacityMin + (e * OpacityEnergySpan)), 0.0, 1.0);
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/SendspinClient.Services.Tests/SendspinClient.Services.Tests.csproj --filter "FullyQualifiedName~AmbientMathTests"`
Expected: PASS — all existing and new `AmbientMathTests` green (existing 2-arg cases still pass via the `intensity = 1.0` default).

- [ ] **Step 5: Commit**

```bash
git add src/SendspinClient.Services/Visualization/AmbientMath.cs tests/SendspinClient.Services.Tests/Visualization/AmbientMathTests.cs
git commit -m "feat: scale ambient backdrop reactivity and presence by intensity"
```

---

## Task 2: ViewModel — `Intensity` getter and `SetIntensity`

**Files:**
- Modify: `src/SendspinClient/ViewModels/AmbientBackdropViewModel.cs`

No unit test project covers this WPF-assembly ViewModel; correctness is verified by the build plus the math tests in Task 1 (the floor logic is a one-line `Math.Max`). Verification is `dotnet build`.

- [ ] **Step 1: Add the intensity field, getter, and setter**

In `AmbientBackdropViewModel.cs`, add after the `BlobColor3` property block (before the `BeatTriggered` event) — note `using SendspinClient.Services.Visualization;` is already imported:

```csharp
    private double _intensity = 1.0;

    /// <summary>
    /// Effective intensity (<c>IntensityFloor</c>..2.0) consumed by the renderer. The raw 0
    /// setting is floored so the backdrop never goes fully dark via the slider; use
    /// <see cref="SetEnabled"/> to disable entirely.
    /// </summary>
    public double Intensity => Math.Max(AmbientMath.IntensityFloor, _intensity);

    /// <summary>Sets the raw intensity (0..2) from the settings slider. Does not affect IsActive.</summary>
    public void SetIntensity(double raw) => _intensity = Math.Clamp(raw, 0.0, 2.0);
```

- [ ] **Step 2: Build to verify it compiles**

Run: `dotnet build src/SendspinClient/SendspinClient.csproj`
Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
git add src/SendspinClient/ViewModels/AmbientBackdropViewModel.cs
git commit -m "feat: add floored Intensity to AmbientBackdropViewModel"
```

---

## Task 3: View — apply intensity to scale, opacity, and drift speed

**Files:**
- Modify: `src/SendspinClient/Views/AmbientBackdropView.xaml.cs`

- [ ] **Step 1: Add a drift-phase field**

In `AmbientBackdropView.xaml.cs`, add alongside the other animation fields (after `private double _pulseTarget;`):

```csharp
    // Accumulated drift phase (replaces raw monotonic time). Scaled by intensity each frame so
    // drift speed tracks the slider; accumulating keeps phase continuous when intensity changes
    // and across Loaded/Unloaded cycles (persists for the life of the view instance).
    private double _driftPhase;
```

- [ ] **Step 2: Read intensity and apply it to scale/opacity**

In `OnRendering`, replace these lines:

```csharp
        var scale = AmbientMath.BlobScale(_energy, _pulse);
        var opacity = AmbientMath.BlobOpacity(_energy);
```

with:

```csharp
        var intensity = _vm.Intensity;
        var scale = AmbientMath.BlobScale(_energy, _pulse, intensity);
        var opacity = AmbientMath.BlobOpacity(_energy, intensity);
```

- [ ] **Step 3: Drive drift from the accumulated phase**

In `OnRendering`, replace this block:

```csharp
        // Idle drift: slow sinusoidal motion so the scene is alive at low energy. Use a monotonic
        // timestamp (not the restartable _clock) so the drift phase is continuous across
        // Loaded/Unloaded cycles and does not snap on re-show.
        var t = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
```

with:

```csharp
        // Idle drift: slow sinusoidal motion so the scene is alive at low energy. Accumulate a
        // phase scaled by intensity so drift SPEED tracks the slider; accumulation keeps the phase
        // continuous across intensity changes and Loaded/Unloaded cycles (no snap on re-show).
        _driftPhase += dt * intensity;
        var t = _driftPhase;
```

(The six `Blob*Translate` lines that use `t` are unchanged. Amplitude stays fixed — only speed scales.)

- [ ] **Step 4: Build to verify it compiles**

Run: `dotnet build src/SendspinClient/SendspinClient.csproj`
Expected: Build succeeded. (`Stopwatch` is still used by `_clock`, so leave the `using System.Diagnostics;` in place.)

- [ ] **Step 5: Commit**

```bash
git add src/SendspinClient/Views/AmbientBackdropView.xaml.cs
git commit -m "feat: scale ambient backdrop drift speed and visuals by intensity"
```

---

## Task 4: MainViewModel — setting property, handler, load, save

**Files:**
- Modify: `src/SendspinClient/ViewModels/MainViewModel.cs`

- [ ] **Step 1: Add the observable property**

After the `_settingsEnableAmbientBackdrop` property block (around line 285), add:

```csharp
    /// <summary>
    /// Gets or sets the Ambient Glow backdrop intensity (0.0–2.0, 1.0 = default look).
    /// Scales reactivity, presence, and drift speed together.
    /// </summary>
    [ObservableProperty]
    private double _settingsAmbientBackdropIntensity = 1.0;
```

- [ ] **Step 2: Add the change handler**

After the `OnSettingsEnableAmbientBackdropChanged` method (around line 1929), add:

```csharp
    partial void OnSettingsAmbientBackdropIntensityChanged(double value)
    {
        _ambient.SetIntensity(value);
    }
```

- [ ] **Step 3: Load from config**

After the existing ambient load lines (around line 2178–2179):

```csharp
        SettingsEnableAmbientBackdrop = _configuration.GetValue<bool>("Visualizer:Enabled", true);
        _ambient.SetEnabled(SettingsEnableAmbientBackdrop);
```

add:

```csharp
        SettingsAmbientBackdropIntensity = _configuration.GetValue<double>("Visualizer:Intensity", 1.0);
        _ambient.SetIntensity(SettingsAmbientBackdropIntensity);
```

- [ ] **Step 4: Save to config**

In the Visualizer save block (around line 2428–2430), change:

```csharp
            var visualizerSection = root["Visualizer"]?.AsObject() ?? new JsonObject();
            visualizerSection["Enabled"] = SettingsEnableAmbientBackdrop;
            root["Visualizer"] = visualizerSection;
```

to:

```csharp
            var visualizerSection = root["Visualizer"]?.AsObject() ?? new JsonObject();
            visualizerSection["Enabled"] = SettingsEnableAmbientBackdrop;
            visualizerSection["Intensity"] = SettingsAmbientBackdropIntensity;
            root["Visualizer"] = visualizerSection;
```

- [ ] **Step 5: Build to verify it compiles**

Run: `dotnet build src/SendspinClient/SendspinClient.csproj`
Expected: Build succeeded.

- [ ] **Step 6: Commit**

```bash
git add src/SendspinClient/ViewModels/MainViewModel.cs
git commit -m "feat: persist and apply ambient backdrop intensity setting"
```

---

## Task 5: UI — intensity slider in settings

**Files:**
- Modify: `src/SendspinClient/MainWindow.xaml`

- [ ] **Step 1: Add the slider row**

In `MainWindow.xaml`, immediately after the closing `</Grid>` of the "Ambient Glow reactive backdrop" checkbox row (the `</Grid>` at line ~646, before the "Hardware Media Keys / SMTC" comment), insert:

```xml
                            <!-- Ambient Glow intensity -->
                            <Grid Margin="0,0,0,16"
                                  IsEnabled="{Binding SettingsEnableAmbientBackdrop}">
                                <Grid.ColumnDefinitions>
                                    <ColumnDefinition Width="*"/>
                                    <ColumnDefinition Width="Auto"/>
                                </Grid.ColumnDefinitions>
                                <StackPanel Grid.Column="0" VerticalAlignment="Center">
                                    <TextBlock Text="Backdrop intensity" Style="{StaticResource BodyText}"/>
                                    <TextBlock Text="Scale how strongly the backdrop reacts, glows, and moves"
                                               Style="{StaticResource CaptionText}"
                                               Foreground="{StaticResource TextMutedBrush}"
                                               Margin="0,2,0,0"
                                               TextWrapping="Wrap"/>
                                </StackPanel>
                                <StackPanel Grid.Column="1" Orientation="Horizontal" VerticalAlignment="Center">
                                    <Slider Width="160"
                                            Minimum="0" Maximum="2"
                                            TickFrequency="0.25" IsSnapToTickEnabled="False"
                                            Value="{Binding SettingsAmbientBackdropIntensity, Mode=TwoWay}"
                                            VerticalAlignment="Center"/>
                                    <TextBlock Text="{Binding SettingsAmbientBackdropIntensity, StringFormat={}{0:P0}}"
                                               Style="{StaticResource CaptionText}"
                                               Foreground="{StaticResource TextMutedBrush}"
                                               Width="44" TextAlignment="Right"
                                               Margin="8,0,0,0"
                                               VerticalAlignment="Center"/>
                                </StackPanel>
                            </Grid>
```

- [ ] **Step 2: Build to verify the XAML compiles**

Run: `dotnet build src/SendspinClient/SendspinClient.csproj`
Expected: Build succeeded (no XAML parse errors; `BodyText`, `CaptionText`, `TextMutedBrush` all already resolve in this view).

- [ ] **Step 3: Commit**

```bash
git add src/SendspinClient/MainWindow.xaml
git commit -m "feat: add ambient backdrop intensity slider to settings"
```

---

## Task 6: Config default

**Files:**
- Modify: `src/SendspinClient/appsettings.json`

- [ ] **Step 1: Add the Intensity default**

In `appsettings.json`, change the `Visualizer` section from:

```json
  "Visualizer": {
    "Enabled": true,
    "RateMax": 30,
    "BufferCapacity": 4096
  }
```

to:

```json
  "Visualizer": {
    "Enabled": true,
    "Intensity": 1.0,
    "RateMax": 30,
    "BufferCapacity": 4096
  }
```

- [ ] **Step 2: Commit**

```bash
git add src/SendspinClient/appsettings.json
git commit -m "chore: add Visualizer:Intensity default to appsettings"
```

---

## Task 7: Final verification

- [ ] **Step 1: Full build**

Run: `dotnet build SendspinClient.sln`
Expected: Build succeeded, 0 errors.

- [ ] **Step 2: Full test run**

Run: `dotnet test`
Expected: All tests pass (including the new `AmbientMathTests` cases).

- [ ] **Step 3: Manual smoke test**

Run the app (`dotnet run --project src/SendspinClient/SendspinClient.csproj`), connect, play audio, open Settings:
- Drag the slider 0% → 200%: backdrop visibly calms then intensifies (size, glow, drift speed).
- At 0%: backdrop is faint and slow but still alive (not black/frozen).
- Toggle the backdrop off: the slider greys out (disabled).
- Change the value, restart the app: the value persists (written to `%LOCALAPPDATA%\Sendspin\appsettings.json`).

---

## Self-Review Notes

- **Spec coverage:** math (T1), motion (T3), ViewModel floor (T2), MainViewModel plumbing (T4), UI slider + grey-out + readout (T5), config default (T6), tests (T1). All spec sections mapped.
- **Type consistency:** `SetIntensity(double)` / `Intensity` getter used identically in T2, T3, T4. `SettingsAmbientBackdropIntensity` used identically in T4 and T5. `IntensityFloor` defined in T1, consumed in T2.
- **Floor location:** defined as a constant in `AmbientMath` (T1) but applied only in the ViewModel getter (T2), per the spec — the math functions remain honest multipliers.
