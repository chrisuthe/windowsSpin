namespace Sendspin.Windows.Services.Visualization;

/// <summary>
/// Tracks whether a per-frame render loop should be running and invokes the supplied
/// start/stop actions only on transitions.
/// </summary>
public sealed class RenderLoopGate
{
    private readonly Action _start;
    private readonly Action _stop;

    private bool _running;

    /// <summary>Initializes a new instance of the <see cref="RenderLoopGate"/> class.</summary>
    /// <param name="start">Invoked when the loop transitions from stopped to running.</param>
    /// <param name="stop">Invoked when the loop transitions from running to stopped.</param>
    public RenderLoopGate(Action start, Action stop)
    {
        _start = start;
        _stop = stop;
    }

    /// <summary>Gets a value indicating whether the loop is currently running.</summary>
    public bool IsRunning => _running;

    /// <summary>Starts or stops the loop if <paramref name="shouldRun"/> differs from the current state.</summary>
    /// <param name="shouldRun">Whether the loop should be running.</param>
    public void Update(bool shouldRun)
    {
        if (shouldRun == _running)
        {
            return;
        }

        _running = shouldRun;
        if (shouldRun)
        {
            _start();
        }
        else
        {
            _stop();
        }
    }
}
