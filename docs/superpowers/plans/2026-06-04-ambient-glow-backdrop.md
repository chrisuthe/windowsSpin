# Ambient Glow Reactive Backdrop Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the static blurred-album-art background with an always-on backdrop of soft color blobs driven by the SDK `color@v1` palette, breathing with `loudness` and pulsing on `beat`.

**Architecture:** Pure-WPF renderer (RadialGradientBrush ellipses + a `CompositionTarget.Rendering` easing loop) behind a clean seam. SDK-version-independent math lives in `SendspinClient.Services` (unit-tested locally); the SDK-coupled ViewModel/View are verified by the dev-source CI build. When no palette is present the existing blurred-art layer shows through (XAML visibility only).

**Tech Stack:** .NET 10, WPF, CommunityToolkit.Mvvm, Sendspin.SDK 8.1.0 (dev branch until released), xUnit (new test project).

**Spec:** `docs/superpowers/specs/2026-06-04-ambient-glow-backdrop-design.md`

---

## Important constraints

- **Do NOT merge to `master` until Sendspin.SDK 8.1.0 is published to NuGet.** This branch already contains the 8.1.0 artwork-event migration; pin `8.1.0` at release time.
- **Local builds of `SendspinClient` (the app) will not compile** because they restore stable NuGet `8.0.0`, which lacks the 8.1.0 APIs. Verify the app/VM/View by dispatching the `ci-sdk-dev.yml` workflow (builds against the SDK dev branch). Only the `SendspinClient.Services` project and its tests build/run locally.
- All ViewModel state mutation happens on the UI thread (SDK events are marshaled via `Dispatcher`), so the math and View need no locks.

---

## File structure

| File | Responsibility |
|---|---|
| `src/SendspinClient.Services/Visualization/AmbientMath.cs` | **new** — pure, dependency-free math (energy mapping, easing, decay, visual-param curves). Unit-tested. |
| `tests/SendspinClient.Services.Tests/SendspinClient.Services.Tests.csproj` | **new** — xUnit project referencing `SendspinClient.Services`. |
| `tests/SendspinClient.Services.Tests/Visualization/AmbientMathTests.cs` | **new** — tests for `AmbientMath`. |
| `src/SendspinClient/ViewModels/AmbientBackdropViewModel.cs` | **new** — target state; maps SDK `ColorPalette`/`VisualizerFrame` → displayable state; raises beat events. |
| `src/SendspinClient/Views/AmbientBackdropView.xaml` (+ `.xaml.cs`) | **new** — 3 RadialGradientBrush ellipses + the easing render loop. |
| `src/SendspinClient/ViewModels/MainViewModel.cs` | modify — own `Ambient`, wire `ColorChanged`/`VisualizationReceived` on both connections, settings toggle. |
| `src/SendspinClient/App.xaml.cs` | modify — `ClientCapabilities` roles + `VisualizerSupport`; register `AmbientBackdropViewModel`. |
| `src/SendspinClient/MainWindow.xaml` | modify — host `AmbientBackdropView` as a background layer; settings toggle. |
| `src/SendspinClient/appsettings.json` | modify — new `Visualizer` section defaults. |

---

## Task 1: Add the Services test project

**Files:**
- Create: `tests/SendspinClient.Services.Tests/SendspinClient.Services.Tests.csproj`
- Modify: `SendspinClient.sln`

- [ ] **Step 1: Create the test project file**

Create `tests/SendspinClient.Services.Tests/SendspinClient.Services.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0-windows10.0.17763.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <IsPackable>false</IsPackable>
    <!-- This test project intentionally does NOT reference the WPF app, so it builds
         against stable NuGet Sendspin.SDK and runs locally. -->
    <UseSdkSource>false</UseSdkSource>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.11.1" />
    <PackageReference Include="xunit" Version="2.9.2" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\SendspinClient.Services\SendspinClient.Services.csproj" />
  </ItemGroup>

</Project>
```

- [ ] **Step 2: Add the project to the solution**

Run: `dotnet sln SendspinClient.sln add tests/SendspinClient.Services.Tests/SendspinClient.Services.Tests.csproj`
Expected: "Project ... added to the solution."

- [ ] **Step 3: Verify it restores and runs (no tests yet)**

Run: `dotnet test tests/SendspinClient.Services.Tests/SendspinClient.Services.Tests.csproj`
Expected: build succeeds; output reports 0 tests (or "no test is available") — the point is a clean build/restore against stable NuGet.

- [ ] **Step 4: Commit**

```bash
git add SendspinClient.sln tests/SendspinClient.Services.Tests/SendspinClient.Services.Tests.csproj
git commit -m "test: add SendspinClient.Services test project"
```

---

## Task 2: `AmbientMath.NormalizeLoudness`

**Files:**
- Create: `src/SendspinClient.Services/Visualization/AmbientMath.cs`
- Test: `tests/SendspinClient.Services.Tests/Visualization/AmbientMathTests.cs`

- [ ] **Step 1: Write the failing test**

Create `tests/SendspinClient.Services.Tests/Visualization/AmbientMathTests.cs`:

```csharp
using SendspinClient.Services.Visualization;
using Xunit;

namespace SendspinClient.Services.Tests.Visualization;

public class AmbientMathTests
{
    [Fact]
    public void NormalizeLoudness_Null_ReturnsZero()
    {
        Assert.Equal(0.0, AmbientMath.NormalizeLoudness(null));
    }

    [Theory]
    [InlineData(0, 0.0)]
    [InlineData(65535, 1.0)]
    [InlineData(32767, 0.49999, 0.001)]
    public void NormalizeLoudness_MapsRawToUnitRange(int raw, double expected, double tolerance = 1e-9)
    {
        Assert.Equal(expected, AmbientMath.NormalizeLoudness(raw), tolerance);
    }

    [Theory]
    [InlineData(-100)]
    [InlineData(99999)]
    public void NormalizeLoudness_ClampsOutOfRange(int raw)
    {
        var v = AmbientMath.NormalizeLoudness(raw);
        Assert.InRange(v, 0.0, 1.0);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/SendspinClient.Services.Tests/SendspinClient.Services.Tests.csproj`
Expected: FAIL — `AmbientMath` / `NormalizeLoudness` does not exist (compile error).

- [ ] **Step 3: Write minimal implementation**

Create `src/SendspinClient.Services/Visualization/AmbientMath.cs`:

```csharp
namespace SendspinClient.Services.Visualization;

/// <summary>
/// Pure, dependency-free math for the Ambient Glow backdrop. No WPF or SDK types so it can be
/// unit-tested against any SDK version. The ViewModel adapts SDK frames into these primitives.
/// </summary>
public static class AmbientMath
{
    /// <summary>Maximum raw loudness value carried by a visualizer loudness frame.</summary>
    public const int LoudnessMax = 65535;

    /// <summary>
    /// Maps a raw loudness value (0..65535, already dB-normalized by the server) to a 0..1 energy
    /// level. Null (no loudness frame yet) maps to 0. Out-of-range values are clamped.
    /// </summary>
    public static double NormalizeLoudness(int? rawLoudness)
    {
        if (rawLoudness is not { } raw)
        {
            return 0.0;
        }

        return Math.Clamp(raw / (double)LoudnessMax, 0.0, 1.0);
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/SendspinClient.Services.Tests/SendspinClient.Services.Tests.csproj`
Expected: PASS (all `NormalizeLoudness` tests).

- [ ] **Step 5: Commit**

```bash
git add src/SendspinClient.Services/Visualization/AmbientMath.cs tests/SendspinClient.Services.Tests/Visualization/AmbientMathTests.cs
git commit -m "feat: add AmbientMath.NormalizeLoudness with tests"
```

---

## Task 3: `AmbientMath.Ease` (exponential smoothing)

**Files:**
- Modify: `src/SendspinClient.Services/Visualization/AmbientMath.cs`
- Test: `tests/SendspinClient.Services.Tests/Visualization/AmbientMathTests.cs`

- [ ] **Step 1: Write the failing test**

Append to `AmbientMathTests`:

```csharp
    [Fact]
    public void Ease_ZeroDt_ReturnsCurrent()
    {
        Assert.Equal(0.2, AmbientMath.Ease(0.2, 1.0, dtSeconds: 0.0, timeConstantSeconds: 0.5));
    }

    [Fact]
    public void Ease_MovesTowardTarget()
    {
        var next = AmbientMath.Ease(0.0, 1.0, dtSeconds: 0.1, timeConstantSeconds: 0.5);
        Assert.InRange(next, 0.0, 1.0);
        Assert.True(next > 0.0, "should move toward target");
    }

    [Fact]
    public void Ease_LargeDt_ApproachesTarget()
    {
        var next = AmbientMath.Ease(0.0, 1.0, dtSeconds: 10.0, timeConstantSeconds: 0.5);
        Assert.True(next > 0.99, "after many time constants it should be near target");
    }

    [Fact]
    public void Ease_ZeroTimeConstant_SnapsToTarget()
    {
        Assert.Equal(1.0, AmbientMath.Ease(0.0, 1.0, dtSeconds: 0.016, timeConstantSeconds: 0.0));
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/SendspinClient.Services.Tests/SendspinClient.Services.Tests.csproj`
Expected: FAIL — `Ease` does not exist.

- [ ] **Step 3: Write minimal implementation**

Add to `AmbientMath`:

```csharp
    /// <summary>
    /// Exponentially eases <paramref name="current"/> toward <paramref name="target"/> for a frame
    /// of <paramref name="dtSeconds"/>. <paramref name="timeConstantSeconds"/> is the e-folding time
    /// (smaller = snappier). A non-positive time constant snaps to the target; dt &lt;= 0 is a no-op.
    /// Frame-rate independent.
    /// </summary>
    public static double Ease(double current, double target, double dtSeconds, double timeConstantSeconds)
    {
        if (dtSeconds <= 0.0)
        {
            return current;
        }

        if (timeConstantSeconds <= 0.0)
        {
            return target;
        }

        var alpha = 1.0 - Math.Exp(-dtSeconds / timeConstantSeconds);
        return current + ((target - current) * alpha);
    }
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/SendspinClient.Services.Tests/SendspinClient.Services.Tests.csproj`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/SendspinClient.Services/Visualization/AmbientMath.cs tests/SendspinClient.Services.Tests/Visualization/AmbientMathTests.cs
git commit -m "feat: add AmbientMath.Ease with tests"
```

---

## Task 4: `AmbientMath.Decay` (beat-pulse envelope)

**Files:**
- Modify: `src/SendspinClient.Services/Visualization/AmbientMath.cs`
- Test: `tests/SendspinClient.Services.Tests/Visualization/AmbientMathTests.cs`

- [ ] **Step 1: Write the failing test**

Append to `AmbientMathTests`:

```csharp
    [Fact]
    public void Decay_AfterOneHalfLife_IsHalf()
    {
        var v = AmbientMath.Decay(1.0, dtSeconds: 0.25, halfLifeSeconds: 0.25);
        Assert.Equal(0.5, v, 0.001);
    }

    [Fact]
    public void Decay_ZeroDt_ReturnsCurrent()
    {
        Assert.Equal(0.8, AmbientMath.Decay(0.8, dtSeconds: 0.0, halfLifeSeconds: 0.3));
    }

    [Fact]
    public void Decay_NonPositiveHalfLife_ReturnsZero()
    {
        Assert.Equal(0.0, AmbientMath.Decay(1.0, dtSeconds: 0.016, halfLifeSeconds: 0.0));
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/SendspinClient.Services.Tests/SendspinClient.Services.Tests.csproj`
Expected: FAIL — `Decay` does not exist.

- [ ] **Step 3: Write minimal implementation**

Add to `AmbientMath`:

```csharp
    /// <summary>
    /// Decays <paramref name="current"/> toward zero with the given <paramref name="halfLifeSeconds"/>
    /// over a frame of <paramref name="dtSeconds"/>. Used for the additive beat-pulse envelope: the
    /// caller adds an impulse on a beat, then calls this each frame. Non-positive half-life returns 0.
    /// </summary>
    public static double Decay(double current, double dtSeconds, double halfLifeSeconds)
    {
        if (dtSeconds <= 0.0)
        {
            return current;
        }

        if (halfLifeSeconds <= 0.0)
        {
            return 0.0;
        }

        return current * Math.Pow(0.5, dtSeconds / halfLifeSeconds);
    }
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/SendspinClient.Services.Tests/SendspinClient.Services.Tests.csproj`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/SendspinClient.Services/Visualization/AmbientMath.cs tests/SendspinClient.Services.Tests/Visualization/AmbientMathTests.cs
git commit -m "feat: add AmbientMath.Decay with tests"
```

---

## Task 5: `AmbientMath` visual-parameter curves

**Files:**
- Modify: `src/SendspinClient.Services/Visualization/AmbientMath.cs`
- Test: `tests/SendspinClient.Services.Tests/Visualization/AmbientMathTests.cs`

These map energy (0..1) and the current pulse (>=0) to blob scale and opacity. Bounds chosen for a subtle, never-distracting effect (scale 0.85..1.15 from energy, plus up to +0.15 from a full pulse; opacity 0.45..0.85).

- [ ] **Step 1: Write the failing test**

Append to `AmbientMathTests`:

```csharp
    [Theory]
    [InlineData(0.0, 0.0, 0.85)]
    [InlineData(1.0, 0.0, 1.15)]
    [InlineData(1.0, 1.0, 1.30)]
    public void BlobScale_MapsEnergyAndPulse(double energy, double pulse, double expected)
    {
        Assert.Equal(expected, AmbientMath.BlobScale(energy, pulse), 0.0001);
    }

    [Theory]
    [InlineData(0.0, 0.45)]
    [InlineData(1.0, 0.85)]
    public void BlobOpacity_MapsEnergy(double energy, double expected)
    {
        Assert.Equal(expected, AmbientMath.BlobOpacity(energy), 0.0001);
    }

    [Fact]
    public void BlobScale_ClampsNegativeInputs()
    {
        Assert.Equal(0.85, AmbientMath.BlobScale(-1.0, -1.0), 0.0001);
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/SendspinClient.Services.Tests/SendspinClient.Services.Tests.csproj`
Expected: FAIL — `BlobScale` / `BlobOpacity` do not exist.

- [ ] **Step 3: Write minimal implementation**

Add to `AmbientMath`:

```csharp
    /// <summary>Minimum blob scale (at zero energy).</summary>
    public const double ScaleMin = 0.85;

    /// <summary>Blob scale span contributed by energy (0..1).</summary>
    public const double ScaleEnergySpan = 0.30;

    /// <summary>Additional blob scale contributed by a full (1.0) beat pulse.</summary>
    public const double ScalePulseSpan = 0.15;

    /// <summary>Minimum blob opacity (at zero energy).</summary>
    public const double OpacityMin = 0.45;

    /// <summary>Blob opacity span contributed by energy (0..1).</summary>
    public const double OpacityEnergySpan = 0.40;

    /// <summary>Blob render scale from eased energy and the current beat pulse.</summary>
    public static double BlobScale(double energy, double pulse)
    {
        var e = Math.Clamp(energy, 0.0, 1.0);
        var p = Math.Max(pulse, 0.0);
        return ScaleMin + (e * ScaleEnergySpan) + (p * ScalePulseSpan);
    }

    /// <summary>Blob opacity from eased energy.</summary>
    public static double BlobOpacity(double energy)
    {
        var e = Math.Clamp(energy, 0.0, 1.0);
        return OpacityMin + (e * OpacityEnergySpan);
    }
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/SendspinClient.Services.Tests/SendspinClient.Services.Tests.csproj`
Expected: PASS (all `AmbientMath` tests).

- [ ] **Step 5: Commit**

```bash
git add src/SendspinClient.Services/Visualization/AmbientMath.cs tests/SendspinClient.Services.Tests/Visualization/AmbientMathTests.cs
git commit -m "feat: add AmbientMath blob scale/opacity curves with tests"
```

---

## Task 6: `AmbientBackdropViewModel`

Holds the *target* state the View eases toward, adapts SDK types, and raises a beat event. SDK-coupled — verified by the dev-source CI build (Task 13), not local unit tests.

**Files:**
- Create: `src/SendspinClient/ViewModels/AmbientBackdropViewModel.cs`

- [ ] **Step 1: Create the ViewModel**

Create `src/SendspinClient/ViewModels/AmbientBackdropViewModel.cs`:

```csharp
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using SendspinClient.Services.Visualization;
using Sendspin.SDK.Models;

namespace SendspinClient.ViewModels;

/// <summary>
/// Target state for the Ambient Glow backdrop. The View (<c>AmbientBackdropView</c>) reads these
/// properties from its render loop and eases the displayed visuals toward them. All mutation happens
/// on the UI thread (MainViewModel marshals SDK events via the Dispatcher), so no locking is needed.
/// </summary>
public partial class AmbientBackdropViewModel : ObservableObject
{
    private static readonly Color FallbackBase = Color.FromRgb(0x1E, 0x1E, 0x2E);
    private static readonly Color FallbackBlob1 = Color.FromRgb(0x6D, 0x28, 0xD9);
    private static readonly Color FallbackBlob2 = Color.FromRgb(0x06, 0xB6, 0xD4);
    private static readonly Color FallbackBlob3 = Color.FromRgb(0xDB, 0x27, 0x77);

    /// <summary>Whether the feature is enabled in settings.</summary>
    [ObservableProperty]
    private bool _enabled = true;

    /// <summary>True when a real color palette has been received and the feature is enabled.</summary>
    [ObservableProperty]
    private bool _isActive;

    /// <summary>Target energy (0..1) from the latest loudness frame; eased by the View.</summary>
    public double TargetEnergy { get; private set; }

    /// <summary>Base (background) color target.</summary>
    public Color BaseColor { get; private set; } = FallbackBase;

    /// <summary>The three blob color targets.</summary>
    public Color BlobColor1 { get; private set; } = FallbackBlob1;
    public Color BlobColor2 { get; private set; } = FallbackBlob2;
    public Color BlobColor3 { get; private set; } = FallbackBlob3;

    /// <summary>Raised when a beat frame arrives; strength is ~0.6 for a beat, 1.0 for a downbeat.</summary>
    public event EventHandler<double>? BeatTriggered;

    private bool _hasPalette;

    /// <summary>Applies a color palette (from the <c>color@v1</c> role).</summary>
    public void ApplyColorPalette(ColorPalette palette)
    {
        _hasPalette = palette.Primary is not null || palette.Accent is not null || palette.OnDark is not null
            || palette.BackgroundDark is not null;

        BaseColor = ToColor(palette.BackgroundDark, FallbackBase);
        BlobColor1 = ToColor(palette.Primary, FallbackBlob1);
        BlobColor2 = ToColor(palette.Accent, FallbackBlob2);
        BlobColor3 = ToColor(palette.OnDark, FallbackBlob3);

        UpdateActive();
    }

    /// <summary>Applies one visualizer feature frame (loudness or beat).</summary>
    public void ApplyVisualizerFrame(VisualizerFrame frame)
    {
        if (frame.Loudness is { } loud)
        {
            TargetEnergy = AmbientMath.NormalizeLoudness(loud);
        }

        if (frame.IsDownbeat is { } downbeat)
        {
            BeatTriggered?.Invoke(this, downbeat ? 1.0 : 0.6);
        }
    }

    /// <summary>Enables/disables the effect (from the settings toggle). Immediate, no reconnect.</summary>
    public void SetEnabled(bool enabled)
    {
        Enabled = enabled;
        UpdateActive();
    }

    /// <summary>Resets to the inactive/idle state (e.g. on disconnect).</summary>
    public void Reset()
    {
        _hasPalette = false;
        TargetEnergy = 0.0;
        UpdateActive();
    }

    private void UpdateActive() => IsActive = Enabled && _hasPalette;

    private static Color ToColor(RgbColor? rgb, Color fallback)
        => rgb is { } c ? Color.FromRgb(c.R, c.G, c.B) : fallback;
}
```

- [ ] **Step 2: Verify it compiles (against the SDK source)**

Local build will fail (NuGet 8.0.0). If a local SDK dev checkout is available, verify with:
Run: `dotnet build src/SendspinClient/SendspinClient.csproj -p:UseSdkSource=true -p:SdkSourcePath=C:/CodeProjects/SendspinSDK`
Expected: build succeeds. Otherwise defer compile verification to Task 13 (CI).

- [ ] **Step 3: Commit**

```bash
git add src/SendspinClient/ViewModels/AmbientBackdropViewModel.cs
git commit -m "feat: add AmbientBackdropViewModel (ambient glow target state)"
```

---

## Task 7: `AmbientBackdropView` (renderer + easing loop)

**Files:**
- Create: `src/SendspinClient/Views/AmbientBackdropView.xaml`
- Create: `src/SendspinClient/Views/AmbientBackdropView.xaml.cs`

- [ ] **Step 1: Create the XAML**

Create `src/SendspinClient/Views/AmbientBackdropView.xaml`:

```xml
<UserControl x:Class="SendspinClient.Views.AmbientBackdropView"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             IsHitTestVisible="False"
             ClipToBounds="True">
    <Grid x:Name="RootGrid">
        <!-- Base fill -->
        <Rectangle x:Name="BaseFill"/>

        <!-- Three soft color blobs (RadialGradientBrush = soft glow, no BlurEffect) -->
        <Ellipse x:Name="Blob1" Width="520" Height="520"
                 HorizontalAlignment="Left" VerticalAlignment="Top"
                 RenderTransformOrigin="0.5,0.5">
            <Ellipse.Fill>
                <RadialGradientBrush x:Name="Brush1" GradientOrigin="0.5,0.5">
                    <GradientStop x:Name="Brush1Stop0" Offset="0.0"/>
                    <GradientStop x:Name="Brush1Stop1" Offset="1.0" Color="#00000000"/>
                </RadialGradientBrush>
            </Ellipse.Fill>
            <Ellipse.RenderTransform>
                <TransformGroup>
                    <ScaleTransform x:Name="Blob1Scale"/>
                    <TranslateTransform x:Name="Blob1Translate"/>
                </TransformGroup>
            </Ellipse.RenderTransform>
        </Ellipse>

        <Ellipse x:Name="Blob2" Width="460" Height="460"
                 HorizontalAlignment="Right" VerticalAlignment="Bottom"
                 RenderTransformOrigin="0.5,0.5">
            <Ellipse.Fill>
                <RadialGradientBrush x:Name="Brush2" GradientOrigin="0.5,0.5">
                    <GradientStop x:Name="Brush2Stop0" Offset="0.0"/>
                    <GradientStop x:Name="Brush2Stop1" Offset="1.0" Color="#00000000"/>
                </RadialGradientBrush>
            </Ellipse.Fill>
            <Ellipse.RenderTransform>
                <TransformGroup>
                    <ScaleTransform x:Name="Blob2Scale"/>
                    <TranslateTransform x:Name="Blob2Translate"/>
                </TransformGroup>
            </Ellipse.RenderTransform>
        </Ellipse>

        <Ellipse x:Name="Blob3" Width="420" Height="420"
                 HorizontalAlignment="Center" VerticalAlignment="Center"
                 RenderTransformOrigin="0.5,0.5">
            <Ellipse.Fill>
                <RadialGradientBrush x:Name="Brush3" GradientOrigin="0.5,0.5">
                    <GradientStop x:Name="Brush3Stop0" Offset="0.0"/>
                    <GradientStop x:Name="Brush3Stop1" Offset="1.0" Color="#00000000"/>
                </RadialGradientBrush>
            </Ellipse.Fill>
            <Ellipse.RenderTransform>
                <TransformGroup>
                    <ScaleTransform x:Name="Blob3Scale"/>
                    <TranslateTransform x:Name="Blob3Translate"/>
                </TransformGroup>
            </Ellipse.RenderTransform>
        </Ellipse>
    </Grid>
</UserControl>
```

- [ ] **Step 2: Create the code-behind (easing render loop)**

Create `src/SendspinClient/Views/AmbientBackdropView.xaml.cs`:

```csharp
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SendspinClient.Services.Visualization;
using SendspinClient.ViewModels;

namespace SendspinClient.Views;

/// <summary>
/// Pure-WPF Ambient Glow renderer. Reads target state from its <see cref="AmbientBackdropViewModel"/>
/// DataContext and eases the displayed blobs toward it each frame via CompositionTarget.Rendering.
/// </summary>
public partial class AmbientBackdropView : UserControl
{
    // Easing time constants (seconds).
    private const double EnergyTimeConstant = 0.35;
    private const double ColorTimeConstant = 0.8;
    private const double BeatHalfLife = 0.18;

    private readonly Stopwatch _clock = new();
    private long _lastTicks;

    private double _energy;
    private double _pulse;

    // Eased blob colors (R,G,B as doubles for smooth interpolation).
    private double _b1r, _b1g, _b1b, _b2r, _b2g, _b2b, _b3r, _b3g, _b3b;
    private bool _colorsInitialized;

    private AmbientBackdropViewModel? _vm;

    public AmbientBackdropView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_vm is not null)
        {
            _vm.BeatTriggered -= OnBeat;
        }

        _vm = e.NewValue as AmbientBackdropViewModel;
        if (_vm is not null)
        {
            _vm.BeatTriggered += OnBeat;
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _clock.Restart();
        _lastTicks = _clock.ElapsedTicks;
        CompositionTarget.Rendering += OnRendering;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        CompositionTarget.Rendering -= OnRendering;
        _clock.Stop();
    }

    private void OnBeat(object? sender, double strength) => _pulse += strength;

    private void OnRendering(object? sender, EventArgs e)
    {
        if (_vm is null)
        {
            return;
        }

        var now = _clock.ElapsedTicks;
        var dt = (now - _lastTicks) / (double)Stopwatch.Frequency;
        _lastTicks = now;
        if (dt <= 0.0)
        {
            return;
        }

        // Ease energy toward the VM target; decay the beat pulse.
        _energy = AmbientMath.Ease(_energy, _vm.TargetEnergy, dt, EnergyTimeConstant);
        _pulse = AmbientMath.Decay(_pulse, dt, BeatHalfLife);

        var scale = AmbientMath.BlobScale(_energy, _pulse);
        var opacity = AmbientMath.BlobOpacity(_energy);

        ApplyBlob(Blob1Scale, Blob1, scale, opacity);
        ApplyBlob(Blob2Scale, Blob2, scale * 0.95, opacity * 0.9);
        ApplyBlob(Blob3Scale, Blob3, scale * 1.05, opacity * 0.8);

        // Idle drift: slow sinusoidal motion so the scene is alive at low energy.
        var t = now / (double)Stopwatch.Frequency;
        Blob1Translate.X = Math.Sin(t * 0.13) * 40.0;
        Blob1Translate.Y = Math.Cos(t * 0.11) * 30.0;
        Blob2Translate.X = Math.Sin(t * 0.09 + 2.0) * 50.0;
        Blob2Translate.Y = Math.Cos(t * 0.15 + 1.0) * 35.0;
        Blob3Translate.X = Math.Sin(t * 0.17 + 4.0) * 30.0;
        Blob3Translate.Y = Math.Cos(t * 0.08 + 3.0) * 45.0;

        EaseColors(dt);
    }

    private static void ApplyBlob(ScaleTransform scaleT, FrameworkElement blob, double scale, double opacity)
    {
        scaleT.ScaleX = scale;
        scaleT.ScaleY = scale;
        blob.Opacity = Math.Clamp(opacity, 0.0, 1.0);
    }

    private void EaseColors(double dt)
    {
        var c1 = _vm!.BlobColor1;
        var c2 = _vm.BlobColor2;
        var c3 = _vm.BlobColor3;

        if (!_colorsInitialized)
        {
            (_b1r, _b1g, _b1b) = (c1.R, c1.G, c1.B);
            (_b2r, _b2g, _b2b) = (c2.R, c2.G, c2.B);
            (_b3r, _b3g, _b3b) = (c3.R, c3.G, c3.B);
            _colorsInitialized = true;
        }

        _b1r = AmbientMath.Ease(_b1r, c1.R, dt, ColorTimeConstant);
        _b1g = AmbientMath.Ease(_b1g, c1.G, dt, ColorTimeConstant);
        _b1b = AmbientMath.Ease(_b1b, c1.B, dt, ColorTimeConstant);
        _b2r = AmbientMath.Ease(_b2r, c2.R, dt, ColorTimeConstant);
        _b2g = AmbientMath.Ease(_b2g, c2.G, dt, ColorTimeConstant);
        _b2b = AmbientMath.Ease(_b2b, c2.B, dt, ColorTimeConstant);
        _b3r = AmbientMath.Ease(_b3r, c3.R, dt, ColorTimeConstant);
        _b3g = AmbientMath.Ease(_b3g, c3.G, dt, ColorTimeConstant);
        _b3b = AmbientMath.Ease(_b3b, c3.B, dt, ColorTimeConstant);

        Brush1Stop0.Color = FromDoubles(_b1r, _b1g, _b1b);
        Brush2Stop0.Color = FromDoubles(_b2r, _b2g, _b2b);
        Brush3Stop0.Color = FromDoubles(_b3r, _b3g, _b3b);
        BaseFill.Fill = new SolidColorBrush(_vm.BaseColor);
    }

    private static Color FromDoubles(double r, double g, double b)
        => Color.FromRgb((byte)Math.Clamp(r, 0, 255), (byte)Math.Clamp(g, 0, 255), (byte)Math.Clamp(b, 0, 255));
}
```

- [ ] **Step 3: Verify it compiles (against the SDK source, if available)**

Run: `dotnet build src/SendspinClient/SendspinClient.csproj -p:UseSdkSource=true -p:SdkSourcePath=C:/CodeProjects/SendspinSDK`
Expected: build succeeds. Otherwise defer to Task 13.

- [ ] **Step 4: Commit**

```bash
git add src/SendspinClient/Views/AmbientBackdropView.xaml src/SendspinClient/Views/AmbientBackdropView.xaml.cs
git commit -m "feat: add AmbientBackdropView pure-WPF renderer with easing loop"
```

---

## Task 8: Enable the SDK roles in `ClientCapabilities`

**Files:**
- Modify: `src/SendspinClient/App.xaml.cs` (around lines 220-249)

- [ ] **Step 1: Read the visualizer config and set the roles**

Find this block (currently ~line 220):

```csharp
        // Read buffer capacity early — needed for both ClientCapabilities and AudioPipeline
```

Immediately before `services.AddSingleton(new ClientCapabilities` (~line 238), add:

```csharp
        // Visualizer (ambient glow) capability — request only loudness + beat features.
        var visualizerRateMax = _configuration!.GetValue<int>("Visualizer:RateMax", 30);
        var visualizerBufferCapacity = _configuration!.GetValue<int>("Visualizer:BufferCapacity", 4096);
```

- [ ] **Step 2: Modify the `ClientCapabilities` registration**

Replace the existing registration (lines ~238-249):

```csharp
        services.AddSingleton(new ClientCapabilities
        {
            ClientId = clientId,
            ClientName = playerName,
            ProductName = "Sendspin Windows Client",
            Manufacturer = null, // Set by SDK consumers as needed
            SoftwareVersion = appVersion,
            AudioFormats = audioFormats,
            InitialVolume = playerVolume,
            InitialMuted = playerMuted,
            BufferCapacity = bufferCapacityBytes
        });
```

with:

```csharp
        var clientCapabilities = new ClientCapabilities
        {
            ClientId = clientId,
            ClientName = playerName,
            ProductName = "Sendspin Windows Client",
            Manufacturer = null, // Set by SDK consumers as needed
            SoftwareVersion = appVersion,
            AudioFormats = audioFormats,
            InitialVolume = playerVolume,
            InitialMuted = playerMuted,
            BufferCapacity = bufferCapacityBytes,
            VisualizerSupport = new VisualizerSupport
            {
                Types = new List<string> { VisualizerTypes.Loudness, VisualizerTypes.Beat },
                RateMax = visualizerRateMax,
                BufferCapacity = visualizerBufferCapacity,
            },
        };

        // Advertise the visualizer role (color@v1 is already in the SDK default roles).
        if (!clientCapabilities.Roles.Contains("visualizer@v1"))
        {
            clientCapabilities.Roles.Add("visualizer@v1");
        }

        services.AddSingleton(clientCapabilities);
```

- [ ] **Step 3: Add the using for the visualizer message types**

Ensure `App.xaml.cs` has (add if missing, near the other `using Sendspin.SDK...` lines):

```csharp
using Sendspin.SDK.Protocol.Messages;
```

- [ ] **Step 4: Commit**

```bash
git add src/SendspinClient/App.xaml.cs
git commit -m "feat: advertise visualizer@v1 role (loudness+beat) in ClientCapabilities"
```

---

## Task 9: Register `AmbientBackdropViewModel` in DI

**Files:**
- Modify: `src/SendspinClient/App.xaml.cs` (line ~403)

- [ ] **Step 1: Register the ViewModel**

Find (line ~402-403):

```csharp
        // ViewModels
        services.AddSingleton<MainViewModel>();
```

Replace with:

```csharp
        // ViewModels
        services.AddSingleton<AmbientBackdropViewModel>();
        services.AddSingleton<MainViewModel>();
```

- [ ] **Step 2: Add the using if needed**

Ensure `App.xaml.cs` has `using SendspinClient.ViewModels;` (add near the other usings if missing).

- [ ] **Step 3: Commit**

```bash
git add src/SendspinClient/App.xaml.cs
git commit -m "chore: register AmbientBackdropViewModel in DI"
```

---

## Task 10: Wire `AmbientBackdropViewModel` into `MainViewModel`

**Files:**
- Modify: `src/SendspinClient/ViewModels/MainViewModel.cs`

- [ ] **Step 1: Add the field, constructor param, and property**

Find the field `private readonly ClientCapabilities _clientCapabilities;` (~line 83) and add after it:

```csharp
    private readonly AmbientBackdropViewModel _ambient;
```

Add a public accessor near the other public properties (e.g., just after the constructor or with the observable properties — place it after the `ClientCapabilities` field region). Add:

```csharp
    /// <summary>Ambient Glow backdrop state, bound by the backdrop view in MainWindow.</summary>
    public AmbientBackdropViewModel Ambient => _ambient;
```

In the constructor parameter list (find `ClientCapabilities clientCapabilities,` ~line 455) add after it:

```csharp
        AmbientBackdropViewModel ambient,
```

In the constructor body where fields are assigned (near where `_clientCapabilities = clientCapabilities;` is assigned) add:

```csharp
        _ambient = ambient;
```

- [ ] **Step 2: Subscribe on the host service**

Find (lines ~493-494):

```csharp
        _hostService.ArtworkReceived += OnArtworkReceived;
        _hostService.ArtworkCleared += OnArtworkCleared;
```

Add after:

```csharp
        _hostService.ColorChanged += OnColorChanged;
        _hostService.VisualizationReceived += OnVisualizationReceived;
```

- [ ] **Step 3: Subscribe on the manual client**

Find (lines ~763-764):

```csharp
        _manualClient.ArtworkReceived += OnManualClientArtworkReceived;
        _manualClient.ArtworkCleared += OnArtworkCleared;
```

Add after:

```csharp
        _manualClient.ColorChanged += OnColorChanged;
        _manualClient.VisualizationReceived += OnVisualizationReceived;
```

- [ ] **Step 4: Unsubscribe on the manual client**

Find (lines ~895-896):

```csharp
            _manualClient.ArtworkReceived -= OnManualClientArtworkReceived;
            _manualClient.ArtworkCleared -= OnArtworkCleared;
```

Add after:

```csharp
            _manualClient.ColorChanged -= OnColorChanged;
            _manualClient.VisualizationReceived -= OnVisualizationReceived;
```

- [ ] **Step 5: Add the handlers**

Add next to the artwork handlers (after `OnArtworkCleared`, ~line 1249):

```csharp
    private void OnColorChanged(object? sender, ColorPalette palette)
    {
        App.Current.Dispatcher.Invoke(() => _ambient.ApplyColorPalette(palette));
    }

    private void OnVisualizationReceived(object? sender, VisualizerFrame frame)
    {
        App.Current.Dispatcher.Invoke(() => _ambient.ApplyVisualizerFrame(frame));
    }
```

`ColorPalette` and `VisualizerFrame` resolve via the existing `using Sendspin.SDK.Models;` (line 20).

- [ ] **Step 6: Reset ambient state on disconnect**

Find the host disconnect handler `OnServerDisconnected` (search for `private void OnServerDisconnected`). Inside it, after the existing state-clearing logic, add:

```csharp
        _ambient.Reset();
```

Also in the manual-client cleanup method (the method containing the unsubscribe from Step 4), after the unsubscribe lines add:

```csharp
        _ambient.Reset();
```

- [ ] **Step 7: Verify compile (against SDK source, if available) and commit**

Run: `dotnet build src/SendspinClient/SendspinClient.csproj -p:UseSdkSource=true -p:SdkSourcePath=C:/CodeProjects/SendspinSDK`
Expected: build succeeds (else defer to Task 13).

```bash
git add src/SendspinClient/ViewModels/MainViewModel.cs
git commit -m "feat: wire color/visualizer events into AmbientBackdropViewModel"
```

---

## Task 11: Settings toggle (property, load, persist, apply)

**Files:**
- Modify: `src/SendspinClient/ViewModels/MainViewModel.cs`

- [ ] **Step 1: Add the observable property**

Near the other settings toggles (after `_settingsShowDiscordPresence`, ~line 267) add:

```csharp
    /// <summary>
    /// Gets or sets whether the Ambient Glow reactive backdrop is enabled.
    /// </summary>
    [ObservableProperty]
    private bool _settingsEnableAmbientBackdrop = true;
```

- [ ] **Step 2: Apply on change**

Near `OnSettingsEnableMediaKeysChanged` (~line 1831) add:

```csharp
    partial void OnSettingsEnableAmbientBackdropChanged(bool value)
    {
        _ambient.SetEnabled(value);
    }
```

- [ ] **Step 3: Load at startup**

Find the settings-load block (near `SettingsShowDiscordPresence = _configuration.GetValue<bool>("Discord:Enabled", false);`, ~line 2076). After the Discord block add:

```csharp
        // Load Ambient Glow backdrop setting and apply immediately
        SettingsEnableAmbientBackdrop = _configuration.GetValue<bool>("Visualizer:Enabled", true);
        _ambient.SetEnabled(SettingsEnableAmbientBackdrop);
```

- [ ] **Step 4: Persist in bulk save**

In `SaveSettingsAsync`, find the Discord section write (~lines 2323-2326):

```csharp
            // Update Discord section
            var discordSection = root["Discord"]?.AsObject() ?? new JsonObject();
            discordSection["Enabled"] = SettingsShowDiscordPresence;
            root["Discord"] = discordSection;
```

Add after it:

```csharp
            // Update Visualizer section
            var visualizerSection = root["Visualizer"]?.AsObject() ?? new JsonObject();
            visualizerSection["Enabled"] = SettingsEnableAmbientBackdrop;
            root["Visualizer"] = visualizerSection;
```

- [ ] **Step 5: Commit**

```bash
git add src/SendspinClient/ViewModels/MainViewModel.cs
git commit -m "feat: add ambient backdrop settings toggle (load/apply/persist)"
```

---

## Task 12: Settings toggle UI + backdrop layer in MainWindow

**Files:**
- Modify: `src/SendspinClient/MainWindow.xaml`

- [ ] **Step 1: Declare the views namespace**

In the root `<Window ...>` element, ensure there is a `views` xmlns (add if missing, alongside the other xmlns declarations near the top):

```xml
xmlns:views="clr-namespace:SendspinClient.Views"
```

- [ ] **Step 2: Add the backdrop layer**

Find Layer 0 (lines ~19-38, the blurred-background `Border`). Add the ambient view immediately AFTER that `Border`'s closing `</Border>` (so it sits above the blurred art, below the gradient overlay at Layer 1):

```xml
        <!-- Layer 0b: Ambient Glow reactive backdrop (shown when a palette is active) -->
        <views:AmbientBackdropView DataContext="{Binding Ambient}">
            <views:AmbientBackdropView.Style>
                <Style TargetType="UserControl">
                    <Setter Property="Visibility" Value="Collapsed"/>
                    <Style.Triggers>
                        <MultiDataTrigger>
                            <MultiDataTrigger.Conditions>
                                <Condition Binding="{Binding DataContext.IsConnected, RelativeSource={RelativeSource AncestorType=Window}}" Value="True"/>
                                <Condition Binding="{Binding IsActive}" Value="True"/>
                            </MultiDataTrigger.Conditions>
                            <Setter Property="Visibility" Value="Visible"/>
                        </MultiDataTrigger>
                    </Style.Triggers>
                </Style>
            </views:AmbientBackdropView.Style>
        </views:AmbientBackdropView>
```

- [ ] **Step 3: Hide the blurred art when ambient is active**

In Layer 0 (the blurred `Border`, lines ~21-33), add a third trigger so the blurred art collapses when the ambient backdrop is active. Find:

```xml
                        <DataTrigger Binding="{Binding IsConnected}" Value="False">
                            <Setter Property="Visibility" Value="Collapsed"/>
                        </DataTrigger>
```

Add after it (still inside `<Style.Triggers>`):

```xml
                        <DataTrigger Binding="{Binding Ambient.IsActive}" Value="True">
                            <Setter Property="Visibility" Value="Collapsed"/>
                        </DataTrigger>
```

- [ ] **Step 4: Add the settings toggle**

Find the Discord Rich Presence settings `Grid` (lines ~584-600). Add this new `Grid` immediately after its closing `</Grid>`:

```xml
                            <!-- Ambient Glow reactive backdrop -->
                            <Grid Margin="0,0,0,16">
                                <Grid.ColumnDefinitions>
                                    <ColumnDefinition Width="*"/>
                                    <ColumnDefinition Width="Auto"/>
                                </Grid.ColumnDefinitions>
                                <StackPanel Grid.Column="0">
                                    <TextBlock Text="Reactive backdrop (Ambient Glow)" Style="{StaticResource BodyText}"/>
                                    <TextBlock Text="Animate the background with colors and energy from the music"
                                               Style="{StaticResource CaptionText}"
                                               Foreground="{StaticResource TextMutedBrush}"
                                               Margin="0,2,0,0"/>
                                </StackPanel>
                                <CheckBox Grid.Column="1"
                                          IsChecked="{Binding SettingsEnableAmbientBackdrop}"
                                          VerticalAlignment="Center"/>
                            </Grid>
```

- [ ] **Step 5: Verify compile (against SDK source, if available) and commit**

Run: `dotnet build src/SendspinClient/SendspinClient.csproj -p:UseSdkSource=true -p:SdkSourcePath=C:/CodeProjects/SendspinSDK`
Expected: build succeeds (else defer to Task 13).

```bash
git add src/SendspinClient/MainWindow.xaml
git commit -m "feat: add ambient backdrop layer and settings toggle to MainWindow"
```

---

## Task 13: Config defaults + full verification

**Files:**
- Modify: `src/SendspinClient/appsettings.json`

- [ ] **Step 1: Add the Visualizer section**

In `src/SendspinClient/appsettings.json`, add a top-level `Visualizer` section (sibling of `Audio`, `Player`, etc.):

```json
  "Visualizer": {
    "Enabled": true,
    "RateMax": 30,
    "BufferCapacity": 4096
  },
```

- [ ] **Step 2: Run the local (Services) tests**

Run: `dotnet test tests/SendspinClient.Services.Tests/SendspinClient.Services.Tests.csproj`
Expected: PASS (all `AmbientMath` tests).

- [ ] **Step 3: Verify the app compiles against the SDK dev source**

Push the branch, then dispatch the dev-source build:
Run: `gh workflow run ci-sdk-dev.yml --ref feat/ambient-glow-backdrop -f sdk_ref=dev`
Then watch: `gh run list --workflow ci-sdk-dev.yml --branch feat/ambient-glow-backdrop --limit 1`
Expected: the run completes successfully (compiles WindowsSpin against the SDK dev branch and produces signed prerelease artifacts).

- [ ] **Step 4: Manual visual check**

Install the SDK-dev prerelease artifact (or run locally against a local SDK checkout), connect to a Music Assistant server that emits `color`/`visualizer` data, and confirm:
- Backdrop shows soft color blobs that breathe with volume and pulse on beats.
- Colors match the track's palette and cross-fade on track change.
- With no palette / older server, the blurred album art shows instead.
- Toggling "Reactive backdrop (Ambient Glow)" off in Settings reverts to the blurred art immediately.

- [ ] **Step 5: Commit**

```bash
git add src/SendspinClient/appsettings.json
git commit -m "feat: add Visualizer config section defaults"
```

---

## Self-review notes (for the implementer)

- **Spec coverage:** §3 roles → Task 8; §3 components → Tasks 6/7/10; §4 reactivity → Tasks 2-5 (math) + Task 7 (loop); §5 degradation → Task 12 Step 3 (fallback) + VM `Reset`; §6 settings → Tasks 11/13; §7 threading → Task 10 Dispatcher marshaling; §8 testing → Tasks 1-5 + Task 13.
- **Type consistency:** `AmbientBackdropViewModel` members used by the View — `TargetEnergy`, `BlobColor1/2/3`, `BaseColor`, `IsActive`, `BeatTriggered`, `ApplyColorPalette`, `ApplyVisualizerFrame`, `SetEnabled`, `Reset` — match between Task 6 and Tasks 7/10/11. `AmbientMath` members — `NormalizeLoudness`, `Ease`, `Decay`, `BlobScale`, `BlobOpacity` — match between Tasks 2-5 and Task 7.
- **Known constraint:** Tasks 6/7/10/12 compile only against the dev SDK; the per-task `UseSdkSource` build command is optional (requires a local SDK checkout) and the authoritative gate is the Task 13 CI run.
```
