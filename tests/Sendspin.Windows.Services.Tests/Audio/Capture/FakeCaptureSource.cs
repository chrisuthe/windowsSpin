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

    public bool Running { get; private set; }

    public bool Disposed { get; private set; }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (ThrowOnStart)
        {
            throw new COMException("in use", CaptureErrorMessages.DeviceInUse);
        }

        Running = true;
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        Running = false;
        return Task.CompletedTask;
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
}
