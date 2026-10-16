using Sendspin.SDK.Audio.Source;
using Sendspin.SDK.Synchronization;

namespace Sendspin.Windows.Services.Audio.Capture;

/// <summary>
/// Generates silence in <see cref="CaptureFormat"/>, paced by the clock, without opening any
/// device.
/// </summary>
/// <remarks>
/// This is what Music Assistant receives while Send is off. Accepting its <c>start</c> and
/// streaming silence is what a real line-in with nothing plugged in does; refusing would look
/// broken to MA, which drops a source that sends nothing within five seconds. Because nothing
/// is opened, the Windows microphone indicator stays dark until the user presses Send.
/// </remarks>
public sealed class SilenceSource : ICaptureSource
{
    /// <summary>Length of each emitted chunk.</summary>
    public const int ChunkMilliseconds = 20;

    private const int FramesPerChunk = CaptureFormat.SampleRate * ChunkMilliseconds / 1000;
    private const int PaceMilliseconds = 10;

    // Further behind than this (sleep/resume, a stalled thread) and the timeline restarts from
    // now: minutes of silence in one burst would only flood the stream with stale audio.
    private const long MaxCatchUpMicroseconds = 200_000;

    private static readonly long ChunkMicroseconds = CaptureFormat.FramesToMicroseconds(FramesPerChunk);

    private readonly IHighPrecisionTimer _timer;
    private readonly bool _runPacer;
    private readonly byte[] _silence = new byte[FramesPerChunk * CaptureFormat.BytesPerFrame];
    private readonly object _lock = new();
    private bool _started;
    private long _nextChunkStart;
    private CancellationTokenSource? _pacerCts;
    private Task? _pacer;

    /// <summary>Initializes a new instance of the <see cref="SilenceSource"/> class.</summary>
    /// <param name="timer">The clock capture times are read from.</param>
    /// <param name="runPacer">False in tests, which call <see cref="Pump"/> themselves.</param>
    public SilenceSource(IHighPrecisionTimer timer, bool runPacer = true)
    {
        _timer = timer;
        _runPacer = runPacer;
    }

    /// <inheritdoc/>
    public event EventHandler<CapturedAudio>? FramesCaptured;

    /// <inheritdoc/>
    /// <remarks>Generated silence cannot fail, so this is never raised.</remarks>
    public event EventHandler<Exception>? Faulted
    {
        add { }
        remove { }
    }

    /// <inheritdoc/>
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (_started)
            {
                return Task.CompletedTask;
            }

            _started = true;
            _nextChunkStart = _timer.GetCurrentTimeMicroseconds();
        }

        if (_runPacer)
        {
            _pacerCts = new CancellationTokenSource();
            var token = _pacerCts.Token;
            _pacer = Task.Run(() => PaceAsync(token));
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public async Task StopAsync()
    {
        lock (_lock)
        {
            _started = false;
        }

        if (_pacerCts is { } cts)
        {
            cts.Cancel();
            if (_pacer is { } pacer)
            {
                await pacer.ConfigureAwait(false);
            }

            cts.Dispose();
            _pacerCts = null;
            _pacer = null;
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    /// <summary>
    /// Emits every chunk that is due by now. Called by the pacer; public so tests can drive the
    /// source without waiting on real time.
    /// </summary>
    public void Pump()
    {
        lock (_lock)
        {
            if (!_started)
            {
                return;
            }

            long now = _timer.GetCurrentTimeMicroseconds();
            if (now - _nextChunkStart > MaxCatchUpMicroseconds)
            {
                _nextChunkStart = now - ChunkMicroseconds;
            }

            while (_nextChunkStart + ChunkMicroseconds <= now)
            {
                // The buffer is shared: it is zeros and is never written after construction.
                FramesCaptured?.Invoke(this, new CapturedAudio(_silence, _nextChunkStart));
                _nextChunkStart += ChunkMicroseconds;
            }
        }
    }

    private async Task PaceAsync(CancellationToken token)
    {
        try
        {
            while (true)
            {
                Pump();
                await Task.Delay(PaceMilliseconds, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Stopped.
        }
    }
}
