using Microsoft.Extensions.Logging;
using Sendspin.SDK.Client;

namespace Sendspin.Windows.Services.Audio.Capture;

/// <summary>Whether Send can be turned on, and if not, why.</summary>
public enum SendAvailability
{
    /// <summary>Send can be turned on.</summary>
    Available,

    /// <summary>No server is connected.</summary>
    Disconnected,

    /// <summary>The session is not paired, so the server will not activate the source role.</summary>
    NotPaired,

    /// <summary>Paired, but the server did not activate the source role.</summary>
    ServerDoesNotAcceptInputs,

    /// <summary>This PC has nothing to capture from.</summary>
    NoInputs,

    /// <summary>The selected input is not available.</summary>
    InputMissing,
}

/// <summary>
/// Owns Send: opens the capture gate, reports line-sense, and says why Send is unavailable.
/// </summary>
/// <remarks>
/// Music Assistant records a client's first signal report without acting on it, so
/// <see cref="InitializeAsync"/> reports <c>absent</c> before anything else; the first Send
/// press is then a transition MA turns into autostart. Not thread-safe beyond the fault path:
/// call it from the UI thread.
/// </remarks>
public sealed class SourceSendController
{
    private readonly WindowsAudioCaptureDevice _device;
    private readonly ICaptureInputCatalog _catalog;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private IReadOnlyList<CaptureInput> _inputs = [];
    private SourceRoleState? _server;

    /// <summary>Initializes a new instance of the <see cref="SourceSendController"/> class.</summary>
    /// <param name="device">The capture device handed to the SDK.</param>
    /// <param name="catalog">What can be captured.</param>
    /// <param name="logger">Logger.</param>
    public SourceSendController(
        WindowsAudioCaptureDevice device,
        ICaptureInputCatalog catalog,
        ILogger<SourceSendController> logger)
    {
        _device = device;
        _catalog = catalog;
        _logger = logger;
        _device.Faulted += OnDeviceFaulted;
    }

    /// <summary>Raised when any state the UI shows changes. May be raised off the UI thread.</summary>
    public event EventHandler? StateChanged;

    /// <summary>Raised with a user-facing message when Send turned itself off after a failure.</summary>
    public event EventHandler<string>? SendingStopped;

    /// <summary>
    /// Gets or sets the delivery of the line-sense signal to whichever connection is current. Set by
    /// the view model, which knows the connection mode.
    /// </summary>
    public Func<bool, Task> ReportSignalAsync { get; set; } = _ => Task.CompletedTask;

    /// <summary>Gets a value indicating whether Send is on.</summary>
    public bool IsSending { get; private set; }

    /// <summary>Gets the selected input's ID.</summary>
    public string? InputId { get; private set; }

    /// <summary>Gets the inputs available at the last refresh.</summary>
    public IReadOnlyList<CaptureInput> Inputs => _inputs;

    /// <summary>Gets the selected input, or null when it is not available.</summary>
    public CaptureInput? SelectedInput => _inputs.FirstOrDefault(i => i.Id == InputId);

    /// <summary>Gets whether Send can be turned on.</summary>
    public SendAvailability Availability =>
        _server is null ? SendAvailability.Disconnected
        : !_server.IsActive
            ? (_server.TrustLevel == SendspinTrustLevel.Paired
                ? SendAvailability.ServerDoesNotAcceptInputs
                : SendAvailability.NotPaired)
        : _inputs.Count == 0 ? SendAvailability.NoInputs
        : SelectedInput is null ? SendAvailability.InputMissing
        : SendAvailability.Available;

    /// <summary>Gets why Send is unavailable, or null when it is available.</summary>
    public string? UnavailableReason => Availability switch
    {
        SendAvailability.Disconnected => "Not connected to Music Assistant",
        SendAvailability.NotPaired => "Pair this player with Music Assistant to send audio",
        SendAvailability.ServerDoesNotAcceptInputs => "This server doesn't accept audio inputs",
        SendAvailability.NoInputs => "No audio inputs available",
        SendAvailability.InputMissing =>
            $"{(InputId is { } id ? _catalog.Describe(id)?.Name : null) ?? "The selected input"} isn't connected",
        _ => null,
    };

    /// <summary>
    /// Selects the saved input (or the first available) and reports <c>absent</c>. Call once at
    /// startup, after <see cref="ReportSignalAsync"/> is set.
    /// </summary>
    /// <param name="savedInputId">The persisted input ID, if any.</param>
    /// <returns>A task that completes once applied.</returns>
    public async Task InitializeAsync(string? savedInputId)
    {
        // First, so a failure to enumerate below cannot skip it.
        await ReportAsync(false).ConfigureAwait(false);
        _inputs = _catalog.GetInputs();
        InputId = !string.IsNullOrEmpty(savedInputId) ? savedInputId : _inputs.FirstOrDefault()?.Id;
        await _device.SetInputAsync(InputId).ConfigureAwait(false);
        RaiseStateChanged();
    }

    /// <summary>Re-reads the available inputs.</summary>
    public void RefreshInputs()
    {
        _inputs = _catalog.GetInputs();
        RaiseStateChanged();
    }

    /// <summary>Describes an input by ID, even when it is not plugged in.</summary>
    /// <param name="inputId">The input ID.</param>
    /// <returns>The input, or null if Windows does not know it.</returns>
    public CaptureInput? DescribeInput(string inputId) => _catalog.Describe(inputId);

    /// <summary>Selects what Send captures; applies immediately, including while sending.</summary>
    /// <param name="inputId">The input ID.</param>
    /// <returns>A task that completes once applied.</returns>
    public async Task SelectInputAsync(string inputId)
    {
        InputId = inputId;
        await _device.SetInputAsync(inputId).ConfigureAwait(false);
        RaiseStateChanged();
    }

    /// <summary>Records the connected server's source-role state, or null when disconnected.</summary>
    /// <param name="state">The state, or null.</param>
    public void UpdateServer(SourceRoleState? state)
    {
        _server = state;
        RaiseStateChanged();
    }

    /// <summary>Turns Send on or off. Turning on is ignored while <see cref="Availability"/> says no.</summary>
    /// <param name="sending">True to send.</param>
    /// <returns>A task that completes once applied.</returns>
    public async Task SetSendingAsync(bool sending)
    {
        // Serialized so a quick off-then-on cannot interleave: the off's trailing gate-close would
        // otherwise land after the on's gate-open no-op. OnDeviceFaulted must not take this gate:
        // the device raises Faulted from inside SetSendEnabledAsync while it is held here.
        await _sendGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (sending == IsSending)
            {
                return;
            }

            if (sending)
            {
                try
                {
                    _inputs = _catalog.GetInputs();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not enumerate audio inputs; Send stays off");
                    _inputs = [];
                    RaiseStateChanged();
                    return;
                }

                if (Availability != SendAvailability.Available)
                {
                    RaiseStateChanged();
                    return;
                }
            }

            IsSending = sending;
            RaiseStateChanged();

            if (sending)
            {
                await _device.SetSendEnabledAsync(true).ConfigureAwait(false);

                // A failed start has already turned Send off (OnDeviceFaulted); don't claim signal.
                if (IsSending)
                {
                    await ReportAsync(true).ConfigureAwait(false);
                }
            }
            else
            {
                await ReportAsync(false).ConfigureAwait(false);
                await _device.SetSendEnabledAsync(false).ConfigureAwait(false);
            }
        }
        finally
        {
            _sendGate.Release();
        }
    }

    /// <summary>Stops capture at shutdown.</summary>
    /// <returns>A task that completes once stopped.</returns>
    public Task StopAsync() => _device.StopAllAsync();

    private async Task ReportAsync(bool present)
    {
        try
        {
            await ReportSignalAsync(present).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not report source signal {Signal}", present ? "present" : "absent");
        }
    }

    private void OnDeviceFaulted(object? sender, CaptureFaultedEventArgs e)
    {
        string name = _inputs.FirstOrDefault(i => i.Id == e.InputId)?.Name
            ?? _catalog.Describe(e.InputId)?.Name
            ?? "The input";
        bool wasSending = IsSending;
        IsSending = false;
        RaiseStateChanged();

        if (wasSending)
        {
            _ = ReportAsync(false);
            SendingStopped?.Invoke(this, CaptureErrorMessages.Describe(name, e.Exception));
        }

        // Last, and guarded: enumerating can fail while the device that just faulted is vanishing,
        // and that must not cost the user the stop notice above.
        try
        {
            _inputs = _catalog.GetInputs();
            RaiseStateChanged();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not refresh inputs after a capture fault");
        }
    }

    private void RaiseStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);
}
