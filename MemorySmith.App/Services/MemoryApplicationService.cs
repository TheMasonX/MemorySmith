using System.Text.RegularExpressions;
using Lucene.Net.Analysis.Standard;
using Lucene.Net.Analysis.TokenAttributes;
using Lucene.Net.Util;
using MemorySmith.Core.Indexing;
using MemorySmith.Core.Models;
using MemorySmith.Storage;
using Microsoft.Extensions.Options;

namespace MemorySmith.App.Services;

public class MemoryApplicationService
{
    private const int ReciprocalRankFusionK = 60;
    private const LuceneVersion LuceneMatchVersion = LuceneVersion.LUCENE_48;

    private static readonly Regex SafeIdPattern = new("^[A-Za-z0-9_-]+$", RegexOptions.Compiled);
    private static readonly Regex SearchTokenPattern = new("[A-Za-z0-9]+", RegexOptions.Compiled);
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

    public MemoryApplicationService(
        IMemoryStore store,
        IEventStore eventStore,
        MemoryIndex index,
        BackgroundServiceTelemetryTracker telemetryTracker,
        IMemoryChangePublisher publisher,
        IOptions<MemorySmithOptions> options)
    {
        _store = store;
        _eventStore = eventStore;
        _index = index;
        _telemetryTracker = telemetryTracker;
        _publisher = publisher;
        _options = options.Value;
    }

    public Task<PagedResult<MemoryMetadata>> GetMemoriesAsync(MemoryListQuery query, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var page = Math.Max(1, query.Page);
        var pageSize = Clamp(query.PageSize, 1, _options.Limits.MaxPageSize, 20);
        var tagFilters = NormalizeFilterList(query.Tags);

        var records = ApplyListFilters(_store.LoadAll(), query.Status, tagFilters)
            .OrderByDescending(r => r.LastUpdated)
            .ThenBy(r => r.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var data = records
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(ToMetadata)
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
        cancellationToken.ThrowIfCancellationRequested();

        var limit = Clamp(query.Limit, 1, _options.Limits.MaxSearchLimit, 20);
        var tagFilters = NormalizeFilterList(query.Tags);
        var keyword = query.Query?.Trim();

        var records = ApplyListFilters(_store.LoadAll(), query.Status, tagFilters);
        if (!string.IsNullOrWhiteSpace(keyword))
        {
            records = records.Where(r =>
                r.Title.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                r.Content.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                r.Tags.Any(tag => tag.Contains(keyword, StringComparison.OrdinalIgnoreCase)));
        }

        var results = records
            .OrderByDescending(r => r.LastUpdated)
            .ThenBy(r => r.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Id, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .ToList();

        return Task.FromResult<IReadOnlyList<MemoryRecord>>(results);
    }

    public Task<IReadOnlyList<MemorySearchResult>> SemanticSearchAsync(SemanticMemorySearchQuery query, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var limit = Clamp(query.Limit, 1, _options.Limits.MaxSearchLimit, 20);
        var tagFilters = NormalizeFilterList(query.Tags);
        var queryTokens = ExpandSearchTokens(TokenizeSearchText(query.Query ?? string.Empty));
        var records = ApplyListFilters(_store.LoadAll(), query.Status, tagFilters).ToList();

        var results = RankSemanticResults(records, query.Query, queryTokens)
            .Take(limit)
            .ToList();

        return Task.FromResult<IReadOnlyList<MemorySearchResult>>(results);
    }

    public Task<IReadOnlyList<MemorySearchResult>> HybridSearchAsync(HybridMemorySearchQuery query, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var limit = Clamp(query.Limit, 1, _options.Limits.MaxSearchLimit, 20);
        var tagFilters = NormalizeFilterList(query.Tags);
        var records = ApplyListFilters(_store.LoadAll(), query.Status, tagFilters).ToList();
        var semanticTokens = ExpandSearchTokens(TokenizeSearchText(query.Query ?? string.Empty));
        var lexicalTokens = AnalyzeLexicalText(query.Query ?? string.Empty);

        var lexicalResults = RankLexicalResults(records, query.Query, lexicalTokens);
        var semanticResults = RankSemanticResults(records, query.Query, semanticTokens);
        var lexicalRanks = ToRankMap(lexicalResults);
        var semanticRanks = ToRankMap(semanticResults);
        var lexicalById = lexicalResults.ToDictionary(result => result.Id, StringComparer.OrdinalIgnoreCase);
        var semanticById = semanticResults.ToDictionary(result => result.Id, StringComparer.OrdinalIgnoreCase);
        var recordsById = records.ToDictionary(record => record.Id, StringComparer.OrdinalIgnoreCase);
        var candidateIds = lexicalRanks.Keys
            .Union(semanticRanks.Keys, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var results = candidateIds
            .Select(id =>
            {
                var record = recordsById[id];
                lexicalRanks.TryGetValue(id, out var lexicalRank);
                semanticRanks.TryGetValue(id, out var semanticRank);
                lexicalById.TryGetValue(id, out var lexicalResult);
                semanticById.TryGetValue(id, out var semanticResult);

                var score = ReciprocalRankScore(lexicalRank) + ReciprocalRankScore(semanticRank);
                return new MemorySearchResult(
                    record.Id,
                    record.Title,
                    record.Status,
                    record.Confidence,
                    Math.Round(score, 6),
                    record.Tags,
                    record.UsageCount,
                    semanticResult?.Snippet ?? lexicalResult?.Snippet ?? BuildSnippet(record.Content, semanticTokens),
                    BuildHybridMatchReason(lexicalRank, lexicalResult, semanticRank, semanticResult),
                    record.LastUpdated);
            })
            .OrderByDescending(result => result.Score)
            .ThenByDescending(result => result.LastUpdated)
            .ThenBy(result => result.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(result => result.Id, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .ToList();

        return Task.FromResult<IReadOnlyList<MemorySearchResult>>(results);
    }

    public Task<MemoryRecord?> GetAsync(string id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(IsValidId(id) ? _store.Load(id) : null);
    }

    public async Task<MemoryRecord> CreateAsync(MemoryRecord record, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        record.Id = string.IsNullOrWhiteSpace(record.Id) ? Guid.NewGuid().ToString() : record.Id.Trim();
        NormalizeRecord(record);
        ValidateRecord(record);
        record.LastUpdated = DateTime.UtcNow;

        _store.Save(record);
        _index.Add(record);
        await AuditAndPublishAsync(record.Id, "Created", "Memory created", cancellationToken);
        return record;
    }

    public async Task<MemoryRecord?> UpdateAsync(string id, MemoryRecord record, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsValidId(id) || _store.Load(id) is null)
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
        await AuditAndPublishAsync(record.Id, "Updated", "Memory updated", cancellationToken);
        return record;
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsValidId(id) || _store.Load(id) is null)
        {
            return false;
        }

        _store.Delete(id);
        _index.Remove(id);
        await AuditAndPublishAsync(id, "Deleted", "Memory deleted", cancellationToken);
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

        record.UsageCount++;
        record.LastUpdated = DateTime.UtcNow;
        _store.Save(record);
        _index.Remove(record.Id);
        _index.Add(record);
        await AuditAndPublishAsync(record.Id, "UsageIncremented", "Usage count incremented", cancellationToken);
        return record;
    }

    public Task<StatsSnapshot> GetStatsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(StatsSnapshotFactory.Build(_store.LoadAll()));
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

    private void NormalizeRecord(MemoryRecord record)
    {
        record.Title = record.Title.Trim();
        record.Content = record.Content.Trim();
        record.Tags = NormalizeValues(record.Tags);
        record.References = NormalizeValues(record.References);
        record.Conflicts = NormalizeValues(record.Conflicts);
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

        if (errors.Count > 0)
        {
            throw new MemoryValidationException(errors);
        }
    }

    private static bool IsValidId(string id) =>
        !string.IsNullOrWhiteSpace(id) && SafeIdPattern.IsMatch(id.Trim());

    private static int Clamp(int value, int min, int max, int defaultValue)
    {
        if (value < min)
        {
            return defaultValue;
        }

        return Math.Min(value, max);
    }

    private static IEnumerable<MemoryRecord> ApplyListFilters(
        IEnumerable<MemoryRecord> records,
        MemoryStatus? status,
        IReadOnlyList<string> tagFilters)
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

    private static MemoryMetadata ToMetadata(MemoryRecord record) => new()
    {
        Id = record.Id,
        Title = record.Title,
        Status = record.Status,
        Confidence = record.Confidence,
        Tags = record.Tags,
        UsageCount = record.UsageCount,
        LastUpdated = record.LastUpdated
    };

    private static IReadOnlyList<string> NormalizeFilterList(string? values) =>
        string.IsNullOrWhiteSpace(values)
            ? []
            : NormalizeValues(values.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static List<string> NormalizeValues(IEnumerable<string> values) =>
        values
            .Select(value => value.Trim().ToLowerInvariant())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static IReadOnlyList<MemorySearchResult> RankSemanticResults(
        IReadOnlyList<MemoryRecord> records,
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

    private static IReadOnlyList<MemorySearchResult> RankLexicalResults(
        IReadOnlyList<MemoryRecord> records,
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
}