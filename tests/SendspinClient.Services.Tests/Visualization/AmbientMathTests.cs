using SendspinClient.Services.Visualization;
using Xunit;

namespace SendspinClient.Services.Tests.Visualization;

public class AmbientMathTests
{
    [Fact]
    public void NormalizeLoudness_Null_ReturnsZero()
    {
        Assert.Equal(0.0, AmbientMath.NormalizeLoudness(null));
    }

    [Theory]
    [InlineData(0, 0.0)]
    [InlineData(65535, 1.0)]
    [InlineData(32767, 0.49999, 0.001)]
    public void NormalizeLoudness_MapsRawToUnitRange(int raw, double expected, double tolerance = 1e-9)
    {
        Assert.Equal(expected, AmbientMath.NormalizeLoudness(raw), tolerance);
    }

    [Theory]
    [InlineData(-100)]
    [InlineData(99999)]
    public void NormalizeLoudness_ClampsOutOfRange(int raw)
    {
        var v = AmbientMath.NormalizeLoudness(raw);
        Assert.InRange(v, 0.0, 1.0);
    }
}
