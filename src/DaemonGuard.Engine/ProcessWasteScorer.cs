using DaemonGuard.Engine.Communication;

namespace DaemonGuard.Engine.Analytics;

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
    
    private const long IdleDiskBytesThreshold = 10L * 1024; 

    public static ProcessScoreResult Evaluate(ProcessTelemetry telemetry)
    {
        double score = 0.0;
        long mem = telemetry.WorkingSetMemoryBytes;
        float cpu = telemetry.CpuUsage;
        long totalDiskIo = telemetry.DiskReadBytesPerSec + telemetry.DiskWriteBytesPerSec;

        if (mem > ExtremeMemoryThresholdBytes) score += 40.0;
        else if (mem > HighMemoryThresholdBytes) score += 20.0;

        if (cpu > 80.0f) 
        {
            if (totalDiskIo > 1024L * 1024)
            {
                score -= 15.0;
            }
            else
            {
                score += 30.0;
            }
        }

        bool isHeadlessOrBackground = !telemetry.HasVisibleWindow;
        bool isCpuIdle = cpu < 0.5f;
        bool isDiskIdle = totalDiskIo < IdleDiskBytesThreshold;

        if (mem > HighMemoryThresholdBytes && isCpuIdle && isDiskIdle)
        {
            if (isHeadlessOrBackground)
            {
                score += 45.0; 
            }
            else
            {
                score += 25.0; 
            }
        }

        score = Math.Clamp(score, 0.0, 100.0) / 10.0;

        return score switch
        {
            >= 6.0 => new(telemetry.ProcessId, telemetry.ProcessName, score, "BACKGROUND_IDLE_WASTE", "Process is consuming idle resources without active workload. Recommend termination."),
            >= 3.0 => new(telemetry.ProcessId, telemetry.ProcessName, score, "MODERATE_DRAIN", "Monitor closely for sustained background accumulation."),
            _      => new(telemetry.ProcessId, telemetry.ProcessName, score, "OPTIMAL", "Process operating within normal behavioral parameters.")
        };
    }
}