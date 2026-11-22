using DaemonGuard.Engine.Host;

var dbPath = args.Length > 0
    ? args[0]
    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DaemonGuard", "daemonguard.db");

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

Console.WriteLine($"DaemonGuard engine host started. Database: {dbPath}");
Console.WriteLine("Press Ctrl+C to stop.");
await new MonitoringHost(dbPath).RunAsync(cts.Token);
Console.WriteLine("Stopped.");
