using System.IO.Pipes;

namespace SendspinClient;

/// <summary>
/// Ensures only one instance of the application runs at a time.
/// Uses a named mutex for detection and a named pipe to signal the
/// existing instance to show its window.
/// </summary>
internal sealed class SingleInstanceGuard : IDisposable
{
    private const string MutexName = "Sendspin_SingleInstance";
    private const string PipeName = "Sendspin_ShowWindow";

    private Mutex? _mutex;
    private CancellationTokenSource? _pipeCts;

    /// <summary>
    /// Raised when another instance requests this instance to show its window.
    /// Always raised on a background thread — callers must dispatch to the UI thread.
    /// </summary>
    public event EventHandler? ShowWindowRequested;

    /// <summary>
    /// Attempts to become the single running instance.
    /// </summary>
    /// <returns>
    /// <c>true</c> if this is the first instance (caller should continue startup).
    /// <c>false</c> if another instance is already running (caller should shut down).
    /// </returns>
    public bool TryStart()
    {
        _mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);

        if (createdNew)
        {
            // We are the first instance — start listening for show-window requests
            _pipeCts = new CancellationTokenSource();
            _ = ListenForShowRequestsAsync(_pipeCts.Token);
            return true;
        }

        // Another instance owns the mutex — signal it to show its window
        SignalExistingInstance();
        return false;
    }

    /// <summary>
    /// Listens for incoming pipe connections from subsequent instances.
    /// Each connection triggers <see cref="ShowWindowRequested"/>.
    /// </summary>
    private async Task ListenForShowRequestsAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.In,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await server.WaitForConnectionAsync(ct);

                // Connection received — raise event (don't need to read data)
                ShowWindowRequested?.Invoke(this, EventArgs.Empty);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // Pipe error — wait briefly and retry
                try { await Task.Delay(500, ct); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    /// <summary>
    /// Connects to the existing instance's named pipe to signal it
    /// to bring its window to the foreground.
    /// </summary>
    private static void SignalExistingInstance()
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(timeout: 2000);
            // Connection itself is the signal — no data needed
        }
        catch
        {
            // If pipe connect fails, the existing instance may be shutting down.
            // Either way, we exit — worst case the user clicks the tray icon.
        }
    }

    public void Dispose()
    {
        _pipeCts?.Cancel();
        _pipeCts?.Dispose();

        if (_mutex != null)
        {
            try { _mutex.ReleaseMutex(); } catch { }
            _mutex.Dispose();
        }
    }
}
