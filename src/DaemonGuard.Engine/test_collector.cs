using DaemonGuard.Engine.Communication;

namespace DaemonGuard.Collector;

public sealed class ProcessCollectorService
{
    private readonly IEngineIngestor _ingestor;
    private readonly IEngineFeedback _feedback;

    public ProcessCollectorService(IEngineIngestor ingestor, IEngineFeedback feedback)
    {
        _ingestor = ingestor ?? throw new ArgumentNullException(nameof(ingestor));
        _feedback = feedback ?? throw new ArgumentNullException(nameof(feedback));
    }

    public async Task RunTickAsync(CancellationToken cancellationToken)
    {
        var whitelistedPaths = _feedback.GetWhitelistedPaths();
        
        var simulatedScrapedProcesses = new[]
        {
            new { Pid = 101, Name = "svchost", Path = @"C:\Windows\System32\svchost.exe", Ram = 15_000_000L, DiskRead = 512L, DiskWrite = 256L, HasWindow = false },
            new { Pid = 202, Name = "notepad", Path = @"C:\Windows\System32\notepad.exe", Ram = 25_000_000L, DiskRead = 0L, DiskWrite = 0L, HasWindow = true }, 
            new { Pid = 303, Name = "heavy_game", Path = @"D:\Games\HeavyGame.exe", Ram = 4_294_967_296L, DiskRead = 2_048_000L, DiskWrite = 1_024_000L, HasWindow = true }
        };

        var batchToSubmit = new List<ProcessTelemetry>();

        foreach (var p in simulatedScrapedProcesses)
        {
            if (whitelistedPaths.Contains(p.Path))
            {
                Console.WriteLine($"[Collector] SKIPPED whitelisted path: {p.Path}");
                continue;
            }

            batchToSubmit.Add(new ProcessTelemetry(
                ProcessId: p.Pid,
                ProcessName: p.Name,
                ExecutablePath: p.Path,
                CpuUsage: p.Name == "heavy_game" ? 18.5f : 0.4f,
                WorkingSetMemoryBytes: p.Ram,
                DiskReadBytesPerSec: p.DiskRead,
                DiskWriteBytesPerSec: p.DiskWrite,
                HasVisibleWindow: p.HasWindow,
                Timestamp: DateTime.UtcNow
            ));
        }

        if (batchToSubmit.Count > 0)
        {
            await _ingestor.SubmitBatchAsync(batchToSubmit, cancellationToken);
            Console.WriteLine($"[Collector] Dispatched {batchToSubmit.Count} active processes to Engine.");
        }
    }
}