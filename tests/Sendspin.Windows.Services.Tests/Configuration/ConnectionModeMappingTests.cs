using Sendspin.SDK.Client;
using Sendspin.Windows.Services.Configuration;
using Xunit;

namespace Sendspin.Windows.Services.Tests.Configuration;

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
    public void FromConfigValue_NeverReturnsAuto(string? configValue)
    {
        Assert.NotEqual(ConnectionMode.Auto, ConnectionModeMapping.FromConfigValue(configValue));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Auto")]
    [InlineData("Advertise Only")]
    [InlineData("Let servers connect to me")]
    [InlineData("I choose a server")]
    public void FromDisplayName_NeverReturnsAuto(string? displayName)
    {
        Assert.NotEqual(ConnectionMode.Auto, ConnectionModeMapping.FromDisplayName(displayName));
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
    public void ToConfigValue_Auto_CoercedToAdvertiseOnly()
    {
        // Auto is unreachable through our own mapping, but the SDK enum still allows it.
        // Coerce rather than emit a value we would refuse to read back.
        Assert.Equal("AdvertiseOnly", ConnectionModeMapping.ToConfigValue(ConnectionMode.Auto));
    }

    [Fact]
    public void ToDisplayName_Auto_CoercedToAdvertiseOnlyLabel()
    {
        Assert.Equal(
            ConnectionModeMapping.AdvertiseOnlyDisplayName,
            ConnectionModeMapping.ToDisplayName(ConnectionMode.Auto));
    }

    [Fact]
    public void DisplayNames_ContainsExactlyTheTwoSupportedModes()
    {
        Assert.Equal(
            new[] { ConnectionModeMapping.AdvertiseOnlyDisplayName, ConnectionModeMapping.DiscoverOnlyDisplayName },
            ConnectionModeMapping.DisplayNames);
    }
}
