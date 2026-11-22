namespace DaemonGuard.UI.Services;

public record TrendPoint(DateTime Time, double Score);

public interface IDashboardService
{
    Task<List<ProcessInfo>> GetProcessesAsync();
    Task<List<TrendPoint>> GetHistoryAsync(ProcessInfo process);
    Task LogActionAsync(ProcessInfo process, string action, string result);
}