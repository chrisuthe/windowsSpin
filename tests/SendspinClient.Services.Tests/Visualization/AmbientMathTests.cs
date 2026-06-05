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
    [InlineData(32767, 0.4999924, 1e-6)]
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

    [Fact]
    public void Ease_ZeroDt_ReturnsCurrent()
    {
        Assert.Equal(0.2, AmbientMath.Ease(0.2, 1.0, dtSeconds: 0.0, timeConstantSeconds: 0.5));
    }

    [Fact]
    public void Ease_MovesTowardTarget()
    {
        // alpha = 1 - e^(-0.1/0.5) = 1 - e^(-0.2) = 0.1812692...
        var next = AmbientMath.Ease(0.0, 1.0, dtSeconds: 0.1, timeConstantSeconds: 0.5);
        Assert.Equal(0.1812692, next, 1e-6);
    }

    [Fact]
    public void Ease_LargeDt_ApproachesTarget()
    {
        var next = AmbientMath.Ease(0.0, 1.0, dtSeconds: 10.0, timeConstantSeconds: 0.5);
        Assert.True(next > 0.99, "after many time constants it should be near target");
    }

    [Fact]
    public void Ease_ZeroTimeConstant_SnapsToTarget()
    {
        Assert.Equal(1.0, AmbientMath.Ease(0.0, 1.0, dtSeconds: 0.016, timeConstantSeconds: 0.0));
    }

    [Fact]
    public void Decay_AfterOneHalfLife_IsHalf()
    {
        var v = AmbientMath.Decay(1.0, dtSeconds: 0.25, halfLifeSeconds: 0.25);
        Assert.Equal(0.5, v, 0.001);
    }

    [Fact]
    public void Decay_ZeroDt_ReturnsCurrent()
    {
        Assert.Equal(0.8, AmbientMath.Decay(0.8, dtSeconds: 0.0, halfLifeSeconds: 0.3));
    }

    [Fact]
    public void Decay_NonPositiveHalfLife_ReturnsZero()
    {
        Assert.Equal(0.0, AmbientMath.Decay(1.0, dtSeconds: 0.016, halfLifeSeconds: 0.0));
    }

    [Theory]
    [InlineData(0.0, 0.0, 0.82)]
    [InlineData(1.0, 0.0, 1.32)]
    [InlineData(1.0, 1.0, 1.67)]
    [InlineData(1.0, 2.0, 1.67)]
    public void BlobScale_MapsEnergyAndPulse(double energy, double pulse, double expected)
    {
        Assert.Equal(expected, AmbientMath.BlobScale(energy, pulse), 0.0001);
    }

    [Theory]
    [InlineData(0.0, 0.55)]
    [InlineData(1.0, 0.97)]
    public void BlobOpacity_MapsEnergy(double energy, double expected)
    {
        Assert.Equal(expected, AmbientMath.BlobOpacity(energy), 0.0001);
    }

    [Fact]
    public void BlobScale_ClampsNegativeInputs()
    {
        Assert.Equal(0.82, AmbientMath.BlobScale(-1.0, -1.0), 0.0001);
    }

    [Fact]
    public void Ease_NegativeDt_ReturnsCurrent()
    {
        Assert.Equal(0.3, AmbientMath.Ease(0.3, 1.0, dtSeconds: -0.1, timeConstantSeconds: 0.5));
    }

    [Fact]
    public void Decay_NegativeDt_ReturnsCurrent()
    {
        Assert.Equal(0.8, AmbientMath.Decay(0.8, dtSeconds: -0.1, halfLifeSeconds: 0.3));
    }

    [Fact]
    public void BlobOpacity_ClampsOutOfRange()
    {
        Assert.Equal(0.55, AmbientMath.BlobOpacity(-1.0), 0.0001);
        Assert.Equal(0.97, AmbientMath.BlobOpacity(2.0), 0.0001);
    }
}
