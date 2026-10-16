// <copyright file="WindowsAudioCaptureDeviceTests.cs" company="Sendspin Windows Client">
// Licensed under the MIT License. See LICENSE file in the project root.
// </copyright>

using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging.Abstractions;
using Sendspin.SDK.Audio.Source;
using Sendspin.Windows.Services.Audio.Capture;
using Xunit;

namespace Sendspin.Windows.Services.Tests.Audio.Capture;

public class WindowsAudioCaptureDeviceTests
{
    [Fact]
    public async Task WhileSendIsOff_AStartRunsSilence_AndNeverConstructsTheInput()
    {
        // The privacy guarantee: an MA start with Send off opens no device at all.
        var h = new Harness();
        await h.Device.SetInputAsync("mic");

        await h.Device.StartAsync();

        Assert.True(Assert.Single(h.Silences).Running);
        Assert.Empty(h.Inputs);
    }

    [Fact]
    public async Task SendOn_SwitchesToTheInput_AndOff_BackToSilence()
    {
        var h = new Harness();
        await h.Device.SetInputAsync("mic");
        await h.Device.StartAsync();

        await h.Device.SetSendEnabledAsync(true);
        Assert.True(Assert.Single(h.Inputs).Running);
        Assert.True(h.Silences[0].Disposed);
        Assert.Equal(1, h.Running);

        await h.Device.SetSendEnabledAsync(false);
        Assert.True(h.Inputs[0].Disposed);
        Assert.True(h.Silences[^1].Running);
        Assert.Equal(1, h.Running);
    }

    [Fact]
    public async Task BeforeTheSdkStarts_SendAndInputChanges_OpenNothing()
    {
        var h = new Harness();

        await h.Device.SetInputAsync("mic");
        await h.Device.SetSendEnabledAsync(true);

        Assert.Empty(h.Silences);
        Assert.Empty(h.Inputs);
    }

    [Fact]
    public async Task ChangingInputWhileSending_SwitchesTheSource()
    {
        var h = new Harness();
        await h.Device.SetInputAsync("a");
        await h.Device.StartAsync();
        await h.Device.SetSendEnabledAsync(true);

        await h.Device.SetInputAsync("b");

        Assert.Equal(new[] { "a", "b" }, h.Inputs.Select(i => i.Name));
        Assert.True(h.Inputs[0].Disposed);
        Assert.True(h.Inputs[1].Running);
    }

    [Fact]
    public async Task AnInputThatFailsToStart_FallsBackToSilence_AndCanBeRetried()
    {
        var h = new Harness();
        h.FailingInputs.Add("mic");
        await h.Device.SetInputAsync("mic");
        await h.Device.StartAsync();

        await h.Device.SetSendEnabledAsync(true);

        var fault = Assert.Single(h.Faults);
        Assert.Equal("mic", fault.InputId);
        Assert.True(h.Silences[^1].Running);

        // Plugged back in: the next Send opens a fresh source.
        h.FailingInputs.Clear();
        await h.Device.SetSendEnabledAsync(true);
        Assert.True(h.Inputs[^1].Running);
        Assert.Equal(2, h.Inputs.Count);
    }

    [Fact]
    public async Task ARunningInputThatFails_FallsBackToSilence()
    {
        var h = new Harness();
        await h.Device.SetInputAsync("mic");
        await h.Device.StartAsync();
        await h.Device.SetSendEnabledAsync(true);

        h.Inputs[0].Fail(new COMException("gone", CaptureErrorMessages.DeviceInvalidated));

        await WaitUntilAsync(() => h.Faults.Count == 1);
        await WaitUntilAsync(() => h.Silences[^1].Running && h.Running == 1);
        Assert.Equal("mic", h.Faults.Single().InputId);
    }

    [Fact]
    public async Task FramesFromAStoppedSource_AreDropped()
    {
        var h = new Harness();
        await h.Device.SetInputAsync("mic");
        await h.Device.StartAsync();
        var silence = h.Silences[0];
        await h.Device.SetSendEnabledAsync(true);

        silence.Emit(captureTime: 0, frames: 960);

        Assert.Empty(h.Delivered);
    }

    [Fact]
    public async Task AnOverlapAcrossASwitch_IsTrimmed()
    {
        var h = new Harness();
        await h.Device.SetInputAsync("mic");
        await h.Device.StartAsync();
        h.Silences[0].Emit(captureTime: 0, frames: 960, fill: 0);
        await h.Device.SetSendEnabledAsync(true);

        // The input's first packet starts 10 ms before the silence ended.
        h.Inputs[0].Emit(captureTime: 10_000, frames: 960, fill: 7);

        Assert.Equal(2, h.Delivered.Count);
        Assert.Equal(20_000, h.Delivered[1].CaptureTimeMicroseconds);
        Assert.Equal(480 * CaptureFormat.BytesPerFrame, h.Delivered[1].Pcm.Length);
    }

    [Fact]
    public async Task AStop_ResetsTheTimeline()
    {
        // MA may stop and later restart the stream; the next stream's frames are not "overlaps".
        var h = new Harness();
        await h.Device.StartAsync();
        h.Silences[0].Emit(captureTime: 1_000_000, frames: 960);
        await h.Device.StopAsync();
        await h.Device.StartAsync();

        h.Silences[^1].Emit(captureTime: 0, frames: 960);

        Assert.Equal(0, h.Delivered[^1].CaptureTimeMicroseconds);
        Assert.Equal(960 * CaptureFormat.BytesPerFrame, h.Delivered[^1].Pcm.Length);
    }

    [Fact]
    public async Task TheFormat_IsFixedPcm48k16Stereo()
    {
        var h = new Harness();
        var before = h.Device.Format;
        await h.Device.StartAsync();
        await h.Device.SetSendEnabledAsync(true);

        Assert.Same(before, h.Device.Format);
        Assert.Equal(("pcm", 48000, 2, (int?)16), (before.Codec, before.SampleRate, before.Channels, before.BitDepth));
    }

    [Fact]
    public async Task DisposeIsNotTerminal()
    {
        // The SDK disposes the shared device whenever it disposes an owning client — the dial
        // cleanup, the host's stop. The next connection's start must still work.
        var h = new Harness();
        await h.Device.StartAsync();

        await h.Device.DisposeAsync();
        Assert.Equal(0, h.Running);

        await h.Device.StartAsync();
        Assert.True(h.Silences[^1].Running);
    }

    [Fact]
    public async Task ConcurrentToggles_LeaveExactlyOneSourceRunning()
    {
        var h = new Harness();
        await h.Device.SetInputAsync("mic");
        await h.Device.StartAsync();

        await Task.WhenAll(Enumerable.Range(0, 20).Select(i => h.Device.SetSendEnabledAsync(i % 2 == 0)));

        Assert.Equal(1, h.Running);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out");
            await Task.Delay(10);
        }
    }

    private sealed class Harness
    {
        public Harness()
        {
            Device = new WindowsAudioCaptureDevice(
                () => Add(Silences, new FakeCaptureSource("silence")),
                id => Add(Inputs, new FakeCaptureSource(id) { ThrowOnStart = FailingInputs.Contains(id) }),
                NullLogger<WindowsAudioCaptureDevice>.Instance);
            Device.AudioCaptured += (_, a) => Delivered.Add(a);
            Device.Faulted += (_, f) => Faults.Enqueue(f);
        }

        public WindowsAudioCaptureDevice Device { get; }

        public List<FakeCaptureSource> Silences { get; } = new();

        public List<FakeCaptureSource> Inputs { get; } = new();

        public HashSet<string> FailingInputs { get; } = new();

        public List<CapturedAudio> Delivered { get; } = new();

        public ConcurrentQueue<CaptureFaultedEventArgs> Faults { get; } = new();

        public int Running => Silences.Concat(Inputs).Count(s => s.Running);

        private static FakeCaptureSource Add(List<FakeCaptureSource> list, FakeCaptureSource source)
        {
            lock (list)
            {
                list.Add(source);
            }

            return source;
        }
    }
}
