using MemorySmith.Core.Models;

namespace MemorySmith.Tests;

[TestFixture]
public class StatsSnapshotFactoryTests
{
    [Test]
    public void Build_WithEmptyRecords_ReturnsZeroedSnapshot()
    {
        var snapshot = StatsSnapshotFactory.Build([]);

        Assert.That(snapshot.TotalCount, Is.EqualTo(0));
        Assert.That(snapshot.Unconsolidated, Is.EqualTo(0));
        Assert.That(snapshot.Working, Is.EqualTo(0));
        Assert.That(snapshot.Core, Is.EqualTo(0));
        Assert.That(snapshot.Deprecated, Is.EqualTo(0));
        Assert.That(snapshot.AverageConfidence, Is.EqualTo(0));
        Assert.That(snapshot.TotalUsage, Is.EqualTo(0));
    }

    [Test]
    public void Build_WithMixedStatuses_CalculatesExpectedCountsAndAggregates()
    {
        var records = new List<MemoryRecord>
        {
            new() { Id = "u1", Status = MemoryStatus.Unconsolidated, Confidence = 0.2, UsageCount = 1 },
            new() { Id = "u2", Status = MemoryStatus.Unconsolidated, Confidence = 0.4, UsageCount = 2 },
            new() { Id = "w1", Status = MemoryStatus.Working, Confidence = 0.6, UsageCount = 3 },
            new() { Id = "c1", Status = MemoryStatus.Core, Confidence = 0.8, UsageCount = 4 },
            new() { Id = "d1", Status = MemoryStatus.Deprecated, Confidence = 1.0, UsageCount = 5 }
        };

        var snapshot = StatsSnapshotFactory.Build(records);

        Assert.That(snapshot.TotalCount, Is.EqualTo(5));
        Assert.That(snapshot.Unconsolidated, Is.EqualTo(2));
        Assert.That(snapshot.Working, Is.EqualTo(1));
        Assert.That(snapshot.Core, Is.EqualTo(1));
        Assert.That(snapshot.Deprecated, Is.EqualTo(1));
        Assert.That(snapshot.AverageConfidence, Is.EqualTo(0.6).Within(0.001));
        Assert.That(snapshot.TotalUsage, Is.EqualTo(15));
    }

    [Test]
    public void Build_WithNullRecords_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => StatsSnapshotFactory.Build(null!));
    }
}
