// <copyright file="TransportCommands.cs" company="Sendspin Windows Client">
// Licensed under the MIT License. See LICENSE file in the project root.
// </copyright>

using Sendspin.SDK.Models;
using Sendspin.SDK.Protocol.Messages;

namespace Sendspin.Windows.Services.Playback;

/// <summary>
/// Maps the transport controls to the controller command each one sends, and decides from the
/// group's <c>supported_commands</c> whether that command may be offered.
/// </summary>
/// <remarks>
/// The SDK drops a controller command the group does not list, with only a log warning, so a
/// control left enabled for an unlisted command is a silent no-op. The view model enables each
/// control from <see cref="IsSupported"/> applied to the same mapping it sends with.
/// </remarks>
public static class TransportCommands
{
    /// <summary>Gets the command the play/pause control sends in the given state.</summary>
    public static string ForPlayPause(PlaybackState state) =>
        state == PlaybackState.Playing ? Commands.Pause : Commands.Play;

    /// <summary>Gets the command the shuffle control sends: the opposite of the current state.</summary>
    public static string ForShuffleToggle(bool shuffleEnabled) =>
        shuffleEnabled ? Commands.Unshuffle : Commands.Shuffle;

    /// <summary>
    /// Gets the command the repeat control sends: the next mode in off → all → one → off that
    /// the group lists, so a group without <c>repeat_one</c> cycles off → all → off. When no
    /// other mode is listed this is the unlisted next step, which leaves the control disabled.
    /// </summary>
    public static string ForRepeatCycle(string? repeatMode, IReadOnlyCollection<string>? supportedCommands)
    {
        string[] cycle = [Commands.RepeatAll, Commands.RepeatOne, Commands.RepeatOff];
        var next = repeatMode switch
        {
            "all" => 1,
            "one" => 2,
            _ => 0,
        };

        // Two candidates: the third step of the cycle is the mode already set.
        for (var step = 0; step < cycle.Length - 1; step++)
        {
            var candidate = cycle[(next + step) % cycle.Length];
            if (IsSupported(supportedCommands, candidate))
            {
                return candidate;
            }
        }

        return cycle[next];
    }

    /// <summary>
    /// Whether the group lists <paramref name="command"/>. A group that has not reported a list
    /// yet supports nothing, matching the SDK's gate.
    /// </summary>
    public static bool IsSupported(IReadOnlyCollection<string>? supportedCommands, string command) =>
        supportedCommands?.Contains(command) == true;
}
