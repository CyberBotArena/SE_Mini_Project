namespace DaemonGuard.Engine.Analytics;

public readonly record struct TrendResult(
    int ProcessId,
    string ProcessName,
    double AverageMemoryMb,
    double GrowthRateBytesPerTick,
    string TrendStatus,
    string Insight
);

public static class ProcessTrendCalculator
{
    /// <summary>
    /// Analyzes a time-series history of telemetry points for a single process
    /// to detect leaks or rapid resource scaling.
    /// </summary>
    public static TrendResult AnalyzeTrend(int processId, string processName, IEnumerable<(long MemoryBytes, double Score, DateTime Timestamp)> historyPoints)
    {
        var points = historyPoints.ToList();
        if (points.Count < 2)
        {
            return new TrendResult(
                processId, 
                processName, 
                points.FirstOrDefault().MemoryBytes / 1024.0 / 1024.0, 
                0.0, 
                "INSUFFICIENT_DATA", 
                "Waiting for more historical ticks to calculate trend."
            );
        }

        // Calculate average memory footprint
        double totalMemory = 0;
        foreach (var p in points) totalMemory += p.MemoryBytes;
        double avgMemoryBytes = totalMemory / points.Count;
        double avgMemoryMb = avgMemoryBytes / 1024.0 / 1024.0;

        // Simple linear delta check between the oldest and newest points in the window
        var oldest = points.First();
        var newest = points.Last();

        long memoryDeltaBytes = newest.MemoryBytes - oldest.MemoryBytes;
        double tickCount = points.Count - 1;
        double growthRate = memoryDeltaBytes / tickCount; // Bytes per tick growth rate

        // Determine trend classification based on growth rate
        string status;
        string insight;

        if (growthRate > 50L * 1024 * 1024) // Growing by more than 50MB per tick
        {
            status = "AGGRESSIVE_LEAK";
            insight = "Memory usage is scaling rapidly. Potential memory leak detected.";
        }
        else if (growthRate > 10L * 1024 * 1024) // Growing by 10MB to 50MB per tick
        {
            status = "GRADUAL_CLIMB";
            insight = "Steady upward memory drift observed over recent collection cycles.";
        }
        else if (growthRate < -10L * 1024 * 1024)
        {
            status = "DECREASING";
            insight = "Process memory footprint is actively releasing resources.";
        }
        else
        {
            status = "STABLE";
            insight = "Resource utilization is holding flat within normal margins.";
        }

        return new TrendResult(processId, processName, avgMemoryMb, growthRate, status, insight);
    }
}