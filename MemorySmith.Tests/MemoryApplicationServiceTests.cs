using MemorySmith.App.Services;
using MemorySmith.Core.Models;
using MemorySmith.Storage;
using Microsoft.Extensions.Options;

namespace MemorySmith.Tests;

[TestFixture]
public class MemoryApplicationServiceTests
{
    private InMemoryMemoryStore _store = null!;
    private RecordingEventStore _events = null!;
    private RecordingMemoryChangePublisher _publisher = null!;
    private MemoryApplicationService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _store = new InMemoryMemoryStore();
        _events = new RecordingEventStore();
        _publisher = new RecordingMemoryChangePublisher();
        _service = TestServiceFactory.CreateMemoryApplicationService(_store, _events, _publisher);
    }

    [Test]
    public async Task GetMemoriesAsync_ClampsBoundsAndOrdersDeterministically()
    {
        _store.Save(new MemoryRecord
        {
            Id = "old",
            Title = "Old",
            Status = MemoryStatus.Working,
            Tags = ["alpha"],
            LastUpdated = new DateTime(2026, 05, 01, 0, 0, 0, DateTimeKind.Utc)
        });
        _store.Save(new MemoryRecord
        {
            Id = "new",
            Title = "New",
            Status = MemoryStatus.Working,
            Tags = ["alpha", "beta"],
            LastUpdated = new DateTime(2026, 05, 02, 0, 0, 0, DateTimeKind.Utc)
        });
        _store.Save(new MemoryRecord
        {
            Id = "other-status",
            Title = "Other",
            Status = MemoryStatus.Core,
            Tags = ["alpha"],
            LastUpdated = new DateTime(2026, 05, 03, 0, 0, 0, DateTimeKind.Utc)
        });

        var result = await _service.GetMemoriesAsync(
            new MemoryListQuery(Page: -7, PageSize: 500, Status: MemoryStatus.Working, Tags: "alpha"),
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Page, Is.EqualTo(1));
            Assert.That(result.PageSize, Is.EqualTo(100));
            Assert.That(result.TotalCount, Is.EqualTo(2));
            Assert.That(result.Data.Select(x => x.Id), Is.EqualTo(new[] { "new", "old" }));
        });
    }

    [Test]
    public void CreateAsync_WithBlankContent_ThrowsValidationAndDoesNotPersist()
    {
        var record = new MemoryRecord { Id = "invalid", Title = "No content", Content = "   " };

        var exception = Assert.ThrowsAsync<MemoryValidationException>(async () =>
            await _service.CreateAsync(record, CancellationToken.None));

        Assert.Multiple(() =>
        {
            Assert.That(exception!.Errors.Keys, Does.Contain(nameof(MemoryRecord.Content)));
            Assert.That(_store.LoadAll(), Is.Empty);
            Assert.That(_events.Events, Is.Empty);
            Assert.That(_publisher.MemoryUpdates, Is.Empty);
            Assert.That(_publisher.StatsUpdates, Is.Empty);
        });
    }

    [Test]
    public async Task CreateAsync_NormalizesTagsReferencesAndAuditsMutation()
    {
        var record = new MemoryRecord
        {
            Id = "new-memory",
            Title = "Created",
            Content = "Useful content",
            Tags = [" alpha ", "ALPHA", "", "beta"],
            References = [" ref-1 ", "ref-1", ""]
        };

        var created = await _service.CreateAsync(record, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(created.Tags, Is.EqualTo(new[] { "alpha", "beta" }));
            Assert.That(created.References, Is.EqualTo(new[] { "ref-1" }));
            Assert.That(_store.Load("new-memory"), Is.Not.Null);
            Assert.That(_events.Events.Single().Action, Is.EqualTo("Created"));
            Assert.That(_publisher.MemoryUpdates.Single().Action, Is.EqualTo("Created"));
            Assert.That(_publisher.StatsUpdates.Single().TotalCount, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task SearchAsync_AppliesQueryStatusTagsAndLimitClamp()
    {
        for (var i = 0; i < 105; i++)
        {
            _store.Save(new MemoryRecord
            {
                Id = $"match-{i:D3}",
                Title = $"Match {i:D3}",
                Content = "needle content",
                Status = MemoryStatus.Working,
                Tags = ["alpha"],
                LastUpdated = new DateTime(2026, 05, 01, 0, 0, 0, DateTimeKind.Utc).AddMinutes(i)
            });
        }
        _store.Save(new MemoryRecord { Id = "wrong-tag", Title = "needle", Content = "needle", Status = MemoryStatus.Working, Tags = ["beta"] });
        _store.Save(new MemoryRecord { Id = "wrong-status", Title = "needle", Content = "needle", Status = MemoryStatus.Core, Tags = ["alpha"] });

        var results = await _service.SearchAsync(
            new MemorySearchQuery(Query: "needle", Status: MemoryStatus.Working, Tags: "alpha", Limit: 500),
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(results, Has.Count.EqualTo(100));
            Assert.That(results.First().Id, Is.EqualTo("match-104"));
            Assert.That(results.Select(x => x.Id), Does.Not.Contain("wrong-tag"));
            Assert.That(results.Select(x => x.Id), Does.Not.Contain("wrong-status"));
        });
    }

    [Test]
    public async Task SemanticSearchAsync_ReturnsMetadataScoreAndMatchReason()
    {
        _store.Save(new MemoryRecord
        {
            Id = "semantic-result",
            Title = "MCP Search Tool",
            Content = "Tooling for model context protocol search.",
            Status = MemoryStatus.Core,
            Confidence = 0.87,
            Tags = ["project-wiki", "mcp"],
            UsageCount = 7,
            LastUpdated = new DateTime(2026, 05, 12, 0, 0, 0, DateTimeKind.Utc)
        });

        var results = await _service.SemanticSearchAsync(
            new SemanticMemorySearchQuery(Query: "model context protocol", Tags: "project-wiki", Limit: 5),
            CancellationToken.None);

        var result = results.Single();
        Assert.Multiple(() =>
        {
            Assert.That(result.Id, Is.EqualTo("semantic-result"));
            Assert.That(result.Status, Is.EqualTo(MemoryStatus.Core));
            Assert.That(result.Confidence, Is.EqualTo(0.87));
            Assert.That(result.UsageCount, Is.EqualTo(7));
            Assert.That(result.Score, Is.GreaterThan(0));
            Assert.That(result.MatchReason, Does.Contain("title"));
            Assert.That(result.Snippet, Does.Contain("model context protocol"));
        });
    }

    [Test]
    public async Task HybridSearchAsync_FusesLexicalAndSemanticRanksWithRrf()
    {
        _store.Save(new MemoryRecord
        {
            Id = "hybrid-result",
            Title = "Hybrid Search RRF",
            Content = "Lucene style lexical analysis combines with semantic vector retrieval through reciprocal rank fusion.",
            Status = MemoryStatus.Core,
            Confidence = 0.92,
            Tags = ["project-wiki", "search"],
            UsageCount = 4,
            LastUpdated = new DateTime(2026, 05, 12, 0, 0, 0, DateTimeKind.Utc)
        });
        _store.Save(new MemoryRecord
        {
            Id = "semantic-only",
            Title = "Embedding Search Roadmap",
            Content = "Conceptual similarity and vector scoring are future search improvements.",
            Status = MemoryStatus.Core,
            Tags = ["project-wiki", "search"],
            LastUpdated = new DateTime(2026, 05, 13, 0, 0, 0, DateTimeKind.Utc)
        });

        var results = await _service.HybridSearchAsync(
            new HybridMemorySearchQuery(Query: "lucene vector fusion", Tags: "project-wiki", Limit: 5),
            CancellationToken.None);

        var result = results.First();
        Assert.Multiple(() =>
        {
            Assert.That(result.Id, Is.EqualTo("hybrid-result"));
            Assert.That(result.Score, Is.GreaterThan(0));
            Assert.That(result.MatchReason, Does.Contain("RRF"));
            Assert.That(result.MatchReason, Does.Contain("lexical rank"));
            Assert.That(result.MatchReason, Does.Contain("semantic rank"));
            Assert.That(result.Snippet, Does.Contain("semantic vector retrieval"));
        });
    }

    [Test]
    public async Task IncrementUsageAsync_UpdatesRecordAuditsAndPublishesStats()
    {
        _store.Save(new MemoryRecord { Id = "usage", Title = "Usage", Content = "Track me", UsageCount = 2 });

        var updated = await _service.IncrementUsageAsync("usage", CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(updated, Is.Not.Null);
            Assert.That(updated!.UsageCount, Is.EqualTo(3));
            Assert.That(_store.Load("usage")!.UsageCount, Is.EqualTo(3));
            Assert.That(_events.Events.Single().Action, Is.EqualTo("UsageIncremented"));
            Assert.That(_publisher.MemoryUpdates.Single().Action, Is.EqualTo("UsageIncremented"));
            Assert.That(_publisher.StatsUpdates.Single().TotalUsage, Is.EqualTo(3));
        });
    }
}

internal static class TestServiceFactory
{
    public static MemoryApplicationService CreateMemoryApplicationService(
        IMemoryStore store,
        IEventStore eventStore,
        IMemoryChangePublisher publisher)
    {
        return new MemoryApplicationService(
            store,
            eventStore,
            new MemorySmith.Core.Indexing.MemoryIndex(),
            new BackgroundServiceTelemetryTracker(),
            publisher,
            Options.Create(new MemorySmithOptions()));
    }
}

internal sealed class RecordingEventStore : IEventStore
{
    public List<MemoryEvent> Events { get; } = [];

    public void AppendEvent(MemoryEvent @event) => Events.Add(@event);

    public IEnumerable<MemoryEvent> GetEvents(string? memoryId = null, DateTime? since = null) =>
        Events.Where(e =>
            (memoryId is null || e.MemoryId == memoryId) &&
            (!since.HasValue || e.Timestamp >= since.Value));
}

internal sealed class RecordingMemoryChangePublisher : IMemoryChangePublisher
{
    public event Func<MemoryUpdateEvent, Task>? MemoryChanged;
    public event Func<StatsSnapshot, Task>? StatsChanged;

    public List<MemoryUpdateEvent> MemoryUpdates { get; } = [];
    public List<StatsSnapshot> StatsUpdates { get; } = [];

    public async Task PublishMemoryChangedAsync(MemoryUpdateEvent update)
    {
        MemoryUpdates.Add(update);
        if (MemoryChanged is not null)
        {
            await MemoryChanged(update);
        }
    }

    public async Task PublishStatsChangedAsync(StatsSnapshot stats)
    {
        StatsUpdates.Add(stats);
        if (StatsChanged is not null)
        {
            await StatsChanged(stats);
        }
    }
}