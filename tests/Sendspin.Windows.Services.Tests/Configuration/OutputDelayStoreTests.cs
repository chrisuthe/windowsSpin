using Microsoft.Extensions.Logging.Abstractions;
using Sendspin.Windows.Services.Configuration;
using Xunit;

namespace Sendspin.Windows.Services.Tests.Configuration;

public class OutputDelayStoreTests
{
    [Fact]
    public void Load_ReturnsTheSavedSetting()
    {
        var store = CreateStore(read: () => 250, write: _ => Task.CompletedTask);

        Assert.Equal(250, store.Load());
    }

    [Fact]
    public void Load_ReadsTheSettingEachTime()
    {
        // The slider can change the setting between connections, and the SDK loads on each one.
        var setting = 100.0;
        var store = CreateStore(read: () => setting, write: _ => Task.CompletedTask);

        Assert.Equal(100, store.Load());
        setting = 300;
        Assert.Equal(300, store.Load());
    }

    [Fact]
    public void Save_WritesTheSettingAndRaisesSaved()
    {
        var written = new List<double>();
        var raised = new List<double>();
        var store = CreateStore(
            read: () => 0,
            write: value =>
            {
                written.Add(value);
                return Task.CompletedTask;
            });
        store.Saved += raised.Add;

        store.Save(420);

        Assert.Equal([420], written);
        Assert.Equal([420], raised);
    }

    [Fact]
    public void Save_WhenTheWriteFails_DoesNotThrowAndStillRaisesSaved()
    {
        // The SDK has already applied and acknowledged the delay; the slider must still follow.
        var raised = new List<double>();
        var store = CreateStore(
            read: () => 0,
            write: _ => Task.FromException(new IOException("disk full")));
        store.Saved += raised.Add;

        store.Save(420);

        Assert.Equal([420], raised);
    }

    [Fact]
    public void Save_WhenTheWriteThrowsSynchronously_DoesNotThrow()
    {
        var store = CreateStore(read: () => 0, write: _ => throw new IOException("disk full"));

        store.Save(420);
    }

    private static OutputDelayStore CreateStore(Func<double> read, Func<double, Task> write)
        => new(read, write, NullLogger<OutputDelayStore>.Instance);
}
