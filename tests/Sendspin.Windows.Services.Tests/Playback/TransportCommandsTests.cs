// <copyright file="TransportCommandsTests.cs" company="Sendspin Windows Client">
// Licensed under the MIT License. See LICENSE file in the project root.
// </copyright>

using Sendspin.SDK.Models;
using Sendspin.SDK.Protocol.Messages;
using Sendspin.Windows.Services.Playback;
using Xunit;

namespace Sendspin.Windows.Services.Tests.Playback;

public class TransportCommandsTests
{
    [Theory]
    [InlineData(PlaybackState.Playing, Commands.Pause)]
    [InlineData(PlaybackState.Paused, Commands.Play)]
    [InlineData(PlaybackState.Idle, Commands.Play)]
    public void PlayPause_SendsPauseOnlyWhilePlaying(PlaybackState state, string expected)
    {
        Assert.Equal(expected, TransportCommands.ForPlayPause(state));
    }

    [Theory]
    [InlineData(false, Commands.Shuffle)]
    [InlineData(true, Commands.Unshuffle)]
    public void ShuffleToggle_SendsTheOppositeOfTheCurrentState(bool enabled, string expected)
    {
        Assert.Equal(expected, TransportCommands.ForShuffleToggle(enabled));
    }

    private static readonly string[] AllRepeatModes = [Commands.RepeatOff, Commands.RepeatAll, Commands.RepeatOne];

    [Theory]
    [InlineData("off", Commands.RepeatAll)]
    [InlineData("all", Commands.RepeatOne)]
    [InlineData("one", Commands.RepeatOff)]
    [InlineData(null, Commands.RepeatAll)]
    public void RepeatCycle_AdvancesOffAllOne(string? mode, string expected)
    {
        Assert.Equal(expected, TransportCommands.ForRepeatCycle(mode, AllRepeatModes));
    }

    [Fact]
    public void RepeatCycle_SkipsAModeTheGroupDoesNotList()
    {
        // Without the skip, a group lacking repeat_one would leave the control disabled at
        // "all", with no way to turn repeat off again.
        var supported = new[] { Commands.RepeatOff, Commands.RepeatAll };

        Assert.Equal(Commands.RepeatOff, TransportCommands.ForRepeatCycle("all", supported));
    }

    [Fact]
    public void RepeatCycle_OffersNothingWhenNoOtherModeIsListed()
    {
        var supported = new[] { Commands.RepeatOff };

        var command = TransportCommands.ForRepeatCycle("off", supported);

        Assert.False(TransportCommands.IsSupported(supported, command));
    }

    [Fact]
    public void NothingIsSupported_BeforeTheServerReportsAList()
    {
        // The SDK treats a group with no supported_commands yet as permitting nothing and
        // drops the command; offering the control would make it a silent no-op.
        Assert.False(TransportCommands.IsSupported(null, Commands.Next));
    }

    [Fact]
    public void OnlyListedCommandsAreSupported()
    {
        var supported = new[] { Commands.Play, Commands.Pause };

        Assert.True(TransportCommands.IsSupported(supported, Commands.Pause));
        Assert.False(TransportCommands.IsSupported(supported, Commands.Next));
    }
}
