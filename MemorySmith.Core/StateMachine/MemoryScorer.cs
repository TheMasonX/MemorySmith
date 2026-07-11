using MemorySmith.Core.Models;

namespace MemorySmith.Core.StateMachine;

public static class MemoryScorer
{
    public static double Score(MemoryRecord record)
    {
        var daysSince = (DateTime.UtcNow - record.LastUpdated).TotalDays;
        var recencyFactor = 1.0 / (1 + daysSince);
        var usageFactor = Math.Log10(record.UsageCount + 1);
        return 0.50 * usageFactor
             + 0.25 * record.Confidence
             + 0.15 * record.References.Count
             + 0.10 * recencyFactor;
    }
}
