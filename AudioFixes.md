# Audio Synchronization Fixes

## Overview

This document tracks audio synchronization issues identified in the SendSpin Windows Client. The time sync was reported as being 5 seconds ahead by testers.

---

## Fix 1: Gate Playback on Clock Sync Convergence - COMPLETED

**Status:** Implemented

**Problem:** Playback was starting before the Kalman filter clock sync converged, potentially with wildly inaccurate offset estimates.

**Solution:** Added `_clockSync.IsConverged` check before starting playback.

**File:** `src/SendSpinClient.Core/Audio/AudioPipeline.cs:198-205`

---

## Fix 2: Add Drift Compensation to ServerToClientTime - COMPLETED

**Status:** Implemented

**Problem:** `ServerToClientTime()` doesn't account for clock drift, while `ClientToServerTime()` does. This causes growing sync error over long playback sessions.

**File:** `src/SendSpinClient.Core/Synchronization/KalmanClockSynchronizer.cs`

**Solution:** Added drift compensation to `ServerToClientTime()` mirroring the behavior of `ClientToServerTime()`:
- Uses approximate client time to calculate elapsed seconds since last sync
- Applies drift extrapolation only when drift estimate is reliable
- Includes static delay for user tuning

**Risk:** Medium - this is called on every audio sample write (hot path). Performance verified during build.

**Testing:** Monitor drift values in logs and verify sync stability over 30+ minute sessions.

---

## Fix 2b: Fix CalculateSyncError to Account for Drift - COMPLETED

**Status:** Implemented

**Problem:** `CalculateSyncError()` in `TimedAudioBuffer.cs` assumed local time passes at the same rate as server time:
```csharp
// OLD (broken): Assumes 1 local μs = 1 server μs
var expectedServerTime = _playbackStartServerTime + elapsedLocalTimeMicroseconds;
```

This caused the sync correction algorithm to detect phantom errors when clocks drifted, leading to incorrect drop/insert corrections that made sync progressively WORSE on each play/pause/play cycle.

**File:** `src/SendSpinClient.Core/Audio/TimedAudioBuffer.cs`

**Solution:** Use `ClientToServerTime()` to properly convert current local time to server time:
```csharp
// NEW (correct): Properly converts local time to server time
var currentServerTime = _clockSync.ClientToServerTime(currentLocalTime);
var elapsedServerTimeMicroseconds = currentServerTime - _playbackStartServerTime;
```

**Testing:** Verify sync error stays stable (±2ms) during extended playback and across pause/resume cycles.

---

## Fix 3: Audio Output Latency Compensation - PENDING

**Status:** Not yet implemented

**Problem:** The code releases audio samples when their playback time arrives, but doesn't account for the time it takes for audio to physically reach the speakers after being sent to the audio subsystem.

**Engineer feedback:** "You need to fetch the time from the OS that it takes to get audio physically playing from the speakers and subtract that from the delay"

### Architecture Context

The audio pipeline has these latency sources:

| Component | Location | Typical Latency |
|-----------|----------|-----------------|
| WASAPI buffer | `WasapiAudioPlayer.cs:102` | 50ms (configured) |
| Audio driver | OS/hardware | 10-50ms (varies) |
| DAC | Hardware | 1-10ms |
| Bluetooth (if used) | Hardware | 40-200ms |

**Total uncompensated latency: ~65-300ms depending on hardware**

### Key Files

1. **TimedAudioBuffer.cs** - Where playback timing decision is made
   - Path: `src/SendSpinClient.Core/Audio/TimedAudioBuffer.cs`
   - Line 171: `if (currentLocalTime < firstSegment.LocalPlaybackTime - 5000)` (5ms early tolerance)
   - This is where latency compensation should be applied

2. **WasapiAudioPlayer.cs** - WASAPI output configuration
   - Path: `src/SendSpinClient.Services/Audio/WasapiAudioPlayer.cs`
   - Line 100-102: Creates WasapiOut with 50ms latency
   - Can query actual latency via NAudio

3. **AudioPipeline.cs** - Orchestrates the pipeline
   - Path: `src/SendSpinClient.Core/Audio/AudioPipeline.cs`
   - Creates TimedAudioBuffer and WasapiAudioPlayer

### Implementation Approach

#### Option A: Configurable Static Offset (Simpler)

Add a configurable audio output latency parameter:

```csharp
// In ITimedAudioBuffer interface
public interface ITimedAudioBuffer : IDisposable
{
    // Add this property
    long AudioOutputLatencyMicroseconds { get; set; }
    // ... existing members
}

// In TimedAudioBuffer.cs
public long AudioOutputLatencyMicroseconds { get; set; } = 100_000; // Default 100ms

// Modify the timing check (line ~171):
// Release audio EARLIER by the output latency amount
long adjustedPlaybackTime = firstSegment.LocalPlaybackTime - AudioOutputLatencyMicroseconds;
if (currentLocalTime < adjustedPlaybackTime - 5000)
{
    buffer.Fill(0f);
    return 0;
}
```

#### Option B: Query WASAPI Latency (More Accurate)

NAudio's WasapiOut provides latency information:

```csharp
// In WasapiAudioPlayer.cs, add property:
public int OutputLatencyMs => _wasapiOut?.OutputWaveFormat != null
    ? (int)(_wasapiOut.Latency) // NAudio reports this in ms
    : 50; // Default fallback

// Pass this to the audio pipeline/buffer during initialization
```

Then use this value to adjust the timing in TimedAudioBuffer.

#### Option C: User-Tunable (Most Flexible)

Add a user setting for "Audio Delay Compensation" (in milliseconds) that can be adjusted in the UI. This handles:
- Hardware variations (USB DAC vs onboard vs Bluetooth)
- User preferences
- Unknown system configurations

```csharp
// In app settings/configuration
public class AudioSettings
{
    /// <summary>
    /// Compensation for audio output latency in milliseconds.
    /// Positive values make audio play earlier, negative values delay it.
    /// Default: 100ms (typical for shared-mode WASAPI + driver overhead)
    /// </summary>
    public int OutputLatencyCompensationMs { get; set; } = 100;
}
```

### Testing Strategy

1. **Baseline test:** Connect two clients on same network, play audio, measure offset with metronome or click track
2. **Apply fix:** Set compensation to 100ms, re-measure
3. **Tune:** Adjust until clients are in sync (typically 80-150ms)
4. **Edge cases:** Test with Bluetooth speakers (may need 200-300ms compensation)

### Risk Assessment

| Risk | Mitigation |
|------|------------|
| Over-compensation (audio too early) | Use conservative default (100ms), allow user tuning |
| Under-compensation (still late) | Log actual WASAPI latency for debugging |
| Hardware variation | Make configurable, document typical values |
| Performance (hot path) | Single subtraction, negligible impact |

### Acceptance Criteria

- [ ] Audio output latency is compensated in TimedAudioBuffer
- [ ] Default value works for typical Windows audio setups
- [ ] User can adjust compensation via settings (optional)
- [ ] Logs show configured latency value at startup
- [ ] Multi-room sync accuracy is within 20ms for same hardware

---

## Diagnostic Logging

The following log messages help debug sync issues:

```
Clock sync: offset=XXms (±YYms), converged=True
Starting playback: buffer=80ms, sync offset=XXms (±YYms)
```

Check that:
1. Sync converges BEFORE playback starts
2. Offset uncertainty is <1ms at playback start
3. Offset value is reasonable (typically -500ms to +500ms for local network)

---

## Related Code References

- Clock sync: `src/SendSpinClient.Core/Synchronization/KalmanClockSynchronizer.cs`
- Timed buffer: `src/SendSpinClient.Core/Audio/TimedAudioBuffer.cs`
- Audio player: `src/SendSpinClient.Services/Audio/WasapiAudioPlayer.cs`
- Pipeline: `src/SendSpinClient.Core/Audio/AudioPipeline.cs`
- Client orchestration: `src/SendSpinClient.Core/Client/SendSpinClient.cs`
