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
    public async Task CreateAsync_PreservesSourceLinkLineRanges()
    {
        var record = new MemoryRecord
        {
            Id = "source-link-range",
            Title = "Source Link Range",
            Content = "Range metadata should survive normalization.",
            SourceLinks =
            [
                new SourceLink
                {
                    Label = " file ",
                    Uri = " %MemorySmithRepo%file.cs ",
                    StartLine = 10,
                    EndLine = 20
                }
            ]
        };

        var created = await _service.CreateAsync(record, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(created.SourceLinks.Single().Label, Is.EqualTo("file"));
            Assert.That(created.SourceLinks.Single().Uri, Is.EqualTo("%MemorySmithRepo%file.cs"));
            Assert.That(created.SourceLinks.Single().StartLine, Is.EqualTo(10));
            Assert.That(created.SourceLinks.Single().EndLine, Is.EqualTo(20));
        });
    }

    [Test]
    public void CreateAsync_WithInvalidSourceLinkRange_ThrowsValidation()
    {
        var record = new MemoryRecord
        {
            Id = "bad-source-link-range",
            Title = "Bad Range",
            Content = "Invalid range metadata should be rejected.",
            SourceLinks = [new SourceLink { Uri = "%MemorySmithRepo%file.cs", StartLine = 20, EndLine = 10 }]
        };

        var exception = Assert.ThrowsAsync<MemoryValidationException>(async () =>
            await _service.CreateAsync(record, CancellationToken.None));

        Assert.That(exception!.Errors.Keys, Does.Contain(nameof(MemoryRecord.SourceLinks)));
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
    public async Task SemanticSearchAsync_UsesEmbeddingRankerWhenAvailable()
    {
        var embeddingSearch = new SemanticEmbeddingSearchService(
            new FakeTextEmbeddingProvider(),
            Options.Create(new MemorySmithOptions()));
        var service = TestServiceFactory.CreateMemoryApplicationService(_store, _events, _publisher, embeddingSearch);
        _store.Save(new MemoryRecord
        {
            Id = "embedding-match",
            Title = "Embedding Match",
            Content = "recall vector target",
            Status = MemoryStatus.Core,
            Tags = ["search"],
            LastUpdated = new DateTime(2026, 05, 17, 0, 0, 0, DateTimeKind.Utc)
        });
        _store.Save(new MemoryRecord
        {
            Id = "embedding-miss",
            Title = "Embedding Miss",
            Content = "unrelated content",
            Status = MemoryStatus.Core,
            Tags = ["search"],
            LastUpdated = new DateTime(2026, 05, 18, 0, 0, 0, DateTimeKind.Utc)
        });

        var results = await service.SemanticSearchAsync(
            new SemanticMemorySearchQuery(Query: "durable recall", Tags: "search", Limit: 5),
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(results.Select(result => result.Id), Is.EqualTo(new[] { "embedding-match" }));
            Assert.That(results.Single().MatchReason, Does.Contain("Embedding cosine similarity"));
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
    public async Task BuildContextPackAsync_IncludesHybridRootsAndLinkedRecords()
    {
        _store.Save(new MemoryRecord
        {
            Id = "root-memory",
            Title = "Hybrid MCP Context Pack",
            Content = "The MCP context pack starts from hybrid search and follows linked project memories.",
            Status = MemoryStatus.Core,
            Tags = ["project-wiki", "mcp", "search"],
            References = ["linked-memory"],
            LastUpdated = new DateTime(2026, 05, 12, 0, 0, 0, DateTimeKind.Utc)
        });
        _store.Save(new MemoryRecord
        {
            Id = "linked-memory",
            Title = "Linked Tool Detail",
            Content = "Referenced context that should be packaged with the root result.",
            Status = MemoryStatus.Core,
            Tags = ["project-wiki", "mcp"],
            LastUpdated = new DateTime(2026, 05, 11, 0, 0, 0, DateTimeKind.Utc)
        });

        var pack = await _service.BuildContextPackAsync(
            new MemoryContextPackQuery(Query: "hybrid mcp context pack", Tags: "project-wiki", Limit: 1, ReferenceDepth: 1),
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(pack.Query, Is.EqualTo("hybrid mcp context pack"));
            Assert.That(pack.Records.Select(record => record.Id), Is.EqualTo(new[] { "root-memory", "linked-memory" }));
            Assert.That(pack.Records[0].Relationship, Is.EqualTo("root"));
            Assert.That(pack.Records[0].MatchReason, Does.Contain("RRF"));
            Assert.That(pack.Records[1].Relationship, Is.EqualTo("reference of root-memory"));
            Assert.That(pack.Records[1].Content, Does.Contain("Referenced context"));
        });
    }

    [Test]
    public async Task BuildContextPackAsync_WithIds_UsesExplicitRootsBeforeSearch()
    {
        _store.Save(new MemoryRecord
        {
            Id = "explicit-root",
            Title = "Explicit Root",
            Content = "Known record selected by id should be included even when the query does not match.",
            Status = MemoryStatus.Core,
            Tags = ["project-wiki"],
            References = ["explicit-link"]
        });
        _store.Save(new MemoryRecord
        {
            Id = "explicit-link",
            Title = "Explicit Link",
            Content = "Linked record expanded from explicit root.",
            Status = MemoryStatus.Core,
            Tags = ["project-wiki"]
        });

        var pack = await _service.BuildContextPackAsync(
            new MemoryContextPackQuery(Query: "unrelated terms", Tags: "project-wiki", Limit: 1, ReferenceDepth: 1, Ids: "explicit-root"),
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(pack.Records.Select(record => record.Id), Is.EqualTo(new[] { "explicit-root", "explicit-link" }));
            Assert.That(pack.Records[0].Relationship, Is.EqualTo("root"));
            Assert.That(pack.Records[0].MatchReason, Is.EqualTo("Explicit root id."));
            Assert.That(pack.Records[1].Relationship, Is.EqualTo("reference of explicit-root"));
        });
    }

    [Test]
    public async Task BuildContextPackAsync_WarnsForMissingRootsAndLinks()
    {
        _store.Save(new MemoryRecord
        {
            Id = "root-with-missing-link",
            Title = "Missing Link Root",
            Content = "Root references a missing memory id.",
            Status = MemoryStatus.Core,
            Tags = ["project-wiki"],
            References = ["missing-reference"]
        });

        var pack = await _service.BuildContextPackAsync(
            new MemoryContextPackQuery(Ids: "root-with-missing-link,missing-root", ReferenceDepth: 1),
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(pack.Records.Select(record => record.Id), Is.EqualTo(new[] { "root-with-missing-link" }));
            Assert.That(pack.Warnings, Does.Contain("Explicit root id 'missing-root' was not found."));
            Assert.That(pack.Warnings, Does.Contain("Reference 'missing-reference' from 'root-with-missing-link' was not found."));
        });
    }

    [Test]
    public async Task BuildContextPackAsync_IncludesBacklinksWhenRequested()
    {
        _store.Save(new MemoryRecord
        {
            Id = "root-with-backlink",
            Title = "Root With Backlink",
            Content = "Root selected by explicit id.",
            Status = MemoryStatus.Core,
            Tags = ["project-wiki"]
        });
        _store.Save(new MemoryRecord
        {
            Id = "incoming-reference",
            Title = "Incoming Reference",
            Content = "This record references the root and should be included as a backlink.",
            Status = MemoryStatus.Core,
            Tags = ["project-wiki"],
            References = ["root-with-backlink"]
        });

        var pack = await _service.BuildContextPackAsync(
            new MemoryContextPackQuery(Ids: "root-with-backlink", ReferenceDepth: 1, IncludeBacklinks: true),
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(pack.Records.Select(record => record.Id), Is.EqualTo(new[] { "root-with-backlink", "incoming-reference" }));
            Assert.That(pack.Records[1].Relationship, Is.EqualTo("references root-with-backlink"));
        });
    }

    [Test]
    public async Task BuildContextPackAsync_StopsAtMaxRecordsAndWarnsWhenExpansionIsOmitted()
    {
        _store.Save(new MemoryRecord
        {
            Id = "budget-root",
            Title = "Budget Root",
            Content = "Root with more linked records than the pack budget allows.",
            Status = MemoryStatus.Core,
            Tags = ["project-wiki"],
            References = ["budget-link-1", "budget-link-2", "budget-link-3"]
        });

        for (var i = 1; i <= 3; i++)
        {
            _store.Save(new MemoryRecord
            {
                Id = $"budget-link-{i}",
                Title = $"Budget Link {i}",
                Content = "Linked record that may be omitted by the context-pack budget.",
                Status = MemoryStatus.Core,
                Tags = ["project-wiki"]
            });
        }

        var pack = await _service.BuildContextPackAsync(
            new MemoryContextPackQuery(Ids: "budget-root", ReferenceDepth: 1, MaxRecords: 2),
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(pack.Records.Select(record => record.Id), Is.EqualTo(new[] { "budget-root", "budget-link-1" }));
            Assert.That(pack.Warnings, Does.Contain("Context pack hit maxRecords 2; additional records were omitted."));
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
        IMemoryChangePublisher publisher,
        SemanticEmbeddingSearchService? semanticEmbeddings = null)
    {
        return new MemoryApplicationService(
            store,
            eventStore,
            new MemorySmith.Core.Indexing.MemoryIndex(),
            new BackgroundServiceTelemetryTracker(),
            publisher,
            Options.Create(new MemorySmithOptions()),
            semanticEmbeddings);
    }
}

internal sealed class FakeTextEmbeddingProvider : ITextEmbeddingProvider
{
    public EmbeddingProviderStatus GetStatus() => new(true, "Fake embedding provider is available.", null, null, 2);

    public bool TryEmbed(string text, EmbeddingInputKind kind, out float[] embedding, out string? reason)
    {
        reason = null;
        embedding = kind == EmbeddingInputKind.Query || text.Contains("recall vector target", StringComparison.OrdinalIgnoreCase)
            ? [1, 0]
            : [0, 1];
        return true;
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