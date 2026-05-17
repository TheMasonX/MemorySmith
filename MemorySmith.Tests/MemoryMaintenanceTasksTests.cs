using MemorySmith.App.Services;
using MemorySmith.Core.Models;

namespace MemorySmith.Tests;

[TestFixture]
public class MemoryMaintenanceTasksTests
{
    private InMemoryMemoryStore _store = null!;
    private RecordingEventStore _events = null!;
    private MemorySmith.Core.Indexing.MemoryIndex _index = null!;
    private MemoryMaintenanceTasks _tasks = null!;

    [SetUp]
    public void SetUp()
    {
        _store = new InMemoryMemoryStore();
        _events = new RecordingEventStore();
        _index = new MemorySmith.Core.Indexing.MemoryIndex();
        _tasks = new MemoryMaintenanceTasks(_store, _events, _index);
    }

    [Test]
    public async Task RunTriageAsync_PersistsTransitionsAndEvents()
    {
        _store.Save(new MemoryRecord
        {
            Id = "triage",
            Content = "Promote me",
            Status = MemoryStatus.Unconsolidated,
            UsageCount = 10,
            Confidence = 1,
            LastUpdated = DateTime.UtcNow.AddDays(-1)
        });

        await _tasks.RunTriageAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(_store.Load("triage")!.Status, Is.EqualTo(MemoryStatus.Working));
            Assert.That(_events.Events.Single().MemoryId, Is.EqualTo("triage"));
            Assert.That(_events.Events.Single().Details, Does.Contain("Unconsolidated"));
        });
    }

    [Test]
    public async Task RunIndexRebuildAsync_RebuildsIndexFromStorageSnapshot()
    {
        _store.Save(new MemoryRecord { Id = "indexed", Content = "Index", Tags = ["tag"], References = ["ref"] });

        await _tasks.RunIndexRebuildAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(_index.ById, Does.ContainKey("indexed"));
            Assert.That(_index.ByTag["tag"], Does.Contain("indexed"));
            Assert.That(_index.ByReference["ref"], Does.Contain("indexed"));
        });
    }

    [Test]
    public async Task RunConsolidationAsync_MergesPromotesAndDeprecates()
    {
        _store.Save(new MemoryRecord { Id = "dupe-1", Title = "Same", Content = "A", UsageCount = 1, Tags = ["one"] });
        _store.Save(new MemoryRecord { Id = "dupe-2", Title = "same", Content = "B", UsageCount = 2, Tags = ["two"] });
        _store.Save(new MemoryRecord
        {
            Id = "promote",
            Title = "Promote",
            Content = "Stable",
            Status = MemoryStatus.Working,
            References = ["a", "b"],
            Confidence = 0.8,
            LastUpdated = DateTime.UtcNow.AddDays(-31)
        });
        _store.Save(new MemoryRecord
        {
            Id = "deprecate",
            Title = "Deprecate",
            Content = "Old low value",
            Status = MemoryStatus.Unconsolidated,
            Confidence = 0,
            LastUpdated = DateTime.UtcNow.AddDays(-200)
        });

        await _tasks.RunConsolidationAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(_store.Load("dupe-1")!.UsageCount, Is.EqualTo(3));
            Assert.That(_store.Load("dupe-1")!.Tags, Is.EquivalentTo(new[] { "one", "two" }));
            Assert.That(_store.Load("dupe-2"), Is.Null);
            Assert.That(_store.Load("promote")!.Status, Is.EqualTo(MemoryStatus.Core));
            Assert.That(_store.Load("deprecate")!.Status, Is.EqualTo(MemoryStatus.Deprecated));
        });
    }

    [Test]
    public void FormatInterval_UsesConfiguredCadenceLabels()
    {
        Assert.Multiple(() =>
        {
            Assert.That(MemoryMaintenanceService.FormatInterval(TimeSpan.FromMinutes(7)), Is.EqualTo("7 min"));
            Assert.That(MemoryMaintenanceService.FormatInterval(TimeSpan.FromMinutes(90)), Is.EqualTo("1.5h"));
            Assert.That(MemoryMaintenanceService.FormatInterval(TimeSpan.FromHours(48)), Is.EqualTo("2d"));
        });
    }
}