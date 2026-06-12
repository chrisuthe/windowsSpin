// <copyright file="SyncHealthLogTests.cs" company="Sendspin Windows Client">
// Licensed under the MIT License. See LICENSE file in the project root.
// </copyright>

using SendspinClient.Services.Diagnostics;
using Xunit;

namespace SendspinClient.Services.Tests.Diagnostics;

public sealed class SyncHealthLogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sendspin-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static EpisodeRecord SampleEpisode() => new()
    {
        StartedAt = new DateTimeOffset(2026, 6, 11, 21, 14, 32, TimeSpan.Zero),
        DurationSeconds = 8.2,
        Drops = 18234,
        Underruns = 3,
        MaxAbsSyncErrorMs = 142.1,
        MinBufferedMs = 12,
        TargetMs = 250,
        MaxChunkGapMs = 2310,
        MaxChunkAgeMs = 2280,
        PreRollMinBufferedMs = 12,
        SampleRate = 48000,
        Channels = 2,
    };

    [Fact]
    public void WriteSessionHeader_CreatesFileWithHeader()
    {
        var log = new SyncHealthLog(_dir);
        log.WriteSessionHeader("app=3.4.0 sdk=9.0.2 device=Speakers format=48000Hz/2ch target=250ms");

        var content = File.ReadAllText(Path.Combine(_dir, "sync-health.log"));
        Assert.Contains("SESSION", content);
        Assert.Contains("sdk=9.0.2", content);
    }

    [Fact]
    public void WriteEpisode_ContainsVerdictAndKeyValues()
    {
        var log = new SyncHealthLog(_dir);
        var classification = EpisodeClassifier.Classify(SampleEpisode());
        log.WriteEpisode(SampleEpisode(), classification);

        var content = File.ReadAllText(Path.Combine(_dir, "sync-health.log"));
        Assert.Contains("EPISODE verdict=network-starvation", content);
        Assert.Contains("duration=8.2s", content);
        Assert.Contains("drops=18234", content);
    }

    [Fact]
    public void ExceedingCap_RotatesToBackupFile()
    {
        var log = new SyncHealthLog(_dir, maxBytes: 2048); // tiny cap for test
        var classification = EpisodeClassifier.Classify(SampleEpisode());
        for (var i = 0; i < 50; i++)
        {
            log.WriteEpisode(SampleEpisode(), classification);
        }

        Assert.True(File.Exists(Path.Combine(_dir, "sync-health.1.log")), "backup should exist");
        var mainSize = new FileInfo(Path.Combine(_dir, "sync-health.log")).Length;
        Assert.True(mainSize <= 2048 + 1024, $"main log should stay near cap, was {mainSize}");
    }

    [Fact]
    public void WriteFailure_DoesNotThrow()
    {
        // Point at an un-creatable path (file used as directory)
        var filePath = Path.Combine(Path.GetTempPath(), "sendspin-tests-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(filePath, "block");
        var log = new SyncHealthLog(Path.Combine(filePath, "nested"));
        var ex = Record.Exception(() => log.WriteSessionHeader("header"));
        Assert.Null(ex);
        File.Delete(filePath);
    }
}
