using System.Text.RegularExpressions;
using MemorySmith.Core.Models;
using MemorySmith.Storage;

namespace MemorySmith.App.Services;

public sealed record MeasurementBaselineSnapshot(
    DateTime ObservedAtUtc,
    SearchQualityMeasurement Search,
    EmbeddingProviderStatus SemanticEmbeddings,
    string SemanticSearchMode,
    PageCorpusMeasurement Pages,
    TagPolicyHealthMeasurement Tags,
    SourceLinkHealthMeasurement SourceLinks,
    MeasurementBaselineThresholds Thresholds);

public sealed record MeasurementBaselineThresholds(
    double SearchPromotionMinimumMrr = 0.75,
    double SearchPromotionMinimumRecallAt5 = 0.80,
    double MaximumDiagnosticWarningRate = 0.20,
    double MaximumBrokenSourceLinkRate = 0.05,
    int PageChunkingTokenThreshold = 1500,
    double PageChunkingLongPageRatioThreshold = 0.20);

public sealed record SearchQualityMeasurement(IReadOnlyList<SearchModeMeasurement> Modes);

public sealed record SearchModeMeasurement(
    string Mode,
    int ProbeCount,
    double MeanReciprocalRank,
    double RecallAt5,
    double TopHitAccuracy,
    double DiagnosticWarningRate,
    double AverageResultCount,
    IReadOnlyList<SearchProbeMeasurement> Probes);

public sealed record SearchProbeMeasurement(
    string Query,
    string ExpectedKind,
    string ExpectedId,
    int? Rank,
    string? TopKind,
    string? TopId,
    bool FoundInTop5,
    int ResultCount,
    int WarningResultCount,
    IReadOnlyList<SearchResultMeasurement> Results);

public sealed record SearchResultMeasurement(
    string Kind,
    string Id,
    string Title,
    double? Score,
    int WarningCount);

public sealed record PageCorpusMeasurement(
    int PageCount,
    long TotalCharacters,
    double AverageCharacters,
    int TotalHeadings,
    double AverageHeadings,
    int PagesOverChunkingTokenThreshold,
    IReadOnlyList<PageSizeBucket> SizeBuckets,
    IReadOnlyList<PageCorpusItem> LongestPages);

public sealed record PageSizeBucket(string Label, int Count);

public sealed record PageCorpusItem(string Slug, string Title, int Characters, int EstimatedTokens, int HeadingCount);

public sealed record TagPolicyHealthMeasurement(
    int RecordCount,
    int TagUseCount,
    int DistinctTagCount,
    int UnknownPlainTagCount,
    int BlockedTagUseCount,
    int AliasCandidateUseCount,
    int DuplicatePolicyNamespaceWarningCount,
    int BroadTagCount,
    int LowValueTagUseCount,
    IReadOnlyList<TagUseMeasurement> BroadTags,
    IReadOnlyDictionary<string, int> DiagnosticsByCode);

public sealed record TagUseMeasurement(string Tag, int Count, double RecordRatio);

public sealed record SourceLinkHealthMeasurement(
    int TotalSourceLinks,
    int RecordsWithSourceLinks,
    int SourceWarningCount,
    int MissingVariableCount,
    int UnresolvedLocalPathCount,
    int MissingFileCount,
    int DisallowedRootCount,
    int InvalidLineRangeCount,
    int LineOutOfRangeCount,
    double BrokenSourceLinkRate,
    IReadOnlyDictionary<string, int> DiagnosticsByCode);

public sealed class MeasurementBaselineService
{
    private static readonly MeasurementSearchProbe[] MemorySearchProbes =
    [
        new("single host blazor app architecture", "memory", "project-wiki-active-architecture"),
        new("mcp context pack format json", "memory", "project-wiki-mcp-context-pack"),
        new("semantic search token scoring fallback", "memory", "project-wiki-semantic-search-gap"),
        new("chat provider tool calls trace", "memory", "project-wiki-chat-agent-provider"),
        new("tag governance staleness diagnostics", "memory", "ai-memory-suite-implementation-plan-20260520")
    ];

    private static readonly MeasurementSearchProbe[] UnifiedSearchProbes =
    [
        .. MemorySearchProbes,
        new("council workflow decisions evidence", "page", "llm-council"),
        new("search and chat good search habits", "page", "search-and-chat")
    ];

    private static readonly HashSet<string> LowValueTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "misc", "general", "important", "stuff", "todo", "notes", "old", "new"
    };

    private static readonly Regex HeadingPattern = new("^#{1,6}\\s+", RegexOptions.Compiled | RegexOptions.Multiline);

    private readonly MemoryApplicationService _memories;
    private readonly IPageService _pages;
    private readonly IMemoryStore _store;
    private readonly MemoryDiagnosticsService _diagnostics;
    private readonly TagPolicyService _tagPolicy;
    private readonly ITextEmbeddingProvider _embeddingProvider;
    private readonly MeasurementBaselineThresholds _thresholds = new();

    public MeasurementBaselineService(
        MemoryApplicationService memories,
        IPageService pages,
        IMemoryStore store,
        MemoryDiagnosticsService diagnostics,
        TagPolicyService tagPolicy,
        ITextEmbeddingProvider embeddingProvider)
    {
        _memories = memories;
        _pages = pages;
        _store = store;
        _diagnostics = diagnostics;
        _tagPolicy = tagPolicy;
        _embeddingProvider = embeddingProvider;
    }

    public async Task<MeasurementBaselineSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var records = MemoryRecordLookup.ToRecordList(_store.LoadAll());
        var diagnosticsById = _diagnostics.AnalyzeAll(records);
        var semanticStatus = _embeddingProvider.GetStatus();

        return new MeasurementBaselineSnapshot(
            DateTime.UtcNow,
            await MeasureSearchAsync(cancellationToken),
            semanticStatus,
            semanticStatus.Available ? "onnx" : "token-fallback",
            await MeasurePagesAsync(cancellationToken),
            MeasureTags(records, diagnosticsById),
            MeasureSourceLinks(records, diagnosticsById),
            _thresholds);
    }

    private async Task<SearchQualityMeasurement> MeasureSearchAsync(CancellationToken cancellationToken)
    {
        var modes = new List<SearchModeMeasurement>
        {
            await MeasureModeAsync("lexical", MemorySearchProbes, probe => SearchLexicalAsync(probe, cancellationToken)),
            await MeasureModeAsync("semantic", MemorySearchProbes, probe => SearchSemanticAsync(probe, cancellationToken)),
            await MeasureModeAsync("hybrid", MemorySearchProbes, probe => SearchHybridAsync(probe, cancellationToken)),
            await MeasureModeAsync("unified", UnifiedSearchProbes, probe => SearchUnifiedAsync(probe, cancellationToken)),
            await MeasureModeAsync("context-pack", MemorySearchProbes, probe => SearchContextPackAsync(probe, cancellationToken))
        };

        return new SearchQualityMeasurement(modes);
    }

    private static async Task<SearchModeMeasurement> MeasureModeAsync(
        string mode,
        IReadOnlyList<MeasurementSearchProbe> probes,
        Func<MeasurementSearchProbe, Task<IReadOnlyList<SearchResultMeasurement>>> search)
    {
        var results = new List<SearchProbeMeasurement>();
        foreach (var probe in probes)
        {
            var items = await search(probe);
            var rank = FindRank(items, probe.ExpectedKind, probe.ExpectedId);
            var top = items.FirstOrDefault();
            results.Add(new SearchProbeMeasurement(
                probe.Query,
                probe.ExpectedKind,
                probe.ExpectedId,
                rank,
                top?.Kind,
                top?.Id,
                rank is <= 5,
                items.Count,
                items.Count(item => item.WarningCount > 0),
                items.Take(10).ToList()));
        }

        var resultCount = results.Sum(result => result.ResultCount);
        var warningCount = results.Sum(result => result.WarningResultCount);
        return new SearchModeMeasurement(
            mode,
            probes.Count,
            Math.Round(results.Average(result => result.Rank.HasValue ? 1.0 / result.Rank.Value : 0.0), 4),
            Math.Round(results.Count(result => result.FoundInTop5) / (double)probes.Count, 4),
            Math.Round(results.Count(result => result.Rank == 1) / (double)probes.Count, 4),
            resultCount == 0 ? 0 : Math.Round(warningCount / (double)resultCount, 4),
            Math.Round(results.Average(result => result.ResultCount), 2),
            results);
    }

    private async Task<IReadOnlyList<SearchResultMeasurement>> SearchLexicalAsync(MeasurementSearchProbe probe, CancellationToken cancellationToken) =>
        (await _memories.SearchMetadataAsync(new MemorySearchQuery(probe.Query, Tags: probe.Tags, Limit: probe.Limit), cancellationToken))
        .Select(result => new SearchResultMeasurement("memory", result.Id, result.Title, null, CountWarnings(result.Diagnostics)))
        .ToList();

    private async Task<IReadOnlyList<SearchResultMeasurement>> SearchSemanticAsync(MeasurementSearchProbe probe, CancellationToken cancellationToken) =>
        (await _memories.SemanticSearchAsync(new SemanticMemorySearchQuery(probe.Query, Tags: probe.Tags, Limit: probe.Limit), cancellationToken))
        .Select(ToMemoryResult)
        .ToList();

    private async Task<IReadOnlyList<SearchResultMeasurement>> SearchHybridAsync(MeasurementSearchProbe probe, CancellationToken cancellationToken) =>
        (await _memories.HybridSearchAsync(new HybridMemorySearchQuery(probe.Query, Tags: probe.Tags, Limit: probe.Limit), cancellationToken))
        .Select(ToMemoryResult)
        .ToList();

    private async Task<IReadOnlyList<SearchResultMeasurement>> SearchContextPackAsync(MeasurementSearchProbe probe, CancellationToken cancellationToken) =>
        (await _memories.BuildContextPackAsync(new MemoryContextPackQuery(
            Query: probe.Query,
            Tags: probe.Tags,
            Limit: Math.Min(5, probe.Limit),
            ReferenceDepth: 1,
            MaxRecords: probe.Limit,
            MaxContentChars: 500), cancellationToken))
        .Records
        .Select(record => new SearchResultMeasurement("memory", record.Id, record.Title, record.Score, CountWarnings(record.Diagnostics)))
        .ToList();

    private async Task<IReadOnlyList<SearchResultMeasurement>> SearchUnifiedAsync(MeasurementSearchProbe probe, CancellationToken cancellationToken)
    {
        var memoryLimit = Math.Max(1, probe.Limit / 2);
        var pageLimit = Math.Max(1, probe.Limit - memoryLimit);
        var memoryResults = await _memories.HybridSearchAsync(new HybridMemorySearchQuery(probe.Query, Tags: probe.Tags, Limit: memoryLimit), cancellationToken);
        var pageResults = await _pages.SearchAsync(new PageSearchQuery(probe.Query, pageLimit), cancellationToken);

        return memoryResults.Select(ToMemoryResult)
            .Concat(pageResults.Select(page => new SearchResultMeasurement("page", page.Slug, page.Title, null, 0)))
            .OrderByDescending(result => result.Score ?? 0)
            .ThenBy(result => result.Kind, StringComparer.OrdinalIgnoreCase)
            .ThenBy(result => result.Title, StringComparer.OrdinalIgnoreCase)
            .Take(probe.Limit)
            .ToList();
    }

    private async Task<PageCorpusMeasurement> MeasurePagesAsync(CancellationToken cancellationToken)
    {
        var summaries = await _pages.ListAsync(cancellationToken);
        var pages = new List<PageCorpusItem>();
        foreach (var summary in summaries)
        {
            var document = await _pages.GetAsync(summary.Slug, cancellationToken);
            if (document is null)
            {
                continue;
            }

            pages.Add(new PageCorpusItem(
                document.Slug,
                document.Title,
                document.Markdown.Length,
                EstimateTokens(document.Markdown.Length),
                HeadingPattern.Matches(document.Markdown).Count));
        }

        return new PageCorpusMeasurement(
            pages.Count,
            pages.Sum(page => page.Characters),
            pages.Count == 0 ? 0 : Math.Round(pages.Average(page => page.Characters), 2),
            pages.Sum(page => page.HeadingCount),
            pages.Count == 0 ? 0 : Math.Round(pages.Average(page => page.HeadingCount), 2),
            pages.Count(page => page.EstimatedTokens > _thresholds.PageChunkingTokenThreshold),
            BuildPageBuckets(pages),
            pages.OrderByDescending(page => page.Characters).ThenBy(page => page.Slug, StringComparer.OrdinalIgnoreCase).Take(10).ToList());
    }

    private TagPolicyHealthMeasurement MeasureTags(
        IReadOnlyList<MemoryRecord> records,
        IReadOnlyDictionary<string, IReadOnlyList<MemoryDiagnostic>> diagnosticsById)
    {
        var policy = _tagPolicy.GetPolicy();
        var allowlist = policy.PlainTags.Allowlist.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var blocklist = policy.PlainTags.Blocklist.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var aliases = new HashSet<string>(policy.PlainTags.Aliases.Keys, StringComparer.OrdinalIgnoreCase);
        var tagCounts = records
            .SelectMany(record => record.Tags.Distinct(StringComparer.OrdinalIgnoreCase))
            .GroupBy(tag => tag, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        var broadThreshold = Math.Max(1, (int)Math.Ceiling(records.Count * 0.60));
        var broadTags = tagCounts
            .Where(pair => pair.Value >= broadThreshold)
            .OrderByDescending(pair => pair.Value)
            .ThenBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => new TagUseMeasurement(pair.Key, pair.Value, records.Count == 0 ? 0 : Math.Round(pair.Value / (double)records.Count, 4)))
            .ToList();
        var diagnosticsByCode = CountDiagnosticsByCode(diagnosticsById.Values.SelectMany(value => value).Where(diagnostic => diagnostic.Category == "tag"));

        var unknownPlainTags = tagCounts.Keys.Count(tag =>
            !tag.Contains(':', StringComparison.Ordinal) &&
            !tag.StartsWith('#') &&
            !allowlist.Contains(tag) &&
            !blocklist.Contains(tag) &&
            !aliases.Contains(tag));

        return new TagPolicyHealthMeasurement(
            records.Count,
            records.Sum(record => record.Tags.Count),
            tagCounts.Count,
            unknownPlainTags,
            records.SelectMany(record => record.Tags).Count(tag => blocklist.Contains(tag)),
            records.SelectMany(record => record.Tags).Count(tag => aliases.Contains(tag)),
            diagnosticsByCode.GetValueOrDefault("tag.policy_duplicate_namespace"),
            broadTags.Count,
            records.SelectMany(record => record.Tags).Count(tag => blocklist.Contains(tag) || LowValueTags.Contains(tag)),
            broadTags,
            diagnosticsByCode);
    }

    private static SourceLinkHealthMeasurement MeasureSourceLinks(
        IReadOnlyList<MemoryRecord> records,
        IReadOnlyDictionary<string, IReadOnlyList<MemoryDiagnostic>> diagnosticsById)
    {
        var sourceDiagnostics = diagnosticsById.Values
            .SelectMany(value => value)
            .Where(diagnostic => diagnostic.Category == "source")
            .ToList();
        var diagnosticsByCode = CountDiagnosticsByCode(sourceDiagnostics);
        var unresolved = sourceDiagnostics.Where(diagnostic => diagnostic.Code == "source.unresolved").ToList();
        var totalLinks = records.Sum(record => record.SourceLinks.Count);
        var brokenCount = sourceDiagnostics.Count(IsWarningOrError);

        return new SourceLinkHealthMeasurement(
            totalLinks,
            records.Count(record => record.SourceLinks.Count > 0),
            brokenCount,
            diagnosticsByCode.GetValueOrDefault("source.missing_variable"),
            unresolved.Count,
            unresolved.Count(diagnostic => diagnostic.Message.Contains("does not exist", StringComparison.OrdinalIgnoreCase)),
            unresolved.Count(diagnostic => diagnostic.Message.Contains("outside the configured allowed source roots", StringComparison.OrdinalIgnoreCase)),
            diagnosticsByCode.GetValueOrDefault("source.invalid_line_range"),
            diagnosticsByCode.GetValueOrDefault("source.line_out_of_range"),
            totalLinks == 0 ? 0 : Math.Round(brokenCount / (double)totalLinks, 4),
            diagnosticsByCode);
    }

    private static IReadOnlyList<PageSizeBucket> BuildPageBuckets(IReadOnlyList<PageCorpusItem> pages) =>
    [
        new("0-499 chars", pages.Count(page => page.Characters < 500)),
        new("500-1499 chars", pages.Count(page => page.Characters is >= 500 and < 1500)),
        new("1500-5999 chars", pages.Count(page => page.Characters is >= 1500 and < 6000)),
        new("6000+ chars", pages.Count(page => page.Characters >= 6000))
    ];

    private static SearchResultMeasurement ToMemoryResult(MemorySearchResult result) =>
        new("memory", result.Id, result.Title, result.Score, CountWarnings(result.Diagnostics));

    private static IReadOnlyDictionary<string, int> CountDiagnosticsByCode(IEnumerable<MemoryDiagnostic> diagnostics) =>
        diagnostics
            .GroupBy(diagnostic => diagnostic.Code, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);

    private static int CountWarnings(IEnumerable<MemoryDiagnostic> diagnostics) =>
        diagnostics.Count(IsWarningOrError);

    private static bool IsWarningOrError(MemoryDiagnostic diagnostic) =>
        !string.Equals(diagnostic.Severity, "Info", StringComparison.OrdinalIgnoreCase);

    private static int? FindRank(IReadOnlyList<SearchResultMeasurement> results, string expectedKind, string expectedId)
    {
        for (var index = 0; index < results.Count; index++)
        {
            var result = results[index];
            if (string.Equals(result.Kind, expectedKind, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(result.Id, expectedId, StringComparison.OrdinalIgnoreCase))
            {
                return index + 1;
            }
        }

        return null;
    }

    private static int EstimateTokens(int characters) =>
        (int)Math.Ceiling(characters / 4.0);

    private sealed record MeasurementSearchProbe(string Query, string ExpectedKind, string ExpectedId, int Limit = 10, string Tags = "project-wiki");
}