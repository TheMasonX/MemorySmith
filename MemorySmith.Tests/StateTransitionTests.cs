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
    public void NoChange_WhenAlreadyCorrectStatus()
    {
        var record = new MemoryRecord
        {
            Status = MemoryStatus.Core,
            UsageCount = 5,
            Confidence = 0.9,
            LastUpdated = DateTime.UtcNow
        };
        record.References.AddRange(new[] { "r1", "r2", "r3", "r4" });
        var (status, evt) = _machine.Evaluate(record);
        Assert.That(status, Is.EqualTo(MemoryStatus.Core));
        Assert.That(evt, Is.Null);
    }
}
