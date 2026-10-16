// <copyright file="CaptureTimeTests.cs" company="Sendspin Windows Client">
// Licensed under the MIT License. See LICENSE file in the project root.
// </copyright>

using Sendspin.Windows.Services.Audio.Capture;
using Xunit;

namespace Sendspin.Windows.Services.Tests.Audio.Capture;

public class CaptureTimeTests
{
    [Theory]
    // Stopwatch at 10 MHz: 5 s now, packet captured at 4.99 s → 10 ms old.
    [InlineData(10_000_000L, 50_000_000L, 49_900_000L, 10_000L)]
    // Stopwatch at 3 MHz: 5 s now, packet at 4.9 s → 100 ms old.
    [InlineData(3_000_000L, 15_000_000L, 49_000_000L, 100_000L)]
    public void APacketsTimestamp_IsNowMinusItsAge(long frequency, long nowTicks, long qpc100ns, long expectedAgeUs)
    {
        const long nowUs = 1_700_000_000_000_000;

        Assert.Equal(nowUs - expectedAgeUs, CaptureTime.ToTimerMicroseconds(qpc100ns, nowUs, nowTicks, frequency));
    }
}
