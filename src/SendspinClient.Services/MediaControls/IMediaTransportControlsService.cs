using Sendspin.SDK.Models;

namespace SendspinClient.Services.MediaControls;

public interface IMediaTransportControlsService : IDisposable
{
    event EventHandler? PlayPauseRequested;

    event EventHandler? NextRequested;

    event EventHandler? PreviousRequested;

    void Initialize(IntPtr windowHandle);

    void UpdateState(PlaybackState state);

    void UpdateMetadata(TrackMetadata? track);

    void UpdateThumbnail(byte[]? imageBytes);
}
