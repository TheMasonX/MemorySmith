namespace MemorySmith.Core.Models;

public class StatsSnapshot
{
    public int TotalCount { get; set; }
    public int Unconsolidated { get; set; }
    public int Working { get; set; }
    public int Core { get; set; }
    public int Deprecated { get; set; }
    public double AverageConfidence { get; set; }
    public int TotalUsage { get; set; }
    public DateTime CapturedAt { get; set; } = DateTime.UtcNow;
}
