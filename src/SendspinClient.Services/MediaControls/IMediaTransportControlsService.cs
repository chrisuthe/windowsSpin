using Sendspin.SDK.Models;

namespace SendspinClient.Services.MediaControls;

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

    void UpdateMetadata(TrackMetadata? track);

    void UpdateThumbnail(byte[]? imageBytes);
}
