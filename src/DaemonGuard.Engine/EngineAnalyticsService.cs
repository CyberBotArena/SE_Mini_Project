using DaemonGuard.Engine.Communication;

namespace DaemonGuard.Engine.Analytics;

public readonly record struct EngineEvaluation(
    ProcessTelemetry Telemetry,
    ProcessScoreResult Score,
    TrendResult Trend,
    bool IsWatched);

public sealed class EngineAnalyticsService
{
    private readonly EnginePipelineBridge _bridge;
    private readonly Func<EngineEvaluation, CancellationToken, ValueTask> _sink;
    private readonly Action<Exception>? _onSinkError;
    private readonly int _windowSize;
    private readonly TimeSpan _staleAfter;
    private readonly Dictionary<int, List<(long MemoryBytes, double Score, DateTime Timestamp)>> _history = new();
    private DateTime _lastPrune = DateTime.MinValue;
    private int _running;

    public EngineAnalyticsService(
        EnginePipelineBridge bridge,
        Func<EngineEvaluation, CancellationToken, ValueTask> sink,
        Action<Exception>? onSinkError = null,
        int windowSize = 30,
        TimeSpan? staleAfter = null)
    {
        _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        if (windowSize < 2) throw new ArgumentOutOfRangeException(nameof(windowSize));
        _onSinkError = onSinkError;
        _windowSize = windowSize;
        _staleAfter = staleAfter ?? TimeSpan.FromMinutes(2);
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _running, 1) != 0)
            throw new InvalidOperationException("EngineAnalyticsService is already running.");

        var reader = _bridge.Reader;
        try
        {
            while (await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (reader.TryRead(out var telemetry))
                    await ProcessAsync(telemetry, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        finally { Volatile.Write(ref _running, 0); }
    }

    private async ValueTask ProcessAsync(ProcessTelemetry telemetry, CancellationToken cancellationToken)
    {
        if (_bridge.IsPathWhitelisted(telemetry.ExecutablePath))
        {
            _history.Remove(telemetry.ProcessId);
            return;
        }

        var score = ProcessWasteScorer.Evaluate(telemetry);

        if (!_history.TryGetValue(telemetry.ProcessId, out var points))
            _history[telemetry.ProcessId] = points = new();
        else if (points.Count > 0 && telemetry.Timestamp - points[^1].Timestamp > _staleAfter)
            points.Clear();

        points.Add((telemetry.WorkingSetMemoryBytes, score.WasteScore, telemetry.Timestamp));
        if (points.Count > _windowSize) points.RemoveAt(0);

        var trend = ProcessTrendCalculator.AnalyzeTrend(telemetry.ProcessId, telemetry.ProcessName, points);
        PruneStale(telemetry.Timestamp);

        try
        {
            await _sink(new EngineEvaluation(telemetry, score, trend, _bridge.IsWatched(telemetry.ProcessId)),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _onSinkError?.Invoke(ex); 
        }
    }

    private void PruneStale(DateTime now)
    {
        if (now - _lastPrune < _staleAfter) return;
        _lastPrune = now;
        var dead = _history.Where(e => e.Value.Count == 0 || now - e.Value[^1].Timestamp > _staleAfter)
                           .Select(e => e.Key).ToList();
        foreach (var pid in dead) _history.Remove(pid);
    }
}
