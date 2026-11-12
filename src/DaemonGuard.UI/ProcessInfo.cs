namespace DaemonGuard.UI;

public class ProcessInfo
{
    public long? ScoreId { get; set; }
    public int Pid { get; set; }
    public string Name { get; set; } = "";
    public double Score { get; set; }
    public double Risk { get; set; }
    public string Status { get; set; } = "";   // Healthy | Suspicious | Wasteful
    public string Explanation { get; set; } = "";
    public bool IsProtected { get; set; }
    public string Breakdown { get; set; } = "";

    // Engine and Database use different names for the same classes.
    // Everything is mapped to the 3 names the UI colours use.
    public static string NormalizeStatus(string? classification)
    {
        var c = (classification ?? "").Trim().ToUpperInvariant();
        if (c is "OPTIMAL" or "HEALTHY") return "Healthy";
        if (c is "MODERATE_DRAIN" or "SUSPICIOUS") return "Suspicious";
        if (c is "BACKGROUND_IDLE_WASTE" or "POTENTIALLY WASTEFUL" or "WASTEFUL") return "Wasteful";
        return "Healthy";
    }
}