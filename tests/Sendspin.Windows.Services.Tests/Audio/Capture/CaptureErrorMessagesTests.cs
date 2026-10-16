// <copyright file="CaptureErrorMessagesTests.cs" company="Sendspin Windows Client">
// Licensed under the MIT License. See LICENSE file in the project root.
// </copyright>

using System.Runtime.InteropServices;
using Sendspin.Windows.Services.Audio.Capture;
using Xunit;

namespace Sendspin.Windows.Services.Tests.Audio.Capture;

public class CaptureErrorMessagesTests
{
    [Fact]
    public void PrivacyDenial_PointsAtTheWindowsSetting()
    {
        var message = CaptureErrorMessages.Describe("Line In", new UnauthorizedAccessException());

        Assert.Contains("Privacy & security › Microphone", message);
    }

    [Theory]
    [InlineData(CaptureErrorMessages.DeviceInUse, "Line In is in use by another app.")]
    [InlineData(CaptureErrorMessages.DeviceInvalidated, "Line In was disconnected.")]
    [InlineData(unchecked((int)0x80004005), "Line In stopped unexpectedly.")]
    public void DeviceErrors_NameTheInput(int hresult, string expected)
    {
        Assert.Equal(expected, CaptureErrorMessages.Describe("Line In", new COMException("x", hresult)));
    }
}
