// <copyright file="FakeTimer.cs" company="Sendspin Windows Client">
// Licensed under the MIT License. See LICENSE file in the project root.
// </copyright>

using Sendspin.SDK.Synchronization;

namespace Sendspin.Windows.Services.Tests.Audio.Capture;

/// <summary>A clock the test moves by hand.</summary>
internal sealed class FakeTimer : IHighPrecisionTimer
{
    public long Now { get; set; }

    public long GetCurrentTimeMicroseconds() => Now;

    public long GetElapsedMicroseconds(long fromTimeMicroseconds) => Now - fromTimeMicroseconds;
}
