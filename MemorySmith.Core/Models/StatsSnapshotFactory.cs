namespace MemorySmith.Core.Models;

public static class StatsSnapshotFactory
{
    public static StatsSnapshot Build(IEnumerable<MemoryRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);

        var materializedRecords = records.ToList();
        return new StatsSnapshot
        {
            TotalCount = materializedRecords.Count,
            Unconsolidated = materializedRecords.Count(r => r.Status == MemoryStatus.Unconsolidated),
            Working = materializedRecords.Count(r => r.Status == MemoryStatus.Working),
            Core = materializedRecords.Count(r => r.Status == MemoryStatus.Core),
            Deprecated = materializedRecords.Count(r => r.Status == MemoryStatus.Deprecated),
            AverageConfidence = materializedRecords.Count > 0 ? materializedRecords.Average(r => r.Confidence) : 0,
            TotalUsage = materializedRecords.Sum(r => r.UsageCount)
        };
    }
}
