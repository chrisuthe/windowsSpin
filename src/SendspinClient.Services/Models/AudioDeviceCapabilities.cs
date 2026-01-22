// <copyright file="AudioDeviceCapabilities.cs" company="Sendspin Windows Client">
// Licensed under the MIT License. See LICENSE file in the project root.
// </copyright>

namespace SendspinClient.Services.Models;

/// <summary>
/// Audio capabilities discovered from a WASAPI device.
/// </summary>
public record AudioDeviceCapabilities
{
    /// <summary>
    /// Native sample rate of the device's mixer (e.g., 48000, 96000, 192000).
    /// </summary>
    public int NativeSampleRate { get; init; } = 48000;

    /// <summary>
    /// Native bit depth of the device's mixer (e.g., 16, 24, 32).
    /// </summary>
    public int NativeBitDepth { get; init; } = 16;

    /// <summary>
    /// Number of channels supported.
    /// </summary>
    public int Channels { get; init; } = 2;

    /// <summary>
    /// Whether the device supports high-resolution audio (>48kHz or >16-bit).
    /// </summary>
    public bool IsHighResolution => NativeSampleRate > 48000 || NativeBitDepth > 16;

    /// <summary>
    /// Returns a display string for the capabilities (e.g., "96kHz/24-bit Hi-Res").
    /// </summary>
    public string ToDisplayString()
    {
        var hiRes = IsHighResolution ? " Hi-Res" : "";
        return $"{NativeSampleRate / 1000.0:0.#}kHz/{NativeBitDepth}-bit{hiRes}";
    }
}
