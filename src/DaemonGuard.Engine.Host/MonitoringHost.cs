using System.Diagnostics;
using System.Text.Json;
using DaemonGuard.Collector;
using DaemonGuard.Database;
using DaemonGuard.Engine.Analytics;
using DaemonGuard.Engine.Communication;

namespace DaemonGuard.Engine.Host;

public sealed class MonitoringHost
{
    private static readonly TimeSpan PersistEvery = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ProcessRefresh = TimeSpan.FromSeconds(30);
    private const long MinPersistMemoryBytes = 20L * 1024 * 1024;
    private const double AlwaysPersistScore = 3.0;

    private sealed class PidState
    {
        public DateTime StartTimeUtc;
        public long ProcessId;
        public DateTime LastUpsert;
        public DateTime LastPersist;
        public string LastClassification = "";
    }

    private readonly EnginePipelineBridge _bridge = new();
    private readonly DatabaseRepository _repo;
    private readonly ProcessCollectorService _collector;
    private readonly EngineAnalyticsService _analytics;
    private readonly Dictionary<int, PidState> _state = new();

    public MonitoringHost(string dbPath)
    {
        _repo = new DatabaseRepository(dbPath);
        var adapter = new CollectorBridgeAdapter(_bridge);
        _collector = new ProcessCollectorService(adapter, adapter);
        _analytics = new EngineAnalyticsService(_bridge, OnEvaluatedAsync,
            ex => Trace.TraceError("DaemonGuard analytics sink error: " + ex));
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.WhenAll(
                Guard("collector", ct => _collector.RunAsync(ct), cancellationToken),
                Guard("analytics", ct => _analytics.RunAsync(ct), cancellationToken),
                Guard("maintenance", MaintenanceAsync, cancellationToken)).ConfigureAwait(false);
        }
        finally
        {
            _bridge.CompleteWriting();
            try { await _repo.ForceFlushAsync().ConfigureAwait(false); } catch { }
        }
    }

    private static async Task Guard(string name, Func<CancellationToken, Task> work, CancellationToken ct)
    {
        try { await work(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Trace.TraceError($"DaemonGuard {name} stopped: {ex}");
            Console.Error.WriteLine($"[{name}] stopped: {ex.Message}");
        }
    }

    private async ValueTask OnEvaluatedAsync(EngineEvaluation e, CancellationToken ct)
    {
        var t = e.Telemetry;
        if (t.WorkingSetMemoryBytes < MinPersistMemoryBytes && e.Score.WasteScore < AlwaysPersistScore) return;

        if (_state.Count > 1000)
            foreach (var pid in _state.Where(s => t.Timestamp - s.Value.LastPersist > TimeSpan.FromMinutes(2))
                                      .Select(s => s.Key).ToList())
                _state.Remove(pid);

        if (!_state.TryGetValue(t.ProcessId, out var st))
            _state[t.ProcessId] = st = new PidState { StartTimeUtc = ResolveStartTime(t.ProcessId) };

        bool changed = st.LastClassification != e.Score.Classification;
        if (!changed && t.Timestamp - st.LastPersist < PersistEvery) return;

        if (st.ProcessId == 0 || t.Timestamp - st.LastUpsert > ProcessRefresh)
        {
            st.ProcessId = await _repo.UpsertProcessAsync(
                t.ProcessId, st.StartTimeUtc, t.ProcessName,
                string.IsNullOrEmpty(t.ExecutablePath) ? null : t.ExecutablePath,
                owner: null, isProtected: false).ConfigureAwait(false);
            st.LastUpsert = t.Timestamp;
        }

        double memoryMb = t.WorkingSetMemoryBytes / 1024.0 / 1024.0;
        var sample = new ResourceSample
        {
            ProcessId = st.ProcessId,
            Timestamp = t.Timestamp,
            CpuPercent = t.CpuUsage,
            MemoryMb = memoryMb,
            DiskIoReadBytes = t.DiskReadBytesPerSec,
            DiskIoWriteBytes = t.DiskWriteBytesPerSec
        };

        var score = new WasteScoreRecord
        {
            WasteScore = e.Score.WasteScore,
            RiskScore = t.HasVisibleWindow ? 6.0 : 2.0,
            Classification = ToDatabaseLabel(e.Score.Classification),
            ExplanationText = $"{e.Score.Recommendation} Memory trend: {e.Trend.TrendStatus}. {e.Trend.Insight}",
            BreakdownJson = JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["Memory (MB)"] = Math.Round(memoryMb, 1),
                ["CPU (%)"] = Math.Round(t.CpuUsage, 1),
                ["Disk I/O (KB/s)"] = Math.Round((t.DiskReadBytesPerSec + t.DiskWriteBytesPerSec) / 1024.0, 1),
                ["Visible window"] = t.HasVisibleWindow,
                ["Memory trend"] = e.Trend.TrendStatus,
                ["Growth (MB/tick)"] = Math.Round(e.Trend.GrowthRateBytesPerTick / 1024.0 / 1024.0, 2)
            })
        };

        await _repo.QueueSampleAsync(sample, score).ConfigureAwait(false);
        st.LastPersist = t.Timestamp;
        st.LastClassification = e.Score.Classification;
    }

    private static string ToDatabaseLabel(string classification) => classification switch
    {
        "BACKGROUND_IDLE_WASTE" => "Potentially Wasteful",
        "MODERATE_DRAIN" => "Suspicious",
        _ => "Healthy"
    };

    private static DateTime ResolveStartTime(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.StartTime.ToUniversalTime();
        }
        catch { return DateTime.UnixEpoch; }
    }

    private async Task MaintenanceAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        var nextRetention = DateTime.UtcNow;
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            try
            {
                await _repo.ForceFlushAsync().ConfigureAwait(false);
                if (DateTime.UtcNow >= nextRetention)
                {
                    await _repo.RunRetentionCleanupAsync().ConfigureAwait(false);
                    nextRetention = DateTime.UtcNow.AddHours(1);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Trace.TraceError("DaemonGuard maintenance error: " + ex);
            }
        }
    }
}
