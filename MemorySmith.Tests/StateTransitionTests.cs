using MemorySmith.Core.Models;
using MemorySmith.Core.StateMachine;
using NUnit.Framework;

namespace MemorySmith.Tests;

[TestFixture]
public class StateTransitionTests
{
    private MemoryStateMachine _machine = null!;

    [SetUp]
    public void SetUp() => _machine = new MemoryStateMachine();

    [Test]
    public void LowScore_TransitionsToDeprecated()
    {
        var record = new MemoryRecord
        {
            Status = MemoryStatus.Working,
            UsageCount = 0,
            Confidence = 0,
            LastUpdated = DateTime.UtcNow.AddDays(-1000)
        };
        var (status, evt) = _machine.Evaluate(record);
        Assert.That(status, Is.EqualTo(MemoryStatus.Deprecated));
        Assert.That(evt, Is.Not.Null);
    }

    [Test]
    public void LowScore_DoesNotDeprecateWhenDeprecationDisabled()
    {
        var record = new MemoryRecord
        {
            Status = MemoryStatus.Working,
            UsageCount = 0,
            Confidence = 0,
            LastUpdated = DateTime.UtcNow.AddDays(-1000)
        };
        var (status, evt) = _machine.Evaluate(record, allowDeprecation: false);
        Assert.Multiple(() =>
        {
            Assert.That(status, Is.EqualTo(MemoryStatus.Working));
            Assert.That(evt, Is.Null);
        });
    }

    [Test]
    public void HighScore_PromotesUnconsolidatedToWorking()
    {
        var record = new MemoryRecord
        {
            Status = MemoryStatus.Unconsolidated,
            UsageCount = 3,
            Confidence = 0.9,
            LastUpdated = DateTime.UtcNow
        };
        record.References.AddRange(new[] { "r1", "r2", "r3" });
        var (status, _) = _machine.Evaluate(record);
        Assert.That(status, Is.EqualTo(MemoryStatus.Working));
    }

    [Test]
    public void NoChange_WhenCoreRecordScoreExceedsThreshold()
    {
        var record = new MemoryRecord
        {
            Status = MemoryStatus.Core,
            UsageCount = 20,
            Confidence = 0.95,
            LastUpdated = DateTime.UtcNow
        };
        record.References.AddRange(new[] { "r1", "r2", "r3", "r4", "r5", "r6", "r7", "r8" });
        var (status, evt) = _machine.Evaluate(record);
        Assert.That(status, Is.EqualTo(MemoryStatus.Core));
        Assert.That(evt, Is.Null);
    }

    [Test]
    public void CoreRecord_DemotesToWorking_WhenScoreDropsBelowCoreThreshold()
    {
        var record = new MemoryRecord
        {
            Status = MemoryStatus.Core,
            UsageCount = 1,
            Confidence = 0.3,
            LastUpdated = DateTime.UtcNow.AddDays(-30)
        };
        record.References.Add("r1");
        var (status, evt) = _machine.Evaluate(record);
        Assert.That(status, Is.EqualTo(MemoryStatus.Working));
        Assert.That(evt, Is.Not.Null);
        Assert.That(evt!.Details, Does.Contain("Core").And.Contain("Working"));
    }

    [Test]
    public void DeprecatedRecord_RepromotesToWorking_WhenScoreRecoversAboveWorkingThreshold()
    {
        var record = new MemoryRecord
        {
            Status = MemoryStatus.Deprecated,
            UsageCount = 3,
            Confidence = 0.9,
            LastUpdated = DateTime.UtcNow
        };
        record.References.AddRange(new[] { "r1", "r2", "r3" });
        var (status, evt) = _machine.Evaluate(record);
        Assert.That(status, Is.EqualTo(MemoryStatus.Working));
        Assert.That(evt, Is.Not.Null);
        Assert.That(evt!.Details, Does.Contain("Deprecated").And.Contain("Working"));
    }

    [Test]
    public void DeprecatedRecord_StaysDeprecated_WhenScoreIsBelowWorkingThreshold()
    {
        var record = new MemoryRecord
        {
            Status = MemoryStatus.Deprecated,
            UsageCount = 0,
            Confidence = 0,
            LastUpdated = DateTime.UtcNow.AddDays(-1000)
        };
        var (status, evt) = _machine.Evaluate(record);
        Assert.That(status, Is.EqualTo(MemoryStatus.Deprecated));
        Assert.That(evt, Is.Null);
    }

    [Test]
    public void UnconsolidatedRecord_WithLowScore_DoesNotDeprecate()
    {
        // TSK-0364: A fresh Unconsolidated record with default score (~0.1) must
        // NOT be deprecated on first evaluation. It should stay Unconsolidated
        // until a promotion cycle raises it to Working.
        var record = new MemoryRecord
        {
            Status = MemoryStatus.Unconsolidated,
            UsageCount = 0,
            Confidence = 0,
            LastUpdated = DateTime.UtcNow
        };
        var (status, evt) = _machine.Evaluate(record);
        Assert.That(status, Is.EqualTo(MemoryStatus.Unconsolidated));
        Assert.That(evt, Is.Null);
    }
}
