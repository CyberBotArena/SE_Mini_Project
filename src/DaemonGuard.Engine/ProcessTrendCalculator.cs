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
    public static TrendResult AnalyzeTrend(int processId, string processName, IEnumerable<(long MemoryBytes, double Score, DateTime Timestamp)> historyPoints)
    {
        ArgumentNullException.ThrowIfNull(historyPoints);
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

        double totalMemory = 0;
        foreach (var p in points) totalMemory += p.MemoryBytes;
        double avgMemoryBytes = totalMemory / points.Count;
        double avgMemoryMb = avgMemoryBytes / 1024.0 / 1024.0;

        var oldest = points.First();
        var newest = points.Last();

        long memoryDeltaBytes = newest.MemoryBytes - oldest.MemoryBytes;
        double tickCount = points.Count - 1;
        double growthRate = memoryDeltaBytes / tickCount;

        string status;
        string insight;

        if (growthRate > 50L * 1024 * 1024)
        {
            status = "AGGRESSIVE_LEAK";
            insight = "Memory usage is scaling rapidly. Potential memory leak detected.";
        }
        else if (growthRate > 10L * 1024 * 1024)
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