# Sendspin.SDK 6.0.0 Migration Guide

## Overview

Version 6.0.0 brings the SDK into alignment with the official [Sendspin protocol specification](https://www.sendspin-audio.com/spec/). This release removes non-spec extensions, adds missing spec fields, and fixes naming mismatches.

**Why this matters**: Strict spec compliance ensures compatibility with all Sendspin servers, not just aiosendspin. Non-spec fields were removed because they created false expectations about what data `group/update` provides.

---

## Breaking Changes Summary

| Area | Change | Impact |
|------|--------|--------|
| `GroupUpdatePayload` | Removed 6 fields | Medium - Use `GroupState` instead |
| `TrackMetadata` | Restructured, Duration/Position read-only | Medium - Use `Progress` object |
| `ServerHelloPayload` | Removed 3 fields, version type changed | Low - Rarely accessed directly |
| `StreamStartPayload` | Removed 2 fields | Low - Internal use only |
| `ClientHelloPayload` | Property name fixed | None - Internal JSON change |

---

## 1. GroupUpdatePayload Changes

### What Changed

The `GroupUpdatePayload` class now only contains the three fields defined in the spec:

```csharp
// ✅ KEPT (in spec)
public string GroupId { get; set; }
public string? GroupName { get; set; }
public PlaybackState? PlaybackState { get; set; }

// ❌ REMOVED (not in spec)
// public int Volume { get; set; }
// public bool Muted { get; set; }
// public TrackMetadata? Metadata { get; set; }
// public double Position { get; set; }
// public bool Shuffle { get; set; }
// public string? Repeat { get; set; }
```

### Why This Changed

Per the Sendspin spec, `group/update` only carries playback state and identity:
- **Volume/Muted** come from `server/state` controller object
- **Metadata/Shuffle/Repeat** come from `server/state` metadata object

The SDK was providing these fields as "convenience" accessors, but they were always populated from `server/state`, not `group/update`. This was misleading.

### Migration

If you were accessing `GroupUpdatePayload` directly (rare), use `GroupState` instead:

```csharp
// ❌ OLD - Direct payload access (rare pattern)
client.MessageReceived += (s, msg) => {
    if (msg is GroupUpdateMessage update) {
        var volume = update.Payload.Volume;  // COMPILE ERROR
    }
};

// ✅ NEW - Use GroupState (recommended pattern)
client.GroupStateChanged += (s, group) => {
    var volume = group.Volume;        // From server/state controller
    var metadata = group.Metadata;    // From server/state metadata
    var state = group.PlaybackState;  // From group/update
};
```

**Most apps already use `GroupStateChanged` and require no changes.**

---

## 2. TrackMetadata Changes

### What Changed

The `TrackMetadata` class has been restructured to match the spec:

```csharp
// ✅ KEPT
public string? Title { get; set; }
public string? Artist { get; set; }
public string? Album { get; set; }
public string? ArtworkUrl { get; set; }

// ✅ NEW (added from spec)
public long? Timestamp { get; set; }      // Server timestamp (microseconds)
public string? AlbumArtist { get; set; }  // May differ from Artist on compilations
public int? Year { get; set; }            // Release year
public int? Track { get; set; }           // Track number on album
public PlaybackProgress? Progress { get; set; }  // Duration/position container
public string? Repeat { get; set; }       // "off", "one", "all"
public bool? Shuffle { get; set; }

// ⚠️ CHANGED - Now read-only computed properties
public double? Duration { get; }  // Computed from Progress?.TrackDuration / 1000.0
public double? Position { get; }  // Computed from Progress?.TrackProgress / 1000.0

// ❌ REMOVED (not in spec)
// public string? ArtworkUri { get; set; }  // Use ArtworkUrl
// public string? Uri { get; set; }
// public string? MediaType { get; set; }
```

### The Progress Object

Duration and position are now nested in a `PlaybackProgress` object (per spec):

```csharp
public sealed class PlaybackProgress
{
    [JsonPropertyName("track_progress")]
    public long TrackProgress { get; set; }  // Milliseconds

    [JsonPropertyName("track_duration")]
    public long TrackDuration { get; set; }  // Milliseconds

    [JsonPropertyName("playback_speed")]
    public int PlaybackSpeed { get; set; }   // × 1000 (1000 = normal speed)
}
```

### Migration

**Reading Duration/Position** - No changes needed! Computed properties handle this:

```csharp
// ✅ Still works - computed properties
var duration = metadata.Duration;  // Returns Progress?.TrackDuration / 1000.0
var position = metadata.Position;  // Returns Progress?.TrackProgress / 1000.0
```

**Setting Duration/Position** - If you were setting these (unlikely), set `Progress` instead:

```csharp
// ❌ OLD - Won't compile (now read-only)
metadata.Duration = 180.5;
metadata.Position = 45.2;

// ✅ NEW - Set Progress object
metadata.Progress = new PlaybackProgress
{
    TrackDuration = 180500,  // Milliseconds
    TrackProgress = 45200    // Milliseconds
};
```

**Using Uri** - If you used `Uri` for track identification, use a composite key:

```csharp
// ❌ OLD - Uri property removed
var trackId = metadata.Uri;

// ✅ NEW - Use composite key
var trackId = $"{metadata.Title}|{metadata.Artist}|{metadata.Album}";
```

**Using ArtworkUri** - Use `ArtworkUrl` instead (same value, spec-compliant name):

```csharp
// ❌ OLD
var artworkUrl = metadata.ArtworkUri;

// ✅ NEW
var artworkUrl = metadata.ArtworkUrl;
```

---

## 3. ServerHelloPayload Changes

### What Changed

```csharp
// ✅ KEPT
public string ServerId { get; set; }
public string? Name { get; set; }
public List<string> ActiveRoles { get; set; }
public string? ConnectionReason { get; set; }

// ⚠️ CHANGED - Type changed from string to int
public int Version { get; set; }  // Was: public string ProtocolVersion

// ❌ REMOVED (not in spec)
// public string? GroupId { get; set; }
// public Dictionary<string, object>? Support { get; set; }
```

### Migration

```csharp
// ❌ OLD
var protoVersion = serverHello.Payload.ProtocolVersion;  // string
var groupId = serverHello.Payload.GroupId;

// ✅ NEW
var version = serverHello.Payload.Version;  // int (always 1)
// GroupId is not available from server/hello - use group/update instead
```

**Most apps don't access ServerHelloPayload directly and require no changes.**

---

## 4. StreamStartPayload Changes

### What Changed

```csharp
// ✅ KEPT
public AudioFormat Format { get; set; }  // codec, sample_rate, channels, etc.

// ❌ REMOVED (not in spec)
// public string? StreamId { get; set; }
// public long TargetTimestamp { get; set; }
```

### Migration

These fields were SDK-internal and not exposed via events. **No app-level changes needed.**

---

## 5. ClientHelloPayload Changes

### What Changed

The JSON property name for player support was corrected:

```csharp
// ❌ OLD (wrong JSON name)
[JsonPropertyName("player_support")]
public PlayerSupport? PlayerV1Support { get; init; }

// ✅ NEW (spec-compliant JSON name)
[JsonPropertyName("player@v1_support")]
public PlayerSupport? PlayerV1Support { get; init; }
```

### Migration

**No app-level changes needed.** This is a wire-protocol fix - the C# property name is unchanged.

> **Note**: aiosendspin accepts both names for backward compatibility, but other spec-compliant servers may require the correct name.

---

## New Features

### New TrackMetadata Fields

Take advantage of the new spec-compliant fields:

```csharp
client.GroupStateChanged += (s, group) => {
    var meta = group.Metadata;
    if (meta == null) return;

    // NEW - Available in 6.0.0
    Console.WriteLine($"Album Artist: {meta.AlbumArtist}");
    Console.WriteLine($"Year: {meta.Year}");
    Console.WriteLine($"Track #: {meta.Track}");
    Console.WriteLine($"Timestamp: {meta.Timestamp}");

    // NEW - Playback speed (for variable-speed playback)
    if (meta.Progress?.PlaybackSpeed is int speed)
    {
        var multiplier = speed / 1000.0;  // 1000 = 1.0x
        Console.WriteLine($"Playback Speed: {multiplier:F2}x");
    }
};
```

### Improved Documentation

All models now have comprehensive XML documentation explaining:
- Which protocol message populates each field
- Expected value ranges and formats
- SDK extensions vs spec-compliant fields

```csharp
/// <summary>
/// Aggregate state for display purposes. Populated from multiple message types.
/// </summary>
/// <remarks>
/// <para>Field sources per Sendspin spec:</para>
/// <list type="bullet">
///   <item><c>GroupId</c>, <c>Name</c>, <c>PlaybackState</c> - from <c>group/update</c></item>
///   <item><c>Volume</c>, <c>Muted</c> - from <c>server/state</c> controller object</item>
///   <item><c>Metadata</c>, <c>Shuffle</c>, <c>Repeat</c> - from <c>server/state</c> metadata object</item>
/// </list>
/// </remarks>
public sealed class GroupState { ... }
```

---

## Retained SDK Extensions

These non-spec features are intentionally kept and documented:

| Extension | Purpose | Location |
|-----------|---------|----------|
| `client/sync_offset` | GroupSync acoustic calibration | Protocol message |
| `client/sync_offset_ack` | ACK for sync offset | Protocol message |
| `PlayerStatePayload.BufferLevel` | Diagnostic buffer reporting | ClientStateMessage |
| `PlayerStatePayload.Error` | Error message reporting | ClientStateMessage |

These are marked in XML docs as "SDK extension (not part of Sendspin spec)".

---

## Migration Checklist

### Required Changes

- [ ] If setting `TrackMetadata.Duration`/`.Position` directly → Set `Progress` object instead
- [ ] If using `TrackMetadata.Uri` → Use composite key (title|artist|album)
- [ ] If using `TrackMetadata.ArtworkUri` → Use `ArtworkUrl`
- [ ] If accessing `ServerHelloPayload.ProtocolVersion` → Use `Version` (int)
- [ ] If accessing `GroupUpdatePayload.Volume`/`.Muted`/etc. → Use `GroupState`

### No Changes Needed If

- [x] You use `GroupStateChanged` event (most apps)
- [x] You only read `TrackMetadata.Duration`/`.Position` (computed properties work)
- [x] You use `TrackMetadata.ArtworkUrl` (unchanged)
- [x] You don't access protocol payloads directly

---

## Compatibility Notes

### aiosendspin Compatibility

The aiosendspin server accepts both old and new JSON property names via aliases. Your upgraded SDK will work with existing aiosendspin servers.

### Other Sendspin Servers

Spec-compliant servers that don't have legacy aliases will now work correctly with the SDK. The `player@v1_support` fix in particular ensures proper handshake with strict servers.

---

## Questions?

If you encounter issues migrating, check:
1. The SDK XML documentation on each class
2. The [Sendspin spec](https://www.sendspin-audio.com/spec/)
3. The [SDK release notes](https://www.nuget.org/packages/Sendspin.SDK) on NuGet
