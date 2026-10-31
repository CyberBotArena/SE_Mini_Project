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
    private const long HighMemoryThresholdBytes = 512L * 1024 * 1024; // 512 MB
    private const long ExtremeMemoryThresholdBytes = 2L * 1024 * 1024 * 1024; // 2 GB
    
    // Threshold for idle disk writing/reading (e.g., < 10 KB/sec of disk activity)
    private const long IdleDiskBytesThreshold = 10L * 1024; 

    public static ProcessScoreResult Evaluate(ProcessTelemetry telemetry)
    {
        double score = 0.0;
        long mem = telemetry.WorkingSetMemoryBytes;
        float cpu = telemetry.CpuUsage;
        long totalDiskIo = telemetry.DiskReadBytesPerSec + telemetry.DiskWriteBytesPerSec;

        // 1. Base Memory Footprint Scoring
        if (mem > ExtremeMemoryThresholdBytes) score += 40.0;
        else if (mem > HighMemoryThresholdBytes) score += 20.0;

        // 2. CPU Behavior Check
        if (cpu > 80.0f) 
        {
            // High CPU + Heavy Disk I/O usually indicates an active workload (compile, game loading, file transfer)
            if (totalDiskIo > 1024L * 1024) // > 1 MB/s
            {
                score -= 15.0; // Penalize less: it's doing active legitimate work
            }
            else
            {
                score += 30.0; // High CPU with NO disk activity? Suspicious loop or spin-lock.
            }
        }

        // 3. THE IDLE WASTE DETECTOR (The core breakthrough)
        // If it's holding massive memory, has NO visible UI window, is virtually dead on CPU, 
        // AND has flatlined disk I/O, it's an orphaned or idle background leak.
        bool isHeadlessOrBackground = !telemetry.HasVisibleWindow;
        bool isCpuIdle = cpu < 0.5f;
        bool isDiskIdle = totalDiskIo < IdleDiskBytesThreshold;

        if (mem > HighMemoryThresholdBytes && isCpuIdle && isDiskIdle)
        {
            if (isHeadlessOrBackground)
            {
                // Massive penalty: Classic background memory leak / idle waste hog
                score += 45.0; 
            }
            else
            {
                // Minimized app with an open window (e.g., minimized browser tab holding RAM)
                score += 25.0; 
            }
        }

        // Clamp score between 0.0 and 100.0
        score = Math.Clamp(score, 0.0, 100.0);

        // Switch expressions compile down efficiently
        return score switch
        {
            >= 60.0 => new(telemetry.ProcessId, telemetry.ProcessName, score, "BACKGROUND_IDLE_WASTE", "Process is consuming idle resources without active workload. Recommend termination."),
            >= 30.0 => new(telemetry.ProcessId, telemetry.ProcessName, score, "MODERATE_DRAIN", "Monitor closely for sustained background accumulation."),
            _       => new(telemetry.ProcessId, telemetry.ProcessName, score, "OPTIMAL", "Process operating within normal behavioral parameters.")
        };
    }
}