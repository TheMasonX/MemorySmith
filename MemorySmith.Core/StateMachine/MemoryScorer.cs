using MemorySmith.Core.Models;

namespace MemorySmith.Core.StateMachine;

public static class MemoryScorer
{
    public static double Score(MemoryRecord record)
    {
        var daysSince = (DateTime.UtcNow - record.LastUpdated).TotalDays;
        var recencyFactor = 1.0 / (1 + daysSince);
        return 0.4 * record.UsageCount
             + 0.3 * record.Confidence
             + 0.2 * record.References.Count
             + 0.1 * recencyFactor;
    }
}
