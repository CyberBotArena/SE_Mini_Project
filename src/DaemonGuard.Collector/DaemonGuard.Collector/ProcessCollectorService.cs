using System.ComponentModel;
using System.Diagnostics;

namespace DaemonGuard.Collector;

/// <summary>Single-worker, bounded-state Windows process sampler. Never performs UI operations.</summary>
public sealed class ProcessCollectorService
{
    private sealed class PreviousSample
    {
        public long StartTimeTicks;
        public TimeSpan CpuTime;
        public ulong ReadBytes;
        public ulong WriteBytes;
        public long StopwatchTicks;
        public string? ExecutablePath;
        public int LastSeenGeneration;
    }

    private readonly IEngineIngestor _ingestor;
    private readonly IEngineFeedback _feedback;
    private readonly CollectorOptions _options;
    private readonly Dictionary<int, PreviousSample> _previous = new();
    private readonly SemaphoreSlim _sampleGate = new(1, 1);
    private int _generation;

    public ProcessCollectorService(IEngineIngestor ingestor, IEngineFeedback feedback, CollectorOptions? options = null)
    {
        _ingestor = ingestor ?? throw new ArgumentNullException(nameof(ingestor));
        _feedback = feedback ?? throw new ArgumentNullException(nameof(feedback));
        _options = options ?? new CollectorOptions();
        if (_options.Interval < TimeSpan.FromMilliseconds(100))
            throw new ArgumentOutOfRangeException(nameof(options), "Sampling interval must be at least 100 ms.");
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows only.");
        using var timer = new PeriodicTimer(_options.Interval);
        await SampleOnceAsync(cancellationToken).ConfigureAwait(false);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            await SampleOnceAsync(cancellationToken).ConfigureAwait(false);
    }

    // Can be invoked for a manual refresh. Gate prevents overlapping sample cycles.
    public async Task SampleOnceAsync(CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows only.");
        await _sampleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var generation = ++_generation;
            var whitelist = _feedback.GetWhitelistedPaths();
            var batch = new List<ProcessTelemetry>(256);
            Process[] processes = Process.GetProcesses();
            try
            {
                foreach (var process in processes)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try { SampleProcess(process, generation, whitelist, batch); }
                    catch (Exception ex) when (ex is Win32Exception or UnauthorizedAccessException or InvalidOperationException or NotSupportedException)
                    {
                        // Protected/exited process: skip only this process.
                    }
                }
            }
            finally
            {
                foreach (var process in processes) process.Dispose();
            }

            // Remove dead/reused PID state; storage remains proportional to live processes.
            var stale = new List<int>();
            foreach (var entry in _previous)
                if (entry.Value.LastSeenGeneration != generation) stale.Add(entry.Key);
            foreach (int pid in stale) _previous.Remove(pid);

            if (batch.Count != 0)
                await _ingestor.SubmitBatchAsync(batch, cancellationToken).ConfigureAwait(false);
        }
        finally { _sampleGate.Release(); }
    }

    private void SampleProcess(Process process, int generation, IReadOnlySet<string> whitelist, List<ProcessTelemetry> batch)
    {
        int pid = process.Id;
        long startTicks = process.StartTime.ToUniversalTime().Ticks;
        long nowTicks = Stopwatch.GetTimestamp();
        TimeSpan cpuTime = process.TotalProcessorTime;
        long memory = process.WorkingSet64;
        string name = process.ProcessName;

        // If this counter is inaccessible, do not fabricate a zero I/O rate.
        if (!NativeMethods.GetProcessIoCounters(process.SafeHandle, out var io)) return;

        bool isSameProcess = _previous.TryGetValue(pid, out var old) && old!.StartTimeTicks == startTicks;
        string? path = isSameProcess ? old!.ExecutablePath : null;
        if (_options.CollectExecutablePaths && path is null)
        {
            try { path = process.MainModule?.FileName; }
            catch (Exception ex) when (ex is Win32Exception or UnauthorizedAccessException or InvalidOperationException) { }
        }

        // The bridge should normalize whitelist entries; OrdinalIgnoreCase is the Windows path policy.
        bool ignored = path is not null && whitelist.Any(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        _previous[pid] = new PreviousSample
        {
            StartTimeTicks = startTicks, CpuTime = cpuTime,
            ReadBytes = io.ReadTransferCount, WriteBytes = io.WriteTransferCount,
            StopwatchTicks = nowTicks, ExecutablePath = path, LastSeenGeneration = generation
        };
        if (ignored) return;

        double cpu = 0, readRate = 0, writeRate = 0;
        if (isSameProcess)
        {
            double elapsed = (nowTicks - old!.StopwatchTicks) / (double)Stopwatch.Frequency;
            if (elapsed > 0)
            {
                // CPU percentage normalized to total machine capacity (0..100%).
                cpu = Math.Clamp((cpuTime - old.CpuTime).TotalSeconds / (elapsed * Environment.ProcessorCount) * 100, 0, 100);
                if (io.ReadTransferCount >= old.ReadBytes)
                    readRate = (io.ReadTransferCount - old.ReadBytes) / elapsed;
                if (io.WriteTransferCount >= old.WriteBytes)
                    writeRate = (io.WriteTransferCount - old.WriteBytes) / elapsed;
            }
        }

        // MainWindowHandle is a heuristic for having a GUI window, not a proof of visibility.
        bool hasWindow = process.MainWindowHandle != IntPtr.Zero;
        batch.Add(new ProcessTelemetry(pid, name, path, cpu, readRate, writeRate,
            memory, hasWindow, DateTime.UtcNow));
    }
}
