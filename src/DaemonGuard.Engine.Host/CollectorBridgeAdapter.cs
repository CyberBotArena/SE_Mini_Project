using DaemonGuard.Engine.Communication;

namespace DaemonGuard.Engine.Host;

internal sealed class CollectorBridgeAdapter : DaemonGuard.Collector.IEngineIngestor, DaemonGuard.Collector.IEngineFeedback
{
    private readonly EnginePipelineBridge _bridge;

    public CollectorBridgeAdapter(EnginePipelineBridge bridge) => _bridge = bridge;

    public ValueTask SubmitBatchAsync(IReadOnlyList<DaemonGuard.Collector.ProcessTelemetry> batch, CancellationToken cancellationToken)
    {
        var converted = new List<ProcessTelemetry>(batch.Count);
        foreach (var t in batch)
        {
            converted.Add(new ProcessTelemetry(
                ProcessId: t.ProcessId,
                ProcessName: t.ProcessName,
                ExecutablePath: t.ExecutablePath ?? string.Empty,
                CpuUsage: (float)t.CpuUsage,
                WorkingSetMemoryBytes: t.WorkingSetMemoryBytes,
                DiskReadBytesPerSec: (long)t.DiskReadBytesPerSec,
                DiskWriteBytesPerSec: (long)t.DiskWriteBytesPerSec,
                HasVisibleWindow: t.HasVisibleWindow,
                Timestamp: t.TimestampUtc));
        }
        return _bridge.SubmitBatchAsync(converted, cancellationToken);
    }

    public IReadOnlySet<string> GetWhitelistedPaths() =>
        new HashSet<string>(_bridge.GetWhitelistedPaths(), StringComparer.OrdinalIgnoreCase);
}
