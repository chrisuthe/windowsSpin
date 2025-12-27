# Album Art Proxy for Discord Rich Presence

## Overview

Implement a .NET-based WebSocket proxy system (similar to [loon](https://github.com/ungive/loon)) that enables Discord Rich Presence to display dynamic album artwork. The proxy solves the NAT traversal problem - making locally-available album art accessible via a public URL that Discord can fetch.

## Architecture

```
+------------------+     WebSocket      +----------------------+
|  WindowsSpin     | -----------------> |  Cloudflare Worker   |
|  (Windows App)   |   (outbound)       |  + Durable Object    |
+------------------+                    +----------------------+
        |                                         |
        | Has artwork                             | Provides URL
        | as byte[]                               | https://art.sendspin.io/{id}
        v                                         v
+------------------+                    +----------------------+
| AlbumArtProxy    |                    |  Discord Servers     |
| ClientService    | <---- HTTP --------|  (fetches artwork)   |
+------------------+   (via tunnel)     +----------------------+
```

**How it works:**
1. Client connects outbound to proxy server via WebSocket
2. Registers artwork → receives temporary public URL immediately
3. Discord requests that URL → proxy forwards request via WebSocket
4. Client responds with image data → proxy returns to Discord
5. Images cached briefly (30s) then discarded

## Implementation Phases

### Phase 1: Server Infrastructure (Serverless Proxy)

**Location:** Separate deployment (e.g., `art-proxy.sendspin.io`)

**Provider Options** (any works):
- **Cloudflare Workers** (recommended) - Free tier, global edge, native WebSocket support
- **AWS Lambda + API Gateway** - WebSocket API available, more config needed
- **Azure Functions** - Similar to AWS, good .NET integration

**Files to create:**
- `worker.js` - Main Worker entry point
- `durable-object.js` - WebSocket session management
- `wrangler.toml` - Cloudflare config

**Protocol:**
```json
// Client -> Server: Register artwork
{ "type": "register", "imageId": "uuid", "hash": "sha256" }

// Server -> Client: Request artwork
{ "type": "fetch", "imageId": "uuid" }

// Client -> Server: Artwork response
{ "type": "image", "imageId": "uuid", "data": "base64", "contentType": "image/png" }
```

### Phase 2: Client Service Implementation

**New Files:**

| File | Purpose |
|------|---------|
| `src/SendSpinClient.Services/AlbumArtProxy/IAlbumArtProxyService.cs` | Interface |
| `src/SendSpinClient.Services/AlbumArtProxy/AlbumArtProxyService.cs` | WebSocket client, artwork registration |
| `src/SendSpinClient.Services/AlbumArtProxy/ImageProcessor.cs` | Resize/crop to 256x256 |

**Interface:**
```csharp
public interface IAlbumArtProxyService : IAsyncDisposable
{
    bool IsConnected { get; }
    bool IsEnabled { get; set; }
    Task ConnectAsync(CancellationToken ct = default);
    Task DisconnectAsync();
    Task<string?> RegisterArtworkAsync(byte[] imageData, CancellationToken ct = default);
    void ClearArtwork();
}
```

**Dependencies to add:**
```xml
<PackageReference Include="SkiaSharp" Version="2.88.7" />
```

### Phase 3: Discord Service Integration

**Modify:** `src/SendSpinClient.Services/Discord/IDiscordRichPresenceService.cs`
```csharp
// Add overload
void UpdatePresence(TrackMetadata? track, PlaybackState state, double positionSeconds, byte[]? albumArtwork = null);
```

**Modify:** `src/SendSpinClient.Services/Discord/DiscordRichPresenceService.cs`
- Inject `IAlbumArtProxyService`
- Register artwork and use returned URL
- Fallback to static `sendspin_logo` if proxy unavailable

### Phase 4: ViewModel Integration

**Modify:** `src/SendSpinClient/ViewModels/MainViewModel.cs`
- Pass `AlbumArtwork` byte[] to Discord service
- Update in `OnCurrentTrackChanged` and `OnPlaybackStateChanged`

**Modify:** `src/SendSpinClient/App.xaml.cs`
- Register `IAlbumArtProxyService` in DI

### Phase 5: Configuration

**Modify:** `src/SendSpinClient/appsettings.json`
```json
{
  "Discord": {
    "Enabled": true,
    "ApplicationId": "...",
    "ShowAlbumArt": true
  },
  "AlbumArtProxy": {
    "Enabled": true,
    "ServerUrl": "wss://art-proxy.sendspin.io/ws",
    "TimeoutSeconds": 5
  }
}
```

## Files Summary

| Action | File Path |
|--------|-----------|
| Create | `src/SendSpinClient.Services/AlbumArtProxy/IAlbumArtProxyService.cs` |
| Create | `src/SendSpinClient.Services/AlbumArtProxy/AlbumArtProxyService.cs` |
| Create | `src/SendSpinClient.Services/AlbumArtProxy/ImageProcessor.cs` |
| Modify | `src/SendSpinClient.Services/Discord/IDiscordRichPresenceService.cs` |
| Modify | `src/SendSpinClient.Services/Discord/DiscordRichPresenceService.cs` |
| Modify | `src/SendSpinClient/ViewModels/MainViewModel.cs` |
| Modify | `src/SendSpinClient/App.xaml.cs` |
| Modify | `src/SendSpinClient/appsettings.json` |
| Modify | `src/SendSpinClient.Services/SendSpinClient.Services.csproj` |

## Fallback Strategy

| Condition | Behavior |
|-----------|----------|
| Proxy connected + artwork registered | Use proxy URL |
| Proxy unavailable/timeout | Use static `sendspin_logo` |
| No artwork available | Use static `sendspin_logo` |
| Large image processing fails | Use static `sendspin_logo` |

## Security

- **Random UUIDs** for image IDs (unguessable)
- **30-second TTL** on proxy (no persistent storage)
- **256KB max** image size
- **Rate limiting** via Cloudflare
- **TLS everywhere** (WSS + HTTPS)

## Testing Checklist

- [ ] Proxy connects on app start
- [ ] Artwork appears in Discord when playing
- [ ] Artwork updates on track change
- [ ] Graceful fallback when proxy unavailable
- [ ] Reconnection after disconnect
- [ ] Various image sizes work (small, large, non-square)
- [ ] Memory usage stable (no leaks)

## Estimated Effort

| Phase | Time |
|-------|------|
| Server Infrastructure | 4-6 hours |
| Client Service | 4-6 hours |
| Discord Integration | 2-3 hours |
| ViewModel Integration | 1-2 hours |
| Testing & Polish | 2-4 hours |
| **Total** | **13-21 hours** |

## Reference

- [loon library](https://github.com/ungive/loon) - C++/Go tunnel system this is modeled after
- [discord-music-presence](https://github.com/ungive/discord-music-presence) - Uses loon for album art
