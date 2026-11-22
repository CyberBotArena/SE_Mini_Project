using System.Collections.Concurrent;
using System.Threading.Channels;

namespace DaemonGuard.Engine.Communication;

public sealed record ProcessTelemetry(
    int ProcessId,
    string ProcessName,
    string ExecutablePath,
    float CpuUsage,
    long WorkingSetMemoryBytes,
    long DiskReadBytesPerSec,
    long DiskWriteBytesPerSec,
    bool HasVisibleWindow,
    DateTime Timestamp
);

public interface IEngineIngestor
{
    ValueTask SubmitTelemetryAsync(ProcessTelemetry telemetry, CancellationToken cancellationToken = default);
    ValueTask SubmitBatchAsync(IEnumerable<ProcessTelemetry> batch, CancellationToken cancellationToken = default);
}

public interface IEngineFeedback
{
    IReadOnlyCollection<int> GetActiveWatchlistPids();
    void UpdateWatchlist(IEnumerable<int> targetPids);
    IReadOnlyCollection<string> GetWhitelistedPaths();
    void UpdatePathWhitelist(IEnumerable<string> paths);
}

public sealed class EnginePipelineBridge : IEngineIngestor, IEngineFeedback, IDisposable
{
    private readonly Channel<ProcessTelemetry> _telemetryChannel;
    private readonly ConcurrentDictionary<int, byte> _activeWatchlistPids;
    private readonly ConcurrentDictionary<string, byte> _whitelistedPaths;
    private bool _disposed;

    public EnginePipelineBridge(int capacity = 10_000)
    {
        var options = new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleWriter = false,
            SingleReader = true
        };

        _telemetryChannel = Channel.CreateBounded<ProcessTelemetry>(options);
        _activeWatchlistPids = new ConcurrentDictionary<int, byte>();
        _whitelistedPaths = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
    }

    public ChannelReader<ProcessTelemetry> Reader => _telemetryChannel.Reader;

    public async ValueTask SubmitTelemetryAsync(ProcessTelemetry telemetry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(telemetry);
        await _telemetryChannel.Writer.WriteAsync(telemetry, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask SubmitBatchAsync(IEnumerable<ProcessTelemetry> batch, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        foreach (var telemetry in batch)
        {
            if (cancellationToken.IsCancellationRequested) break;
            await _telemetryChannel.Writer.WriteAsync(telemetry, cancellationToken).ConfigureAwait(false);
        }
    }

    public IReadOnlyCollection<int> GetActiveWatchlistPids() => _activeWatchlistPids.Keys.ToArray();

    public void UpdateWatchlist(IEnumerable<int> targetPids)
    {
        _activeWatchlistPids.Clear();
        foreach (var pid in targetPids) _activeWatchlistPids[pid] = 0;
    }

    public IReadOnlyCollection<string> GetWhitelistedPaths() => _whitelistedPaths.Keys.ToArray();

    public void UpdatePathWhitelist(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            var normalized = NormalizePath(path);
            if (normalized is not null) _whitelistedPaths[normalized] = 0;
        }
    }

    public bool IsPathWhitelisted(string? path) => path is not null && _whitelistedPaths.ContainsKey(path);

    public bool RemoveWhitelistedPath(string path)
    {
        var normalized = NormalizePath(path);
        return normalized is not null && _whitelistedPaths.TryRemove(normalized, out _);
    }

    public void ClearPathWhitelist() => _whitelistedPaths.Clear();

    public bool IsWatched(int processId) => _activeWatchlistPids.ContainsKey(processId);

    public static string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var p = path.Trim().Trim('"').Trim();
        foreach (var c in p)
            if (char.IsControl(c)) return null;
        try
        {
            if (!Path.IsPathFullyQualified(p)) return null;
            p = Path.GetFullPath(p);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
        p = p.TrimEnd('\\', '/');
        return p.Length == 0 ? null : p;
    }

    public void CompleteWriting() => _telemetryChannel.Writer.TryComplete();

    public void Dispose()
    {
        if (_disposed) return;
        CompleteWriting();
        _disposed = true;
    }
}