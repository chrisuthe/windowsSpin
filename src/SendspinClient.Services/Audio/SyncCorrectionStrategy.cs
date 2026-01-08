// <copyright file="SyncCorrectionStrategy.cs" company="Sendspin Windows Client">
// Licensed under the MIT License. See LICENSE file in the project root.
// </copyright>

namespace SendspinClient.Services.Audio;

/// <summary>
/// Specifies the strategy for correcting audio sync drift.
/// </summary>
public enum SyncCorrectionStrategy
{
    /// <summary>
    /// Use both playback rate resampling (for small errors) and frame drop/insert (for larger errors).
    /// This is the default and provides smooth correction for small drifts while handling larger errors quickly.
    /// Audio passes through the WDL resampler for rate adjustment.
    /// </summary>
    Combined,

    /// <summary>
    /// Use only frame drop/insert for sync correction. No resampling is performed.
    /// Audio passes directly to WASAPI without any resampler in the chain.
    /// This matches the Python CLI behavior and avoids any resampler artifacts.
    /// May produce occasional clicks when frames are dropped or inserted.
    /// </summary>
    DropInsertOnly,
}
