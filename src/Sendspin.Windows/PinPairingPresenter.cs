using System.Windows;
using Microsoft.Extensions.Logging;
using Sendspin.SDK.Client;
using Sendspin.SDK.Extensions;
using Sendspin.Windows.Views;

namespace Sendspin.Windows;

/// <summary>
/// Bridges the SDK's dynamic-PIN presenter (<c>SendspinClientOptions.PresentPinAsync</c>)
/// to a modal <see cref="PinPairingDialog"/>, tracking a single dialog instance.
/// </summary>
/// <remarks>
/// Threading contract: the SDK invokes <see cref="PresentPinAsync"/> on a connection's
/// receive thread and awaits it before sending <c>client/pair-auth</c>, and the server's
/// PIN-request feedback timeout is short - so the presenter must complete as soon as the
/// PIN is on screen, never when the dialog is dismissed. <c>ShowDialog()</c> runs a nested
/// message loop and does not return until the dialog closes, so the modal show is posted
/// to the dispatcher WITHOUT awaiting the dispatcher operation; the presenter instead
/// awaits a completion source resolved by the dialog's <c>ContentRendered</c> (or by the
/// in-place PIN update on retry). All UI access happens in dispatcher-posted delegates.
/// </remarks>
public sealed class PinPairingPresenter
{
    /// <summary>
    /// How long a torn-down attempt's dialog lingers before closing. A wrong PIN makes
    /// the server abort and immediately re-activate with fresh nonces (retry-in-place),
    /// which cancels the failed attempt's token milliseconds before the new PIN arrives;
    /// waiting lets the retry update the open dialog instead of closing and reopening it.
    /// A genuine abort has no successor, so the deferred close proceeds.
    /// </summary>
    private static readonly TimeSpan RetryGracePeriod = TimeSpan.FromSeconds(2);

    private readonly ILogger<PinPairingPresenter> _logger;

    /// <summary>The open dialog, if any. Touched only on the dispatcher thread.</summary>
    private PinPairingDialog? _dialog;

    /// <summary>
    /// Monotonic id of the latest presented PIN. Incremented by <see cref="PresentPinAsync"/>
    /// (receive thread), read by the deferred close to detect that a retry superseded the
    /// attempt whose cancellation scheduled it.
    /// </summary>
    private int _generation;

    public PinPairingPresenter(ILogger<PinPairingPresenter> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// SDK presenter callback. Shows the PIN in a modal dialog (updating the open dialog
    /// in place on retry) and completes once the PIN is on screen. The PIN itself is never
    /// logged. The cancellation token belongs to the pairing attempt: its cancellation
    /// closes the dialog, since the displayed PIN is dead once the attempt is torn down.
    /// </summary>
    /// <param name="presentation">
    /// The derived PIN and the server's language hints. The hints are informational — the
    /// spec makes emitting in another language a non-error — and this dialog renders in the
    /// app's own UI language, so only <see cref="PinPresentation.Pin"/> is used today.
    /// </param>
    /// <param name="cancellationToken">The pairing attempt's token; see the remarks above.</param>
    public async ValueTask PresentPinAsync(PinPresentation presentation, CancellationToken cancellationToken)
    {
        string pin = presentation.Pin;

        var generation = Interlocked.Increment(ref _generation);
        var dispatcher = Application.Current?.Dispatcher
            ?? throw new InvalidOperationException("Cannot present a pairing PIN: no WPF application is running");

        var shown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Deliberately not scoped to this method: the SDK cancels this token when the
        // attempt is torn down (abort, supersession, disconnect), typically long after
        // presentation completed, and that cancellation is the close signal for the
        // dialog. The SDK disposes the attempt's CTS right after cancelling, which
        // releases the registration.
        cancellationToken.Register(() =>
        {
            shown.TrySetCanceled(cancellationToken);
            CloseAfterRetryGraceAsync(generation).SafeFireAndForget(_logger);
        });

        // Post the modal show WITHOUT awaiting the dispatcher operation: its delegate
        // does not return until the dialog closes (nested message loop), but the dialog
        // is on screen - and 'shown' completed - long before that.
        _ = dispatcher.InvokeAsync(() => ShowOrUpdate(pin, cancellationToken, shown));

        await shown.Task.ConfigureAwait(false);
        _logger.LogInformation("Pairing PIN presented to the operator");
    }

    /// <summary>
    /// Closes the PIN dialog if it is open. Safe to call from any thread; used when
    /// pairing completes, at which point the displayed PIN is spent.
    /// </summary>
    public void CloseDialog()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return;
        }

        _ = dispatcher.InvokeAsync(() => CloseDialogCore("pairing completed"));
    }

    /// <summary>
    /// Surfaces the main window so the modal PIN dialog has a visible owner and gets
    /// noticed: the app normally lives in the tray (MainWindow hides on close), and a
    /// modal owned by a hidden window is itself effectively invisible. Pairing is
    /// deliberate one-time setup, so pulling the app forward is desirable here.
    /// </summary>
    private static Window? BringMainWindowForward()
    {
        var window = Application.Current?.MainWindow;
        if (window is null)
        {
            return null;
        }

        window.Show();
        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Activate();
        return window;
    }

    /// <summary>
    /// Runs on the dispatcher thread. Updates the open dialog in place (retry), or brings
    /// the app forward and shows a new modal dialog. <paramref name="shown"/> completes as
    /// soon as the PIN is visible - on update immediately, on first show when the dialog
    /// has rendered - so the awaiting presenter never waits for dismissal.
    /// </summary>
    private void ShowOrUpdate(string pin, CancellationToken cancellationToken, TaskCompletionSource shown)
    {
        try
        {
            if (cancellationToken.IsCancellationRequested)
            {
                // The attempt was torn down before the dispatcher got here; nothing to show.
                shown.TrySetCanceled(cancellationToken);
                return;
            }

            if (_dialog is { } open)
            {
                // Retry-in-place: same window, new PIN. Never a second window.
                open.UpdatePin(pin);
                shown.TrySetResult();
                return;
            }

            var owner = BringMainWindowForward();
            var dialog = new PinPairingDialog();
            if (owner is not null)
            {
                dialog.Owner = owner;
            }
            else
            {
                dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            }

            dialog.UpdatePin(pin);
            dialog.ContentRendered += (_, _) => shown.TrySetResult();
            dialog.Closed += (_, _) =>
            {
                _dialog = null;

                // Unblocks the presenter if the dialog was closed before its first render.
                shown.TrySetResult();
            };

            _dialog = dialog;
            dialog.ShowDialog();
        }
        catch (Exception ex)
        {
            if (_dialog is { IsVisible: false })
            {
                _dialog = null;
            }

            _logger.LogError(ex, "Failed to show the pairing PIN dialog");
            shown.TrySetException(ex);
        }
    }

    /// <summary>
    /// Deferred close scheduled by an attempt's cancellation: waits out
    /// <see cref="RetryGracePeriod"/>, then closes the dialog unless a newer PIN has been
    /// presented in the meantime (server retry-in-place), in which case the dialog now
    /// shows the live PIN and must stay.
    /// </summary>
    private async Task CloseAfterRetryGraceAsync(int generation)
    {
        await Task.Delay(RetryGracePeriod).ConfigureAwait(false);

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return;
        }

        _ = dispatcher.InvokeAsync(() =>
        {
            if (Volatile.Read(ref _generation) != generation)
            {
                return;
            }

            CloseDialogCore("pairing attempt ended");
        });
    }

    /// <summary>Runs on the dispatcher thread.</summary>
    private void CloseDialogCore(string reason)
    {
        if (_dialog is not { } dialog)
        {
            return;
        }

        _logger.LogInformation("Closing the pairing PIN dialog ({Reason})", reason);
        dialog.Close();
    }
}
