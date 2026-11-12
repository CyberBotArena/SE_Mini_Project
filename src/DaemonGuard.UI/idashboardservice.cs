namespace DaemonGuard.UI.Services;

public interface IDashboardService
{
    Task<List<ProcessInfo>> GetProcessesAsync();
    Task LogActionAsync(ProcessInfo process, string action, string result);
}