using Microsoft.Extensions.Logging;
using Sendspin.SDK.Audio.Source;
using Sendspin.SDK.Models;

namespace Sendspin.Windows.Services.Audio.Capture;

/// <summary>
/// The single capture device handed to the SDK for the <c>source@v1</c> role.
/// </summary>
/// <remarks>
/// <para>
/// Two independent states meet here. Music Assistant owns whether a stream is open: it calls
/// <see cref="StartAsync"/> and <see cref="StopAsync"/> through the SDK. The user owns what fills
/// it: <see cref="SetSendEnabledAsync"/>. While Send is off a started device runs generated
/// silence and opens nothing; with Send on it runs the selected input. The stream stays open
/// across every switch and <see cref="Format"/> never changes, so the SDK sees one continuous
/// stream.
/// </para>
/// <para>
/// Emitted capture times are monotonic and never overlap. Across a switch, a frame that starts
/// before the previous one ended is trimmed by whole frames. Within one source an overlap is
/// only timestamp rounding and jitter, so the frame is re-stamped to start where the previous
/// one ended and keeps every sample. Gaps are allowed; MA's source bridge absorbs them.
/// </para>
/// <para>
/// <see cref="DisposeAsync"/> only stops. The SDK disposes this device whenever it disposes a
/// client that owns its pipelines — every dial cleanup and host stop — and the next connection
/// must still be able to start it.
/// </para>
/// </remarks>
public sealed class WindowsAudioCaptureDevice : IAudioCaptureDevice
{
    private const long NoFrames = long.MinValue;

    private readonly Func<ICaptureSource> _createSilence;
    private readonly Func<string, ICaptureSource> _createInput;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _switchGate = new(1, 1);
    private readonly object _frameLock = new();

    // Guarded by _switchGate.
    private string? _inputId;
    private bool _sendEnabled;
    private bool _started;
    private string? _activeInputId;

    // Written under _switchGate, read on capture threads.
    private ICaptureSource? _active;

    // Guarded by _frameLock.
    private long _lastFrameEnd = NoFrames;
    private object? _lastSender;

    /// <summary>Initializes a new instance of the <see cref="WindowsAudioCaptureDevice"/> class.</summary>
    /// <param name="createSilence">Creates the generated-silence source.</param>
    /// <param name="createInput">Creates the source for an input ID.</param>
    /// <param name="logger">Logger.</param>
    public WindowsAudioCaptureDevice(
        Func<ICaptureSource> createSilence,
        Func<string, ICaptureSource> createInput,
        ILogger<WindowsAudioCaptureDevice> logger)
    {
        _createSilence = createSilence;
        _createInput = createInput;
        _logger = logger;
    }

    /// <inheritdoc/>
    public AudioFormat Format { get; } = CaptureFormat.Create();

    /// <inheritdoc/>
    public event EventHandler<CapturedAudio>? AudioCaptured;

    /// <summary>
    /// Raised when the selected input fails to start or stops working. Send is already off inside
    /// the device and silence is flowing; the handler only has to tell the user.
    /// </summary>
    public event EventHandler<CaptureFaultedEventArgs>? Faulted;

    /// <summary>Selects what Send captures. Applies immediately if sending.</summary>
    /// <param name="inputId">An input ID, or null for none.</param>
    /// <returns>A task that completes once the change is applied.</returns>
    public Task SetInputAsync(string? inputId) => SwitchAsync(() => _inputId = inputId);

    /// <summary>Opens or closes the Send gate.</summary>
    /// <param name="enabled">True to capture the selected input; false for silence.</param>
    /// <returns>A task that completes once the change is applied.</returns>
    public Task SetSendEnabledAsync(bool enabled) => SwitchAsync(() => _sendEnabled = enabled);

    /// <inheritdoc/>
    public Task StartAsync(CancellationToken cancellationToken = default) => SwitchAsync(() => _started = true);

    /// <inheritdoc/>
    public Task StopAsync(CancellationToken cancellationToken = default) => SwitchAsync(() => _started = false);

    /// <inheritdoc/>
    public ValueTask DisposeAsync() => new(StopAsync());

    private async Task SwitchAsync(Action change)
    {
        CaptureFaultedEventArgs? fault;
        await _switchGate.WaitAsync().ConfigureAwait(false);
        try
        {
            change();
            fault = await ApplyAsync().ConfigureAwait(false);
        }
        finally
        {
            _switchGate.Release();
        }

        // Outside the gate, so a handler may call back in.
        if (fault is not null)
        {
            Faulted?.Invoke(this, fault);
        }
    }

    private async Task<CaptureFaultedEventArgs?> ApplyAsync()
    {
        bool wantsInput = _started && _sendEnabled && _inputId is not null;
        string? wantedInput = wantsInput ? _inputId : null;
        bool running = _active is not null;

        if (!_started && !running)
        {
            return null;
        }

        if (_started && running && _activeInputId == wantedInput)
        {
            return null;
        }

        await StopActiveAsync().ConfigureAwait(false);
        if (!_started)
        {
            lock (_frameLock)
            {
                _lastFrameEnd = NoFrames;
                _lastSender = null;
            }

            return null;
        }

        if (!wantsInput)
        {
            await StartSourceAsync(_createSilence(), null).ConfigureAwait(false);
            return null;
        }

        try
        {
            await StartSourceAsync(_createInput(wantedInput!), wantedInput).ConfigureAwait(false);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not start capture from {InputId}; sending silence", wantedInput);
            _sendEnabled = false;
            await StartSourceAsync(_createSilence(), null).ConfigureAwait(false);
            return new CaptureFaultedEventArgs(wantedInput!, ex);
        }
    }

    private async Task StartSourceAsync(ICaptureSource source, string? inputId)
    {
        source.FramesCaptured += OnFramesCaptured;
        source.Faulted += OnSourceFaulted;
        Volatile.Write(ref _active, source);
        _activeInputId = inputId;
        try
        {
            await source.StartAsync().ConfigureAwait(false);
        }
        catch
        {
            await StopActiveAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task StopActiveAsync()
    {
        var source = _active;
        if (source is null)
        {
            return;
        }

        Volatile.Write(ref _active, null);
        _activeInputId = null;
        source.FramesCaptured -= OnFramesCaptured;
        source.Faulted -= OnSourceFaulted;
        try
        {
            await source.StopAsync().ConfigureAwait(false);
            await source.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Stopping a capture source failed");
        }
    }

    private void OnFramesCaptured(object? sender, CapturedAudio audio)
    {
        lock (_frameLock)
        {
            // A source being stopped may still deliver a packet it had in hand.
            if (!ReferenceEquals(sender, Volatile.Read(ref _active)))
            {
                return;
            }

            long start = audio.CaptureTimeMicroseconds;
            var pcm = audio.Pcm;
            int frames = pcm.Length / CaptureFormat.BytesPerFrame;

            if (_lastFrameEnd != NoFrames && start < _lastFrameEnd)
            {
                if (ReferenceEquals(sender, _lastSender))
                {
                    // One continuous source: the overlap is µs rounding and QPC jitter, not
                    // repeated audio. Re-stamp rather than cut a frame out of the signal.
                    start = _lastFrameEnd;
                }
                else
                {
                    long overlap = _lastFrameEnd - start;
                    long drop = ((overlap * CaptureFormat.SampleRate) + 999_999) / 1_000_000;
                    _logger.LogDebug("Trimmed {Frames} overlapping frames across a capture switch", Math.Min(drop, frames));
                    if (drop >= frames)
                    {
                        return;
                    }

                    pcm = pcm.Slice((int)drop * CaptureFormat.BytesPerFrame);
                    start += CaptureFormat.FramesToMicroseconds(drop);
                    frames -= (int)drop;
                }
            }

            _lastFrameEnd = start + CaptureFormat.FramesToMicroseconds(frames);
            _lastSender = sender;

            // Inside the lock so frames reach the SDK in timeline order across a switch.
            AudioCaptured?.Invoke(this, new CapturedAudio(pcm, start));
        }
    }

    private void OnSourceFaulted(object? sender, Exception exception) =>
        _ = HandleSourceFaultAsync(sender as ICaptureSource, exception);

    private async Task HandleSourceFaultAsync(ICaptureSource? source, Exception exception)
    {
        try
        {
            string? failedInput = null;
            await SwitchAsync(() =>
            {
                if (source is not null && ReferenceEquals(source, _active) && _activeInputId is not null)
                {
                    failedInput = _activeInputId;
                    _sendEnabled = false;
                }
            }).ConfigureAwait(false);

            if (failedInput is not null)
            {
                _logger.LogWarning(exception, "Capture from {InputId} failed; sending silence", failedInput);
                Faulted?.Invoke(this, new CaptureFaultedEventArgs(failedInput, exception));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Recovering from a capture failure failed");
        }
    }
}
