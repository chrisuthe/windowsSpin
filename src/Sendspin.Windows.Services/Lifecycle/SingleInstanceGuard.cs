using System.Diagnostics;
using System.IO.Pipes;

namespace Sendspin.Windows.Services.Lifecycle;

/// <summary>
/// Ensures only one instance of the application runs per Windows session.
/// Uses a named mutex for detection and a named pipe to signal the
/// existing instance to show its window.
/// </summary>
/// <remarks>
/// Both names are scoped to the session, so another user's copy (fast user switching, Remote
/// Desktop) neither blocks this one nor receives its requests. A mutex name without a prefix
/// already lives in the session's <c>Local\</c> namespace; the prefix is written out so the scope
/// is explicit, and so an installer <c>AppMutex</c> check can name the same object. Named pipes
/// have no session namespace — every pipe on the machine shares one — so the pipe name carries the
/// session id, and both ends use <see cref="PipeOptions.CurrentUserOnly"/>: the server's pipe
/// admits only this user, and the client refuses a server that another user created under the
/// same name.
/// </remarks>
public sealed class SingleInstanceGuard : IDisposable
{
    /// <summary>The mutex that marks a running instance in this session.</summary>
    public const string DefaultMutexName = @"Local\Sendspin_SingleInstance";

    private readonly string _mutexName;
    private readonly string _pipeName;

    private Mutex? _mutex;
    private CancellationTokenSource? _pipeCts;

    /// <summary>
    /// Creates a guard on the app's own mutex and pipe.
    /// </summary>
    public SingleInstanceGuard()
        : this(DefaultMutexName, DefaultPipeName)
    {
    }

    /// <summary>
    /// Creates a guard on the given names. Tests use this so they never share the app's.
    /// </summary>
    /// <param name="mutexName">The mutex marking a running instance.</param>
    /// <param name="pipeName">The pipe a second launch signals through.</param>
    public SingleInstanceGuard(string mutexName, string pipeName)
    {
        _mutexName = mutexName;
        _pipeName = pipeName;
    }

    /// <summary>
    /// Raised when another instance requests this instance to show its window.
    /// Always raised on a background thread — callers must dispatch to the UI thread.
    /// </summary>
    public event EventHandler? ShowWindowRequested;

    /// <summary>Gets the pipe a second launch uses to ask the running instance to show its window.</summary>
    public static string DefaultPipeName { get; } = $"Sendspin_ShowWindow_{CurrentSessionId()}";

    /// <summary>
    /// Attempts to become the single running instance.
    /// </summary>
    /// <returns>
    /// <c>true</c> if this is the first instance (caller should continue startup).
    /// <c>false</c> if another instance is already running (caller should shut down).
    /// </returns>
    public bool TryStart()
    {
        _mutex = new Mutex(initiallyOwned: true, _mutexName, out var createdNew);

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

    private static int CurrentSessionId()
    {
        using var process = Process.GetCurrentProcess();
        return process.SessionId;
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
                    _pipeName,
                    PipeDirection.In,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

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
    private void SignalExistingInstance()
    {
        try
        {
            // CurrentUserOnly: refuse to signal a pipe another user created under this name.
            using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
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
