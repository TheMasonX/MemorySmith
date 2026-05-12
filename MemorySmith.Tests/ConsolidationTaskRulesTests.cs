using MemorySmith.App.Services;
using MemorySmith.Core.Indexing;
using MemorySmith.Core.Models;

namespace MemorySmith.Tests;

[TestFixture]
public class ConsolidationTaskRulesTests
{
    private InMemoryMemoryStore _store = null!;
    private MemoryMaintenanceTasks _tasks = null!;

    [SetUp]
    public void Setup()
    {
        _store = new InMemoryMemoryStore();
        _tasks = new MemoryMaintenanceTasks(_store, new RecordingEventStore(), new MemoryIndex());
    }

    [Test]
    public async Task DuplicateTitles_MergeUsageAndDeleteSecondaryRecord()
    {
        _store.Save(new MemoryRecord { Id = "1", Title = "Test Memory", Content = "A", UsageCount = 5 });
        _store.Save(new MemoryRecord { Id = "2", Title = "test memory", Content = "B", UsageCount = 3 });

        await _tasks.RunConsolidationAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(_store.Load("1")!.UsageCount, Is.EqualTo(8));
            Assert.That(_store.Load("2"), Is.Null);
        });
    }

    [Test]
    public async Task DuplicateTitles_MergeTagsWithoutDuplicates()
    {
        _store.Save(new MemoryRecord { Id = "1", Title = "Test", Content = "A", Tags = ["tag1", "tag2"] });
        _store.Save(new MemoryRecord { Id = "2", Title = "test", Content = "B", Tags = ["tag2", "tag3"] });

        await _tasks.RunConsolidationAsync(CancellationToken.None);

        Assert.That(_store.Load("1")!.Tags, Is.EquivalentTo(new[] { "tag1", "tag2", "tag3" }));
    }

    [Test]
    public async Task StableWorkingMemory_PromotesToCore()
    {
        _store.Save(new MemoryRecord
        {
            Id = "stable",
            Title = "Stable",
            Content = "Stable content",
            Status = MemoryStatus.Working,
            LastUpdated = DateTime.UtcNow.AddDays(-31),
            References = ["ref1", "ref2"],
            Confidence = 0.8
        });

        await _tasks.RunConsolidationAsync(CancellationToken.None);

        Assert.That(_store.Load("stable")!.Status, Is.EqualTo(MemoryStatus.Core));
    }

    [Test]
    public async Task WorkingMemoryTooYoung_DoesNotPromote()
    {
        _store.Save(new MemoryRecord
        {
            Id = "young",
            Title = "Young",
            Content = "Young content",
            Status = MemoryStatus.Working,
            LastUpdated = DateTime.UtcNow.AddDays(-15),
            References = ["ref1", "ref2"],
            Confidence = 0.8
        });

        await _tasks.RunConsolidationAsync(CancellationToken.None);

        Assert.That(_store.Load("young")!.Status, Is.EqualTo(MemoryStatus.Working));
    }

    [Test]
    public async Task WorkingMemoryWithInsufficientReferences_DoesNotPromote()
    {
        _store.Save(new MemoryRecord
        {
            Id = "few-refs",
            Title = "Few Refs",
            Content = "Few refs content",
            Status = MemoryStatus.Working,
            LastUpdated = DateTime.UtcNow.AddDays(-31),
            References = ["ref1"],
            Confidence = 0.8
        });

        await _tasks.RunConsolidationAsync(CancellationToken.None);

        Assert.That(_store.Load("few-refs")!.Status, Is.EqualTo(MemoryStatus.Working));
    }

    [Test]
    public async Task WorkingMemoryWithLowConfidence_DoesNotPromote()
    {
        _store.Save(new MemoryRecord
        {
            Id = "low-confidence",
            Title = "Low Confidence",
            Content = "Low confidence content",
            Status = MemoryStatus.Working,
            LastUpdated = DateTime.UtcNow.AddDays(-31),
            References = ["ref1", "ref2"],
            Confidence = 0.6
        });

        await _tasks.RunConsolidationAsync(CancellationToken.None);

        Assert.That(_store.Load("low-confidence")!.Status, Is.EqualTo(MemoryStatus.Working));
    }

    [Test]
    public async Task LowScoreMemory_Deprecates()
    {
        _store.Save(new MemoryRecord
        {
            Id = "low-score",
            Title = "Low Score",
            Content = "Low score content",
            Status = MemoryStatus.Unconsolidated,
            UsageCount = 0,
            Confidence = 0.1,
            LastUpdated = DateTime.UtcNow.AddMonths(-6)
        });

        await _tasks.RunConsolidationAsync(CancellationToken.None);

        Assert.That(_store.Load("low-score")!.Status, Is.EqualTo(MemoryStatus.Deprecated));
    }

    [Test]
    public async Task AlreadyDeprecatedMemory_RemainsDeprecated()
    {
        _store.Save(new MemoryRecord
        {
            Id = "deprecated",
            Title = "Deprecated",
            Content = "Deprecated content",
            Status = MemoryStatus.Deprecated,
            UsageCount = 0,
            Confidence = 0,
            LastUpdated = DateTime.UtcNow.AddMonths(-6)
        });

        await _tasks.RunConsolidationAsync(CancellationToken.None);

        Assert.That(_store.Load("deprecated")!.Status, Is.EqualTo(MemoryStatus.Deprecated));
    }
}