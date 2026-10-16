// <copyright file="FakeCaptureSource.cs" company="Sendspin Windows Client">
// Licensed under the MIT License. See LICENSE file in the project root.
// </copyright>

using System.Runtime.InteropServices;
using Sendspin.SDK.Audio.Source;
using Sendspin.Windows.Services.Audio.Capture;

namespace Sendspin.Windows.Services.Tests.Audio.Capture;

/// <summary>A capture source the test drives: emits frames and failures on demand.</summary>
internal sealed class FakeCaptureSource(string name) : ICaptureSource
{
    public event EventHandler<CapturedAudio>? FramesCaptured;

    public event EventHandler<Exception>? Faulted;

    public string Name { get; } = name;

    public bool ThrowOnStart { get; init; }

    /// <summary>Gets a value indicating whether start and stop yield before taking effect, so callers really interleave.</summary>
    public bool YieldOnStartStop { get; init; }

    /// <summary>Gets the counter of sources running at once, shared by every source a test creates.</summary>
    public RunningCounter? Counter { get; init; }

    public bool Running { get; private set; }

    public bool Disposed { get; private set; }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (YieldOnStartStop)
        {
            await Task.Yield();
        }

        if (ThrowOnStart)
        {
            throw new COMException("in use", CaptureErrorMessages.DeviceInUse);
        }

        SetRunning(true);
    }

    public async Task StopAsync()
    {
        if (YieldOnStartStop)
        {
            await Task.Yield();
        }

        SetRunning(false);
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }

    public void Emit(long captureTime, int frames, byte fill = 1) =>
        FramesCaptured?.Invoke(
            this,
            new CapturedAudio(Enumerable.Repeat(fill, frames * CaptureFormat.BytesPerFrame).ToArray(), captureTime));

    public void Fail(Exception exception) => Faulted?.Invoke(this, exception);

    private void SetRunning(bool running)
    {
        if (Running == running)
        {
            return;
        }

        Running = running;
        if (running)
        {
            Counter?.Increment();
        }
        else
        {
            Counter?.Decrement();
        }
    }
}

/// <summary>Counts sources running at once and remembers the most there ever were.</summary>
internal sealed class RunningCounter
{
    private readonly object _lock = new();

    public int Current { get; private set; }

    public int Peak { get; private set; }

    public void Increment()
    {
        lock (_lock)
        {
            Current++;
            Peak = Math.Max(Peak, Current);
        }
    }

    public void Decrement()
    {
        lock (_lock)
        {
            Current--;
        }
    }
}
