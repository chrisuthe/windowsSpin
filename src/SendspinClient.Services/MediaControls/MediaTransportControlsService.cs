using Microsoft.Extensions.Logging;
using Sendspin.SDK.Models;
using Windows.Media;
using Windows.Storage.Streams;

namespace SendspinClient.Services.MediaControls;

public sealed class MediaTransportControlsService : IMediaTransportControlsService
{
    private readonly ILogger<MediaTransportControlsService> _logger;
    private SystemMediaTransportControls? _smtc;
    private SystemMediaTransportControlsDisplayUpdater? _displayUpdater;
    private bool _disposed;

    public MediaTransportControlsService(ILogger<MediaTransportControlsService> logger)
    {
        _logger = logger;
    }

    public event EventHandler? PlayPauseRequested;

    public event EventHandler? NextRequested;

    public event EventHandler? PreviousRequested;

    public void Initialize(IntPtr windowHandle)
    {
        if (_smtc != null)
        {
            return;
        }

        try
        {
            _smtc = SystemMediaTransportControlsInterop.GetForWindow(windowHandle);
            _smtc.IsEnabled = true;
            _smtc.IsPlayEnabled = true;
            _smtc.IsPauseEnabled = true;
            _smtc.IsNextEnabled = true;
            _smtc.IsPreviousEnabled = true;
            _smtc.PlaybackStatus = MediaPlaybackStatus.Stopped;
            _smtc.ButtonPressed += OnButtonPressed;

            _displayUpdater = _smtc.DisplayUpdater;
            _displayUpdater.Type = MediaPlaybackType.Music;
            _displayUpdater.Update();

            _logger.LogInformation("System Media Transport Controls initialized");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize System Media Transport Controls");
            _smtc = null;
            _displayUpdater = null;
        }
    }

    public void UpdateState(PlaybackState state)
    {
        if (_smtc == null)
        {
            return;
        }

        _smtc.PlaybackStatus = state switch
        {
            PlaybackState.Playing => MediaPlaybackStatus.Playing,
            PlaybackState.Paused => MediaPlaybackStatus.Paused,
            _ => MediaPlaybackStatus.Stopped,
        };
    }

    public void UpdateMetadata(TrackMetadata? track)
    {
        if (_displayUpdater == null)
        {
            return;
        }

        if (track == null)
        {
            _displayUpdater.ClearAll();
            _displayUpdater.Type = MediaPlaybackType.Music;
        }
        else
        {
            var music = _displayUpdater.MusicProperties;
            music.Title = track.Title ?? string.Empty;
            music.Artist = track.Artist ?? string.Empty;
            music.AlbumTitle = track.Album ?? string.Empty;
        }

        _displayUpdater.Update();
    }

    public void UpdateThumbnail(byte[]? imageBytes)
    {
        if (_displayUpdater == null)
        {
            return;
        }

        try
        {
            _displayUpdater.Thumbnail = imageBytes is { Length: > 0 }
                ? CreateStreamReference(imageBytes)
                : null;
            _displayUpdater.Update();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to update SMTC thumbnail");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_smtc != null)
        {
            _smtc.ButtonPressed -= OnButtonPressed;
            _smtc.IsEnabled = false;
            _smtc = null;
            _displayUpdater = null;
        }
    }

    private void OnButtonPressed(SystemMediaTransportControls sender, SystemMediaTransportControlsButtonPressedEventArgs args)
    {
        switch (args.Button)
        {
            case SystemMediaTransportControlsButton.Play:
            case SystemMediaTransportControlsButton.Pause:
                PlayPauseRequested?.Invoke(this, EventArgs.Empty);
                break;
            case SystemMediaTransportControlsButton.Next:
                NextRequested?.Invoke(this, EventArgs.Empty);
                break;
            case SystemMediaTransportControlsButton.Previous:
                PreviousRequested?.Invoke(this, EventArgs.Empty);
                break;
        }
    }

    private static RandomAccessStreamReference CreateStreamReference(byte[] bytes)
    {
        var stream = new InMemoryRandomAccessStream();
        var writer = new DataWriter(stream);
        try
        {
            writer.WriteBytes(bytes);
            writer.StoreAsync().AsTask().GetAwaiter().GetResult();
            writer.DetachStream();
        }
        finally
        {
            writer.Dispose();
        }

        stream.Seek(0);
        return RandomAccessStreamReference.CreateFromStream(stream);
    }
}
