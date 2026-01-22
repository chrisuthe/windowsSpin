// <copyright file="AudioDeviceInfo.cs" company="Sendspin Windows Client">
// Licensed under the MIT License. See LICENSE file in the project root.
// </copyright>

using SendspinClient.Services.Models;

namespace SendspinClient.Models;

/// <summary>
/// Represents an audio output device for the playback device dropdown.
/// </summary>
public class AudioDeviceInfo
{
    /// <summary>
    /// Gets or sets the device ID used by WASAPI.
    /// Null or empty string represents the system default device.
    /// </summary>
    public string? DeviceId { get; set; }

    /// <summary>
    /// Gets or sets the friendly display name for the device.
    /// </summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// Gets whether this represents the system default device.
    /// </summary>
    public bool IsDefault => string.IsNullOrEmpty(DeviceId);

    /// <summary>
    /// Gets or sets the discovered audio capabilities for this device.
    /// Null until capabilities have been queried.
    /// </summary>
    public AudioDeviceCapabilities? Capabilities { get; set; }

    /// <summary>
    /// Creates the default device entry.
    /// </summary>
    public static AudioDeviceInfo Default => new()
    {
        DeviceId = null,
        DisplayName = "System Default",
    };

    /// <inheritdoc/>
    public override string ToString() => DisplayName;
}
