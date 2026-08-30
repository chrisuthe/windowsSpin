using Sendspin.Windows.Services.Visualization;
using Xunit;

namespace Sendspin.Windows.Services.Tests.Visualization;

public class RenderLoopGateTests
{
    [Fact]
    public void Update_True_StartsLoop()
    {
        var starts = 0;
        var gate = new RenderLoopGate(() => starts++, () => { });

        gate.Update(true);

        Assert.Equal(1, starts);
    }

    [Fact]
    public void Update_TrueTwice_StartsOnlyOnce()
    {
        var starts = 0;
        var gate = new RenderLoopGate(() => starts++, () => { });

        gate.Update(true);
        gate.Update(true);

        Assert.Equal(1, starts);
    }

    [Fact]
    public void Update_FalseWhenNeverStarted_DoesNotStop()
    {
        var stops = 0;
        var gate = new RenderLoopGate(() => { }, () => stops++);

        gate.Update(false);

        Assert.Equal(0, stops);
    }

    [Fact]
    public void Update_FalseAfterStart_StopsLoop()
    {
        var stops = 0;
        var gate = new RenderLoopGate(() => { }, () => stops++);

        gate.Update(true);
        gate.Update(false);

        Assert.Equal(1, stops);
    }

    [Fact]
    public void Update_FalseTwice_StopsOnlyOnce()
    {
        var stops = 0;
        var gate = new RenderLoopGate(() => { }, () => stops++);

        gate.Update(true);
        gate.Update(false);
        gate.Update(false);

        Assert.Equal(1, stops);
    }

    [Fact]
    public void Update_Toggling_StartsAndStopsEachTransition()
    {
        var starts = 0;
        var stops = 0;
        var gate = new RenderLoopGate(() => starts++, () => stops++);

        gate.Update(true);
        gate.Update(false);
        gate.Update(true);
        gate.Update(false);

        Assert.Equal(2, starts);
        Assert.Equal(2, stops);
    }

    [Fact]
    public void IsRunning_IsFalseBeforeFirstUpdate()
    {
        var gate = new RenderLoopGate(() => { }, () => { });

        Assert.False(gate.IsRunning);
    }

    [Fact]
    public void IsRunning_TracksLatestTransition()
    {
        var gate = new RenderLoopGate(() => { }, () => { });

        gate.Update(true);
        Assert.True(gate.IsRunning);

        gate.Update(false);
        Assert.False(gate.IsRunning);
    }
}
