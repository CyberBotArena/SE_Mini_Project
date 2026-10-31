using DaemonGuard.Engine.Communication;

namespace DaemonGuard.Engine.Analytics;

// Use a readonly struct to allocate on the stack, not the heap (Zero GC pressure)
public readonly record struct ProcessScoreResult(
    int ProcessId,
    string ProcessName,
    double WasteScore,
    string Classification,
    string Recommendation
);

public static class ProcessWasteScorer
{
    private const long HighMemoryThresholdBytes = 512L * 1024 * 1024;
    private const long ExtremeMemoryThresholdBytes = 2L * 1024 * 1024 * 1024;

    public static ProcessScoreResult Evaluate(ProcessTelemetry telemetry)
    {
        // Using local variables to avoid redundant property lookups
        double score = 0.0;
        long mem = telemetry.WorkingSetMemoryBytes;
        float cpu = telemetry.CpuUsage;

        if (mem > ExtremeMemoryThresholdBytes) score += 50.0;
        else if (mem > HighMemoryThresholdBytes) score += 25.0;

        if (cpu > 80.0f) score += 40.0;
        else if (cpu < 0.1f && mem > HighMemoryThresholdBytes) score += 15.0;

        // Switch expressions compile down efficiently
        return score switch
        {
            >= 60.0 => new(telemetry.ProcessId, telemetry.ProcessName, score, "CRITICAL_BLOAT", "Recommend immediate throttle or termination."),
            >= 30.0 => new(telemetry.ProcessId, telemetry.ProcessName, score, "MODERATE_DRAIN", "Monitor closely for sustained usage spikes."),
            _       => new(telemetry.ProcessId, telemetry.ProcessName, score, "OPTIMAL", "Process operating within normal parameters.")
        };
    }
}