using MemorySmith.Core.Models;
using MemorySmith.Core.StateMachine;
using NUnit.Framework;

namespace MemorySmith.Tests;

[TestFixture]
public class ScoringTests
{
    [Test]
    public void Score_IsNonNegative()
    {
        var record = new MemoryRecord { UsageCount = 0, Confidence = 0 };
        var score = MemoryScorer.Score(record);
        Assert.That(score, Is.GreaterThanOrEqualTo(0));
    }

    [Test]
    public void Score_IncreasesWithUsage()
    {
        var r1 = new MemoryRecord { UsageCount = 0, Confidence = 0.5, LastUpdated = DateTime.UtcNow };
        var r2 = new MemoryRecord { UsageCount = 10, Confidence = 0.5, LastUpdated = DateTime.UtcNow };
        Assert.That(MemoryScorer.Score(r2), Is.GreaterThan(MemoryScorer.Score(r1)));
    }

    [Test]
    public void Score_IncreasesWithReferences()
    {
        var r1 = new MemoryRecord { Confidence = 0.5, LastUpdated = DateTime.UtcNow };
        var r2 = new MemoryRecord { Confidence = 0.5, LastUpdated = DateTime.UtcNow };
        r2.References.Add("ref1");
        r2.References.Add("ref2");
        Assert.That(MemoryScorer.Score(r2), Is.GreaterThan(MemoryScorer.Score(r1)));
    }

    [Test]
    public void Score_RecencyDecaysOverTime()
    {
        var recent = new MemoryRecord { LastUpdated = DateTime.UtcNow };
        var old = new MemoryRecord { LastUpdated = DateTime.UtcNow.AddDays(-365) };
        Assert.That(MemoryScorer.Score(recent), Is.GreaterThan(MemoryScorer.Score(old)));
    }
}
