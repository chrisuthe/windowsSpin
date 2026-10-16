using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Sendspin.SDK.Audio.Source;
using Sendspin.SDK.Synchronization;

namespace Sendspin.Windows.Services.Audio.Capture;

/// <summary>
/// Event-driven WASAPI capture on a dedicated thread, shared by the endpoint and process-loopback
/// sources.
/// </summary>
/// <remarks>
/// The client is opened, read and closed on the capture thread, an MTA thread, so no COM call
/// crosses apartments. That matters most for process loopback, whose client comes from an
/// asynchronous activation completed on a worker thread.
/// </remarks>
public abstract class WasapiCaptureSource : ICaptureSource
{
    /// <summary>Shared-mode buffer: 200 ms, in 100-ns units.</summary>
    protected const long BufferDuration = 2_000_000;

    private readonly IHighPrecisionTimer _timer;
    private readonly ILogger _logger;
    private readonly object _lock = new();
    private Thread? _thread;
    private AutoResetEvent? _packetReady;
    private volatile bool _stopping;

    /// <summary>Initializes a new instance of the <see cref="WasapiCaptureSource"/> class.</summary>
    /// <param name="timer">The clock capture times are expressed in.</param>
    /// <param name="logger">Logger for capture failures.</param>
    protected WasapiCaptureSource(IHighPrecisionTimer timer, ILogger logger)
    {
        _timer = timer;
        _logger = logger;
    }

    /// <inheritdoc/>
    public event EventHandler<CapturedAudio>? FramesCaptured;

    /// <inheritdoc/>
    public event EventHandler<Exception>? Faulted;

    /// <inheritdoc/>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lock)
        {
            if (_thread is not null)
            {
                return;
            }

            _stopping = false;
            _packetReady = new AutoResetEvent(false);
            _thread = new Thread(() => Run(opened))
            {
                IsBackground = true,
                Name = "Sendspin capture",
                Priority = ThreadPriority.AboveNormal,
            };
            _thread.SetApartmentState(ApartmentState.MTA);
            _thread.Start();
        }

        try
        {
            await opened.Task.ConfigureAwait(false);
        }
        catch
        {
            await StopAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc/>
    public Task StopAsync()
    {
        Thread? thread;
        AutoResetEvent? packetReady;
        lock (_lock)
        {
            thread = _thread;
            packetReady = _packetReady;
            _thread = null;
            _packetReady = null;
        }

        if (thread is null)
        {
            return Task.CompletedTask;
        }

        _stopping = true;
        packetReady!.Set();

        // Joined off the caller's thread: a Faulted handler may be the one stopping us, and it
        // runs on the capture thread itself.
        return Task.Run(() =>
        {
            thread.Join();
            packetReady.Dispose();
        });
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    /// <summary>
    /// Opens and starts the client, arranging for <paramref name="packetReady"/> to be signalled
    /// as packets arrive. Runs on the capture thread.
    /// </summary>
    /// <param name="packetReady">The event WASAPI signals.</param>
    /// <returns>The reader for the started client.</returns>
    protected abstract ICapturePacketReader Open(EventWaitHandle packetReady);

    /// <summary>Stops and releases what <see cref="Open"/> acquired. Runs on the capture thread.</summary>
    protected abstract void Close();

    private void Run(TaskCompletionSource opened)
    {
        var packetReady = _packetReady!;
        ICapturePacketReader reader;
        try
        {
            reader = Open(packetReady);
        }
        catch (Exception ex)
        {
            SafeClose();
            opened.TrySetException(ex);
            return;
        }

        opened.TrySetResult();
        try
        {
            while (!_stopping)
            {
                packetReady.WaitOne(100);
                Drain(reader);
            }
        }
        catch (Exception ex) when (!_stopping)
        {
            _logger.LogWarning(ex, "Audio capture failed");
            Faulted?.Invoke(this, ex);
        }
        finally
        {
            SafeClose();
        }
    }

    private void Drain(ICapturePacketReader reader)
    {
        while (!_stopping && reader.GetNextPacketSize() > 0)
        {
            IntPtr data = reader.GetBuffer(out int frames, out bool silent, out long qpcPosition);
            try
            {
                var pcm = new byte[frames * CaptureFormat.BytesPerFrame];
                if (!silent)
                {
                    Marshal.Copy(data, pcm, 0, pcm.Length);
                }

                FramesCaptured?.Invoke(this, new CapturedAudio(pcm, CaptureTimeOf(qpcPosition, frames)));
            }
            finally
            {
                reader.ReleaseBuffer(frames);
            }
        }
    }

    private long CaptureTimeOf(long qpcPosition, int frames)
    {
        long now = _timer.GetCurrentTimeMicroseconds();
        return qpcPosition > 0
            ? CaptureTime.ToTimerMicroseconds(qpcPosition, now, Stopwatch.GetTimestamp(), Stopwatch.Frequency)
            : now - CaptureFormat.FramesToMicroseconds(frames);
    }

    private void SafeClose()
    {
        try
        {
            Close();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Closing audio capture failed");
        }
    }
}
