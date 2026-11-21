namespace DaemonGuard.Collector;

public readonly record struct ProcessTelemetry(
    int ProcessId,
    string ProcessName,
    string? ExecutablePath,
    double CpuUsage,
    double DiskReadBytesPerSec,
    double DiskWriteBytesPerSec,
    long WorkingSetMemoryBytes,
    bool HasVisibleWindow,
    DateTime TimestampUtc);

// Integration adapter: replace with your team's bridge interface if its signature differs.
public interface IEngineIngestor
{
    ValueTask SubmitBatchAsync(IReadOnlyList<ProcessTelemetry> batch, CancellationToken cancellationToken);
}

public interface IEngineFeedback
{
    // Return a thread-safe snapshot or a concurrently readable collection.
    IReadOnlySet<string> GetWhitelistedPaths();
}

public sealed class CollectorOptions
{
    public TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(2);
    // Resolving paths is relatively expensive. Cache successful resolutions by process identity.
    public bool CollectExecutablePaths { get; init; } = true;
}
