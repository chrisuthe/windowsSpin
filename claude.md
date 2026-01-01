# Sendspin Windows Client - Project Notes

## Project Structure

The solution is organized into three projects:

```
src/
├── Sendspin.SDK/              # Cross-platform protocol SDK (NuGet package)
│   ├── Audio/                 # Audio pipeline, buffer, decoders
│   ├── Client/                # Main client orchestration
│   ├── Connection/            # WebSocket connection management
│   ├── Discovery/             # mDNS server discovery
│   ├── Protocol/              # Message serialization and types
│   └── Synchronization/       # Clock sync (Kalman filter)
├── SendspinClient.Services/   # Windows-specific services
│   ├── Audio/                 # WASAPI player (NAudio)
│   ├── Discord/               # Discord Rich Presence
│   └── Notifications/         # Windows toast notifications
└── SendspinClient/            # WPF desktop application
    ├── ViewModels/            # MVVM view models
    └── Views/                 # XAML views
```

> **Historical Note**: Prior to v2.0.0, the core protocol implementation was in `SendspinClient.Core`. This was renamed to `Sendspin.SDK` to support NuGet packaging and cross-platform use.

## Project Purpose

This project is a Windows implementation of the Sendspin protocol, providing a native Windows-based player that can:
- Run in the background (system tray)
- Show toast notifications
- Sync properly with other players for multi-room playback

## Reference Implementation

**Gold Standard**: The CLI player located at `C:\Users\chris\Downloads\sendspin-cli-main`

When making changes to sync, audio buffering, or timing logic, ALWAYS refer back to the CLI implementation to understand how it handles these cases. Don't guess - read the Python code.

## Design Principles

1. **Simple** - Easy to use for end users
2. **Easy to sync** - Multi-room playback is the core feature; sync accuracy is critical
3. **Maintainable** - Easy for other engineers to contribute to

## Key Technical Areas

### Clock Synchronization
- Uses NTP-style 4-timestamp exchange with Kalman filter
- Server uses monotonic time (near 0), client uses Unix epoch
- Clock offset can be billions of microseconds - this is normal and correct

### Audio Pipeline
- WASAPI audio output with ~50ms buffer latency
- Server sends audio ~5 seconds ahead with future timestamps
- Sync correction via frame drop/insert to handle drift

### Critical Files
- `src/Sendspin.SDK/Audio/TimedAudioBuffer.cs` - Audio buffering with timestamp-based playback
- `src/Sendspin.SDK/Synchronization/KalmanClockSynchronizer.cs` - Clock offset and drift tracking
- `src/Sendspin.SDK/Audio/AudioPipeline.cs` - Orchestrates decoder, buffer, and player

## Known Issues / Areas of Complexity

### Track Change / Stream Restart
When tracks change, the sync error calculation can get into a stuck "dropping" state. Need to compare with CLI implementation to understand proper reset behavior.

## CLI Reference Paths

Reference: `C:\Users\chris\Downloads\sendspin-cli-main\sendspin-cli-main\sendspin\audio.py`

### CLI Sync Error Calculation (line 1032)
```python
sync_error_us = self._last_known_playback_position_us - self._server_ts_cursor_us
```

**Both values are in SERVER timestamp space!**
- `_last_known_playback_position_us` = server timestamp of audio currently playing (DAC → loop → server time)
- `_server_ts_cursor_us` = server timestamp of audio being read from buffer

### CLI Clear/Reset Behavior (critical for track changes!)
When `clear()` is called, the CLI resets **EVERYTHING**:
```python
self._playback_state = PlaybackState.INITIALIZING
self._scheduled_start_loop_time_us = None
self._server_ts_cursor_us = 0
self._sync_error_filter.reset()
self._insert_every_n_frames = 0
self._drop_every_n_frames = 0
# ... all timing state reset
```

The next audio chunk that arrives re-initializes all anchors from scratch.

### CLI Constants
```python
_CORRECTION_DEADBAND_US = 2_000      # 2ms
_REANCHOR_THRESHOLD_US = 500_000     # 500ms
_MAX_SPEED_CORRECTION = 0.04         # 4%
_CORRECTION_TARGET_SECONDS = 2.0     # Fix error in 2 seconds
```

### Sync Error Implementation (2024-12-26)

#### Key Insight: Track samples READ, not OUTPUT
For sync correction to work, we must track samples READ from the buffer, not samples OUTPUT to speakers:
- When DROPPING: read 2 frames, output 1 → `_samplesReadSinceStart += 2`
- When INSERTING: read 0 frames, output 1 → `_samplesReadSinceStart += 0`

This matches CLI's approach where the server cursor (`_server_ts_cursor_us`) advances when reading.

```csharp
// Correct: Track samples READ
_samplesReadSinceStart += actualRead;

// Sync error = wall_clock_elapsed - samples_read_time
// Positive = behind (need DROP), Negative = ahead (need INSERT)
_currentSyncErrorMicroseconds = elapsedTimeMicroseconds - samplesReadTimeMicroseconds;
```

#### Anchor Point: Use ACTUAL start time, not intended playback time
**IMPORTANT**: Use `currentLocalTime` when playback starts, NOT `firstSegment.LocalPlaybackTime`!

The server sends audio ~5 seconds ahead. The first chunk's `LocalPlaybackTime` is in the FUTURE.
If we use that as anchor:
- `elapsed = now - (now + 5s) = -5 seconds` ← massive negative error!
- Immediately triggers re-anchor threshold (500ms)
- Creates infinite loop of re-anchoring

**Correct approach**:
```csharp
// Use ACTUAL start time - the LocalPlaybackTime being in the future is normal!
_playbackStartLocalTime = currentLocalTime;
```

The buffered audio having future timestamps is expected behavior, not a sync error.
Sync error measures drift DURING playback, not initial buffer depth.

#### How dropping works
```
Initial: error = 0ms (just started)
After 1 second: wall clock = 1000ms, samplesReadTime = 1000ms → error = 0
If reading slow: wall clock = 1000ms, samplesReadTime = 990ms → error = +10ms → DROP
After dropping: samplesReadTime advances faster → error shrinks ✓
```

#### Output Latency Compensation (2024-12-26)

**Problem**: Constant ~34ms sync error even when dropping correctly.

**Cause**: The CLI uses DAC timing callbacks (`outputBufferDacTime`) to know exactly when audio reaches the speaker. WASAPI doesn't expose this. When comparing wall clock vs samples read:
- Wall clock: "50ms has passed"
- Samples read: 50ms worth
- Samples at speaker: ~0ms (still in WASAPI's 50ms output buffer!)

Without compensation, we see a constant offset equal to the output buffer latency.

**Solution**: Subtract output latency from elapsed time before comparing:
```csharp
// Account for WASAPI output buffer delay
var adjustedElapsedMicroseconds = elapsedTimeMicroseconds - OutputLatencyMicroseconds;
_currentSyncErrorMicroseconds = adjustedElapsedMicroseconds - samplesReadTimeMicroseconds;
```

This asks "how much audio has actually played through the speaker?" instead of "how much wall clock time has passed?"

#### CLI's DAC Timing (Reference)

The CLI uses PyAudio's `outputBufferDacTime` in the audio callback:
```python
# audio.py line 472
dac_time_us = int(time.outputBufferDacTime * 1_000_000)

# Stores calibration pairs to map between DAC time and loop time
self._dac_loop_calibrations.append((dac_time_us, loop_time_us))

# Uses calibration to estimate playback position in server time
estimated_position = self._compute_server_time(loop_at_dac_us)
self._last_known_playback_position_us = estimated_position
```

The sync error is then calculated entirely in server timestamp space:
```python
sync_error_us = _last_known_playback_position_us - _server_ts_cursor_us
```
