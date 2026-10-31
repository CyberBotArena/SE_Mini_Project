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
            new { Pid = 101, Name = "svchost", Path = @"C:\Windows\System32\svchost.exe", Ram = 15_000_000L },
            new { Pid = 202, Name = "notepad", Path = @"C:\Windows\System32\notepad.exe", Ram = 25_000_000L }, 
            new { Pid = 303, Name = "heavy_game", Path = @"D:\Games\HeavyGame.exe", Ram = 4_294_967_296L }
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
                CpuUsage: 4.5f,
                WorkingSetMemoryBytes: p.Ram,
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