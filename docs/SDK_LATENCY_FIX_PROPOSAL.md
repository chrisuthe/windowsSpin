# Sendspin SDK: Latency Compensation Fix Proposal

**Date**: January 2026
**Issue**: Sync error calculation ignores output latency, causing incorrect error values on ALSA systems
**Impact**: Audio quality degradation due to constant playback rate correction

---

## Problem Summary

The Sendspin SDK calculates sync error without accounting for output latency. This causes:

1. **Incorrect sync error values** (~-200ms on typical ALSA devices)
2. **Constant playback rate correction** attempting to fix the "error"
3. **Audible artifacts** (warbling, pitch shifts) from unnecessary corrections

---

## Technical Background

### ALSA Push Model vs WASAPI Pull Model

| Aspect | WASAPI (Windows) | ALSA (Linux) |
|--------|------------------|--------------|
| Model | Pull - system requests audio when needed | Push - app must fill buffer before playback |
| Buffer fill | System manages buffer | App must pre-fill to ~75% before auto-start |
| Startup latency | Hidden by driver | Visible to application (50-500ms+) |

### What Happens on ALSA

1. Pipeline starts at T=0
2. App writes audio to ALSA buffer (PREPARED state)
3. Buffer fills over ~150ms (device-dependent)
4. ALSA auto-starts playback (RUNNING state)
5. Total latency: buffer size (~50ms) + fill time (~150ms) = ~200ms

### Current SDK Behavior

```csharp
// SDK sync calculation (observed)
var elapsed = wallClockTime - pipelineStartTime;
var readTime = timestampOfLastReadSample;
var syncError = elapsed - readTime;  // latencyComp NOT used

// Logging shows latencyComp but doesn't use it
_logger.LogDebug("Sync drift: error={Error}ms, latencyComp={Latency}ms", syncError, latencyComp);
```

**Result**: At T=200ms, SDK calculates:
- `elapsed` = 200ms (wall clock)
- `readTime` = 400ms (samples consumed to fill buffer + ongoing)
- `syncError` = 200 - 400 = **-200ms** (incorrect - this is expected behavior!)

---

## Proposed Fix

### Option A: Adjust Error Calculation (Recommended)

Subtract output latency from `readTime` since that audio hasn't been heard yet:

```csharp
// Corrected sync calculation
var outputLatencyMs = player.OutputLatencyMs;  // From IAudioPlayer
var effectiveReadTime = readTime - outputLatencyMs;
var syncError = elapsed - effectiveReadTime;
```

**Result**: At T=200ms with 200ms output latency:
- `elapsed` = 200ms
- `readTime` = 400ms
- `effectiveReadTime` = 400 - 200 = 200ms
- `syncError` = 200 - 200 = **0ms** (correct!)

### Option B: Adjust Elapsed Time

Add output latency to elapsed time (equivalent mathematically):

```csharp
var effectiveElapsed = elapsed + outputLatencyMs;
var syncError = effectiveElapsed - readTime;
```

### Option C: Apply Threshold Based on Latency

Don't correct if error is within expected latency range:

```csharp
var syncError = elapsed - readTime;
var errorThreshold = outputLatencyMs * 1.1;  // 10% margin
if (Math.Abs(syncError) < errorThreshold)
{
    // Don't apply correction - this is expected behavior
    correctionMode = CorrectionMode.None;
}
```

---

## Interface Considerations

### Current IAudioPlayer Interface

```csharp
public interface IAudioPlayer
{
    int OutputLatencyMs { get; }  // Already exists
    // ... other members
}
```

### For ALSA-style backends, OutputLatencyMs should include:

1. **Buffer latency**: Time for audio in ring buffer (from `snd_pcm_get_params`)
2. **Startup latency**: Time to fill buffer before playback starts (measured)

The MultiRoomAudio ALSA player now measures and reports both:

```csharp
// AlsaPlayer.cs
public int OutputLatencyMs => _bufferLatencyMs + _measuredStartupLatencyMs;
```

---

## Affected SDK Files (Likely Locations)

The sync error calculation is likely in one of these:

- `TimedAudioBuffer` or similar buffer statistics class
- `ClockSynchronizer` or sync correction logic
- `AudioPipeline` where sample scheduling happens
- Wherever `latencyComp` is logged but not used

---

## Validation

After the fix, logs should show:

**Before (current behavior):**
```
Sync drift: error=-196.04ms, elapsed=5462ms, readTime=5658ms, latencyComp=195ms
```

**After (with fix):**
```
Sync drift: error=-1.04ms, elapsed=5462ms, readTime=5658ms, latencyComp=195ms
```

---

## Device Latency Examples

Measured startup latencies (time from first write to RUNNING state):

| Device Type | Buffer Latency | Startup Latency | Total |
|-------------|----------------|-----------------|-------|
| USB DAC (budget) | ~50ms | ~150ms | ~200ms |
| USB DAC (pro) | ~20ms | ~80ms | ~100ms |
| HDMI audio | ~40ms | ~120ms | ~160ms |
| Onboard audio | ~50ms | ~100ms | ~150ms |
| Virtual device (dmix) | ~100ms | ~200ms | ~300ms |
| USB DAC (high buffer) | ~50ms | ~450ms | ~500ms |

---

## Implementation Priority

**Recommended approach**: Option A (adjust error calculation)

- Most accurate representation of sync state
- Minimal code change
- No change to IAudioPlayer interface needed
- Backward compatible

---

## Contact

For questions about ALSA latency measurement, see:
- `src/MultiRoomAudio/Audio/Alsa/AlsaPlayer.cs` - calibration implementation
- `docs/SYNC_ERROR_INVESTIGATION.md` - investigation notes
