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
    public async Task SearchAsync_UsesLuceneLexicalTokensInsteadOfSubstringContains()
    {
        _store.Save(new MemoryRecord
        {
            Id = "lexical-tokenized",
            Title = "Tokenized Lexical Search",
            Content = "The model context protocol path should match hyphenated lexical queries.",
            Status = MemoryStatus.Core,
            Tags = ["search"],
            LastUpdated = new DateTime(2026, 05, 01, 0, 0, 0, DateTimeKind.Utc)
        });
        _store.Save(new MemoryRecord
        {
            Id = "newer-unrelated",
            Title = "Newer Unrelated",
            Content = "A newer record should not win without lexical token overlap.",
            Status = MemoryStatus.Core,
            Tags = ["search"],
            LastUpdated = new DateTime(2026, 05, 02, 0, 0, 0, DateTimeKind.Utc)
        });

        var results = await _service.SearchAsync(
            new MemorySearchQuery(Query: "model-context", Status: MemoryStatus.Core, Tags: "search", Limit: 5),
            CancellationToken.None);

        Assert.That(results.Select(result => result.Id), Is.EqualTo(new[] { "lexical-tokenized" }));
    }

    [Test]
    public async Task SearchMetadataAsync_AttachesDiagnosticsFromSingleStorageSnapshot()
    {
        var store = new CountingMemoryStore();
        var options = Options.Create(new MemorySmithOptions());
        var diagnostics = new MemoryDiagnosticsService(
            new TagPolicyService(options),
            new VarResolver(new EmptyVarStore(), options),
            store,
            options);
        var service = new MemoryApplicationService(
            store,
            _events,
            new MemorySmith.Core.Indexing.MemoryIndex(),
            new BackgroundServiceTelemetryTracker(),
            _publisher,
            options,
            diagnostics: diagnostics);

        for (var i = 0; i < 25; i++)
        {
            store.Save(new MemoryRecord
            {
                Id = $"diagnostic-hit-{i:D2}",
                Title = $"Needle {i:D2}",
                Content = "needle content",
                Tags = ["working"],
                Confidence = 1,
                LastUpdated = DateTime.UtcNow
            });
        }

        var results = await service.SearchMetadataAsync(new MemorySearchQuery("needle", Limit: 10), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(results, Has.Count.EqualTo(10));
            Assert.That(results, Has.All.Matches<MemoryMetadata>(metadata =>
                metadata.Diagnostics.Any(diagnostic => diagnostic.Code == "tag.blocked")));
            Assert.That(store.LoadAllCallCount, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task LexicalSearchAsync_KeepsWarningRecordsRetrievableAndBuildsEnvelopeWarnings()
    {
        var options = Options.Create(new MemorySmithOptions());
        var diagnostics = new MemoryDiagnosticsService(
            new TagPolicyService(options),
            new VarResolver(new EmptyVarStore(), options),
            _store,
            options);
        var service = TestServiceFactory.CreateMemoryApplicationService(_store, _events, _publisher, diagnostics: diagnostics);
        _store.Save(new MemoryRecord
        {
            Id = "lexical-warning-record",
            Title = "Lexical Warning Record",
            Content = "retrieval warning propagation token",
            Status = MemoryStatus.Core,
            Tags = ["project-wiki"],
            Confidence = 1,
            SourceLinks = [new SourceLink { Uri = "%MissingVariable%MemorySmith.App/Program.cs" }]
        });

        var results = await service.LexicalSearchAsync(new MemorySearchQuery("retrieval warning propagation", Limit: 5), CancellationToken.None);
        var envelope = service.BuildRetrievalEnvelope("lexical", MemoryApplicationService.GetLexicalProviderMetadata(), results);

        Assert.Multiple(() =>
        {
            Assert.That(results.Single().Id, Is.EqualTo("lexical-warning-record"));
            Assert.That(results.Single().Diagnostics.Select(diagnostic => diagnostic.Code), Does.Contain("source.missing_variable"));
            Assert.That(envelope.SchemaVersion, Is.EqualTo("memorysmith.retrieval-results.v1"));
            Assert.That(envelope.Provider.Kind, Is.EqualTo("lexical"));
            Assert.That(envelope.Warnings, Has.Some.Contains("source.missing_variable"));
        });
    }

    [Test]
    public async Task GetMemoriesAsync_ToleratesDuplicateIdsAndUsesLatestRecord()
    {
        var store = new DuplicateMemoryStore(
        [
            new MemoryRecord
            {
                Id = "duplicate-id",
                Title = "Older Duplicate",
                Content = "older duplicate",
                LastUpdated = new DateTime(2026, 05, 01, 0, 0, 0, DateTimeKind.Utc)
            },
            new MemoryRecord
            {
                Id = "DUPLICATE-ID",
                Title = "Newer Duplicate",
                Content = "newer duplicate",
                LastUpdated = new DateTime(2026, 05, 02, 0, 0, 0, DateTimeKind.Utc)
            },
            new MemoryRecord
            {
                Id = "unique-id",
                Title = "Unique",
                Content = "unique",
                LastUpdated = new DateTime(2026, 05, 03, 0, 0, 0, DateTimeKind.Utc)
            }
        ]);
        var service = TestServiceFactory.CreateMemoryApplicationService(store, _events, _publisher);

        var result = await service.GetMemoriesAsync(new MemoryListQuery(PageSize: 10), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Data, Has.Count.EqualTo(2));
            Assert.That(result.Data.Select(metadata => metadata.Id), Is.Unique.IgnoreCase);
            Assert.That(result.Data.Single(metadata => string.Equals(metadata.Id, "DUPLICATE-ID", StringComparison.OrdinalIgnoreCase)).Title, Is.EqualTo("Newer Duplicate"));
        });
    }

    [Test]
    public async Task SearchMetadataAsync_ToleratesDuplicateIdsInSearchSnapshot()
    {
        var store = new DuplicateMemoryStore(
        [
            new MemoryRecord
            {
                Id = "duplicate-id",
                Title = "Older Needle",
                Content = "needle older duplicate",
                LastUpdated = new DateTime(2026, 05, 01, 0, 0, 0, DateTimeKind.Utc)
            },
            new MemoryRecord
            {
                Id = "DUPLICATE-ID",
                Title = "Newer Needle",
                Content = "needle newer duplicate",
                LastUpdated = new DateTime(2026, 05, 02, 0, 0, 0, DateTimeKind.Utc)
            },
            new MemoryRecord
            {
                Id = "unique-id",
                Title = "Unique Needle",
                Content = "needle unique",
                LastUpdated = new DateTime(2026, 05, 03, 0, 0, 0, DateTimeKind.Utc)
            }
        ]);
        var service = TestServiceFactory.CreateMemoryApplicationService(store, _events, _publisher);

        var results = await service.SearchMetadataAsync(new MemorySearchQuery("needle", Limit: 10), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(results, Has.Count.EqualTo(2));
            Assert.That(results.Select(metadata => metadata.Id), Is.Unique.IgnoreCase);
            Assert.That(results.Single(metadata => string.Equals(metadata.Id, "DUPLICATE-ID", StringComparison.OrdinalIgnoreCase)).Title, Is.EqualTo("Newer Needle"));
        });
    }

    [Test]
    public async Task HybridSearchAsync_AttachesDiagnosticsAfterApplyingLimit()
    {
        var store = new CountingMemoryStore();
        var varStore = new CountingVarStore();
        var options = Options.Create(new MemorySmithOptions());
        var diagnostics = new MemoryDiagnosticsService(
            new TagPolicyService(options),
            new VarResolver(varStore, options),
            store,
            options);
        var service = new MemoryApplicationService(
            store,
            _events,
            new MemorySmith.Core.Indexing.MemoryIndex(),
            new BackgroundServiceTelemetryTracker(),
            _publisher,
            options,
            diagnostics: diagnostics);

        for (var i = 0; i < 20; i++)
        {
            store.Save(new MemoryRecord
            {
                Id = $"hybrid-diagnostic-hit-{i:D2}",
                Title = $"Hybrid Needle {i:D2}",
                Content = "hybrid needle content",
                Tags = ["project-wiki"],
                Confidence = 1,
                LastUpdated = DateTime.UtcNow.AddMinutes(-i)
            });
        }

        var results = await service.HybridSearchAsync(new HybridMemorySearchQuery("hybrid needle", Limit: 3), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(results, Has.Count.EqualTo(3));
            Assert.That(varStore.LoadCallCount, Is.EqualTo(3));
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
    public async Task SemanticSearchAsync_ReusesCachedDocumentEmbeddingsAcrossRepeatedQueries()
    {
        var tempRoot = CreateSemanticCacheTempRoot();

        try
        {
            var options = CreateSemanticCacheOptions(tempRoot);
            var provider = new CountingTextEmbeddingProvider();
            using var embeddingSearch = new SemanticEmbeddingSearchService(provider, options);
            var service = TestServiceFactory.CreateMemoryApplicationService(_store, _events, _publisher, embeddingSearch, options: options);

            _store.Save(new MemoryRecord
            {
                Id = "cached-match",
                Title = "Cached Match",
                Content = "relevant target",
                Status = MemoryStatus.Core,
                Tags = ["search"],
                LastUpdated = new DateTime(2026, 05, 19, 0, 0, 0, DateTimeKind.Utc)
            });
            _store.Save(new MemoryRecord
            {
                Id = "cached-miss",
                Title = "Cached Miss",
                Content = "unrelated content",
                Status = MemoryStatus.Core,
                Tags = ["search"],
                LastUpdated = new DateTime(2026, 05, 20, 0, 0, 0, DateTimeKind.Utc)
            });

            var first = await service.SemanticSearchAsync(
                new SemanticMemorySearchQuery(Query: "durable recall", Tags: "search", Limit: 5),
                CancellationToken.None);
            var second = await service.SemanticSearchAsync(
                new SemanticMemorySearchQuery(Query: "durable recall", Tags: "search", Limit: 5),
                CancellationToken.None);

            Assert.Multiple(() =>
            {
                Assert.That(first.Select(result => result.Id), Is.EqualTo(new[] { "cached-match" }));
                Assert.That(second.Select(result => result.Id), Is.EqualTo(new[] { "cached-match" }));
                Assert.That(provider.DocumentEmbeddingsRequested, Is.EqualTo(2), "Each document should be embedded once and then served from cache.");
                Assert.That(provider.QueryEmbeddingsRequested, Is.EqualTo(1), "Repeated identical queries should reuse the cached query embedding.");
            });
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Test]
    public async Task SemanticSearchAsync_RefreshesCachedDocumentEmbeddingsWhenRecordTextChanges()
    {
        var tempRoot = CreateSemanticCacheTempRoot();

        try
        {
            var options = CreateSemanticCacheOptions(tempRoot);
            var provider = new CountingTextEmbeddingProvider();
            using var embeddingSearch = new SemanticEmbeddingSearchService(provider, options);
            var service = TestServiceFactory.CreateMemoryApplicationService(_store, _events, _publisher, embeddingSearch, options: options);

            _store.Save(new MemoryRecord
            {
                Id = "cache-invalidation",
                Title = "Cache Invalidation",
                Content = "relevant target",
                Status = MemoryStatus.Core,
                Tags = ["search"],
                LastUpdated = new DateTime(2026, 05, 21, 0, 0, 0, DateTimeKind.Utc)
            });

            var before = await service.SemanticSearchAsync(
                new SemanticMemorySearchQuery(Query: "durable recall", Tags: "search", Limit: 5),
                CancellationToken.None);

            _store.Save(new MemoryRecord
            {
                Id = "cache-invalidation",
                Title = "Cache Invalidation",
                Content = "unrelated content",
                Status = MemoryStatus.Core,
                Tags = ["search"],
                LastUpdated = new DateTime(2026, 05, 22, 0, 0, 0, DateTimeKind.Utc)
            });

            var after = await service.SemanticSearchAsync(
                new SemanticMemorySearchQuery(Query: "durable recall", Tags: "search", Limit: 5),
                CancellationToken.None);

            Assert.Multiple(() =>
            {
                Assert.That(before.Select(result => result.Id), Is.EqualTo(new[] { "cache-invalidation" }));
                Assert.That(after, Is.Empty, "Changed record text should invalidate the cached document embedding.");
                Assert.That(provider.DocumentEmbeddingsRequested, Is.EqualTo(2), "The changed record should be re-embedded after its text changes.");
            });
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Test]
    public async Task SemanticSearchAsync_ReusesPersistedDocumentEmbeddingsAcrossServiceRecreation()
    {
        var tempRoot = CreateSemanticCacheTempRoot();

        try
        {
            var options = CreateSemanticCacheOptions(tempRoot);
            _store.Save(new MemoryRecord
            {
                Id = "persisted-match",
                Title = "Persisted Match",
                Content = "relevant target",
                Status = MemoryStatus.Core,
                Tags = ["search"],
                LastUpdated = new DateTime(2026, 05, 23, 0, 0, 0, DateTimeKind.Utc)
            });
            _store.Save(new MemoryRecord
            {
                Id = "persisted-miss",
                Title = "Persisted Miss",
                Content = "unrelated content",
                Status = MemoryStatus.Core,
                Tags = ["search"],
                LastUpdated = new DateTime(2026, 05, 24, 0, 0, 0, DateTimeKind.Utc)
            });

            var firstProvider = new CountingTextEmbeddingProvider();
            using var firstSearch = new SemanticEmbeddingSearchService(firstProvider, options);
            var firstService = TestServiceFactory.CreateMemoryApplicationService(_store, _events, _publisher, firstSearch, options: options);
            var first = await firstService.SemanticSearchAsync(
                new SemanticMemorySearchQuery(Query: "durable recall", Tags: "search", Limit: 5),
                CancellationToken.None);

            var secondProvider = new CountingTextEmbeddingProvider();
            using var secondSearch = new SemanticEmbeddingSearchService(secondProvider, options);
            var secondService = TestServiceFactory.CreateMemoryApplicationService(_store, _events, _publisher, secondSearch, options: options);
            var second = await secondService.SemanticSearchAsync(
                new SemanticMemorySearchQuery(Query: "durable recall", Tags: "search", Limit: 5),
                CancellationToken.None);

            Assert.Multiple(() =>
            {
                Assert.That(first.Select(result => result.Id), Is.EqualTo(new[] { "persisted-match" }));
                Assert.That(second.Select(result => result.Id), Is.EqualTo(new[] { "persisted-match" }));
                Assert.That(firstProvider.DocumentEmbeddingsRequested, Is.EqualTo(2));
                Assert.That(secondProvider.DocumentEmbeddingsRequested, Is.EqualTo(0), "Recreated services should load unchanged document embeddings from disk.");
                Assert.That(secondProvider.QueryEmbeddingsRequested, Is.EqualTo(1), "Query embeddings remain process-local and should be recomputed after service recreation.");
            });
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Test]
    public async Task SemanticSearchAsync_InvalidatesPersistedDocumentEmbeddingsWhenRecordTextChangesAcrossServiceRecreation()
    {
        var tempRoot = CreateSemanticCacheTempRoot();

        try
        {
            var options = CreateSemanticCacheOptions(tempRoot);
            _store.Save(new MemoryRecord
            {
                Id = "persisted-invalidation",
                Title = "Persisted Invalidation",
                Content = "relevant target",
                Status = MemoryStatus.Core,
                Tags = ["search"],
                LastUpdated = new DateTime(2026, 05, 25, 0, 0, 0, DateTimeKind.Utc)
            });

            var firstProvider = new CountingTextEmbeddingProvider();
            using (var firstSearch = new SemanticEmbeddingSearchService(firstProvider, options))
            {
                var firstService = TestServiceFactory.CreateMemoryApplicationService(_store, _events, _publisher, firstSearch, options: options);
                var first = await firstService.SemanticSearchAsync(
                    new SemanticMemorySearchQuery(Query: "durable recall", Tags: "search", Limit: 5),
                    CancellationToken.None);

                Assert.That(first.Select(result => result.Id), Is.EqualTo(new[] { "persisted-invalidation" }));
            }

            _store.Save(new MemoryRecord
            {
                Id = "persisted-invalidation",
                Title = "Persisted Invalidation",
                Content = "unrelated content",
                Status = MemoryStatus.Core,
                Tags = ["search"],
                LastUpdated = new DateTime(2026, 05, 26, 0, 0, 0, DateTimeKind.Utc)
            });

            var secondProvider = new CountingTextEmbeddingProvider();
            using var secondSearch = new SemanticEmbeddingSearchService(secondProvider, options);
            var secondService = TestServiceFactory.CreateMemoryApplicationService(_store, _events, _publisher, secondSearch, options: options);
            var second = await secondService.SemanticSearchAsync(
                new SemanticMemorySearchQuery(Query: "durable recall", Tags: "search", Limit: 5),
                CancellationToken.None);

            Assert.Multiple(() =>
            {
                Assert.That(second, Is.Empty);
                Assert.That(secondProvider.DocumentEmbeddingsRequested, Is.EqualTo(1), "Changed text should invalidate the persisted document embedding and force one fresh embed.");
            });
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Test]
    public async Task SemanticSearchAsync_InvalidatesPersistedDocumentEmbeddingsWhenPoolingModeChangesAcrossServiceRecreation()
    {
        var tempRoot = CreateSemanticCacheTempRoot();

        try
        {
            var meanOptions = CreateSemanticCacheOptions(tempRoot, poolingMode: "Mean");
            var clsOptions = CreateSemanticCacheOptions(tempRoot, poolingMode: "Cls");

            _store.Save(new MemoryRecord
            {
                Id = "persisted-pooling-change",
                Title = "Persisted Pooling Change",
                Content = "relevant target",
                Status = MemoryStatus.Core,
                Tags = ["search"],
                LastUpdated = new DateTime(2026, 05, 27, 0, 0, 0, DateTimeKind.Utc)
            });

            var firstProvider = new CountingTextEmbeddingProvider();
            using (var firstSearch = new SemanticEmbeddingSearchService(firstProvider, meanOptions))
            {
                var firstService = TestServiceFactory.CreateMemoryApplicationService(_store, _events, _publisher, firstSearch, options: meanOptions);
                var first = await firstService.SemanticSearchAsync(
                    new SemanticMemorySearchQuery(Query: "durable recall", Tags: "search", Limit: 5),
                    CancellationToken.None);

                Assert.That(first.Select(result => result.Id), Is.EqualTo(new[] { "persisted-pooling-change" }));
            }

            var secondProvider = new CountingTextEmbeddingProvider();
            using var secondSearch = new SemanticEmbeddingSearchService(secondProvider, clsOptions);
            var secondService = TestServiceFactory.CreateMemoryApplicationService(_store, _events, _publisher, secondSearch, options: clsOptions);
            var second = await secondService.SemanticSearchAsync(
                new SemanticMemorySearchQuery(Query: "durable recall", Tags: "search", Limit: 5),
                CancellationToken.None);

            Assert.Multiple(() =>
            {
                Assert.That(second.Select(result => result.Id), Is.EqualTo(new[] { "persisted-pooling-change" }));
                Assert.That(secondProvider.DocumentEmbeddingsRequested, Is.EqualTo(1), "Changing pooling mode should invalidate persisted document embeddings because the provider semantics changed.");
            });
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    private static string CreateSemanticCacheTempRoot()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"memorysmith-semantic-cache-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(tempRoot, "Memories"));
        return tempRoot;
    }

    private static IOptions<MemorySmithOptions> CreateSemanticCacheOptions(string tempRoot, string poolingMode = "Mean", string tokenizerKind = "WordPiece") =>
        Options.Create(new MemorySmithOptions
        {
            DataPath = Path.Combine(tempRoot, "Memories"),
            SemanticSearch = new SemanticSearchOptions
            {
                EmbeddingsEnabled = true,
                ModelPath = Path.Combine("Models", "embedding-model.onnx"),
                VocabularyPath = Path.Combine("Models", "vocab.txt"),
                TokenizerKind = tokenizerKind,
                PoolingMode = poolingMode,
                MaxInputTokens = 512,
                MaxIndexedTextCharacters = 6000,
                QueryPrefix = "query: ",
                DocumentPrefix = "passage: "
            }
        });

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
    public async Task BuildContextPackAsync_AddsOnlyWarningDiagnosticsToWarningSummaries()
    {
        var options = Options.Create(new MemorySmithOptions());
        var diagnostics = new MemoryDiagnosticsService(
            new TagPolicyService(options),
            new VarResolver(new EmptyVarStore(), options),
            _store,
            options);
        var service = new MemoryApplicationService(
            _store,
            _events,
            new MemorySmith.Core.Indexing.MemoryIndex(),
            new BackgroundServiceTelemetryTracker(),
            _publisher,
            options,
            diagnostics: diagnostics);
        _store.Save(new MemoryRecord
        {
            Id = "diagnostic-root",
            Title = "Diagnostic Root",
            Content = "Context pack diagnostics should keep warning signal without alias noise.",
            Status = MemoryStatus.Core,
            Tags = ["retrieval", "working"],
            Confidence = 1,
            LastUpdated = DateTime.UtcNow
        });

        var pack = await service.BuildContextPackAsync(
            new MemoryContextPackQuery(Ids: "diagnostic-root", ReferenceDepth: 0),
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(pack.Records.Single().Diagnostics.Select(diagnostic => diagnostic.Code), Does.Contain("tag.alias"));
            Assert.That(pack.Warnings, Has.Some.Contains("tag.blocked"));
            Assert.That(pack.Warnings, Has.None.Contains("tag.alias"));
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
        SemanticEmbeddingSearchService? semanticEmbeddings = null,
        MemoryDiagnosticsService? diagnostics = null,
        IOptions<MemorySmithOptions>? options = null)
    {
        return new MemoryApplicationService(
            store,
            eventStore,
            new MemorySmith.Core.Indexing.MemoryIndex(),
            new BackgroundServiceTelemetryTracker(),
            publisher,
            options ?? Options.Create(new MemorySmithOptions()),
            semanticEmbeddings,
            diagnostics: diagnostics);
    }
}

internal sealed class CountingMemoryStore : IMemoryStore
{
    private readonly Dictionary<string, MemoryRecord> _records = new(StringComparer.OrdinalIgnoreCase);

    public int LoadAllCallCount { get; private set; }

    public MemoryRecord? Load(string id) =>
        _records.TryGetValue(id, out var record) ? record : null;

    public void Save(MemoryRecord record) =>
        _records[record.Id] = record;

    public void Delete(string id) =>
        _records.Remove(id);

    public IEnumerable<MemoryRecord> LoadAll()
    {
        LoadAllCallCount++;
        return _records.Values.ToList();
    }
}

internal sealed class DuplicateMemoryStore : IMemoryStore
{
    private readonly List<MemoryRecord> _records;

    public DuplicateMemoryStore(IEnumerable<MemoryRecord> records)
    {
        _records = records.ToList();
    }

    public MemoryRecord? Load(string id) =>
        _records.FirstOrDefault(record => string.Equals(record.Id, id, StringComparison.OrdinalIgnoreCase));

    public void Save(MemoryRecord record)
    {
        Delete(record.Id);
        _records.Add(record);
    }

    public void Delete(string id) =>
        _records.RemoveAll(record => string.Equals(record.Id, id, StringComparison.OrdinalIgnoreCase));

    public IEnumerable<MemoryRecord> LoadAll() =>
        _records.ToList();
}

internal sealed class EmptyVarStore : IVarStore
{
    public IReadOnlyDictionary<string, string> Load() => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public void Save(IReadOnlyDictionary<string, string> vars)
    {
    }
}

internal sealed class CountingVarStore : IVarStore
{
    public int LoadCallCount { get; private set; }

    public IReadOnlyDictionary<string, string> Load()
    {
        LoadCallCount++;
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    public void Save(IReadOnlyDictionary<string, string> vars)
    {
    }
}

internal sealed class FakeTextEmbeddingProvider : ITextEmbeddingProvider
{
    public EmbeddingProviderStatus GetStatus() => new(true, "Fake embedding provider is available.", null, null, 2, "Cpu", "Cpu", null, null);

    public bool TryEmbed(string text, EmbeddingInputKind kind, out float[] embedding, out string? reason)
    {
        reason = null;
        embedding = kind == EmbeddingInputKind.Query || text.Contains("recall vector target", StringComparison.OrdinalIgnoreCase)
            ? [1, 0]
            : [0, 1];
        return true;
    }
}

internal sealed class CountingTextEmbeddingProvider : ITextEmbeddingProvider
{
    public int QueryEmbeddingsRequested { get; private set; }
    public int DocumentEmbeddingsRequested { get; private set; }

    public EmbeddingProviderStatus GetStatus() => new(true, "Counting embedding provider is available.", null, null, 2, "Cpu", "Cpu", null, null);

    public bool TryEmbed(string text, EmbeddingInputKind kind, out float[] embedding, out string? reason)
    {
        reason = null;

        if (kind == EmbeddingInputKind.Query)
        {
            QueryEmbeddingsRequested++;
            embedding = [1, 0];
            return true;
        }

        DocumentEmbeddingsRequested++;
        embedding = text.Contains("relevant target", StringComparison.OrdinalIgnoreCase)
            ? [1, 0]
            : [0, 1];
        return true;
    }
}

internal sealed class RecordingEventStore : IEventStore
{
    public List<MemoryEvent> Events { get; } = [];

    public int GetEventsCallCount { get; private set; }

    public void AppendEvent(MemoryEvent @event) => Events.Add(@event);

    public IEnumerable<MemoryEvent> GetEvents(string? memoryId = null, DateTime? since = null)
    {
        GetEventsCallCount++;
        return Events.Where(e =>
            (memoryId is null || e.MemoryId == memoryId) &&
            (!since.HasValue || e.Timestamp >= since.Value));
    }
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