// <copyright file="SourceSendControllerTests.cs" company="Sendspin Windows Client">
// Licensed under the MIT License. See LICENSE file in the project root.
// </copyright>

using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging.Abstractions;
using Sendspin.SDK.Client;
using Sendspin.Windows.Services.Audio.Capture;
using Xunit;

namespace Sendspin.Windows.Services.Tests.Audio.Capture;

public class SourceSendControllerTests
{
    private static readonly CaptureInput Desktop = new(CaptureInputCatalog.DesktopId, "Desktop audio");
    private static readonly CaptureInput LineIn = new("line-in", "Line In");
    private static readonly SourceRoleState Active = new("ma", IsActive: true, SendspinTrustLevel.Paired);

    [Fact]
    public async Task Initialize_ReportsAbsent_AndPicksTheFirstInput()
    {
        // MA records a client's first signal silently: absent must come before the first present.
        var h = new Harness();

        await h.Controller.InitializeAsync(savedInputId: null);

        Assert.Equal(new[] { false }, h.Signals);
        Assert.Equal(CaptureInputCatalog.DesktopId, h.Controller.InputId);
    }

    [Fact]
    public async Task ASavedInputThatIsUnplugged_IsKept_AndExplained()
    {
        var h = new Harness();
        h.Catalog.Known["usb-1"] = new CaptureInput("usb-1", "USB Interface", IsAvailable: false);

        await h.Controller.InitializeAsync("usb-1");
        h.Controller.UpdateServer(Active);

        Assert.Equal("usb-1", h.Controller.InputId);
        Assert.Equal(SendAvailability.InputMissing, h.Controller.Availability);
        Assert.Equal("USB Interface isn't connected", h.Controller.UnavailableReason);
    }

    [Fact]
    public async Task SendOn_ReportsPresent_AndOff_ReportsAbsent()
    {
        var h = new Harness();
        await h.Controller.InitializeAsync(null);
        h.Controller.UpdateServer(Active);

        await h.Controller.SetSendingAsync(true);
        Assert.True(h.Controller.IsSending);

        await h.Controller.SetSendingAsync(false);
        Assert.False(h.Controller.IsSending);
        Assert.Equal(new[] { false, true, false }, h.Signals);
    }

    [Fact]
    public async Task RepeatedCalls_AreIdempotent()
    {
        var h = new Harness();
        await h.Controller.InitializeAsync(null);
        h.Controller.UpdateServer(Active);

        await h.Controller.SetSendingAsync(true);
        await h.Controller.SetSendingAsync(true);

        Assert.Equal(new[] { false, true }, h.Signals);
    }

    public static TheoryData<SourceRoleState?, bool, SendAvailability, string> Unavailable => new()
    {
        { null, true, SendAvailability.Disconnected, "Not connected to Music Assistant" },
        { new SourceRoleState("ma", false, SendspinTrustLevel.Unpaired), true, SendAvailability.NotPaired, "Pair this player with Music Assistant to send audio" },
        { new SourceRoleState("ma", false, SendspinTrustLevel.Paired), true, SendAvailability.ServerDoesNotAcceptInputs, "This server doesn't accept audio inputs" },
        { new SourceRoleState("ma", true, SendspinTrustLevel.Paired), false, SendAvailability.NoInputs, "No audio inputs available" },
    };

    [Theory]
    [MemberData(nameof(Unavailable))]
    public async Task SendIsRefused_WhenUnavailable_WithTheReason(
        SourceRoleState? server, bool haveInputs, SendAvailability expected, string reason)
    {
        var h = new Harness();
        if (!haveInputs)
        {
            h.Catalog.Inputs.Clear();
        }

        await h.Controller.InitializeAsync(null);
        h.Controller.UpdateServer(server);

        await h.Controller.SetSendingAsync(true);

        Assert.False(h.Controller.IsSending);
        Assert.Equal(expected, h.Controller.Availability);
        Assert.Equal(reason, h.Controller.UnavailableReason);
        Assert.Equal(new[] { false }, h.Signals);
    }

    [Fact]
    public async Task AFailingInput_TurnsSendOff_ReportsAbsent_AndExplains()
    {
        var h = new Harness();
        h.FailingInputs.Add(CaptureInputCatalog.DesktopId);
        await h.Controller.InitializeAsync(null);
        h.Controller.UpdateServer(Active);
        await h.Device.StartAsync(); // MA is already streaming

        await h.Controller.SetSendingAsync(true);

        Assert.False(h.Controller.IsSending);
        Assert.DoesNotContain(true, h.Signals);
        Assert.Equal("Desktop audio is in use by another app.", Assert.Single(h.StopMessages));
    }

    [Fact]
    public async Task AfterAFault_SendWorksAgain()
    {
        var h = new Harness();
        h.FailingInputs.Add(CaptureInputCatalog.DesktopId);
        await h.Controller.InitializeAsync(null);
        h.Controller.UpdateServer(Active);
        await h.Device.StartAsync();
        await h.Controller.SetSendingAsync(true);

        h.FailingInputs.Clear();
        await h.Controller.SetSendingAsync(true);

        Assert.True(h.Controller.IsSending);
        Assert.Equal(true, h.Signals[^1]);
    }

    [Fact]
    public async Task SendStaysOn_AcrossADisconnect_AndCanStillBeTurnedOff()
    {
        var h = new Harness();
        await h.Controller.InitializeAsync(null);
        h.Controller.UpdateServer(Active);
        await h.Controller.SetSendingAsync(true);

        h.Controller.UpdateServer(null);
        Assert.True(h.Controller.IsSending);

        await h.Controller.SetSendingAsync(false);
        Assert.False(h.Controller.IsSending);
        Assert.False(h.Signals[^1]);
    }

    private sealed class Harness
    {
        public Harness()
        {
            Device = new WindowsAudioCaptureDevice(
                () => new FakeCaptureSource("silence"),
                id => new FakeCaptureSource(id) { ThrowOnStart = FailingInputs.Contains(id) },
                NullLogger<WindowsAudioCaptureDevice>.Instance);
            Controller = new SourceSendController(Device, Catalog, NullLogger<SourceSendController>.Instance)
            {
                ReportSignalAsync = present =>
                {
                    Signals.Add(present);
                    return Task.CompletedTask;
                },
            };
            Controller.SendingStopped += (_, message) => StopMessages.Add(message);
        }

        public FakeCatalog Catalog { get; } = new();

        public HashSet<string> FailingInputs { get; } = new();

        public WindowsAudioCaptureDevice Device { get; }

        public SourceSendController Controller { get; }

        public List<bool> Signals { get; } = new();

        public List<string> StopMessages { get; } = new();
    }

    private sealed class FakeCatalog : ICaptureInputCatalog
    {
        public List<CaptureInput> Inputs { get; } = [Desktop, LineIn];

        public Dictionary<string, CaptureInput> Known { get; } = new();

        public IReadOnlyList<CaptureInput> GetInputs() => Inputs.ToList();

        public CaptureInput? Describe(string inputId) =>
            Inputs.FirstOrDefault(i => i.Id == inputId) ?? Known.GetValueOrDefault(inputId);
    }
}
