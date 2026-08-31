using Sendspin.SDK.Client;
using Sendspin.Windows.Services.Configuration;
using Xunit;

namespace Sendspin.Windows.Services.Tests.Configuration;

// SDK 10 removed ConnectionMode.Auto and made AdvertiseOnly the zero value, so the legacy mode
// is no longer representable and the CS0618 suppression these tests needed on 9.3.x is gone.
// The properties it guarded still matter, now expressed without naming Auto: every input
// resolves to one of the two supported modes, and an out-of-range cast is coerced rather than
// round-tripped.
//
// The legacy *config string* "Auto" is a separate concern and still tested below — the enum
// change does not touch it, because upgrading installs still carry that value on disk.
public class ConnectionModeMappingTests
{
    [Theory]
    [InlineData("AdvertiseOnly")]
    [InlineData("DiscoverOnly")]
    public void FromConfigValue_RoundTripsKnownValues(string configValue)
    {
        var mode = ConnectionModeMapping.FromConfigValue(configValue);
        Assert.Equal(configValue, ConnectionModeMapping.ToConfigValue(mode));
    }

    [Fact]
    public void FromConfigValue_DiscoverOnly_ReturnsDiscoverOnly()
    {
        Assert.Equal(ConnectionMode.DiscoverOnly, ConnectionModeMapping.FromConfigValue("DiscoverOnly"));
    }

    [Fact]
    public void FromConfigValue_AdvertiseOnly_ReturnsAdvertiseOnly()
    {
        Assert.Equal(ConnectionMode.AdvertiseOnly, ConnectionModeMapping.FromConfigValue("AdvertiseOnly"));
    }

    [Fact]
    public void FromConfigValue_LegacyAuto_MigratesToAdvertiseOnly()
    {
        Assert.Equal(ConnectionMode.AdvertiseOnly, ConnectionModeMapping.FromConfigValue("Auto"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Nonsense")]
    [InlineData("advertiseonly")]
    public void FromConfigValue_UnrecognizedInput_DefaultsToAdvertiseOnly(string? configValue)
    {
        Assert.Equal(ConnectionMode.AdvertiseOnly, ConnectionModeMapping.FromConfigValue(configValue));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Auto")]
    [InlineData("Nonsense")]
    [InlineData("AdvertiseOnly")]
    [InlineData("DiscoverOnly")]
    public void FromConfigValue_AlwaysReturnsASupportedMode(string? configValue)
    {
        Assert.Contains(
            ConnectionModeMapping.FromConfigValue(configValue),
            new[] { ConnectionMode.AdvertiseOnly, ConnectionMode.DiscoverOnly });
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Auto")]
    [InlineData("Advertise Only")]
    [InlineData("Let servers connect to me")]
    [InlineData("I choose a server")]
    public void FromDisplayName_AlwaysReturnsASupportedMode(string? displayName)
    {
        Assert.Contains(
            ConnectionModeMapping.FromDisplayName(displayName),
            new[] { ConnectionMode.AdvertiseOnly, ConnectionMode.DiscoverOnly });
    }

    [Fact]
    public void FromDisplayName_DiscoverOnlyLabel_ReturnsDiscoverOnly()
    {
        Assert.Equal(
            ConnectionMode.DiscoverOnly,
            ConnectionModeMapping.FromDisplayName(ConnectionModeMapping.DiscoverOnlyDisplayName));
    }

    [Fact]
    public void FromDisplayName_UnrecognizedLabel_DefaultsToAdvertiseOnly()
    {
        Assert.Equal(ConnectionMode.AdvertiseOnly, ConnectionModeMapping.FromDisplayName("Advertise Only"));
    }

    [Theory]
    [InlineData(ConnectionMode.AdvertiseOnly)]
    [InlineData(ConnectionMode.DiscoverOnly)]
    public void ToDisplayName_RoundTrips(ConnectionMode mode)
    {
        Assert.Equal(mode, ConnectionModeMapping.FromDisplayName(ConnectionModeMapping.ToDisplayName(mode)));
    }

    [Fact]
    public void ToConfigValue_UnknownMode_CoercedToAdvertiseOnly()
    {
        // Nothing in the enum is invalid any more — SDK 10 removed Auto — but an out-of-range
        // cast still reaches this method (a stale persisted int, a future enum member on an
        // older build). Coerce rather than emit a value we would refuse to read back.
        Assert.Equal("AdvertiseOnly", ConnectionModeMapping.ToConfigValue((ConnectionMode)999));
    }

    [Fact]
    public void ToDisplayName_UnknownMode_CoercedToAdvertiseOnlyLabel()
    {
        Assert.Equal(
            ConnectionModeMapping.AdvertiseOnlyDisplayName,
            ConnectionModeMapping.ToDisplayName((ConnectionMode)999));
    }

    [Fact]
    public void DisplayNames_ContainsExactlyTheTwoSupportedModes()
    {
        Assert.Equal(
            new[] { ConnectionModeMapping.AdvertiseOnlyDisplayName, ConnectionModeMapping.DiscoverOnlyDisplayName },
            ConnectionModeMapping.DisplayNames);
    }
}
