using Sendspin.SDK.Models;

namespace Sendspin.Windows.Services.MediaControls;

public interface IMediaTransportControlsService : IDisposable
{
    /// <summary>
    /// Gets or sets whether System Media Transport Controls integration is active.
    /// When false, Windows hides our media controls and incoming button events are ignored.
    /// </summary>
    bool IsEnabled { get; set; }

    event EventHandler? PlayPauseRequested;

    event EventHandler? NextRequested;

    event EventHandler? PreviousRequested;

    void Initialize(IntPtr windowHandle);

    void UpdateState(PlaybackState state);

    /// <summary>
    /// Enables only the media keys whose command the active group lists in its
    /// <c>supported_commands</c>; null (not connected, or no list yet) disables them all.
    /// </summary>
    void UpdateSupportedCommands(IReadOnlyCollection<string>? supportedCommands);

    void UpdateMetadata(TrackMetadata? track);

    void UpdateThumbnail(byte[]? imageBytes);
}
