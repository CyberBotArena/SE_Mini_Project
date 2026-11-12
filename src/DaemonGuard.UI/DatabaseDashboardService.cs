using System.Text;
using System.Text.Json;
using DaemonGuard.Database;

namespace DaemonGuard.UI.Services;

public sealed class DatabaseDashboardService : IDashboardService
{
    private readonly DatabaseRepository _repo;
    public DatabaseDashboardService(DatabaseRepository repo) => _repo = repo;

    public async Task<List<ProcessInfo>> GetProcessesAsync()
    {
        var rows = await _repo.GetRecentWasteScoresAsync(200);   // newest first
        return rows.GroupBy(r => r.Pid).Select(g => g.First())   // latest row per PID
            .Select(r => new ProcessInfo
            {
                ScoreId = r.ScoreId,
                Pid = r.Pid,
                Name = r.ProcessName,
                Score = r.WasteScore,
                Risk = r.RiskScore,
                Status = ProcessInfo.NormalizeStatus(r.Classification),
                Explanation = r.ExplanationText ?? "",
                IsProtected = r.IsProtected,
                Breakdown = FormatBreakdown(r.BreakdownJson)
            })
            .OrderByDescending(p => p.Score).ToList();
    }

    public Task LogActionAsync(ProcessInfo p, string action, string result) =>
        _repo.InsertUserActionAsync(new UserActionRecord
        {
            ScoreId = p.ScoreId, Pid = p.Pid, ProcessName = p.Name,
            WasteScoreSnapshot = p.Score, RiskScoreSnapshot = p.Risk,
            ActionType = action, ResultStatus = result
        });

    private static string FormatBreakdown(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return "";
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return json;
            var sb = new StringBuilder();
            foreach (var prop in doc.RootElement.EnumerateObject())
                sb.AppendLine($"{prop.Name}: {prop.Value}");
            return sb.ToString().TrimEnd();
        }
        catch (JsonException) { return json; }
    }
}
