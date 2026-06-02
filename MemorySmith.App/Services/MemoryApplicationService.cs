using System.Text.RegularExpressions;
using System.Diagnostics;
using Lucene.Net.Analysis.Standard;
using Lucene.Net.Analysis.TokenAttributes;
using Lucene.Net.Util;
using Lucene.Net.Index;
using Lucene.Net.Search.Highlight;
using Lucene.Net.Search;
using System.Net;
using MemorySmith.Core.Indexing;
using MemorySmith.Core.Models;
using MemorySmith.Storage;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MemorySmith.App.Services;

public partial class MemoryApplicationService
{
    private const int ReciprocalRankFusionK = 60;
    private const LuceneVersion LuceneMatchVersion = LuceneVersion.LUCENE_48;

    private static readonly Regex SafeIdPattern = SafeIdRegex();
    private static readonly Regex SearchTokenPattern = SearchTokenRegex();
    private static readonly HashSet<string> SearchStopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "and", "are", "as", "at", "be", "by", "for", "from", "has", "in", "is", "it", "of", "on", "or", "that", "the", "this", "to", "with"
    };

    private static readonly Dictionary<string, string[]> SearchAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["mcp"] = ["model", "context", "protocol", "tool", "tools", "integration", "json", "rpc"],
        ["model"] = ["mcp"],
        ["context"] = ["mcp"],
        ["protocol"] = ["mcp"],
        ["semantic"] = ["meaning", "concept", "conceptual", "embedding", "embeddings", "vector", "similarity"],
        ["embedding"] = ["semantic", "vector", "similarity"],
        ["embeddings"] = ["semantic", "vector", "similarity"],
        ["search"] = ["find", "query", "lookup", "retrieval", "retrieve"],
        ["wiki"] = ["knowledge", "memory", "memories", "docs", "documentation"],
        ["testbase"] = ["fixture", "fixtures", "test", "tests", "validation", "temp"],
        ["friction"] = ["missing", "issue", "issues", "gap", "pain", "blocker"]
    };

    private readonly IMemoryStore _store;
    private readonly IEventStore _eventStore;
    private readonly MemoryIndex _index;
    private readonly BackgroundServiceTelemetryTracker _telemetryTracker;
    private readonly IMemoryChangePublisher _publisher;
    private readonly MemorySmithOptions _options;
    private readonly SemanticEmbeddingSearchService? _semanticEmbeddings;
    private readonly AuditLogService? _audit;
    private readonly VersionHistoryService? _history;
    private readonly MemoryDiagnosticsService? _diagnostics;
    private readonly ILogger<MemoryApplicationService> _logger;

    public MemoryApplicationService(
        IMemoryStore store,
        IEventStore eventStore,
        MemoryIndex index,
        BackgroundServiceTelemetryTracker telemetryTracker,
        IMemoryChangePublisher publisher,
        IOptions<MemorySmithOptions> options,
        SemanticEmbeddingSearchService? semanticEmbeddings = null,
        AuditLogService? audit = null,
        VersionHistoryService? history = null,
        MemoryDiagnosticsService? diagnostics = null,
        ILogger<MemoryApplicationService>? logger = null)
    {
        _store = store;
        _eventStore = eventStore;
        _index = index;
        _telemetryTracker = telemetryTracker;
        _publisher = publisher;
        _options = options.Value;
        _logger = logger ?? NullLogger<MemoryApplicationService>.Instance;
        _semanticEmbeddings = semanticEmbeddings;
        _audit = audit;
        _history = history;
        _diagnostics = diagnostics;
    }

    public Task<PagedResult<MemoryMetadata>> GetMemoriesAsync(MemoryListQuery query, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var page = Math.Max(1, query.Page);
        var pageSize = Clamp(query.PageSize, 1, _options.Limits.MaxPageSize, 20);
        var tagFilters = NormalizeFilterList(query.Tags);

        var allRecordsById = MemoryRecordLookup.ToRecordMap(_store.LoadAll());
        var allRecords = allRecordsById.Values.ToList();
        var records = ApplyListFilters(allRecords, query.Status, tagFilters)
            .OrderByDescending(r => r.LastUpdated)
            .ThenBy(r => r.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var data = records
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(record => ToMetadata(record, allRecordsById))
            .ToList();

        return Task.FromResult(new PagedResult<MemoryMetadata>
        {
            TotalCount = records.Count,
            Page = page,
            PageSize = pageSize,
            Data = data
        });
    }

    public Task<IReadOnlyList<MemoryRecord>> SearchAsync(MemorySearchQuery query, CancellationToken cancellationToken)
    {
        using var operation = StartTelemetryOperation("memory.search.lexical");
        var started = Stopwatch.GetTimestamp();
        var success = false;
        cancellationToken.ThrowIfCancellationRequested();

        RecordQueryEvent("lexical", query.Query);

        var limit = Clamp(query.Limit, 1, _options.Limits.MaxSearchLimit, 20);
        var lexicalTokens = AnalyzeLexicalText(query.Query ?? string.Empty);
        var snapshot = CreateSearchSnapshot(query.Status, query.Tags);

        var results = RankLexicalResults(snapshot.FilteredRecords, query.Query, lexicalTokens)
            .Take(limit)
            .Select(result => snapshot.FilteredRecordsById[result.Id])
            .ToList();
        success = true;
        LogBenchmark("memory.search.lexical", Stopwatch.GetElapsedTime(started).TotalMilliseconds, success, query: query.Query, resultCount: results.Count);
        return Task.FromResult<IReadOnlyList<MemoryRecord>>(results);
    }

    public Task<IReadOnlyList<MemoryMetadata>> SearchMetadataAsync(MemorySearchQuery query, CancellationToken cancellationToken)
    {
        using var operation = StartTelemetryOperation("memory.search.metadata");
        var started = Stopwatch.GetTimestamp();
        var success = false;
        cancellationToken.ThrowIfCancellationRequested();

        RecordQueryEvent("lexical", query.Query);

        var limit = Clamp(query.Limit, 1, _options.Limits.MaxSearchLimit, 20);
        var lexicalTokens = AnalyzeLexicalText(query.Query ?? string.Empty);
        var snapshot = CreateSearchSnapshot(query.Status, query.Tags);

        var results = RankLexicalResults(snapshot.FilteredRecords, query.Query, lexicalTokens)
            .Take(limit)
            .Select(result => ToMetadata(snapshot.FilteredRecordsById[result.Id], snapshot.AllRecordsById))
            .ToList();
        success = true;
        LogBenchmark("memory.search.metadata", Stopwatch.GetElapsedTime(started).TotalMilliseconds, success, query: query.Query, resultCount: results.Count);
        return Task.FromResult<IReadOnlyList<MemoryMetadata>>(results);
    }

    public Task<IReadOnlyList<MemorySearchResult>> LexicalSearchAsync(MemorySearchQuery query, CancellationToken cancellationToken)
    {
        using var operation = StartTelemetryOperation("memory.search.lexical.diagnostics");
        var started = Stopwatch.GetTimestamp();
        var success = false;
        cancellationToken.ThrowIfCancellationRequested();

        RecordQueryEvent("lexical", query.Query);

        var limit = Clamp(query.Limit, 1, _options.Limits.MaxSearchLimit, 20);
        var lexicalTokens = AnalyzeLexicalText(query.Query ?? string.Empty);
        var snapshot = CreateSearchSnapshot(query.Status, query.Tags);

        var results = RankLexicalResults(snapshot.FilteredRecords, query.Query, lexicalTokens)
            .Take(limit)
            .Select(result => snapshot.FilteredRecordsById.TryGetValue(result.Id, out var record)
                ? result with { Diagnostics = GetDiagnostics(record, snapshot.AllRecordsById) }
                : result)
            .ToList();
        success = true;
        LogBenchmark("memory.search.lexical.diagnostics", Stopwatch.GetElapsedTime(started).TotalMilliseconds, success, query: query.Query, resultCount: results.Count);
        return Task.FromResult<IReadOnlyList<MemorySearchResult>>(results);
    }

    public Task<IReadOnlyList<MemorySearchResult>> SemanticSearchAsync(SemanticMemorySearchQuery query, CancellationToken cancellationToken)
    {
        using var operation = StartTelemetryOperation("memory.search.semantic");
        var started = Stopwatch.GetTimestamp();
        var success = false;
        cancellationToken.ThrowIfCancellationRequested();

        RecordQueryEvent("semantic", query.Query);

        var limit = Clamp(query.Limit, 1, _options.Limits.MaxSearchLimit, 20);
        var queryTokens = ExpandSearchTokens(TokenizeSearchText(query.Query ?? string.Empty));
        var snapshot = CreateSearchSnapshot(query.Status, query.Tags);

        var results = RankSemanticResults(snapshot.FilteredRecords, query.Query, queryTokens)
            .Take(limit)
            .Select(result => snapshot.FilteredRecordsById.TryGetValue(result.Id, out var record)
                ? result with { Diagnostics = GetDiagnostics(record, snapshot.AllRecordsById) }
                : result)
            .ToList();
        success = true;
        LogBenchmark("memory.search.semantic", Stopwatch.GetElapsedTime(started).TotalMilliseconds, success, query: query.Query, resultCount: results.Count);
        return Task.FromResult<IReadOnlyList<MemorySearchResult>>(results);
    }

    public Task<IReadOnlyList<MemorySearchResult>> HybridSearchAsync(HybridMemorySearchQuery query, CancellationToken cancellationToken)
    {
        using var operation = StartTelemetryOperation("memory.search.hybrid");
        var started = Stopwatch.GetTimestamp();
        var success = false;
        cancellationToken.ThrowIfCancellationRequested();

        RecordQueryEvent("hybrid", query.Query);

        var limit = Clamp(query.Limit, 1, _options.Limits.MaxSearchLimit, 20);
        var snapshot = CreateSearchSnapshot(query.Status, query.Tags);
        var results = RankHybridResults(snapshot, query.Query, limit);
        success = true;
        LogBenchmark("memory.search.hybrid", Stopwatch.GetElapsedTime(started).TotalMilliseconds, success, query: query.Query, resultCount: results.Count);
        return Task.FromResult<IReadOnlyList<MemorySearchResult>>(results);
    }

    public RetrievalProviderMetadata GetSemanticProviderMetadata() =>
        _semanticEmbeddings?.GetProviderMetadata() ?? new RetrievalProviderMetadata(
            "semantic",
            "token-fallback",
            false,
            "Semantic embedding service is not configured; token fallback is active.");

    public static RetrievalProviderMetadata GetLexicalProviderMetadata() =>
        new("lexical", "lucene-standard-analyzer", true, "Lucene.NET StandardAnalyzer lexical ranking.");

    public RetrievalResultEnvelope<MemorySearchResult> BuildRetrievalEnvelope(
        string mode,
        RetrievalProviderMetadata provider,
        IReadOnlyList<MemorySearchResult> results) =>
        new(
            "memorysmith.retrieval-results.v1",
            mode,
            provider,
            results,
            MemoryDiagnosticFormatting.ToWarningSummaries(results));

    public async Task<MemoryContextPack> BuildContextPackAsync(MemoryContextPackQuery query, CancellationToken cancellationToken)
    {
        using var operation = StartTelemetryOperation("memory.context-pack");
        var started = Stopwatch.GetTimestamp();
        var success = false;
        cancellationToken.ThrowIfCancellationRequested();

        RecordQueryEvent("context_pack", query.Query);

        var limit = Clamp(query.Limit, 1, _options.Limits.MaxSearchLimit, 5);
        var referenceDepth = Clamp(query.ReferenceDepth, 0, 2, 1);
        var maxContentChars = Clamp(query.MaxContentChars, 200, 6000, 1200);
        var maxRecords = Clamp(query.MaxRecords, 1, 100, 20);
        var warnings = new List<string>();
        var snapshot = CreateSearchSnapshot(query.Status, query.Tags);
        var allRecords = snapshot.AllRecords;
        var recordsById = snapshot.AllRecordsById;
        var explicitRootIds = NormalizeIdList(query.Ids, warnings);
        var roots = string.IsNullOrWhiteSpace(query.Query) && explicitRootIds.Count > 0
            ? []
            : RankHybridResults(snapshot, query.Query, limit).ToList();

        var records = new List<MemoryContextPackRecord>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var frontier = new List<MemoryRecord>();
        var hitMaxRecords = false;

        foreach (var id in explicitRootIds)
        {
            if (!recordsById.TryGetValue(id, out var record))
            {
                warnings.Add($"Explicit root id '{id}' was not found.");
                continue;
            }

            if (!TryAddToPack(record, "root", null, "Explicit root id.", frontier))
            {
                if (hitMaxRecords)
                {
                    break;
                }
            }
        }

        foreach (var result in roots)
        {
            if (!recordsById.TryGetValue(result.Id, out var record))
            {
                continue;
            }

            if (!TryAddToPack(record, "root", result.Score, result.MatchReason, frontier) && hitMaxRecords)
            {
                break;
            }
        }

        for (var depth = 0; depth < referenceDepth && frontier.Count > 0; depth++)
        {
            var nextFrontier = new List<MemoryRecord>();
            foreach (var parent in frontier)
            {
                foreach (var link in EnumerateLinks(parent, allRecords, query.IncludeBacklinks))
                {
                    if (records.Count >= maxRecords)
                    {
                        AddMaxRecordsWarning();
                        break;
                    }

                    if (!recordsById.TryGetValue(link.Id, out var linked))
                    {
                        warnings.Add($"{link.WarningKind} '{link.Id}' from '{parent.Id}' was not found.");
                        continue;
                    }

                    if (!TryAddToPack(linked, FormatLinkedRelationship(link.Relationship, parent.Id), null, null, nextFrontier))
                    {
                        if (hitMaxRecords)
                        {
                            break;
                        }
                    }
                }

                if (hitMaxRecords)
                {
                    break;
                }
            }

            frontier = nextFrontier;
        }

        var diagnosticWarnings = MemoryDiagnosticFormatting.ToWarningSummaries(records);

        // Build reverse-reference map: for each packed record, find store records that
        // cite it via References or Conflicts but are not themselves in the pack.
        // O(|store| × avgRefs) — fast for in-memory stores of typical corpus size. TSK-0268.
        var reverseRefMap = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var pr in records)
        {
            reverseRefMap[pr.Id] = [];
        }

        foreach (var candidate in allRecords)
        {
            if (reverseRefMap.ContainsKey(candidate.Id))
            {
                continue; // skip pack members — backlinks from them are already expressed as Relationship
            }

            foreach (var rid in candidate.References)
            {
                if (reverseRefMap.TryGetValue(rid, out var rrList))
                {
                    rrList.Add(candidate.Id);
                }
            }

            foreach (var cid in candidate.Conflicts)
            {
                if (reverseRefMap.TryGetValue(cid, out var rcList))
                {
                    rcList.Add(candidate.Id);
                }
            }
        }

        for (var i = 0; i < records.Count; i++)
        {
            if (reverseRefMap.TryGetValue(records[i].Id, out var reverseRefs) && reverseRefs.Count > 0)
            {
                records[i] = records[i] with
                {
                    ReverseReferences = reverseRefs.Order(StringComparer.OrdinalIgnoreCase).ToList()
                };
            }
        }

        var pack = new MemoryContextPack(
            query.Query,
            DateTime.UtcNow,
            records,
            warnings.Concat(diagnosticWarnings).Distinct(StringComparer.OrdinalIgnoreCase).ToList());
        success = true;
        LogBenchmark("memory.context-pack", Stopwatch.GetElapsedTime(started).TotalMilliseconds, success, query: query.Query, resultCount: pack.Records.Count);
        return pack;

        bool TryAddToPack(MemoryRecord record, string relationship, double? score, string? matchReason, List<MemoryRecord> targetFrontier)
        {
            if (seen.Contains(record.Id))
            {
                return false;
            }

            if (records.Count >= maxRecords)
            {
                AddMaxRecordsWarning();
                return false;
            }

            seen.Add(record.Id);
            records.Add(ToContextPackRecord(record, relationship, score, matchReason, maxContentChars, recordsById));
            targetFrontier.Add(record);
            return true;
        }

        void AddMaxRecordsWarning()
        {
            if (hitMaxRecords)
            {
                return;
            }

            warnings.Add($"Context pack hit maxRecords {maxRecords}; additional records were omitted.");
            hitMaxRecords = true;
        }

        static IEnumerable<(string Id, string Relationship, string WarningKind)> EnumerateLinks(
            MemoryRecord record,
            IReadOnlyList<MemoryRecord> allRecords,
            bool includeBacklinks)
        {
            foreach (var id in record.References)
            {
                yield return (id, "reference", "Reference");
            }

            foreach (var id in record.Conflicts)
            {
                yield return (id, "conflict", "Conflict");
            }

            if (!includeBacklinks)
            {
                yield break;
            }

            foreach (var backlink in allRecords.Where(candidate => !string.Equals(candidate.Id, record.Id, StringComparison.OrdinalIgnoreCase)))
            {
                if (backlink.References.Any(id => string.Equals(id, record.Id, StringComparison.OrdinalIgnoreCase)))
                {
                    yield return (backlink.Id, "references", "Backlink");
                }

                if (backlink.Conflicts.Any(id => string.Equals(id, record.Id, StringComparison.OrdinalIgnoreCase)))
                {
                    yield return (backlink.Id, "conflicts with", "Backlink");
                }
            }
        }

        static string FormatLinkedRelationship(string relationship, string parentId) => relationship switch
        {
            "references" => $"references {parentId}",
            "conflicts with" => $"conflicts with {parentId}",
            _ => $"{relationship} of {parentId}"
        };
    }

    public Task<MemoryRecord?> GetAsync(string id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(IsValidId(id) ? _store.Load(id) : null);
    }

    public Task<IReadOnlyList<MemoryDiagnostic>> GetDiagnosticsAsync(string id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var record = IsValidId(id) ? _store.Load(id) : null;
        if (record is null)
        {
            return Task.FromResult<IReadOnlyList<MemoryDiagnostic>>([]);
        }

        var recordsById = MemoryRecordLookup.ToRecordMap(_store.LoadAll());
        return Task.FromResult(GetDiagnostics(record, recordsById));
    }

    /// <summary>
    /// Returns the IDs of all memory records whose <see cref="MemoryRecord.References"/> or
    /// <see cref="MemoryRecord.Conflicts"/> arrays contain <paramref name="id"/>.
    /// Used to populate the "Incoming References" panel in the workbench detail view
    /// and the <c>reverseReferences</c> field in context-pack responses (TSK-0268).
    /// </summary>
    public Task<IReadOnlyList<string>> GetReverseReferencesAsync(string id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(id))
        {
            return Task.FromResult<IReadOnlyList<string>>([]);
        }

        var result = _store.LoadAll()
            .Where(r => !string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase)
                     && (r.References.Any(rid => string.Equals(rid, id, StringComparison.OrdinalIgnoreCase))
                      || r.Conflicts.Any(cid => string.Equals(cid, id, StringComparison.OrdinalIgnoreCase))))
            .Select(r => r.Id)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return Task.FromResult<IReadOnlyList<string>>(result);
    }

    /// <summary>
    /// Returns all records that have at least one source link whose raw or resolved URI contains
    /// <paramref name="pattern"/> (case-insensitive). Pass <paramref name="resolveUri"/> to also
    /// match after variable expansion (e.g. <c>_vars.Resolve</c>).
    /// </summary>
    public Task<IReadOnlyList<MemoryRecord>> FindBySourceAsync(
        string pattern,
        Func<string, string>? resolveUri,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = _store.LoadAll()
            .Where(r => r.SourceLinks.Any(sl =>
                sl.Uri.Contains(pattern, StringComparison.OrdinalIgnoreCase) ||
                (resolveUri != null && resolveUri(sl.Uri).Contains(pattern, StringComparison.OrdinalIgnoreCase))))
            .ToList();
        return Task.FromResult<IReadOnlyList<MemoryRecord>>(result);
    }

    public async Task<MemoryRecord> CreateAsync(MemoryRecord record, CancellationToken cancellationToken)
    {
        using var operation = StartTelemetryOperation("memory.create");
        var started = Stopwatch.GetTimestamp();
        var success = false;
        cancellationToken.ThrowIfCancellationRequested();

        record.Id = string.IsNullOrWhiteSpace(record.Id) ? Guid.NewGuid().ToString() : record.Id.Trim();
        NormalizeRecord(record);
        ValidateRecord(record);
        record.LastUpdated = DateTime.UtcNow;

        _store.Save(record);
        _index.Add(record);
        var version = _history is null ? null : await _history.RecordMemoryAsync("Created", null, record, null, cancellationToken);
        if (_audit is not null)
        {
            await _audit.RecordAsync(
                "memory.created",
                "Memory",
                record.Id,
                MemorySmithAuditOutcomes.Success,
                afterHash: AuditLogService.ComputeJsonHash(record),
                diffRef: version?.HistoryPath,
                details: new { record.Title, record.Status },
                cancellationToken: cancellationToken);
        }

        await AuditAndPublishAsync(record.Id, "Created", "Memory created", cancellationToken);
        success = true;
        LogBenchmark("memory.create", Stopwatch.GetElapsedTime(started).TotalMilliseconds, success, recordId: record.Id);
        return record;
    }

    public async Task<MemoryRecord?> UpdateAsync(string id, MemoryRecord record, CancellationToken cancellationToken)
    {
        using var operation = StartTelemetryOperation("memory.update");
        var started = Stopwatch.GetTimestamp();
        var success = false;
        cancellationToken.ThrowIfCancellationRequested();
        var before = IsValidId(id) ? _store.Load(id) : null;
        if (before is null)
        {
            return null;
        }

        record.Id = id.Trim();
        NormalizeRecord(record);
        ValidateRecord(record);
        record.LastUpdated = DateTime.UtcNow;

        _store.Save(record);
        _index.Remove(record.Id);
        _index.Add(record);
        var version = _history is null ? null : await _history.RecordMemoryAsync("Updated", before, record, null, cancellationToken);
        if (_audit is not null)
        {
            await _audit.RecordAsync(
                "memory.updated",
                "Memory",
                record.Id,
                MemorySmithAuditOutcomes.Success,
                beforeHash: AuditLogService.ComputeJsonHash(before),
                afterHash: AuditLogService.ComputeJsonHash(record),
                diffRef: version?.HistoryPath,
                details: new { record.Title, record.Status },
                cancellationToken: cancellationToken);
        }

        await AuditAndPublishAsync(record.Id, "Updated", "Memory updated", cancellationToken);
        success = true;
        LogBenchmark("memory.update", Stopwatch.GetElapsedTime(started).TotalMilliseconds, success, recordId: record.Id);
        return record;
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken cancellationToken)
    {
        using var operation = StartTelemetryOperation("memory.delete");
        var started = Stopwatch.GetTimestamp();
        var success = false;
        cancellationToken.ThrowIfCancellationRequested();
        var before = IsValidId(id) ? _store.Load(id) : null;
        if (before is null)
        {
            return false;
        }

        _store.Delete(id);
        _index.Remove(id);
        var version = _history is null ? null : await _history.RecordMemoryAsync("Deleted", before, null, null, cancellationToken);
        if (_audit is not null)
        {
            await _audit.RecordAsync(
                "memory.deleted",
                "Memory",
                id,
                MemorySmithAuditOutcomes.Success,
                beforeHash: AuditLogService.ComputeJsonHash(before),
                diffRef: version?.HistoryPath,
                details: new { before.Title, before.Status },
                cancellationToken: cancellationToken);
        }

        await AuditAndPublishAsync(id, "Deleted", "Memory deleted", cancellationToken);
        success = true;
        LogBenchmark("memory.delete", Stopwatch.GetElapsedTime(started).TotalMilliseconds, success, recordId: id);
        return true;
    }

    public async Task<MemoryRecord?> IncrementUsageAsync(string id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsValidId(id))
        {
            return null;
        }

        var record = _store.Load(id);
        if (record is null)
        {
            return null;
        }

        var before = new MemoryRecord
        {
            Id = record.Id,
            Title = record.Title,
            Content = record.Content,
            Status = record.Status,
            Confidence = record.Confidence,
            Tags = record.Tags.ToList(),
            References = record.References.ToList(),
            Conflicts = record.Conflicts.ToList(),
            SourceLinks = record.SourceLinks.ToList(),
            UsageCount = record.UsageCount,
            LastUpdated = record.LastUpdated
        };

        record.UsageCount++;
        record.LastUpdated = DateTime.UtcNow;
        _store.Save(record);
        _index.Remove(record.Id);
        _index.Add(record);
        if (_audit is not null)
        {
            await _audit.RecordAsync(
                "memory.usage.incremented",
                "Memory",
                record.Id,
                MemorySmithAuditOutcomes.Success,
                beforeHash: AuditLogService.ComputeJsonHash(before),
                afterHash: AuditLogService.ComputeJsonHash(record),
                details: new { record.UsageCount },
                cancellationToken: cancellationToken);
        }

        await AuditAndPublishAsync(record.Id, "UsageIncremented", "Usage count incremented", cancellationToken);
        return record;
    }

    public Task<StatsSnapshot> GetStatsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(StatsSnapshotFactory.Build(_store.LoadAll()));
    }

    /// <summary>
    /// Returns daily event buckets for the last <paramref name="days"/> days (UTC).
    /// Each bucket counts search queries and memory create/update/delete events.
    /// </summary>
    public Task<IReadOnlyList<ActivityBucket>> GetActivityBucketsAsync(int days, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        days = Clamp(days, 1, 365, 30);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var since = DateTime.UtcNow.AddDays(-days + 1).Date; // start of that day

        var buckets = Enumerable.Range(0, days)
            .Select(i => today.AddDays(-(days - 1 - i)))
            .ToDictionary(d => d, _ => (Queries: 0, Changes: 0));

        foreach (var ev in _eventStore.GetEvents(since: since))
        {
            var date = DateOnly.FromDateTime(ev.Timestamp.ToUniversalTime());
            if (!buckets.ContainsKey(date)) continue;
            var (q, c) = buckets[date];
            if (ev.Action.StartsWith("Query:", StringComparison.OrdinalIgnoreCase))
                buckets[date] = (q + 1, c);
            else if (ev.Action is "Created" or "Updated" or "Deleted")
                buckets[date] = (q, c + 1);
        }

        var result = buckets
            .OrderBy(kv => kv.Key)
            .Select(kv => new ActivityBucket(kv.Key, kv.Value.Queries, kv.Value.Changes))
            .ToList();

        return Task.FromResult<IReadOnlyList<ActivityBucket>>(result);
    }

    public Task<IReadOnlyList<BackgroundServiceTelemetry>> GetTelemetryAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_telemetryTracker.GetSnapshot());
    }

    private async Task AuditAndPublishAsync(string memoryId, string action, string details, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _eventStore.AppendEvent(new MemoryEvent
        {
            MemoryId = memoryId,
            Action = action,
            Details = details,
            Timestamp = DateTime.UtcNow
        });

        await _publisher.PublishMemoryChangedAsync(new MemoryUpdateEvent
        {
            Id = memoryId,
            Action = action,
            Timestamp = DateTime.UtcNow
        });
        await _publisher.PublishStatsChangedAsync(StatsSnapshotFactory.Build(_store.LoadAll()));
    }

    private static void NormalizeRecord(MemoryRecord record)
    {
        record.Title = record.Title.Trim();
        record.Content = record.Content.Trim();
        record.Tags = NormalizeValues(record.Tags);
        record.References = NormalizeValues(record.References);
        record.Conflicts = NormalizeValues(record.Conflicts);
        record.SourceLinks = record.SourceLinks
            .Where(sl => !string.IsNullOrWhiteSpace(sl.Uri))
            .Select(sl => new SourceLink
            {
                Label = sl.Label.Trim(),
                Uri = sl.Uri.Trim(),
                StartLine = sl.StartLine,
                EndLine = sl.EndLine
            })
            .ToList();
    }

    private void ValidateRecord(MemoryRecord record)
    {
        var errors = new Dictionary<string, string[]>();

        if (!IsValidId(record.Id))
        {
            errors[nameof(MemoryRecord.Id)] = ["ID may contain only letters, numbers, underscore, and dash."];
        }

        if (string.IsNullOrWhiteSpace(record.Content))
        {
            errors[nameof(MemoryRecord.Content)] = ["Content is required."];
        }
        else if (record.Content.Length > _options.Limits.MaxContentLength)
        {
            errors[nameof(MemoryRecord.Content)] = [$"Content must be at most {_options.Limits.MaxContentLength} characters."];
        }

        if (record.Confidence is < 0 or > 1)
        {
            errors[nameof(MemoryRecord.Confidence)] = ["Confidence must be between 0 and 1."];
        }

        if (record.Tags.Count > _options.Limits.MaxTags)
        {
            errors[nameof(MemoryRecord.Tags)] = [$"At most {_options.Limits.MaxTags} tags are allowed."];
        }

        if (record.References.Count > _options.Limits.MaxReferences)
        {
            errors[nameof(MemoryRecord.References)] = [$"At most {_options.Limits.MaxReferences} references are allowed."];
        }

        var sourceLinkErrors = ValidateSourceLinks(record.SourceLinks);
        if (sourceLinkErrors.Count > 0)
        {
            errors[nameof(MemoryRecord.SourceLinks)] = sourceLinkErrors.ToArray();
        }

        var governanceErrors = GetDiagnostics(record, MemoryRecordLookup.ToRecordMap(_store.LoadAll().Append(record)))
            .Where(diagnostic => string.Equals(diagnostic.Severity, "Error", StringComparison.OrdinalIgnoreCase))
            .Select(diagnostic => diagnostic.Message)
            .ToArray();
        if (governanceErrors.Length > 0)
        {
            errors[nameof(MemoryRecord.Tags)] = governanceErrors;
        }

        if (errors.Count > 0)
        {
            throw new MemoryValidationException(errors);
        }
    }

    private static List<string> ValidateSourceLinks(List<SourceLink> sourceLinks)
    {
        var errors = new List<string>();
        for (var index = 0; index < sourceLinks.Count; index++)
        {
            var link = sourceLinks[index];
            if (link.StartLine is <= 0)
            {
                errors.Add($"SourceLinks[{index}].StartLine must be greater than 0 when provided.");
            }

            if (link.EndLine is <= 0)
            {
                errors.Add($"SourceLinks[{index}].EndLine must be greater than 0 when provided.");
            }

            if (link.StartLine.HasValue && link.EndLine.HasValue && link.EndLine.Value < link.StartLine.Value)
            {
                errors.Add($"SourceLinks[{index}].EndLine must be greater than or equal to StartLine.");
            }
        }

        return errors;
    }

    private static bool IsValidId(string id) =>
        !string.IsNullOrWhiteSpace(id) && SafeIdPattern.IsMatch(id.Trim());

    private static int Clamp(int value, int min, int max, int defaultValue)
    {
        if (value < min)
        {
            return Math.Clamp(defaultValue, min, max);
        }

        return Math.Clamp(value, min, max);
    }

    private static IEnumerable<MemoryRecord> ApplyListFilters(
        IEnumerable<MemoryRecord> records,
        MemoryStatus? status,
        List<string> tagFilters)
    {
        if (status.HasValue)
        {
            records = records.Where(r => r.Status == status.Value);
        }

        if (tagFilters.Count > 0)
        {
            records = records.Where(r => tagFilters.Any(filter =>
                r.Tags.Any(tag => string.Equals(tag, filter, StringComparison.OrdinalIgnoreCase))));
        }

        return records;
    }

    private MemoryMetadata ToMetadata(MemoryRecord record, IReadOnlyDictionary<string, MemoryRecord> recordsById) => new()
    {
        Id = record.Id,
        Title = record.Title,
        Status = record.Status,
        Confidence = record.Confidence,
        Tags = record.Tags,
        Diagnostics = GetDiagnostics(record, recordsById).ToList(),
        UsageCount = record.UsageCount,
        LastUpdated = record.LastUpdated
    };

    private MemoryContextPackRecord ToContextPackRecord(
        MemoryRecord record,
        string relationship,
        double? score,
        string? matchReason,
        int maxContentChars,
        IReadOnlyDictionary<string, MemoryRecord> recordsById) => new(
            record.Id,
            record.Title,
            record.Status,
            record.Confidence,
            record.Tags,
            record.References,
            record.Conflicts,
            record.SourceLinks,
            record.UsageCount,
            record.LastUpdated,
            relationship,
            score,
            matchReason,
            TruncateContent(record.Content, maxContentChars))
        {
            Diagnostics = GetDiagnostics(record, recordsById)
        };

    private IReadOnlyList<MemoryDiagnostic> GetDiagnostics(MemoryRecord record, IReadOnlyDictionary<string, MemoryRecord> recordsById) =>
        _diagnostics?.Analyze(record, recordsById) ?? [];

    private static List<string> NormalizeFilterList(string? values) =>
        string.IsNullOrWhiteSpace(values)
            ? []
            : NormalizeValues(values.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static List<string> NormalizeIdList(string? values, List<string> warnings) =>
        string.IsNullOrWhiteSpace(values)
            ? []
            : values
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(value =>
                {
                    if (IsValidId(value))
                    {
                        return true;
                    }

                    warnings.Add($"Explicit root id '{value}' is invalid.");
                    return false;
                })
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

    private static List<string> NormalizeValues(IEnumerable<string> values) =>
        values
            .Select(value => value.Trim().ToLowerInvariant())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private MemorySearchSnapshot CreateSearchSnapshot(MemoryStatus? status, string? tags)
    {
        var tagFilters = NormalizeFilterList(tags);
        var allRecordsById = MemoryRecordLookup.ToRecordMap(_store.LoadAll());
        var allRecords = allRecordsById.Values.ToList();
        var filteredRecords = ApplyListFilters(allRecords, status, tagFilters).ToList();

        return new MemorySearchSnapshot(
            allRecords,
            allRecordsById,
            filteredRecords,
            MemoryRecordLookup.ToRecordMap(filteredRecords));
    }

    private IReadOnlyList<MemorySearchResult> RankHybridResults(MemorySearchSnapshot snapshot, string? query, int? limit = null)
    {
        var semanticTokens = ExpandSearchTokens(TokenizeSearchText(query ?? string.Empty));
        var lexicalTokens = AnalyzeLexicalText(query ?? string.Empty);

        var lexicalResults = RankLexicalResults(snapshot.FilteredRecords, query, lexicalTokens);
        var semanticResults = RankSemanticResults(snapshot.FilteredRecords, query, semanticTokens);
        var lexicalRanks = ToRankMap(lexicalResults);
        var semanticRanks = ToRankMap(semanticResults);
        var lexicalById = ToResultMap(lexicalResults);
        var semanticById = ToResultMap(semanticResults);
        var candidateIds = lexicalRanks.Keys
            .Union(semanticRanks.Keys, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var orderedResults = candidateIds
            .Select(id =>
            {
                var record = snapshot.FilteredRecordsById[id];
                lexicalRanks.TryGetValue(id, out var lexicalRank);
                semanticRanks.TryGetValue(id, out var semanticRank);
                lexicalById.TryGetValue(id, out var lexicalResult);
                semanticById.TryGetValue(id, out var semanticResult);

                var score = ReciprocalRankScore(lexicalRank) + ReciprocalRankScore(semanticRank);
                // Token asymmetry fix: prefer lexical snippet (StandardAnalyzer tokens) over semantic
                // (suffix-stripped tokens) so the displayed snippet is consistent with lexical scoring.
                // Fall back to lexicalTokens (not semanticTokens) for BuildSnippet when neither
                // individual result produced a snippet.
                return new MemorySearchResult(
                    record.Id,
                    record.Title,
                    record.Status,
                    record.Confidence,
                    Math.Round(score, 6),
                    record.Tags,
                    record.UsageCount,
                    lexicalResult?.Snippet ?? semanticResult?.Snippet ?? BuildSnippet(record.Content, lexicalTokens),
                    BuildHybridMatchReason(lexicalRank, lexicalResult, semanticRank, semanticResult),
                    record.LastUpdated)
                { SnippetHtml = lexicalResult?.SnippetHtml ?? BuildHighlightedSnippetHtml(record.Content, lexicalTokens) };
            })
            .OrderByDescending(result => result.Score)
            .ThenByDescending(result => result.LastUpdated)
            .ThenBy(result => result.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(result => result.Id, StringComparer.OrdinalIgnoreCase);

        var limitedResults = limit.HasValue ? orderedResults.Take(limit.Value) : orderedResults;

        return limitedResults
            .Select(result => snapshot.FilteredRecordsById.TryGetValue(result.Id, out var record)
                ? result with { Diagnostics = GetDiagnostics(record, snapshot.AllRecordsById) }
                : result)
            .ToList();
    }

    private IReadOnlyList<MemorySearchResult> RankSemanticResults(
        List<MemoryRecord> records,
        string? query,
        HashSet<string> queryTokens) =>
        _semanticEmbeddings?.TryRank(records, query, queryTokens, out var embeddingResults) == true
            ? embeddingResults
            : RankTokenSemanticResults(records, query, queryTokens);

    private static List<MemorySearchResult> RankTokenSemanticResults(
        List<MemoryRecord> records,
        string? query,
        HashSet<string> queryTokens) =>
        records
            .Select(record => ScoreSemanticMatch(record, query, queryTokens))
            .Where(result => result.Score > 0 || queryTokens.Count == 0)
            .OrderByDescending(result => result.Score)
            .ThenByDescending(result => result.LastUpdated)
            .ThenBy(result => result.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(result => result.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static List<MemorySearchResult> RankLexicalResults(
        List<MemoryRecord> records,
        string? query,
        HashSet<string> queryTokens) =>
        records
            .Select(record => ScoreLexicalMatch(record, query, queryTokens))
            .Where(result => result.Score > 0 || queryTokens.Count == 0)
            .OrderByDescending(result => result.Score)
            .ThenByDescending(result => result.LastUpdated)
            .ThenBy(result => result.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(result => result.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static Dictionary<string, int> ToRankMap(IReadOnlyList<MemorySearchResult> results)
    {
        var ranks = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < results.Count; index++)
        {
            ranks.TryAdd(results[index].Id, index + 1);
        }

        return ranks;
    }

    private static Dictionary<string, MemorySearchResult> ToResultMap(IReadOnlyList<MemorySearchResult> results)
    {
        var resultsById = new Dictionary<string, MemorySearchResult>(StringComparer.OrdinalIgnoreCase);
        foreach (var result in results)
        {
            resultsById.TryAdd(result.Id, result);
        }

        return resultsById;
    }

    private static double ReciprocalRankScore(int rank) =>
        rank <= 0 ? 0 : 1.0 / (ReciprocalRankFusionK + rank);

    private static MemorySearchResult ScoreSemanticMatch(MemoryRecord record, string? query, HashSet<string> queryTokens)
    {
        var titleTokens = TokenizeSearchText(record.Title);
        var tagTokens = record.Tags.SelectMany(TokenizeSearchText).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var contentTokens = TokenizeSearchText(record.Content);
        var referenceTokens = record.References.SelectMany(TokenizeSearchText).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var score = 0.0;
        var reasons = new List<string>();

        if (queryTokens.Count == 0)
        {
            return new MemorySearchResult(
                record.Id,
                record.Title,
                record.Status,
                record.Confidence,
                0,
                record.Tags,
                record.UsageCount,
                BuildSnippet(record.Content, queryTokens),
                "No query supplied; returned by recency.",
                record.LastUpdated);
        }

        var titleMatches = titleTokens.Intersect(queryTokens, StringComparer.OrdinalIgnoreCase).ToList();
        var tagMatches = tagTokens.Intersect(queryTokens, StringComparer.OrdinalIgnoreCase).ToList();
        var contentMatches = contentTokens.Intersect(queryTokens, StringComparer.OrdinalIgnoreCase).ToList();
        var referenceMatches = referenceTokens.Intersect(queryTokens, StringComparer.OrdinalIgnoreCase).ToList();

        AddScore(titleMatches, 4, "title");
        AddScore(tagMatches, 3, "tags");
        AddScore(referenceMatches, 2, "references");
        AddScore(contentMatches, 1, "content");

        var phrase = query?.Trim();
        if (!string.IsNullOrWhiteSpace(phrase))
        {
            if (record.Title.Contains(phrase, StringComparison.OrdinalIgnoreCase))
            {
                score += 6;
                reasons.Add("exact title phrase");
            }

            if (record.Content.Contains(phrase, StringComparison.OrdinalIgnoreCase))
            {
                score += 3;
                reasons.Add("exact content phrase");
            }
        }

        return new MemorySearchResult(
            record.Id,
            record.Title,
            record.Status,
            record.Confidence,
            Math.Round(score, 3),
            record.Tags,
            record.UsageCount,
            BuildSnippet(record.Content, queryTokens),
            reasons.Count == 0 ? "No semantic token overlap." : string.Join("; ", reasons),
            record.LastUpdated);

        void AddScore(IReadOnlyCollection<string> matches, double weight, string source)
        {
            if (matches.Count == 0)
            {
                return;
            }

            score += matches.Count * weight;
            reasons.Add($"{source}: {string.Join(", ", matches.Order(StringComparer.OrdinalIgnoreCase))}");
        }
    }

    private static MemorySearchResult ScoreLexicalMatch(MemoryRecord record, string? query, HashSet<string> queryTokens)
    {
        var score = 0.0;
        var reasons = new List<string>();

        if (queryTokens.Count == 0)
        {
            return new MemorySearchResult(
                record.Id,
                record.Title,
                record.Status,
                record.Confidence,
                0,
                record.Tags,
                record.UsageCount,
                BuildSnippet(record.Content, queryTokens),
                "No query supplied; returned by recency.",
                record.LastUpdated);
        }

        var titleMatches = AnalyzeLexicalText(record.Title).Intersect(queryTokens, StringComparer.OrdinalIgnoreCase).ToList();
        var tagMatches = record.Tags.SelectMany(tag => AnalyzeLexicalText(tag)).ToHashSet(StringComparer.OrdinalIgnoreCase).Intersect(queryTokens, StringComparer.OrdinalIgnoreCase).ToList();
        var referenceMatches = record.References.SelectMany(reference => AnalyzeLexicalText(reference)).ToHashSet(StringComparer.OrdinalIgnoreCase).Intersect(queryTokens, StringComparer.OrdinalIgnoreCase).ToList();
        var contentTokens = AnalyzeLexicalTokens(record.Content);
        var contentMatches = contentTokens.Where(queryTokens.Contains).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var contentHitCount = contentTokens.Count(queryTokens.Contains);

        AddScore(titleMatches, 5, "lexical title");
        AddScore(tagMatches, 4, "lexical tags");
        AddScore(referenceMatches, 2, "lexical references");
        if (contentMatches.Count > 0)
        {
            score += Math.Min(contentHitCount, 12) * 0.75;
            reasons.Add($"lexical content: {string.Join(", ", contentMatches.Order(StringComparer.OrdinalIgnoreCase))}");
        }

        var phrase = query?.Trim();
        if (!string.IsNullOrWhiteSpace(phrase))
        {
            if (record.Title.Contains(phrase, StringComparison.OrdinalIgnoreCase))
            {
                score += 8;
                reasons.Add("exact lexical title phrase");
            }

            if (record.Content.Contains(phrase, StringComparison.OrdinalIgnoreCase))
            {
                score += 4;
                reasons.Add("exact lexical content phrase");
            }
        }

        return new MemorySearchResult(
            record.Id,
            record.Title,
            record.Status,
            record.Confidence,
            Math.Round(score, 3),
            record.Tags,
            record.UsageCount,
            BuildSnippet(record.Content, queryTokens),
            reasons.Count == 0 ? "No lexical token overlap." : string.Join("; ", reasons),
            record.LastUpdated)
        { SnippetHtml = BuildHighlightedSnippetHtml(record.Content, queryTokens) };

        void AddScore(IReadOnlyCollection<string> matches, double weight, string source)
        {
            if (matches.Count == 0)
            {
                return;
            }

            score += matches.Count * weight;
            reasons.Add($"{source}: {string.Join(", ", matches.Order(StringComparer.OrdinalIgnoreCase))}");
        }
    }

    private static string BuildHybridMatchReason(
        int lexicalRank,
        MemorySearchResult? lexicalResult,
        int semanticRank,
        MemorySearchResult? semanticResult)
    {
        var parts = new List<string>
        {
            $"Hybrid RRF fused lexical rank {FormatRank(lexicalRank)} and semantic rank {FormatRank(semanticRank)}."
        };

        if (lexicalResult is not null)
        {
            parts.Add($"Lexical score {lexicalResult.Score:0.###}: {lexicalResult.MatchReason}");
        }

        if (semanticResult is not null)
        {
            parts.Add($"Semantic score {semanticResult.Score:0.###}: {semanticResult.MatchReason}");
        }

        return string.Join(" ", parts);
    }

    private static string FormatRank(int rank) => rank <= 0 ? "none" : rank.ToString();

    private static HashSet<string> ExpandSearchTokens(IEnumerable<string> tokens)
    {
        var expanded = new HashSet<string>(tokens, StringComparer.OrdinalIgnoreCase);
        foreach (var token in expanded.ToList())
        {
            if (!SearchAliases.TryGetValue(token, out var aliases))
            {
                continue;
            }

            foreach (var alias in aliases)
            {
                expanded.Add(NormalizeSearchToken(alias));
            }
        }

        return expanded;
    }

    private static HashSet<string> TokenizeSearchText(string text) =>
        SearchTokenPattern.Matches(text)
            .Select(match => NormalizeSearchToken(match.Value))
            .Where(token => token.Length > 1 && !SearchStopWords.Contains(token))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static HashSet<string> AnalyzeLexicalText(string text) =>
        AnalyzeLexicalTokens(text).ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static List<string> AnalyzeLexicalTokens(string text)
    {
        using var analyzer = new StandardAnalyzer(LuceneMatchVersion);
        using var reader = new StringReader(text);
        using var tokenStream = analyzer.GetTokenStream("memory", reader);
        var termAttribute = tokenStream.AddAttribute<ICharTermAttribute>();
        var tokens = new List<string>();

        tokenStream.Reset();
        while (tokenStream.IncrementToken())
        {
            var token = termAttribute.ToString();
            if (!string.IsNullOrWhiteSpace(token))
            {
                tokens.Add(token);
            }
        }

        tokenStream.End();
        return tokens;
    }

    [GeneratedRegex("^[A-Za-z0-9_-]+$")]
    private static partial Regex SafeIdRegex();

    [GeneratedRegex("[A-Za-z0-9]+")]
    private static partial Regex SearchTokenRegex();

    private static string NormalizeSearchToken(string value)
    {
        var token = value.Trim().ToLowerInvariant();
        foreach (var suffix in new[] { "ing", "ed", "es", "s" })
        {
            if (token.Length > suffix.Length + 3 && token.EndsWith(suffix, StringComparison.Ordinal))
            {
                return token[..^suffix.Length];
            }
        }

        return token;
    }

    private static string BuildSnippet(string content, IReadOnlySet<string> queryTokens)
    {
        const int maxLength = 220;
        if (content.Length <= maxLength)
        {
            return content;
        }

        var lowerContent = content.ToLowerInvariant();
        var matchIndex = queryTokens
            .Select(token => lowerContent.IndexOf(token.ToLowerInvariant(), StringComparison.Ordinal))
            .Where(index => index >= 0)
            .DefaultIfEmpty(0)
            .Min();
        var start = Math.Max(0, matchIndex - 60);
        var length = Math.Min(maxLength, content.Length - start);
        var prefix = start > 0 ? "..." : string.Empty;
        var suffix = start + length < content.Length ? "..." : string.Empty;
        return prefix + content.Substring(start, length).Trim() + suffix;
    }

    /// <summary>
    /// Returns an HTML fragment with &lt;mark&gt; tags around matched terms, suitable for rendering
    /// in the Blazor UI as a <see cref="Microsoft.AspNetCore.Components.MarkupString"/>.
    /// Returns <c>null</c> when there are no query tokens or no match is found.
    /// Content is pre-HTML-encoded before the Lucene Highlighter is applied (XSS-safe).
    /// Only called for lexical and hybrid results — semantic results get no SnippetHtml.
    /// </summary>
    private static string? BuildHighlightedSnippetHtml(string content, IReadOnlyCollection<string> lexicalQueryTokens)
    {
        if (lexicalQueryTokens.Count == 0) return null;

        // Guard: prevent DoS from very large memory records
        var safe = content.Length > 32_000 ? content[..32_000] : content;

        // Pre-encode BEFORE the Lucene Highlighter sees the text.
        // SimpleHTMLFormatter passes non-matched text verbatim — without encoding,
        // any HTML in the content would inject into the MarkupString render path.
        var encoded = WebUtility.HtmlEncode(safe);

        using var analyzer = new StandardAnalyzer(LuceneMatchVersion);

        // BooleanQuery built from StandardAnalyzer-processed tokens — same tokenizer
        // used for lexical scoring, so highlighted terms align with scored matches.
        // QueryParser is intentionally not used: tokens are already available.
        var boolQuery = new BooleanQuery();
        foreach (var token in lexicalQueryTokens.Take(8))
            boolQuery.Add(new TermQuery(new Term("f", token)), Occur.SHOULD);

        var scorer = new QueryScorer(boolQuery);
        var highlighter = new Highlighter(new SimpleHTMLFormatter("<mark>", "</mark>"), scorer)
        {
            TextFragmenter = new SimpleSpanFragmenter(scorer, 220)
        };

        var fragment = highlighter.GetBestFragment(analyzer, "f", encoded);
        return string.IsNullOrEmpty(fragment) ? null : fragment;
    }

        private static string TruncateContent(string content, int maxLength) =>
        content.Length <= maxLength ? content : content[..maxLength].TrimEnd() + "...";

    private void RecordQueryEvent(string kind, string? text)
    {
        _eventStore.AppendEvent(new MemoryEvent
        {
            Action = $"Query:{kind}",
            Details = text ?? string.Empty,
            Timestamp = DateTime.UtcNow
        });
    }

    private void LogBenchmark(string operation, double elapsedMs, bool success, string? query = null, int? resultCount = null, string? recordId = null)
    {
        var telemetrySettings = _options.Telemetry;
        if (telemetrySettings.Enabled && telemetrySettings.MetricsEnabled && telemetrySettings.InstrumentMemoryOperations)
        {
            var category = operation.Split('.', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "memory";
            var isSlowForTelemetry = elapsedMs >= _options.Logging.BenchmarkSlowThresholdMs;
            MemorySmithTelemetry.RecordOperation(operation, category, elapsedMs, success, isSlowForTelemetry);
        }

        if (!_options.Logging.BenchmarkLoggingEnabled)
        {
            return;
        }

        var isSlow = elapsedMs >= _options.Logging.BenchmarkSlowThresholdMs;
        int? queryLength = string.IsNullOrWhiteSpace(query) ? null : query.Length;
        if (isSlow)
        {
            _logger.LogWarning(
                "Benchmark operation={Operation} elapsedMs={ElapsedMs} slow={IsSlow} success={Success} queryLength={QueryLength} resultCount={ResultCount} recordId={RecordId}",
                operation,
                elapsedMs,
                true,
                success,
                queryLength,
                resultCount,
                recordId);
            return;
        }

        _logger.LogInformation(
            "Benchmark operation={Operation} elapsedMs={ElapsedMs} slow={IsSlow} success={Success} queryLength={QueryLength} resultCount={ResultCount} recordId={RecordId}",
            operation,
            elapsedMs,
            false,
            success,
            queryLength,
            resultCount,
            recordId);
    }

    private Activity? StartTelemetryOperation(string operation)
    {
        var telemetrySettings = _options.Telemetry;
        if (!telemetrySettings.Enabled || !telemetrySettings.TracingEnabled || !telemetrySettings.InstrumentMemoryOperations)
        {
            return null;
        }

        var category = operation.Split('.', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "memory";
        return MemorySmithTelemetry.StartOperation(operation, category);
    }

    private sealed record MemorySearchSnapshot(
        List<MemoryRecord> AllRecords,
        IReadOnlyDictionary<string, MemoryRecord> AllRecordsById,
        List<MemoryRecord> FilteredRecords,
        IReadOnlyDictionary<string, MemoryRecord> FilteredRecordsById);
}