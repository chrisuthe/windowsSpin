// <copyright file="SyncHealthLog.cs" company="Sendspin Windows Client">
// Licensed under the MIT License. See LICENSE file in the project root.
// </copyright>

using System.Globalization;
using System.Text;

namespace SendspinClient.Services.Diagnostics;

/// <summary>
/// Always-on, size-capped writer for sync-health episode records.
/// Independent of the normal logging pipeline so episode evidence survives default config
/// (EnableFileLogging=false). Never throws: diagnostics must not break playback.
/// </summary>
/// <remarks>
/// Writes happen on the monitor's timer thread (background) — never the audio or UI thread.
/// Cap is checked before each write; on overflow the file rotates to sync-health.1.log
/// (one backup kept, ≤ 2× cap on disk).
/// </remarks>
public sealed class SyncHealthLog
{
    private const string FileName = "sync-health.log";
    private const string BackupFileName = "sync-health.1.log";
    private const int DefaultMaxBytes = 1024 * 1024; // 1 MB

    private readonly string _directory;
    private readonly int _maxBytes;
    private readonly object _writeLock = new();

    /// <summary>Initializes a new instance of the <see cref="SyncHealthLog"/> class.</summary>
    /// <param name="directory">Directory where sync-health.log is written.</param>
    /// <param name="maxBytes">Maximum file size in bytes before rotation (default: 1 MB).</param>
    public SyncHealthLog(string directory, int maxBytes = DefaultMaxBytes)
    {
        _directory = directory;
        _maxBytes = maxBytes;
    }

    /// <summary>Writes the session context line (app/SDK versions, device, format, buffer config).</summary>
    /// <param name="context">Free-form context string, e.g. "app=3.4.0 sdk=9.0.2 device=Speakers".</param>
    public void WriteSessionHeader(string context)
    {
        Append($"[{Timestamp()}] SESSION {context}");
    }

    /// <summary>Writes one classified episode block.</summary>
    /// <param name="e">The closed episode record.</param>
    /// <param name="c">The classification assigned to the episode.</param>
    public void WriteEpisode(EpisodeRecord e, SyncHealthClassification c)
    {
        var ci = CultureInfo.InvariantCulture;
        var verdict = c.Verdict switch
        {
            SyncHealthVerdict.NetworkStarvation => "network-starvation",
            SyncHealthVerdict.ClockSyncInstability => "clock-sync-instability",
            SyncHealthVerdict.DeviceClockSkew => "device-clock-skew",
            SyncHealthVerdict.LocalTiming => "local-timing",
            _ => "unknown",
        };

        var sb = new StringBuilder();
        sb.AppendLine(string.Create(ci,
            $"[{e.StartedAt:yyyy-MM-dd HH:mm:ss}] EPISODE verdict={verdict} duration={e.DurationSeconds:F1}s"));
        sb.AppendLine(string.Create(ci,
            $"  drops={e.Drops} inserts={e.Inserts} underruns={e.Underruns} reanchors={e.Reanchors} maxSyncErr={e.MaxAbsSyncErrorMs:+0.0;-0.0}ms"));
        sb.AppendLine(string.Create(ci,
            $"  minBuffer={e.MinBufferedMs:F0}ms/{e.TargetMs:F0}ms preRollMinBuffer={e.PreRollMinBufferedMs:F0}ms maxChunkGap={e.MaxChunkGapMs:F0}ms chunkAge={e.MaxChunkAgeMs:F0}ms bytesPerSec={e.BytesPerSecond:F0}"));
        sb.AppendLine(string.Create(ci,
            $"  rttJitter={e.MaxRttJitterMs:F1}ms offsetTravel={e.OffsetTravelMs:F1}ms dirFlips={e.DirectionFlips} cbGaps={e.CallbackGaps} rateSaturated={e.RateSaturated}"));
        sb.Append(string.Create(ci, $"  evidence=\"{c.Evidence}\""));
        if (c.EstimatedSkewPpm is { } ppm)
        {
            sb.Append(string.Create(ci, $" estSkewPpm={ppm:F0}"));
        }

        Append(sb.ToString());
    }

    private static string Timestamp() =>
        DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    private void Append(string block)
    {
        lock (_writeLock)
        {
            try
            {
                Directory.CreateDirectory(_directory);
                var path = Path.Combine(_directory, FileName);

                var info = new FileInfo(path);
                if (info.Exists && info.Length >= _maxBytes)
                {
                    var backup = Path.Combine(_directory, BackupFileName);
                    File.Delete(backup);         // no-op if missing
                    File.Move(path, backup);
                }

                File.AppendAllText(path, block + Environment.NewLine, Encoding.UTF8);
            }
            catch
            {
                // Diagnostics must never break playback; swallow and continue in-memory only.
            }
        }
    }
}
