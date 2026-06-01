using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MemorySmith.App.Services;

public sealed record CodeSearchQuery(
    string? Query,
    IReadOnlyList<string>? Targets = null,
    int Limit = 10,
    bool RebuildIfStale = true,
    bool ForceRebuild = false);

public sealed record CodeSearchResult(
    string Target,
    string DocumentPath,
    string AbsolutePath,
    int StartLine,
    int EndLine,
    double Score,
    string Snippet,
    string MatchReason,
    DateTime IndexedAtUtc);

public sealed record CodeSearchStatus(
    bool Enabled,
    string RepositoryRoot,
    string IndexPath,
    int IndexedFileCount,
    int IndexedChunkCount,
    string ProviderMode,
    string ProviderStatus,
    CodeSearchBuildProgress Build);

public sealed record CodeSearchShardMergeResult(
    string ShardPath,
    int InsertedChunkCount,
    int UpdatedChunkCount,
    int SkippedChunkCount,
    int TotalShardChunkCount,
    long ElapsedMilliseconds);

public sealed record CodeSearchBuildTimings(
    long ProviderInitializationMilliseconds,
    long FileReadMilliseconds,
    long ContentHashMilliseconds,
    long ChunkingMilliseconds,
    long EmbeddingMilliseconds,
    long DatabaseWriteMilliseconds,
    long RemovedDocumentCleanupMilliseconds,
    int EmbeddingCallCount,
    int EmbeddedChunkCount)
{
    public double AverageEmbeddingMilliseconds => EmbeddingCallCount <= 0
        ? 0
        : Math.Round((double)EmbeddingMilliseconds / EmbeddingCallCount, 3);

    public static CodeSearchBuildTimings Empty { get; } = new(
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        0);
}

public sealed record CodeSearchBuildProgress(
    string State,
    bool IsRunning,
    int TotalFileCount,
    int ProcessedFileCount,
    int ReusedFileCount,
    int UpdatedFileCount,
    int SkippedFileCount,
    int RemovedFileCount,
    int FailedFileCount,
    int PendingWriteCount,
    CodeSearchBuildTimings Timings,
    string? CurrentTarget,
    string? CurrentDocumentPath,
    DateTime? StartedAtUtc,
    DateTime? UpdatedAtUtc,
    DateTime? CompletedAtUtc,
    string? LastError)
{
    public int ProgressPercentage => TotalFileCount <= 0
        ? 0
        : (int)Math.Clamp(
            Math.Round((double)ProcessedFileCount / TotalFileCount * 100, MidpointRounding.AwayFromZero),
            0,
            100);

    public static CodeSearchBuildProgress Idle { get; } = new(
        "idle",
        false,
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        CodeSearchBuildTimings.Empty,
        null,
        null,
        null,
        null,
        null,
        null);
}

public sealed class CodeSearchService : IDisposable
{
    private const int MaxCachedQueryEmbeddings = 256;
    private const int MaxCachedQueryResults = 128;
    private static readonly Regex TokenRegex = new("[A-Za-z0-9_]+", RegexOptions.Compiled);
    private static readonly Regex IdentifierSplitRegex = new("(?<=[a-z0-9])(?=[A-Z])|_+", RegexOptions.Compiled);
    private static readonly Dictionary<string, string[]> QuerySynonyms = new(StringComparer.OrdinalIgnoreCase)
    {
        ["tool"] = ["tools", "tooling", "utility", "utilities", "harness", "script", "scripts", "cli"],
        ["tools"] = ["tool", "tooling", "utility", "utilities", "harness", "script", "scripts", "cli"],
        ["tooling"] = ["tool", "tools", "utility", "utilities", "harness", "script", "scripts", "cli"],
        ["utility"] = ["tool", "tools", "tooling", "utilities", "harness", "script", "scripts"],
        ["utilities"] = ["tool", "tools", "tooling", "utility", "harness", "script", "scripts"],
        ["screwdriver"] = ["tool", "tools", "tooling", "utility", "driver", "drivers"],
        ["hammer"] = ["tool", "tools", "tooling", "utility"],
        ["wrench"] = ["tool", "tools", "tooling", "utility"],
        ["pliers"] = ["tool", "tools", "tooling", "utility"]
    };

    private readonly ITextEmbeddingProvider _embeddingProvider;
    private readonly MemorySmithOptions _settings;
    private readonly CodeSearchOptions _options;
    private readonly SemanticSearchOptions _semanticOptions;
    private readonly ILogger<CodeSearchService> _logger;
    private readonly MemoryCache _queryEmbeddingCache = new(new MemoryCacheOptions { SizeLimit = MaxCachedQueryEmbeddings });
    private readonly MemoryCache _queryResultCache = new(new MemoryCacheOptions { SizeLimit = MaxCachedQueryResults });
    private readonly SemaphoreSlim _indexLock = new(1, 1);
    private readonly object _buildProgressGate = new();
    private readonly string _dataRoot;
    private readonly string _repositoryRoot;
    private readonly string _indexDatabasePath;
    private readonly double _hybridVectorWeight;
    private readonly double _hybridLexicalWeight;
    private readonly double _zeroLexicalEvidencePenalty;
    private readonly double _lexicalScoreSaturation;
    private readonly double _lexicalFrequencyBonusScale;
    private readonly double _maxLexicalFrequencyBonusPerToken;
    private readonly double _minTokenCoverageWeight;
    private readonly double _maxTokenCoverageWeight;
    private CodeSearchBuildProgress _buildProgress = CodeSearchBuildProgress.Idle;
    private long _resultCacheGeneration;
    private long _queryTelemetryCounter;
    private long _nextStalenessCheckUtcTicks;

    private sealed class BuildTimingAccumulator
    {
        public long ProviderInitializationMilliseconds { get; set; }
        public long FileReadMilliseconds { get; set; }
        public long ContentHashMilliseconds { get; set; }
        public long ChunkingMilliseconds { get; set; }
        public long EmbeddingMilliseconds { get; set; }
        public long DatabaseWriteMilliseconds { get; set; }
        public long RemovedDocumentCleanupMilliseconds { get; set; }
        public int EmbeddingCallCount { get; set; }
        public int EmbeddedChunkCount { get; set; }

        public CodeSearchBuildTimings Snapshot() => new(
            ProviderInitializationMilliseconds,
            FileReadMilliseconds,
            ContentHashMilliseconds,
            ChunkingMilliseconds,
            EmbeddingMilliseconds,
            DatabaseWriteMilliseconds,
            RemovedDocumentCleanupMilliseconds,
            EmbeddingCallCount,
            EmbeddedChunkCount);
    }

    private sealed record ChunkFileResult(
        List<CodeChunkRow> Chunks,
        long ChunkingMilliseconds,
        long EmbeddingMilliseconds,
        int EmbeddingCallCount,
        int EmbeddedChunkCount);

    private sealed record PreparedChunk(
        string Target,
        string DocumentPath,
        string AbsolutePath,
        int ChunkId,
        string SourceHash,
        long SourceLengthBytes,
        DateTime SourceLastWriteUtc,
        string ConfigurationHash,
        int StartLine,
        int EndLine,
        string Snippet,
        string SearchText,
        string EmbeddingText);

    private sealed record EmbeddingBatchResult(
        List<float[]> Embeddings,
        long EmbeddingMilliseconds,
        int EmbeddingCallCount,
        int EmbeddedChunkCount);

    private sealed record ParsedChunk(
        int StartLine,
        int EndLine,
        string ChunkText);

    private sealed record VectorCandidateLoadResult(
        List<IndexedChunk> Chunks,
        bool UsedPrefilter);

    public CodeSearchService(
        ITextEmbeddingProvider embeddingProvider,
        IOptions<MemorySmithOptions> options,
        ILogger<CodeSearchService>? logger = null)
    {
        _embeddingProvider = embeddingProvider;
        _settings = options.Value;
        _options = _settings.CodeSearch;
        _semanticOptions = _settings.SemanticSearch;
        _logger = logger ?? NullLogger<CodeSearchService>.Instance;
        _dataRoot = ResolveDataDeploymentRoot(_settings.DataPath);
        _repositoryRoot = ResolveRepositoryRoot(_dataRoot, _options.RepositoryRootPath);
        _indexDatabasePath = Path.Combine(_dataRoot, "Graph", "code-search", "code-search.db");
        _hybridVectorWeight = Math.Clamp(_options.HybridVectorWeight, 0.0, 2.0);
        _hybridLexicalWeight = Math.Clamp(_options.HybridLexicalWeight, 0.0, 2.0);
        _zeroLexicalEvidencePenalty = Math.Clamp(_options.ZeroLexicalEvidencePenalty, 0.0, 2.0);
        _lexicalScoreSaturation = Math.Max(0.001, _options.LexicalScoreSaturation);
        _lexicalFrequencyBonusScale = Math.Clamp(_options.LexicalFrequencyBonusScale, 0.0, 2.0);
        _maxLexicalFrequencyBonusPerToken = Math.Clamp(_options.MaxLexicalFrequencyBonusPerToken, 0.0, 10.0);
        _minTokenCoverageWeight = Math.Clamp(_options.MinTokenCoverageWeight, 0.0, 3.0);
        _maxTokenCoverageWeight = Math.Clamp(_options.MaxTokenCoverageWeight, _minTokenCoverageWeight, 3.0);
    }

    public async Task<IReadOnlyList<CodeSearchResult>> SearchAsync(CodeSearchQuery query, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_options.Enabled || string.IsNullOrWhiteSpace(query.Query))
        {
            return [];
        }

        var queryTelemetryEnabled = _options.QueryTimingTelemetryEnabled;
        var queryStartTimestamp = queryTelemetryEnabled ? Stopwatch.GetTimestamp() : 0L;

        await EnsureIndexedAsync(query.RebuildIfStale, query.ForceRebuild, cancellationToken);

        var normalizedTargets = NormalizeTargets(query.Targets);
        var limit = Math.Clamp(query.Limit, 1, Math.Max(1, _options.MaxResults));
        var queryTokens = Tokenize(query.Query!);
        var expandedQueryTokens = ExpandQueryTokens(queryTokens);

        IReadOnlyList<CodeSearchResult> CompleteSearch(string mode, IReadOnlyList<CodeSearchResult> results, int scannedChunkCount = 0)
        {
            if (queryTelemetryEnabled)
            {
                LogQueryTiming(query.Query!, normalizedTargets, limit, mode, results.Count, scannedChunkCount, queryStartTimestamp);
            }

            return results;
        }

        var resultCacheKey = BuildResultCacheKey(query.Query!, normalizedTargets, limit);
        if (_queryResultCache.TryGetValue(resultCacheKey, out IReadOnlyList<CodeSearchResult>? cachedResults) && cachedResults is not null)
        {
            return CompleteSearch("cache", cachedResults);
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        List<IndexedChunk>? chunks = null;

        if (TryGetQueryEmbedding(query.Query!, out var queryEmbedding))
        {
            var vectorCandidates = await LoadVectorCandidatesAsync(connection, normalizedTargets, expandedQueryTokens, limit, cancellationToken);
            var vectorScored = vectorCandidates.Chunks
                .Where(chunk => chunk.Embedding.Length == queryEmbedding.Length)
                .Select(chunk =>
                {
                    var rawScore = Dot(queryEmbedding, chunk.Embedding);
                    var matchedTokenCount = CountMatchedTokens(chunk, queryTokens);
                    var lexicalScore = ScoreLexical(chunk, expandedQueryTokens);
                    var targetWeight = GetTargetWeight(chunk.Target, chunk.DocumentPath, queryTokens);
                    var hybridScore = ScoreHybrid(rawScore, lexicalScore);
                    var coverageWeight = ScoreTokenCoverageWeight(matchedTokenCount, queryTokens.Count);
                    return new ScoredChunk(chunk, rawScore, lexicalScore, targetWeight, coverageWeight, hybridScore * targetWeight * coverageWeight, matchedTokenCount);
                })
                .Where(entry => entry.WeightedScore > 0)
                .OrderByDescending(entry => entry.WeightedScore)
                .ThenBy(entry => entry.Chunk.DocumentPath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => entry.Chunk.StartLine)
                .ToList();

            var vectorResults = TakeBalancedByDocument(vectorScored, limit, _options.MaxResultsPerDocument)
                .Select(entry => new CodeSearchResult(
                    entry.Chunk.Target,
                    entry.Chunk.DocumentPath,
                    entry.Chunk.AbsolutePath,
                    entry.Chunk.StartLine,
                    entry.Chunk.EndLine,
                    Math.Round(entry.WeightedScore, 6),
                    BuildSnippet(entry.Chunk.Snippet, query.Query!),
                    BuildVectorMatchReason(entry.RawScore, entry.LexicalScore, entry.TargetWeight, entry.CoverageWeight),
                    entry.Chunk.IndexedAtUtc))
                .ToList();

            var shouldRunSparseFallback = vectorCandidates.UsedPrefilter && ShouldRunSparsePrefilterFallback(vectorCandidates.Chunks.Count);

            if (vectorResults.Count > 0 && !shouldRunSparseFallback)
            {
                CacheResults(resultCacheKey, vectorResults);
                return CompleteSearch(vectorCandidates.UsedPrefilter ? "vector-prefilter" : "vector", vectorResults, vectorCandidates.Chunks.Count);
            }

            if (vectorCandidates.UsedPrefilter && (vectorResults.Count == 0 || shouldRunSparseFallback))
            {
                chunks = await LoadChunksAsync(connection, normalizedTargets, cancellationToken);
                var fallbackVectorScored = chunks
                    .Where(chunk => chunk.Embedding.Length == queryEmbedding.Length)
                    .Select(chunk =>
                    {
                        var rawScore = Dot(queryEmbedding, chunk.Embedding);
                        var matchedTokenCount = CountMatchedTokens(chunk, queryTokens);
                        var lexicalScore = ScoreLexical(chunk, expandedQueryTokens);
                        var targetWeight = GetTargetWeight(chunk.Target, chunk.DocumentPath, queryTokens);
                        var hybridScore = ScoreHybrid(rawScore, lexicalScore);
                        var coverageWeight = ScoreTokenCoverageWeight(matchedTokenCount, queryTokens.Count);
                        return new ScoredChunk(chunk, rawScore, lexicalScore, targetWeight, coverageWeight, hybridScore * targetWeight * coverageWeight, matchedTokenCount);
                    })
                    .Where(entry => entry.WeightedScore > 0)
                    .OrderByDescending(entry => entry.WeightedScore)
                    .ThenBy(entry => entry.Chunk.DocumentPath, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(entry => entry.Chunk.StartLine)
                    .ToList();

                var fallbackVectorResults = TakeBalancedByDocument(fallbackVectorScored, limit, _options.MaxResultsPerDocument)
                    .Select(entry => new CodeSearchResult(
                        entry.Chunk.Target,
                        entry.Chunk.DocumentPath,
                        entry.Chunk.AbsolutePath,
                        entry.Chunk.StartLine,
                        entry.Chunk.EndLine,
                        Math.Round(entry.WeightedScore, 6),
                        BuildSnippet(entry.Chunk.Snippet, query.Query!),
                        BuildVectorMatchReason(entry.RawScore, entry.LexicalScore, entry.TargetWeight, entry.CoverageWeight),
                        entry.Chunk.IndexedAtUtc))
                    .ToList();

                if (fallbackVectorResults.Count > 0)
                {
                    CacheResults(resultCacheKey, fallbackVectorResults);
                    var mode = shouldRunSparseFallback && vectorResults.Count > 0
                        ? "vector-sparse-fallback"
                        : "vector-full-fallback";
                    return CompleteSearch(mode, fallbackVectorResults, chunks.Count);
                }
            }

            if (vectorResults.Count > 0)
            {
                CacheResults(resultCacheKey, vectorResults);
                return CompleteSearch(vectorCandidates.UsedPrefilter ? "vector-prefilter" : "vector", vectorResults, vectorCandidates.Chunks.Count);
            }
        }

        chunks ??= await LoadChunksAsync(connection, normalizedTargets, cancellationToken);
        if (chunks.Count == 0)
        {
            return CompleteSearch("empty-index", [], 0);
        }

        var lexicalScored = chunks
            .Select(chunk =>
            {
                var rawScore = ScoreLexical(chunk, expandedQueryTokens);
                var matchedTokenCount = CountMatchedTokens(chunk, queryTokens);
                var targetWeight = GetTargetWeight(chunk.Target, chunk.DocumentPath, queryTokens);
                var coverageWeight = ScoreTokenCoverageWeight(matchedTokenCount, queryTokens.Count);
                return new ScoredChunk(chunk, rawScore, rawScore, targetWeight, coverageWeight, rawScore * targetWeight * coverageWeight, matchedTokenCount);
            })
            .Where(entry => entry.WeightedScore > 0)
            .OrderByDescending(entry => entry.WeightedScore)
            .ThenBy(entry => entry.Chunk.DocumentPath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.Chunk.StartLine)
            .ToList();

        var lexicalResults = TakeBalancedByDocument(lexicalScored, limit, _options.MaxResultsPerDocument)
            .Select(entry => new CodeSearchResult(
                entry.Chunk.Target,
                entry.Chunk.DocumentPath,
                entry.Chunk.AbsolutePath,
                entry.Chunk.StartLine,
                entry.Chunk.EndLine,
                Math.Round(entry.WeightedScore, 6),
                BuildSnippet(entry.Chunk.Snippet, query.Query!),
                BuildLexicalMatchReason(entry.MatchedTokenCount, queryTokens.Count, entry.TargetWeight, entry.CoverageWeight),
                entry.Chunk.IndexedAtUtc))
            .ToList();

            CacheResults(resultCacheKey, lexicalResults);
                return CompleteSearch("lexical", lexicalResults, chunks.Count);
    }

    public async Task<CodeSearchStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await EnsureDatabaseAsync(cancellationToken);

        var fileCount = 0;
        var chunkCount = 0;
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT COUNT(DISTINCT DocumentPath), COUNT(*) FROM CodeSearchChunks;";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                fileCount = reader.IsDBNull(0) ? 0 : reader.GetInt32(0);
                chunkCount = reader.IsDBNull(1) ? 0 : reader.GetInt32(1);
            }
        }

        var provider = _embeddingProvider.GetStatus();
        return new CodeSearchStatus(
            _options.Enabled,
            _repositoryRoot,
            _indexDatabasePath,
            fileCount,
            chunkCount,
            provider.Available ? "vector" : "lexical-fallback",
            provider.Reason,
            GetBuildProgress());
    }

    public async Task EnsureIndexedAsync(bool rebuildIfStale, CancellationToken cancellationToken)
        => await EnsureIndexedAsync(rebuildIfStale, forceRebuild: false, cancellationToken);

    public async Task EnsureIndexedAsync(bool rebuildIfStale, bool forceRebuild, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_options.Enabled || !rebuildIfStale)
        {
            await EnsureDatabaseAsync(cancellationToken);
            return;
        }

        if (!forceRebuild && ShouldSkipStalenessCheck())
        {
            await EnsureDatabaseAsync(cancellationToken);
            return;
        }

        await _indexLock.WaitAsync(cancellationToken);
        try
        {
            if (!forceRebuild && ShouldSkipStalenessCheck())
            {
                await EnsureDatabaseAsync(cancellationToken);
                return;
            }

            await EnsureDatabaseAsync(cancellationToken);
            await BuildIndexCoreAsync(forceRebuild, cancellationToken);
            if (!forceRebuild)
            {
                StampStalenessCooldownWindow();
            }
        }
        finally
        {
            _indexLock.Release();
        }
    }

    public void Dispose()
    {
        _queryEmbeddingCache.Dispose();
        _queryResultCache.Dispose();
    }

    private async Task BuildIndexCoreAsync(bool forceRebuild, CancellationToken cancellationToken)
    {
        var targets = ResolveTargets();
        var matcher = CodeSearchIgnoreMatcher.Create(_repositoryRoot, _options.IncludePatterns, _options.ExcludePatterns);
        var configurationHash = BuildConfigurationHash();
        var timings = new BuildTimingAccumulator();
        var providerStopwatch = Stopwatch.StartNew();
        var providerStatus = _embeddingProvider.GetStatus();
        providerStopwatch.Stop();
        timings.ProviderInitializationMilliseconds = providerStopwatch.ElapsedMilliseconds;
        var canEmbed = providerStatus.Available;
        var candidates = ResolveTargetFiles(targets, matcher);
        BeginBuild(candidates.Count, timings.Snapshot());

        _logger.LogInformation(
            "Starting code search index build for {FileCount} file(s). Force rebuild: {ForceRebuild}. Provider mode: {ProviderMode}.",
            candidates.Count,
            forceRebuild,
            canEmbed ? "vector" : "lexical-fallback");

        if (candidates.Count == 0)
        {
            CompleteBuild(0, 0, 0, 0, 0, 0, null, timings.Snapshot());
            return;
        }

        var liveDocuments = candidates
            .Select(candidate => candidate.DocumentPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var batchSize = Math.Max(1, _options.IndexWriteBatchSize);
        var statusUpdateInterval = Math.Max(1, _options.StatusUpdateIntervalDocuments);
        var pendingDocuments = new List<PendingDocumentUpdate>(batchSize);
        var processedFileCount = 0;
        var reusedFileCount = 0;
        var updatedFileCount = 0;
        var skippedFileCount = 0;
        var removedFileCount = 0;
        var failedFileCount = 0;
        var nextLogAt = statusUpdateInterval;

        await using var connection = await OpenConnectionAsync(cancellationToken);
        var existingDocuments = await LoadExistingDocumentsAsync(connection, cancellationToken);

        var buildId = Guid.NewGuid().ToString("N");
        string? resumedFromBuildId = null;
        var resumedProcessedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!forceRebuild && _options.ResumableBuildsEnabled)
        {
            (resumedFromBuildId, resumedProcessedPaths) = await LoadResumableBuildAsync(connection, configurationHash, cancellationToken);
            if (resumedFromBuildId is not null)
            {
                _logger.LogInformation(
                    "Resuming interrupted code search index build {ResumedBuildId} with {ResumedPathCount} already-processed document(s).",
                    resumedFromBuildId,
                    resumedProcessedPaths.Count);
            }
        }

        await SaveBuildLogStartAsync(connection, buildId, configurationHash, candidates.Count, resumedFromBuildId, cancellationToken);

        try
        {
            foreach (var candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();

                SetBuildProgress(progress => progress with
                {
                    State = "indexing",
                    CurrentTarget = candidate.TargetKey,
                    CurrentDocumentPath = candidate.DocumentPath,
                    PendingWriteCount = pendingDocuments.Count,
                    UpdatedAtUtc = DateTime.UtcNow
                });

                try
                {
                    if (!forceRebuild && resumedProcessedPaths.Contains(candidate.DocumentPath))
                    {
                        reusedFileCount++;
                    }
                    else
                    {
                    var fileInfo = new FileInfo(candidate.AbsolutePath);
                    if (!fileInfo.Exists || fileInfo.Length > _options.MaxFileBytes)
                    {
                        skippedFileCount++;
                    }
                    else if (!forceRebuild &&
                             _options.WarmMetadataReuseEnabled &&
                             existingDocuments.TryGetValue(candidate.DocumentPath, out var existingByMetadata) &&
                             existingByMetadata is not null &&
                             CanWarmReuseByMetadata(existingByMetadata, configurationHash, canEmbed, fileInfo))
                    {
                        reusedFileCount++;
                    }
                    else
                    {
                        var fileReadStopwatch = Stopwatch.StartNew();
                        var sourceText = await File.ReadAllTextAsync(candidate.AbsolutePath, cancellationToken);
                        fileReadStopwatch.Stop();
                        timings.FileReadMilliseconds += fileReadStopwatch.ElapsedMilliseconds;

                        var hashStopwatch = Stopwatch.StartNew();
                        var sourceHash = ComputeHash(sourceText);
                        hashStopwatch.Stop();
                        timings.ContentHashMilliseconds += hashStopwatch.ElapsedMilliseconds;

                        if (!forceRebuild &&
                            existingDocuments.TryGetValue(candidate.DocumentPath, out var existingDocument) &&
                            CanReuseDocument(existingDocument, sourceHash, configurationHash, canEmbed))
                        {
                            reusedFileCount++;
                        }
                        else
                        {
                            var chunkResult = ChunkFile(
                                candidate.TargetKey,
                                candidate.DocumentPath,
                                candidate.AbsolutePath,
                                sourceText,
                                sourceHash,
                                fileInfo.Length,
                                fileInfo.LastWriteTimeUtc,
                                configurationHash,
                                canEmbed);
                            timings.ChunkingMilliseconds += chunkResult.ChunkingMilliseconds;
                            timings.EmbeddingMilliseconds += chunkResult.EmbeddingMilliseconds;
                            timings.EmbeddingCallCount += chunkResult.EmbeddingCallCount;
                            timings.EmbeddedChunkCount += chunkResult.EmbeddedChunkCount;
                            pendingDocuments.Add(new PendingDocumentUpdate(candidate.DocumentPath, chunkResult.Chunks));
                            if (pendingDocuments.Count >= batchSize)
                            {
                                var writeStopwatch = Stopwatch.StartNew();
                                updatedFileCount += await ReplaceDocumentsBatchAsync(connection, pendingDocuments, cancellationToken);
                                writeStopwatch.Stop();
                                timings.DatabaseWriteMilliseconds += writeStopwatch.ElapsedMilliseconds;
                                await SaveBuildLogDocumentsAsync(connection, buildId, pendingDocuments.Select(d => d.DocumentPath), cancellationToken);
                                pendingDocuments.Clear();
                            }
                        }
                    }
                    } // end else (not resumed)
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failedFileCount++;
                    _logger.LogWarning(ex, "Skipping code search document {DocumentPath} after an indexing failure.", candidate.DocumentPath);
                    SetBuildProgress(progress => progress with
                    {
                        LastError = $"{candidate.DocumentPath}: {ex.Message}",
                        UpdatedAtUtc = DateTime.UtcNow
                    });
                }

                processedFileCount++;
                SetBuildProgress(progress => progress with
                {
                    ProcessedFileCount = processedFileCount,
                    ReusedFileCount = reusedFileCount,
                    UpdatedFileCount = updatedFileCount,
                    SkippedFileCount = skippedFileCount,
                    FailedFileCount = failedFileCount,
                    PendingWriteCount = pendingDocuments.Count,
                    Timings = timings.Snapshot(),
                    CurrentTarget = candidate.TargetKey,
                    CurrentDocumentPath = candidate.DocumentPath,
                    UpdatedAtUtc = DateTime.UtcNow
                });

                if (processedFileCount >= nextLogAt)
                {
                    LogBuildProgress(processedFileCount, reusedFileCount, updatedFileCount, skippedFileCount, failedFileCount, pendingDocuments.Count);
                    nextLogAt += statusUpdateInterval;
                }
            }

            if (pendingDocuments.Count > 0)
            {
                var writeStopwatch = Stopwatch.StartNew();
                updatedFileCount += await ReplaceDocumentsBatchAsync(connection, pendingDocuments, cancellationToken);
                writeStopwatch.Stop();
                timings.DatabaseWriteMilliseconds += writeStopwatch.ElapsedMilliseconds;
                await SaveBuildLogDocumentsAsync(connection, buildId, pendingDocuments.Select(d => d.DocumentPath), cancellationToken);
                pendingDocuments.Clear();
                SetBuildProgress(progress => progress with
                {
                    UpdatedFileCount = updatedFileCount,
                    PendingWriteCount = 0,
                    Timings = timings.Snapshot(),
                    UpdatedAtUtc = DateTime.UtcNow
                });
            }

            var cleanupStopwatch = Stopwatch.StartNew();
            removedFileCount = await DeleteRemovedDocumentsAsync(connection, liveDocuments, cancellationToken);
            cleanupStopwatch.Stop();
            timings.RemovedDocumentCleanupMilliseconds += cleanupStopwatch.ElapsedMilliseconds;
            if (updatedFileCount > 0 || removedFileCount > 0 || forceRebuild)
            {
                InvalidateQueryCaches();
            }

            var timingSnapshot = timings.Snapshot();

            CompleteBuild(
                processedFileCount,
                reusedFileCount,
                updatedFileCount,
                skippedFileCount,
                removedFileCount,
                failedFileCount,
                GetBuildProgress().LastError,
                timingSnapshot);

            await FinalizeBuildLogAsync(connection, buildId, "completed", processedFileCount, GetBuildProgress().LastError, cancellationToken);
            await PruneBuildLogAsync(connection, _options.MaxCompletedBuildLogEntries, cancellationToken);

            _logger.LogInformation(
                "Completed code search index build for {ProcessedFileCount} file(s). Reused {ReusedFileCount}, updated {UpdatedFileCount}, removed {RemovedFileCount}, skipped {SkippedFileCount}, failed {FailedFileCount}. Timing ms: provider init {ProviderInitializationMilliseconds}, file read {FileReadMilliseconds}, hash {ContentHashMilliseconds}, chunk prep {ChunkingMilliseconds}, embed {EmbeddingMilliseconds} across {EmbeddingCallCount} call(s) for {EmbeddedChunkCount} chunk(s), DB write {DatabaseWriteMilliseconds}, cleanup {RemovedDocumentCleanupMilliseconds}.",
                processedFileCount,
                reusedFileCount,
                updatedFileCount,
                removedFileCount,
                skippedFileCount,
                failedFileCount,
                timingSnapshot.ProviderInitializationMilliseconds,
                timingSnapshot.FileReadMilliseconds,
                timingSnapshot.ContentHashMilliseconds,
                timingSnapshot.ChunkingMilliseconds,
                timingSnapshot.EmbeddingMilliseconds,
                timingSnapshot.EmbeddingCallCount,
                timingSnapshot.EmbeddedChunkCount,
                timingSnapshot.DatabaseWriteMilliseconds,
                timingSnapshot.RemovedDocumentCleanupMilliseconds);
        }
        catch (OperationCanceledException)
        {
            CancelBuild(processedFileCount, reusedFileCount, updatedFileCount, skippedFileCount, removedFileCount, failedFileCount, GetBuildProgress().LastError, timings.Snapshot());
            _logger.LogWarning("Canceled the code search index build after processing {ProcessedFileCount} file(s).", processedFileCount);
            throw;
        }
        catch (Exception ex)
        {
            FailBuild(processedFileCount, reusedFileCount, updatedFileCount, skippedFileCount, removedFileCount, failedFileCount, ex.Message, timings.Snapshot());
            _logger.LogError(ex, "The code search index build failed after processing {ProcessedFileCount} file(s).", processedFileCount);
            throw;
        }
    }

    private List<ResolvedTarget> ResolveTargets()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var results = new List<ResolvedTarget>();
        foreach (var configured in _options.TargetDirectories.Where(value => !string.IsNullOrWhiteSpace(value)))
        {
            var fullPath = Path.IsPathFullyQualified(configured)
                ? Path.GetFullPath(configured)
                : Path.GetFullPath(Path.Combine(_repositoryRoot, configured));
            if (!Directory.Exists(fullPath))
            {
                continue;
            }

            var key = NormalizeTargetKey(configured, fullPath);
            if (!seen.Add(key))
            {
                continue;
            }

            results.Add(new ResolvedTarget(key, fullPath));
        }

        return results;
    }

    private List<ResolvedFileCandidate> ResolveTargetFiles(IReadOnlyList<ResolvedTarget> targets, CodeSearchIgnoreMatcher matcher)
    {
        var allowedExtensions = new HashSet<string>(
            _options.IncludedFileExtensions
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(NormalizeExtension),
            StringComparer.OrdinalIgnoreCase);

        var results = new List<ResolvedFileCandidate>();

        foreach (var target in targets)
        {
            foreach (var filePath in Directory.EnumerateFiles(target.FullPath, "*", SearchOption.AllDirectories))
            {
                var extension = NormalizeExtension(Path.GetExtension(filePath));
                if (!allowedExtensions.Contains(extension))
                {
                    continue;
                }

                var documentPath = GetDocumentPath(filePath);
                if (matcher.IsIgnored(documentPath))
                {
                    continue;
                }

                results.Add(new ResolvedFileCandidate(target.Key, documentPath, filePath));
            }
        }

        return results;
    }

    private ChunkFileResult ChunkFile(
        string target,
        string documentPath,
        string absolutePath,
        string sourceText,
        string sourceHash,
        long sourceLengthBytes,
        DateTime sourceLastWriteUtc,
        string configurationHash,
        bool canEmbed)
    {
        var chunkingStopwatch = Stopwatch.StartNew();
        var lines = sourceText.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        var parsedChunks = BuildParsedChunks(documentPath, sourceText, lines);
        var preparedChunks = new List<PreparedChunk>(parsedChunks.Count);
        var chunkIndex = 0;

        foreach (var parsedChunk in parsedChunks)
        {
            preparedChunks.Add(new PreparedChunk(
                target,
                documentPath,
                absolutePath,
                chunkIndex++,
                sourceHash,
                sourceLengthBytes,
                sourceLastWriteUtc,
                configurationHash,
                parsedChunk.StartLine,
                parsedChunk.EndLine,
                BuildSnippet(parsedChunk.ChunkText, parsedChunk.ChunkText),
                parsedChunk.ChunkText,
                BuildEmbeddingText(documentPath, parsedChunk.ChunkText)));
        }

        chunkingStopwatch.Stop();
        var embeddingResult = canEmbed
            ? EmbedPreparedChunks(preparedChunks)
            : new EmbeddingBatchResult(new List<float[]>(preparedChunks.Count), 0, 0, 0);

        var indexedAtUtc = DateTime.UtcNow;
        var chunks = new List<CodeChunkRow>(preparedChunks.Count);
        for (var index = 0; index < preparedChunks.Count; index++)
        {
            var preparedChunk = preparedChunks[index];
            var embedding = embeddingResult.Embeddings.Count > index ? embeddingResult.Embeddings[index] : [];
            chunks.Add(new CodeChunkRow(
                preparedChunk.Target,
                preparedChunk.DocumentPath,
                preparedChunk.AbsolutePath,
                preparedChunk.ChunkId,
                preparedChunk.SourceHash,
                preparedChunk.SourceLengthBytes,
                preparedChunk.SourceLastWriteUtc,
                preparedChunk.ConfigurationHash,
                preparedChunk.StartLine,
                preparedChunk.EndLine,
                preparedChunk.Snippet,
                preparedChunk.SearchText,
                embedding,
                indexedAtUtc));
        }

        return new ChunkFileResult(
            chunks,
            chunkingStopwatch.ElapsedMilliseconds,
            embeddingResult.EmbeddingMilliseconds,
            embeddingResult.EmbeddingCallCount,
            embeddingResult.EmbeddedChunkCount);
    }

    private List<ParsedChunk> BuildParsedChunks(string documentPath, string sourceText, string[] lines)
    {
        if (!_options.ParserPipelineEnabled)
        {
            return BuildFixedWindowChunks(lines);
        }

        var strategyOrder = NormalizeParserStrategyOrder(_options.ParserStrategyOrder);
        foreach (var strategy in strategyOrder)
        {
            switch (strategy)
            {
                case "roslyn":
                    if (TryBuildRoslynChunks(documentPath, sourceText, lines, out var roslynChunks))
                    {
                        return roslynChunks;
                    }

                    break;
                case "treesitter":
                    if (TryBuildTreeSitterChunks(documentPath, sourceText, lines, out var treeSitterChunks))
                    {
                        return treeSitterChunks;
                    }

                    break;
                case "heuristic":
                    if (TryBuildHeuristicChunks(lines, out var heuristicChunks))
                    {
                        return heuristicChunks;
                    }

                    break;
                case "fixedwindow":
                    return BuildFixedWindowChunks(lines);
            }
        }

        return BuildFixedWindowChunks(lines);
    }

    private static List<string> NormalizeParserStrategyOrder(IReadOnlyList<string>? configuredOrder)
    {
        var order = new List<string>();
        foreach (var value in configuredOrder ?? [])
        {
            var normalized = value?.Trim().ToLowerInvariant();
            if (normalized is not ("roslyn" or "treesitter" or "heuristic" or "fixedwindow"))
            {
                continue;
            }

            if (!order.Contains(normalized, StringComparer.Ordinal))
            {
                order.Add(normalized);
            }
        }

        if (order.Count == 0)
        {
            order.AddRange(["roslyn", "treesitter", "heuristic", "fixedwindow"]);
        }

        if (!order.Contains("fixedwindow", StringComparer.Ordinal))
        {
            order.Add("fixedwindow");
        }

        return order;
    }

    private bool TryBuildRoslynChunks(string documentPath, string sourceText, string[] lines, out List<ParsedChunk> chunks)
    {
        chunks = [];
        if (!_options.RoslynChunkingEnabled || !documentPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            var syntaxTree = CSharpSyntaxTree.ParseText(sourceText);
            if (syntaxTree.GetRoot() is not CompilationUnitSyntax root)
            {
                return false;
            }

            foreach (var member in root.Members)
            {
                CollectRoslynMemberChunks(member, syntaxTree, lines, chunks);
            }

            chunks = chunks
                .OrderBy(chunk => chunk.StartLine)
                .ThenBy(chunk => chunk.EndLine)
                .ToList();

            return chunks.Count > 0;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Roslyn chunking failed for {DocumentPath}; parser pipeline will continue.", documentPath);
            chunks = [];
            return false;
        }
    }

    private void CollectRoslynMemberChunks(MemberDeclarationSyntax member, SyntaxTree syntaxTree, string[] lines, List<ParsedChunk> chunks)
    {
        switch (member)
        {
            case BaseNamespaceDeclarationSyntax namespaceDeclaration:
                foreach (var namespacedMember in namespaceDeclaration.Members)
                {
                    CollectRoslynMemberChunks(namespacedMember, syntaxTree, lines, chunks);
                }

                return;

            case TypeDeclarationSyntax typeDeclaration:
                if (typeDeclaration.Members.Count == 0)
                {
                    TryAddChunkFromSpan(typeDeclaration.Span, syntaxTree, lines, chunks);
                    return;
                }

                foreach (var typeMember in typeDeclaration.Members)
                {
                    CollectRoslynTypeMemberChunks(typeMember, syntaxTree, lines, chunks);
                }

                return;

            case EnumDeclarationSyntax enumDeclaration:
                TryAddChunkFromSpan(enumDeclaration.Span, syntaxTree, lines, chunks);
                return;

            default:
                TryAddChunkFromSpan(member.Span, syntaxTree, lines, chunks);
                return;
        }
    }

    private void CollectRoslynTypeMemberChunks(MemberDeclarationSyntax member, SyntaxTree syntaxTree, string[] lines, List<ParsedChunk> chunks)
    {
        if (member is TypeDeclarationSyntax nestedType)
        {
            if (nestedType.Members.Count == 0)
            {
                TryAddChunkFromSpan(nestedType.Span, syntaxTree, lines, chunks);
                return;
            }

            foreach (var nestedMember in nestedType.Members)
            {
                CollectRoslynTypeMemberChunks(nestedMember, syntaxTree, lines, chunks);
            }

            return;
        }

        TryAddChunkFromSpan(member.Span, syntaxTree, lines, chunks);
    }

    private void TryAddChunkFromSpan(TextSpan span, SyntaxTree syntaxTree, string[] lines, List<ParsedChunk> chunks)
    {
        var lineSpan = syntaxTree.GetLineSpan(span);
        var startLine = lineSpan.StartLinePosition.Line + 1;
        var endLine = lineSpan.EndLinePosition.Line + 1;
        if (!TryGetChunkText(lines, startLine, endLine, out var chunkText))
        {
            return;
        }

        chunks.Add(new ParsedChunk(startLine, endLine, chunkText));
    }

    private bool TryBuildTreeSitterChunks(string documentPath, string sourceText, string[] lines, out List<ParsedChunk> chunks)
    {
        chunks = [];
        if (!_options.TreeSitterChunkingEnabled)
        {
            return false;
        }

        _logger.LogDebug(
            "Tree-sitter chunking is configured in parser order for {DocumentPath}, but implementation is not available yet. Falling back to next parser strategy.",
            documentPath);
        return false;
    }

    private bool TryBuildHeuristicChunks(string[] lines, out List<ParsedChunk> chunks)
    {
        chunks = [];
        if (!_options.HeuristicChunkingEnabled)
        {
            return false;
        }

        var maxChunkLines = Math.Max(6, _options.ChunkLineCount * 2);
        var currentStart = -1;

        for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            var line = lines[lineIndex];
            var isBlank = string.IsNullOrWhiteSpace(line);

            if (currentStart < 0)
            {
                if (!isBlank)
                {
                    currentStart = lineIndex;
                }

                continue;
            }

            var reachedLimit = (lineIndex - currentStart + 1) >= maxChunkLines;
            if (!isBlank && !reachedLimit)
            {
                continue;
            }

            var endIndex = isBlank ? lineIndex - 1 : lineIndex;
            var startLine = currentStart + 1;
            var endLine = endIndex + 1;
            if (TryGetChunkText(lines, startLine, endLine, out var chunkText))
            {
                chunks.Add(new ParsedChunk(startLine, endLine, chunkText));
            }

            currentStart = isBlank ? -1 : lineIndex + 1;
        }

        if (currentStart >= 0)
        {
            var startLine = currentStart + 1;
            var endLine = lines.Length;
            if (TryGetChunkText(lines, startLine, endLine, out var trailingChunk))
            {
                chunks.Add(new ParsedChunk(startLine, endLine, trailingChunk));
            }
        }

        return chunks.Count > 0;
    }

    private List<ParsedChunk> BuildFixedWindowChunks(string[] lines)
    {
        var chunkLineCount = Math.Max(5, _options.ChunkLineCount);
        var overlapLineCount = Math.Clamp(_options.ChunkOverlapLineCount, 0, Math.Max(0, chunkLineCount - 1));
        var step = Math.Max(1, chunkLineCount - overlapLineCount);
        var chunks = new List<ParsedChunk>();

        for (var startLineIndex = 0; startLineIndex < lines.Length; startLineIndex += step)
        {
            var startLine = startLineIndex + 1;
            var endLine = Math.Min(lines.Length, startLineIndex + chunkLineCount);
            if (!TryGetChunkText(lines, startLine, endLine, out var chunkText))
            {
                continue;
            }

            chunks.Add(new ParsedChunk(startLine, endLine, chunkText));
        }

        return chunks;
    }

    private bool TryGetChunkText(string[] lines, int startLine, int endLine, out string chunkText)
    {
        chunkText = string.Empty;
        if (lines.Length == 0 || startLine < 1 || endLine < startLine)
        {
            return false;
        }

        var clampedStart = Math.Min(lines.Length, Math.Max(1, startLine));
        var clampedEnd = Math.Min(lines.Length, Math.Max(clampedStart, endLine));
        var chunkLines = lines.Skip(clampedStart - 1).Take(clampedEnd - clampedStart + 1);
        var text = string.Join('\n', chunkLines).Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        if (text.Length > _options.MaxChunkCharacters)
        {
            text = text[..Math.Max(1, _options.MaxChunkCharacters)].TrimEnd();
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        chunkText = text;
        return true;
    }

    private EmbeddingBatchResult EmbedPreparedChunks(IReadOnlyList<PreparedChunk> preparedChunks)
    {
        var embeddings = new List<float[]>(preparedChunks.Count);
        if (preparedChunks.Count == 0)
        {
            return new EmbeddingBatchResult(embeddings, 0, 0, 0);
        }

        var embeddingBatchSize = Math.Max(1, _options.EmbeddingBatchSize);
        long embeddingMilliseconds = 0;
        var embeddingCallCount = 0;
        var embeddedChunkCount = 0;

        if (_embeddingProvider is IBatchTextEmbeddingProvider batchProvider && embeddingBatchSize > 1)
        {
            for (var offset = 0; offset < preparedChunks.Count; offset += embeddingBatchSize)
            {
                var batch = preparedChunks.Skip(offset).Take(Math.Min(embeddingBatchSize, preparedChunks.Count - offset)).ToArray();
                var batchTexts = batch.Select(chunk => chunk.EmbeddingText).ToArray();
                var batchStopwatch = Stopwatch.StartNew();
                var usedBatch = batchProvider.TryEmbedBatch(batchTexts, EmbeddingInputKind.Document, out var batchEmbeddings, out var batchReason) &&
                    batchEmbeddings.Count == batchTexts.Length;
                batchStopwatch.Stop();
                embeddingMilliseconds += batchStopwatch.ElapsedMilliseconds;
                embeddingCallCount++;

                if (usedBatch)
                {
                    foreach (var batchEmbedding in batchEmbeddings)
                    {
                        embeddings.Add(batchEmbedding);
                        if (batchEmbedding.Length > 0)
                        {
                            embeddedChunkCount++;
                        }
                    }

                    continue;
                }

                _logger.LogWarning(
                    "Batch code-search embedding failed for {ChunkCount} chunk(s); falling back to scalar document embeddings. Reason: {Reason}",
                    batchTexts.Length,
                    string.IsNullOrWhiteSpace(batchReason) ? "embedding count mismatch" : batchReason);

                foreach (var batchText in batchTexts)
                {
                    var scalarStopwatch = Stopwatch.StartNew();
                    var scalarSuccess = _embeddingProvider.TryEmbed(batchText, EmbeddingInputKind.Document, out var embedding, out var scalarReason);
                    scalarStopwatch.Stop();
                    embeddingMilliseconds += scalarStopwatch.ElapsedMilliseconds;
                    embeddingCallCount++;
                    if (!scalarSuccess || embedding.Length == 0)
                    {
                        throw new InvalidOperationException(string.IsNullOrWhiteSpace(scalarReason)
                            ? "Code-search document embedding fallback returned no vector."
                            : $"Code-search document embedding fallback failed: {scalarReason}");
                    }

                    embeddings.Add(embedding);
                    embeddedChunkCount++;
                }
            }

            return new EmbeddingBatchResult(embeddings, embeddingMilliseconds, embeddingCallCount, embeddedChunkCount);
        }

        foreach (var preparedChunk in preparedChunks)
        {
            var scalarStopwatch = Stopwatch.StartNew();
            var scalarSuccess = _embeddingProvider.TryEmbed(preparedChunk.EmbeddingText, EmbeddingInputKind.Document, out var embedding, out var scalarReason);
            scalarStopwatch.Stop();
            embeddingMilliseconds += scalarStopwatch.ElapsedMilliseconds;
            embeddingCallCount++;
            if (!scalarSuccess || embedding.Length == 0)
            {
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(scalarReason)
                    ? $"Code-search document embedding failed for '{preparedChunk.DocumentPath}'."
                    : $"Code-search document embedding failed for '{preparedChunk.DocumentPath}': {scalarReason}");
            }

            embeddings.Add(embedding);
            embeddedChunkCount++;
        }

        return new EmbeddingBatchResult(embeddings, embeddingMilliseconds, embeddingCallCount, embeddedChunkCount);
    }

    private async Task<Dictionary<string, ExistingDocumentState>> LoadExistingDocumentsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT
    DocumentPath,
    MIN(SourceHash) AS SourceHash,
    MIN(ConfigurationHash) AS ConfigurationHash,
    MAX(SourceLengthBytes) AS SourceLengthBytes,
    MIN(SourceLastWriteUtc) AS SourceLastWriteUtc,
    MIN(CASE WHEN EmbeddingJson IS NULL OR length(trim(EmbeddingJson)) = 0 THEN 0 ELSE 1 END) AS HasEmbeddings
FROM CodeSearchChunks
GROUP BY DocumentPath;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new Dictionary<string, ExistingDocumentState>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows[reader.GetString(0)] = new ExistingDocumentState(
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetInt64(3),
                reader.IsDBNull(4)
                    ? null
                    : DateTime.Parse(reader.GetString(4), null, System.Globalization.DateTimeStyles.RoundtripKind),
                !reader.IsDBNull(5) && reader.GetInt32(5) == 1);
        }

        return rows;
    }

    private bool CanWarmReuseByMetadata(ExistingDocumentState existingDocument, string configurationHash, bool embeddingsAvailable, FileInfo fileInfo)
    {
        if (!_options.WarmMetadataReuseEnabled ||
            existingDocument.SourceLengthBytes is not long sourceLengthBytes ||
            existingDocument.SourceLastWriteUtc is not DateTime sourceLastWriteUtc)
        {
            return false;
        }

        if (sourceLengthBytes != fileInfo.Length ||
            sourceLastWriteUtc.Ticks != fileInfo.LastWriteTimeUtc.Ticks ||
            !string.Equals(existingDocument.ConfigurationHash, configurationHash, StringComparison.Ordinal))
        {
            return false;
        }

        return !embeddingsAvailable || existingDocument.HasEmbeddings;
    }

    private static bool CanReuseDocument(ExistingDocumentState? existingDocument, string sourceHash, string configurationHash, bool embeddingsAvailable)
    {
        if (existingDocument is null)
        {
            return false;
        }

        if (!string.Equals(existingDocument.SourceHash, sourceHash, StringComparison.Ordinal) ||
            !string.Equals(existingDocument.ConfigurationHash, configurationHash, StringComparison.Ordinal))
        {
            return false;
        }

        return !embeddingsAvailable || existingDocument.HasEmbeddings;
    }

    private async Task<int> ReplaceDocumentsBatchAsync(SqliteConnection connection, IReadOnlyList<PendingDocumentUpdate> documents, CancellationToken cancellationToken)
    {
        if (documents.Count == 0)
        {
            return 0;
        }

        var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using (transaction)
        {
            await using var delete = connection.CreateCommand();
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM CodeSearchChunks WHERE DocumentPath = @documentPath;";
            var deleteDocumentPath = delete.Parameters.Add("@documentPath", SqliteType.Text);

            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = @"
INSERT INTO CodeSearchChunks (
    TargetKey,
    DocumentPath,
    AbsolutePath,
    ChunkId,
    SourceHash,
    SourceLengthBytes,
    SourceLastWriteUtc,
    ConfigurationHash,
    StartLine,
    EndLine,
    Snippet,
    SearchText,
    EmbeddingJson,
    IndexedAtUtc)
VALUES (
    @targetKey,
    @documentPath,
    @absolutePath,
    @chunkId,
    @sourceHash,
    @sourceLengthBytes,
    @sourceLastWriteUtc,
    @configurationHash,
    @startLine,
    @endLine,
    @snippet,
    @searchText,
    @embeddingJson,
    @indexedAtUtc);";

            var targetKey = insert.Parameters.Add("@targetKey", SqliteType.Text);
            var documentPath = insert.Parameters.Add("@documentPath", SqliteType.Text);
            var absolutePath = insert.Parameters.Add("@absolutePath", SqliteType.Text);
            var chunkId = insert.Parameters.Add("@chunkId", SqliteType.Integer);
            var sourceHash = insert.Parameters.Add("@sourceHash", SqliteType.Text);
            var sourceLengthBytes = insert.Parameters.Add("@sourceLengthBytes", SqliteType.Integer);
            var sourceLastWriteUtc = insert.Parameters.Add("@sourceLastWriteUtc", SqliteType.Text);
            var configurationHash = insert.Parameters.Add("@configurationHash", SqliteType.Text);
            var startLine = insert.Parameters.Add("@startLine", SqliteType.Integer);
            var endLine = insert.Parameters.Add("@endLine", SqliteType.Integer);
            var snippet = insert.Parameters.Add("@snippet", SqliteType.Text);
            var searchText = insert.Parameters.Add("@searchText", SqliteType.Text);
            var embeddingJson = insert.Parameters.Add("@embeddingJson", SqliteType.Text);
            var indexedAtUtc = insert.Parameters.Add("@indexedAtUtc", SqliteType.Text);

            foreach (var document in documents)
            {
                deleteDocumentPath.Value = document.DocumentPath;
                await delete.ExecuteNonQueryAsync(cancellationToken);

                foreach (var chunk in document.Chunks)
                {
                    targetKey.Value = chunk.Target;
                    documentPath.Value = chunk.DocumentPath;
                    absolutePath.Value = chunk.AbsolutePath;
                    chunkId.Value = chunk.ChunkId;
                    sourceHash.Value = chunk.SourceHash;
                    sourceLengthBytes.Value = chunk.SourceLengthBytes;
                    sourceLastWriteUtc.Value = chunk.SourceLastWriteUtc.ToString("O");
                    configurationHash.Value = chunk.ConfigurationHash;
                    startLine.Value = chunk.StartLine;
                    endLine.Value = chunk.EndLine;
                    snippet.Value = chunk.Snippet;
                    searchText.Value = chunk.SearchText;
                    embeddingJson.Value = chunk.Embedding.Length == 0 ? DBNull.Value : JsonSerializer.Serialize(chunk.Embedding);
                    indexedAtUtc.Value = chunk.IndexedAtUtc.ToString("O");
                    await insert.ExecuteNonQueryAsync(cancellationToken);
                }
            }

            await transaction.CommitAsync(cancellationToken);
        }

        return documents.Count;
    }

    private async Task<int> DeleteRemovedDocumentsAsync(SqliteConnection connection, IReadOnlySet<string> liveDocuments, CancellationToken cancellationToken)
    {
        await using var select = connection.CreateCommand();
        select.CommandText = "SELECT DISTINCT DocumentPath FROM CodeSearchChunks;";
        await using var reader = await select.ExecuteReaderAsync(cancellationToken);
        var stored = new List<string>();
        while (await reader.ReadAsync(cancellationToken))
        {
            stored.Add(reader.GetString(0));
        }

        var staleDocuments = stored
            .Where(document => !liveDocuments.Contains(document))
            .ToList();
        if (staleDocuments.Count == 0)
        {
            return 0;
        }

        var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using (transaction)
        {
            await using var delete = connection.CreateCommand();
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM CodeSearchChunks WHERE DocumentPath = @documentPath;";
            var documentPath = delete.Parameters.Add("@documentPath", SqliteType.Text);
            foreach (var stale in staleDocuments)
            {
                documentPath.Value = stale;
                await delete.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }

        return staleDocuments.Count;
    }

    private async Task<List<IndexedChunk>> LoadChunksAsync(SqliteConnection connection, IReadOnlySet<string> targets, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        if (targets.Count == 0)
        {
            command.CommandText = "SELECT TargetKey, DocumentPath, AbsolutePath, StartLine, EndLine, Snippet, SearchText, EmbeddingJson, IndexedAtUtc FROM CodeSearchChunks;";
        }
        else
        {
            var parameterNames = new List<string>();
            var index = 0;
            foreach (var target in targets)
            {
                var parameterName = $"@target{index++}";
                parameterNames.Add(parameterName);
                command.Parameters.AddWithValue(parameterName, target);
            }

            command.CommandText = $"SELECT TargetKey, DocumentPath, AbsolutePath, StartLine, EndLine, Snippet, SearchText, EmbeddingJson, IndexedAtUtc FROM CodeSearchChunks WHERE TargetKey IN ({string.Join(", ", parameterNames)});";
        }

        return await ReadChunksAsync(command, cancellationToken);
    }

    private static async Task<List<IndexedChunk>> ReadChunksAsync(SqliteCommand command, CancellationToken cancellationToken)
    {
        var chunks = new List<IndexedChunk>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var embeddingJson = reader.IsDBNull(7) ? string.Empty : reader.GetString(7);
            chunks.Add(new IndexedChunk(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt32(3),
                reader.GetInt32(4),
                reader.GetString(5),
                reader.GetString(6),
                string.IsNullOrWhiteSpace(embeddingJson) ? [] : JsonSerializer.Deserialize<float[]>(embeddingJson) ?? [],
                DateTime.Parse(reader.GetString(8), null, System.Globalization.DateTimeStyles.RoundtripKind)));
        }

        return chunks;
    }

    public async Task<CodeSearchShardMergeResult> MergeShardAsync(string shardDatabasePath, bool preferNewer, CancellationToken cancellationToken)
    {
        // Service-layer path guard: validate before any filesystem I/O.
        // The tool layer (ChatToolCatalog.MergeShardAllowedExtensions + IsPathWithinAnyRoot)
        // enforces root-allowlist checks for the MCP/chat code path, but direct callers of
        // this service method have no such protection. Audit finding: CS-01/CS-02 (Audit #7).
        if (string.IsNullOrWhiteSpace(shardDatabasePath))
        {
            throw new ArgumentException("Shard database path must not be empty.", nameof(shardDatabasePath));
        }

        var normalizedShardPath = Path.GetFullPath(shardDatabasePath);
        var shardExtension = Path.GetExtension(normalizedShardPath);
        if (!string.Equals(shardExtension, ".db", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(shardExtension, ".sqlite", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(shardExtension, ".sqlite3", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"Shard database path must have a .db, .sqlite, or .sqlite3 extension (got '{shardExtension}').",
                nameof(shardDatabasePath));
        }

        if (!File.Exists(shardDatabasePath))
        {
            throw new FileNotFoundException($"Shard database not found: {shardDatabasePath}", shardDatabasePath);
        }

        var stopwatch = Stopwatch.StartNew();
        var shardChunks = await LoadShardChunksAsync(shardDatabasePath, cancellationToken);
        var totalShardChunks = shardChunks.Count;

        if (totalShardChunks == 0)
        {
            stopwatch.Stop();
            return new CodeSearchShardMergeResult(shardDatabasePath, 0, 0, 0, 0, stopwatch.ElapsedMilliseconds);
        }

        await _indexLock.WaitAsync(cancellationToken);
        try
        {
            await EnsureDatabaseAsync(cancellationToken);
            await using var connection = await OpenConnectionAsync(cancellationToken);

            var existingTimestamps = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
            await using (var selectCmd = connection.CreateCommand())
            {
                selectCmd.CommandText = "SELECT DocumentPath || '|' || ChunkId, IndexedAtUtc FROM CodeSearchChunks;";
                await using var tsReader = await selectCmd.ExecuteReaderAsync(cancellationToken);
                while (await tsReader.ReadAsync(cancellationToken))
                {
                    existingTimestamps[tsReader.GetString(0)] = DateTime.Parse(
                        tsReader.GetString(1), null, System.Globalization.DateTimeStyles.RoundtripKind);
                }
            }

            var toInsert = new List<CodeChunkRow>();
            var toUpdate = new List<CodeChunkRow>();
            var skippedCount = 0;
            foreach (var chunk in shardChunks)
            {
                var key = $"{chunk.DocumentPath}|{chunk.ChunkId}";
                if (!existingTimestamps.TryGetValue(key, out var existingTimestamp))
                {
                    toInsert.Add(chunk);
                }
                else if (preferNewer && chunk.IndexedAtUtc > existingTimestamp)
                {
                    toUpdate.Add(chunk);
                }
                else
                {
                    skippedCount++;
                }
            }

            const int mergeBatchSize = 100;
            var insertedCount = 0;
            for (var offset = 0; offset < toInsert.Count; offset += mergeBatchSize)
            {
                var batch = toInsert.GetRange(offset, Math.Min(mergeBatchSize, toInsert.Count - offset));
                await InsertMergedChunksAsync(connection, batch, cancellationToken);
                insertedCount += batch.Count;
            }

            var updatedCount = 0;
            for (var offset = 0; offset < toUpdate.Count; offset += mergeBatchSize)
            {
                var batch = toUpdate.GetRange(offset, Math.Min(mergeBatchSize, toUpdate.Count - offset));
                await UpdateMergedChunksAsync(connection, batch, cancellationToken);
                updatedCount += batch.Count;
            }

            if (insertedCount > 0 || updatedCount > 0)
            {
                InvalidateQueryCaches();
            }

            stopwatch.Stop();
            _logger.LogInformation(
                "Merged shard {ShardPath}: inserted {InsertedCount}, updated {UpdatedCount}, skipped {SkippedCount} of {TotalCount} chunk(s) in {ElapsedMs} ms.",
                shardDatabasePath, insertedCount, updatedCount, skippedCount, totalShardChunks, stopwatch.ElapsedMilliseconds);

            return new CodeSearchShardMergeResult(shardDatabasePath, insertedCount, updatedCount, skippedCount, totalShardChunks, stopwatch.ElapsedMilliseconds);
        }
        finally
        {
            _indexLock.Release();
        }
    }

    private static async Task<List<CodeChunkRow>> LoadShardChunksAsync(string shardDatabasePath, CancellationToken cancellationToken)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = shardDatabasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString();

        await using var shardConnection = new SqliteConnection(connectionString);
        await shardConnection.OpenAsync(cancellationToken);

        await using var command = shardConnection.CreateCommand();
        command.CommandText = @"
SELECT TargetKey, DocumentPath, AbsolutePath, ChunkId, SourceHash,
       COALESCE(SourceLengthBytes, 0) AS SourceLengthBytes,
       COALESCE(SourceLastWriteUtc, '0001-01-01T00:00:00.0000000Z') AS SourceLastWriteUtc,
       ConfigurationHash, StartLine, EndLine, Snippet, SearchText, EmbeddingJson, IndexedAtUtc
FROM CodeSearchChunks;";

        var chunks = new List<CodeChunkRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var embeddingJson = reader.IsDBNull(12) ? string.Empty : reader.GetString(12);
            chunks.Add(new CodeChunkRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt32(3),
                reader.GetString(4),
                reader.GetInt64(5),
                DateTime.Parse(reader.GetString(6), null, System.Globalization.DateTimeStyles.RoundtripKind),
                reader.GetString(7),
                reader.GetInt32(8),
                reader.GetInt32(9),
                reader.GetString(10),
                reader.GetString(11),
                string.IsNullOrWhiteSpace(embeddingJson) ? [] : JsonSerializer.Deserialize<float[]>(embeddingJson) ?? [],
                DateTime.Parse(reader.GetString(13), null, System.Globalization.DateTimeStyles.RoundtripKind)));
        }

        return chunks;
    }

    private static async Task InsertMergedChunksAsync(SqliteConnection connection, IReadOnlyList<CodeChunkRow> chunks, CancellationToken cancellationToken)
    {
        if (chunks.Count == 0) return;

        var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using (transaction)
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = @"
INSERT OR IGNORE INTO CodeSearchChunks (
    TargetKey, DocumentPath, AbsolutePath, ChunkId, SourceHash,
    SourceLengthBytes, SourceLastWriteUtc, ConfigurationHash,
    StartLine, EndLine, Snippet, SearchText, EmbeddingJson, IndexedAtUtc)
VALUES (
    @targetKey, @documentPath, @absolutePath, @chunkId, @sourceHash,
    @sourceLengthBytes, @sourceLastWriteUtc, @configurationHash,
    @startLine, @endLine, @snippet, @searchText, @embeddingJson, @indexedAtUtc);";

            var targetKey = insert.Parameters.Add("@targetKey", SqliteType.Text);
            var documentPath = insert.Parameters.Add("@documentPath", SqliteType.Text);
            var absolutePath = insert.Parameters.Add("@absolutePath", SqliteType.Text);
            var chunkId = insert.Parameters.Add("@chunkId", SqliteType.Integer);
            var sourceHash = insert.Parameters.Add("@sourceHash", SqliteType.Text);
            var sourceLengthBytes = insert.Parameters.Add("@sourceLengthBytes", SqliteType.Integer);
            var sourceLastWriteUtc = insert.Parameters.Add("@sourceLastWriteUtc", SqliteType.Text);
            var configurationHash = insert.Parameters.Add("@configurationHash", SqliteType.Text);
            var startLine = insert.Parameters.Add("@startLine", SqliteType.Integer);
            var endLine = insert.Parameters.Add("@endLine", SqliteType.Integer);
            var snippet = insert.Parameters.Add("@snippet", SqliteType.Text);
            var searchText = insert.Parameters.Add("@searchText", SqliteType.Text);
            var embeddingJson = insert.Parameters.Add("@embeddingJson", SqliteType.Text);
            var indexedAtUtc = insert.Parameters.Add("@indexedAtUtc", SqliteType.Text);

            foreach (var chunk in chunks)
            {
                targetKey.Value = chunk.Target;
                documentPath.Value = chunk.DocumentPath;
                absolutePath.Value = chunk.AbsolutePath;
                chunkId.Value = chunk.ChunkId;
                sourceHash.Value = chunk.SourceHash;
                sourceLengthBytes.Value = chunk.SourceLengthBytes;
                sourceLastWriteUtc.Value = chunk.SourceLastWriteUtc.ToString("O");
                configurationHash.Value = chunk.ConfigurationHash;
                startLine.Value = chunk.StartLine;
                endLine.Value = chunk.EndLine;
                snippet.Value = chunk.Snippet;
                searchText.Value = chunk.SearchText;
                embeddingJson.Value = chunk.Embedding.Length == 0 ? DBNull.Value : (object)JsonSerializer.Serialize(chunk.Embedding);
                indexedAtUtc.Value = chunk.IndexedAtUtc.ToString("O");
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
    }

    private static async Task UpdateMergedChunksAsync(SqliteConnection connection, IReadOnlyList<CodeChunkRow> chunks, CancellationToken cancellationToken)
    {
        if (chunks.Count == 0) return;

        var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using (transaction)
        {
            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = @"
UPDATE CodeSearchChunks SET
    TargetKey = @targetKey,
    AbsolutePath = @absolutePath,
    SourceHash = @sourceHash,
    SourceLengthBytes = @sourceLengthBytes,
    SourceLastWriteUtc = @sourceLastWriteUtc,
    ConfigurationHash = @configurationHash,
    StartLine = @startLine,
    EndLine = @endLine,
    Snippet = @snippet,
    SearchText = @searchText,
    EmbeddingJson = @embeddingJson,
    IndexedAtUtc = @indexedAtUtc
WHERE DocumentPath = @documentPath AND ChunkId = @chunkId;";

            var targetKey = update.Parameters.Add("@targetKey", SqliteType.Text);
            var documentPath = update.Parameters.Add("@documentPath", SqliteType.Text);
            var absolutePath = update.Parameters.Add("@absolutePath", SqliteType.Text);
            var chunkId = update.Parameters.Add("@chunkId", SqliteType.Integer);
            var sourceHash = update.Parameters.Add("@sourceHash", SqliteType.Text);
            var sourceLengthBytes = update.Parameters.Add("@sourceLengthBytes", SqliteType.Integer);
            var sourceLastWriteUtc = update.Parameters.Add("@sourceLastWriteUtc", SqliteType.Text);
            var configurationHash = update.Parameters.Add("@configurationHash", SqliteType.Text);
            var startLine = update.Parameters.Add("@startLine", SqliteType.Integer);
            var endLine = update.Parameters.Add("@endLine", SqliteType.Integer);
            var snippet = update.Parameters.Add("@snippet", SqliteType.Text);
            var searchText = update.Parameters.Add("@searchText", SqliteType.Text);
            var embeddingJson = update.Parameters.Add("@embeddingJson", SqliteType.Text);
            var indexedAtUtc = update.Parameters.Add("@indexedAtUtc", SqliteType.Text);

            foreach (var chunk in chunks)
            {
                targetKey.Value = chunk.Target;
                documentPath.Value = chunk.DocumentPath;
                absolutePath.Value = chunk.AbsolutePath;
                chunkId.Value = chunk.ChunkId;
                sourceHash.Value = chunk.SourceHash;
                sourceLengthBytes.Value = chunk.SourceLengthBytes;
                sourceLastWriteUtc.Value = chunk.SourceLastWriteUtc.ToString("O");
                configurationHash.Value = chunk.ConfigurationHash;
                startLine.Value = chunk.StartLine;
                endLine.Value = chunk.EndLine;
                snippet.Value = chunk.Snippet;
                searchText.Value = chunk.SearchText;
                embeddingJson.Value = chunk.Embedding.Length == 0 ? DBNull.Value : (object)JsonSerializer.Serialize(chunk.Embedding);
                indexedAtUtc.Value = chunk.IndexedAtUtc.ToString("O");
                await update.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
    }

    private async Task<(string? ResumeBuildId, HashSet<string> ProcessedPaths)> LoadResumableBuildAsync(
        SqliteConnection connection, string configurationHash, CancellationToken cancellationToken)
    {
        await using var findCmd = connection.CreateCommand();
        findCmd.CommandText = @"
SELECT BuildId FROM CodeSearchBuildLog
WHERE ConfigurationHash = @configHash AND State = 'in-progress'
ORDER BY StartedAtUtc DESC
LIMIT 1;";
        findCmd.Parameters.AddWithValue("@configHash", configurationHash);
        var resumeBuildId = (string?)await findCmd.ExecuteScalarAsync(cancellationToken);

        if (resumeBuildId is null)
        {
            return (null, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        }

        await using var docCmd = connection.CreateCommand();
        docCmd.CommandText = "SELECT DocumentPath FROM CodeSearchBuildLogDocument WHERE BuildId = @buildId;";
        docCmd.Parameters.AddWithValue("@buildId", resumeBuildId);
        var processedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await docCmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            processedPaths.Add(reader.GetString(0));
        }

        return (resumeBuildId, processedPaths);
    }

    private static async Task SaveBuildLogStartAsync(
        SqliteConnection connection,
        string buildId,
        string configurationHash,
        int totalFileCount,
        string? resumedFromBuildId,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow.ToString("O");
        await using var command = connection.CreateCommand();
        command.CommandText = @"
INSERT OR REPLACE INTO CodeSearchBuildLog
    (BuildId, ConfigurationHash, State, TotalFileCount, ProcessedFileCount, ResumedFromBuildId, StartedAtUtc, UpdatedAtUtc, CompletedAtUtc, LastError)
VALUES
    (@buildId, @configHash, 'in-progress', @totalFileCount, 0, @resumedFrom, @now, @now, NULL, NULL);";
        command.Parameters.AddWithValue("@buildId", buildId);
        command.Parameters.AddWithValue("@configHash", configurationHash);
        command.Parameters.AddWithValue("@totalFileCount", totalFileCount);
        command.Parameters.AddWithValue("@resumedFrom", (object?)resumedFromBuildId ?? DBNull.Value);
        command.Parameters.AddWithValue("@now", now);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task SaveBuildLogDocumentsAsync(
        SqliteConnection connection,
        string buildId,
        IEnumerable<string> documentPaths,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow.ToString("O");
        var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using (transaction)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
INSERT OR IGNORE INTO CodeSearchBuildLogDocument (BuildId, DocumentPath, ProcessedAtUtc)
VALUES (@buildId, @documentPath, @now);";
            var buildIdParam = command.Parameters.Add("@buildId", SqliteType.Text);
            var documentPathParam = command.Parameters.Add("@documentPath", SqliteType.Text);
            var nowParam = command.Parameters.Add("@now", SqliteType.Text);
            buildIdParam.Value = buildId;
            nowParam.Value = now;
            foreach (var path in documentPaths)
            {
                documentPathParam.Value = path;
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
    }

    private static async Task FinalizeBuildLogAsync(
        SqliteConnection connection,
        string buildId,
        string state,
        int processedFileCount,
        string? lastError,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow.ToString("O");
        await using var command = connection.CreateCommand();
        command.CommandText = @"
UPDATE CodeSearchBuildLog
SET State = @state, ProcessedFileCount = @processedFileCount, UpdatedAtUtc = @now, CompletedAtUtc = @now, LastError = @lastError
WHERE BuildId = @buildId;";
        command.Parameters.AddWithValue("@buildId", buildId);
        command.Parameters.AddWithValue("@state", state);
        command.Parameters.AddWithValue("@processedFileCount", processedFileCount);
        command.Parameters.AddWithValue("@now", now);
        command.Parameters.AddWithValue("@lastError", (object?)lastError ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task PruneBuildLogAsync(
        SqliteConnection connection,
        int maxEntries,
        CancellationToken cancellationToken)
    {
        maxEntries = Math.Max(1, maxEntries);
        await using var pruneDocsCmd = connection.CreateCommand();
        pruneDocsCmd.CommandText = @"
DELETE FROM CodeSearchBuildLogDocument WHERE BuildId IN (
    SELECT BuildId FROM CodeSearchBuildLog
    WHERE State != 'in-progress'
    ORDER BY StartedAtUtc DESC
    LIMIT -1 OFFSET @maxEntries);";
        pruneDocsCmd.Parameters.AddWithValue("@maxEntries", maxEntries);
        await pruneDocsCmd.ExecuteNonQueryAsync(cancellationToken);

        await using var pruneLogCmd = connection.CreateCommand();
        pruneLogCmd.CommandText = @"
DELETE FROM CodeSearchBuildLog
WHERE State != 'in-progress'
  AND BuildId NOT IN (
    SELECT BuildId FROM CodeSearchBuildLog
    WHERE State != 'in-progress'
    ORDER BY StartedAtUtc DESC
    LIMIT @maxEntries);";
        pruneLogCmd.Parameters.AddWithValue("@maxEntries", maxEntries);
        await pruneLogCmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task EnsureDatabaseAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_indexDatabasePath)!);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = @"
CREATE TABLE IF NOT EXISTS CodeSearchChunks (
    TargetKey TEXT NOT NULL,
    DocumentPath TEXT NOT NULL,
    AbsolutePath TEXT NOT NULL,
    ChunkId INTEGER NOT NULL,
    SourceHash TEXT NOT NULL,
    SourceLengthBytes INTEGER NULL,
    SourceLastWriteUtc TEXT NULL,
    ConfigurationHash TEXT NOT NULL,
    StartLine INTEGER NOT NULL,
    EndLine INTEGER NOT NULL,
    Snippet TEXT NOT NULL,
    SearchText TEXT NOT NULL,
    EmbeddingJson TEXT NULL,
    IndexedAtUtc TEXT NOT NULL,
    PRIMARY KEY (DocumentPath, ChunkId)
);
CREATE INDEX IF NOT EXISTS IX_CodeSearchChunks_TargetKey ON CodeSearchChunks(TargetKey);
CREATE INDEX IF NOT EXISTS IX_CodeSearchChunks_DocumentPath ON CodeSearchChunks(DocumentPath);
CREATE TABLE IF NOT EXISTS CodeSearchBuildLog (
    BuildId TEXT NOT NULL PRIMARY KEY,
    ConfigurationHash TEXT NOT NULL,
    State TEXT NOT NULL,
    TotalFileCount INTEGER NOT NULL DEFAULT 0,
    ProcessedFileCount INTEGER NOT NULL DEFAULT 0,
    ResumedFromBuildId TEXT NULL,
    StartedAtUtc TEXT NOT NULL,
    UpdatedAtUtc TEXT NOT NULL,
    CompletedAtUtc TEXT NULL,
    LastError TEXT NULL);
CREATE TABLE IF NOT EXISTS CodeSearchBuildLogDocument (
    BuildId TEXT NOT NULL,
    DocumentPath TEXT NOT NULL,
    ProcessedAtUtc TEXT NOT NULL,
    PRIMARY KEY (BuildId, DocumentPath));
CREATE INDEX IF NOT EXISTS IX_CodeSearchBuildLog_ConfigState ON CodeSearchBuildLog(ConfigurationHash, State);";
        await command.ExecuteNonQueryAsync(cancellationToken);
        await EnsureColumnAsync(connection, "SourceLengthBytes", "INTEGER NULL", cancellationToken);
        await EnsureColumnAsync(connection, "SourceLastWriteUtc", "TEXT NULL", cancellationToken);
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _indexDatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString());
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private string BuildConfigurationHash()
    {
        var modelPath = ResolveDataAwarePath(_semanticOptions.ModelPath);
        var vocabularyPath = ResolveDataAwarePath(_semanticOptions.VocabularyPath);
        var payload = string.Join('|',
            _repositoryRoot,
            _options.ParserPipelineEnabled,
            string.Join(';', _options.ParserStrategyOrder.Select(value => value.Trim().ToLowerInvariant())),
            _options.RoslynChunkingEnabled,
            _options.TreeSitterChunkingEnabled,
            _options.HeuristicChunkingEnabled,
            string.Join(';', _options.TargetDirectories.Select(value => value.Trim())),
            string.Join(';', _options.IncludedFileExtensions.Select(NormalizeExtension)),
            string.Join(';', _options.IncludePatterns.Select(value => value.Trim())),
            string.Join(';', _options.ExcludePatterns.Select(value => value.Trim())),
            _options.ChunkLineCount,
            _options.ChunkOverlapLineCount,
            _options.MaxFileBytes,
            _options.MaxChunkCharacters,
            _options.MaxResults,
            _semanticOptions.TokenizerKind,
            _semanticOptions.PoolingMode,
            _semanticOptions.QueryPrefix,
            _semanticOptions.DocumentPrefix,
            _semanticOptions.MaxInputTokens,
            _semanticOptions.EmbeddingsEnabled,
            File.Exists(modelPath) ? File.GetLastWriteTimeUtc(modelPath).Ticks.ToString() : "missing",
            File.Exists(vocabularyPath) ? File.GetLastWriteTimeUtc(vocabularyPath).Ticks.ToString() : "missing");
        return ComputeHash(payload);
    }

    private string ResolveDataAwarePath(string path)
    {
        var expanded = Environment.ExpandEnvironmentVariables(path);
        if (Path.IsPathFullyQualified(expanded))
        {
            return Path.GetFullPath(expanded);
        }

        return Path.GetFullPath(Path.Combine(_dataRoot, NormalizeDataRelativePath(expanded)));
    }

    private string GetDocumentPath(string filePath)
    {
        var relative = Path.GetRelativePath(_repositoryRoot, filePath);
        return relative.Replace('\\', '/');
    }

    private static string BuildEmbeddingText(string documentPath, string chunkText) =>
        $"Path: {documentPath}\n{chunkText}";

    private bool TryGetQueryEmbedding(string query, out float[] embedding)
    {
        if (_queryEmbeddingCache.TryGetValue(query, out float[]? cachedEmbedding) && cachedEmbedding is not null)
        {
            embedding = cachedEmbedding;
            return true;
        }

        if (!_embeddingProvider.TryEmbed(query, EmbeddingInputKind.Query, out embedding, out _))
        {
            return false;
        }

        _queryEmbeddingCache.Set(query, embedding, new MemoryCacheEntryOptions { Size = 1 });
        return true;
    }

    private void CacheResults(string cacheKey, IReadOnlyList<CodeSearchResult> results) =>
        _queryResultCache.Set(cacheKey, results, new MemoryCacheEntryOptions { Size = 1 });

    private string BuildResultCacheKey(string query, IReadOnlySet<string> targets, int limit)
    {
        var normalizedTargets = targets.Count == 0
            ? "*"
            : string.Join(';', targets.OrderBy(value => value, StringComparer.OrdinalIgnoreCase));
        return $"{Interlocked.Read(ref _resultCacheGeneration)}|{limit}|{query.Trim()}|{normalizedTargets}";
    }

    private static double GetTargetWeight(string target, string documentPath, IReadOnlySet<string> queryTokens)
    {
        if (IsArtifactFocusedQuery(queryTokens))
        {
            return 1;
        }

        var weight = 1d;
        if (IsDocsArtifact(documentPath))
        {
            weight *= 0.6;
        }

        if (IsTestTarget(target, documentPath))
        {
            weight *= 0.78;
        }

        if (IsBenchmarkTarget(target, documentPath))
        {
            weight *= 0.9;
        }

        return weight;
    }

    private static bool IsArtifactFocusedQuery(IReadOnlySet<string> queryTokens) =>
        queryTokens.Overlaps(["test", "tests", "benchmark", "benchmarks", "doc", "docs", "documentation"]);

    private static bool IsDocsArtifact(string documentPath) =>
        documentPath.Contains("/Docs/", StringComparison.OrdinalIgnoreCase) ||
        documentPath.EndsWith(".md", StringComparison.OrdinalIgnoreCase);

    private static bool IsTestTarget(string target, string documentPath) =>
        target.Contains("Tests", StringComparison.OrdinalIgnoreCase) ||
        documentPath.Contains("/Tests/", StringComparison.OrdinalIgnoreCase) ||
        documentPath.EndsWith("Tests.cs", StringComparison.OrdinalIgnoreCase);

    private static bool IsBenchmarkTarget(string target, string documentPath) =>
        target.Contains("Benchmarks", StringComparison.OrdinalIgnoreCase) ||
        documentPath.Contains("/Benchmarks/", StringComparison.OrdinalIgnoreCase) ||
        documentPath.Contains("Benchmark", StringComparison.OrdinalIgnoreCase);

    private static string BuildVectorMatchReason(double rawScore, double lexicalScore, double targetWeight, double coverageWeight)
    {
        var lexicalEvidence = lexicalScore > 0
            ? $", lexical evidence {lexicalScore:0.###}"
            : ", no lexical evidence";
        var coverageEvidence = coverageWeight < 0.999 || coverageWeight > 1.001
            ? $", token coverage weight {coverageWeight:0.###}"
            : string.Empty;

        return targetWeight < 0.999
            ? $"Code embedding cosine similarity {rawScore:0.###}{lexicalEvidence}{coverageEvidence} (target weight {targetWeight:0.###}, hybrid rerank)."
            : $"Code embedding cosine similarity {rawScore:0.###}{lexicalEvidence}{coverageEvidence} (hybrid rerank).";
    }

    private static string BuildLexicalMatchReason(int matchedTokenCount, int totalTokenCount, double targetWeight, double coverageWeight) =>
        targetWeight < 0.999
            ? $"Lexical fallback matched {matchedTokenCount}/{Math.Max(1, totalTokenCount)} query token(s) in indexed code (coverage weight {coverageWeight:0.###}, target weight {targetWeight:0.###})."
            : $"Lexical fallback matched {matchedTokenCount}/{Math.Max(1, totalTokenCount)} query token(s) in indexed code (coverage weight {coverageWeight:0.###}).";

    private void LogQueryTiming(string queryText, IReadOnlySet<string> targets, int limit, string mode, int resultCount, int scannedChunkCount, long queryStartTimestamp)
    {
        var elapsedMs = Stopwatch.GetElapsedTime(queryStartTimestamp).TotalMilliseconds;
        var queryNumber = Interlocked.Increment(ref _queryTelemetryCounter);
        var logInterval = Math.Max(1, _options.QueryTimingLogInterval);
        var slowThresholdMs = Math.Max(1, _options.QueryTimingSlowThresholdMilliseconds);
        var isSlow = elapsedMs >= slowThresholdMs;
        if (!isSlow && queryNumber % logInterval != 0)
        {
            return;
        }

        var queryPreview = queryText.Length <= 120
            ? queryText
            : queryText[..120];

        if (isSlow)
        {
            _logger.LogWarning(
                "Code-search query timing slow path: {ElapsedMs:0.###} ms (mode={Mode}, results={ResultCount}, scannedChunks={ScannedChunkCount}, targets={TargetCount}, limit={Limit}, query=\"{QueryPreview}\").",
                elapsedMs,
                mode,
                resultCount,
                scannedChunkCount,
                targets.Count,
                limit,
                queryPreview);
            return;
        }

        _logger.LogDebug(
            "Code-search query timing sample #{QueryNumber}: {ElapsedMs:0.###} ms (mode={Mode}, results={ResultCount}, scannedChunks={ScannedChunkCount}, targets={TargetCount}, limit={Limit}, query=\"{QueryPreview}\").",
            queryNumber,
            elapsedMs,
            mode,
            resultCount,
            scannedChunkCount,
            targets.Count,
            limit,
            queryPreview);
    }

    private int CalculateVectorCandidateLimit(int limit)
    {
        var minimum = Math.Max(1, _options.VectorCandidateMinimum);
        var maximum = Math.Max(minimum, _options.VectorCandidateMaximum);
        var scaled = Math.Max(limit * Math.Max(1, _options.VectorCandidateMultiplier), minimum);
        return Math.Clamp(scaled, minimum, maximum);
    }

    private bool ShouldRunSparsePrefilterFallback(int candidateCount)
    {
        var threshold = Math.Max(0, _options.VectorPrefilterFullScanFallbackCandidateCount);
        return threshold > 0 && candidateCount > 0 && candidateCount <= threshold;
    }

    private async Task<VectorCandidateLoadResult> LoadVectorCandidatesAsync(SqliteConnection connection, IReadOnlySet<string> targets, IReadOnlySet<string> queryTokens, int limit, CancellationToken cancellationToken)
    {
        if (!_options.VectorCandidatePrefilterEnabled || queryTokens.Count == 0)
        {
            return new VectorCandidateLoadResult(await LoadChunksAsync(connection, targets, cancellationToken), false);
        }

        await using var command = connection.CreateCommand();
        var whereClauses = new List<string>();
        if (targets.Count > 0)
        {
            var targetParameterNames = new List<string>();
            var targetIndex = 0;
            foreach (var target in targets)
            {
                var parameterName = $"@target{targetIndex++}";
                targetParameterNames.Add(parameterName);
                command.Parameters.AddWithValue(parameterName, target);
            }

            whereClauses.Add($"TargetKey IN ({string.Join(", ", targetParameterNames)})");
        }

        var tokenMatchClauses = new List<string>();
        var tokenScoreClauses = new List<string>();
        var tokenOrdinal = 0;
        foreach (var token in queryTokens.OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
        {
            var parameterName = $"@token{tokenOrdinal++}";
            command.Parameters.AddWithValue(parameterName, token);
            var documentHit = $"instr(lower(DocumentPath), {parameterName}) > 0";
            var searchTextHit = $"instr(lower(SearchText), {parameterName}) > 0";
            tokenMatchClauses.Add($"({documentHit} OR {searchTextHit})");
            tokenScoreClauses.Add($"CASE WHEN {documentHit} THEN 2 ELSE 0 END");
            tokenScoreClauses.Add($"CASE WHEN {searchTextHit} THEN 1 ELSE 0 END");
        }

        if (tokenMatchClauses.Count == 0)
        {
            return new VectorCandidateLoadResult(await LoadChunksAsync(connection, targets, cancellationToken), false);
        }

        whereClauses.Add($"({string.Join(" OR ", tokenMatchClauses)})");
        command.Parameters.AddWithValue("@candidateLimit", CalculateVectorCandidateLimit(limit));
        command.CommandText = $@"
SELECT TargetKey, DocumentPath, AbsolutePath, StartLine, EndLine, Snippet, SearchText, EmbeddingJson, IndexedAtUtc
FROM CodeSearchChunks
WHERE {string.Join(" AND ", whereClauses)}
ORDER BY ({string.Join(" + ", tokenScoreClauses)}) DESC, DocumentPath COLLATE NOCASE, StartLine
LIMIT @candidateLimit;";

        return new VectorCandidateLoadResult(await ReadChunksAsync(command, cancellationToken), true);
    }

    private void InvalidateQueryCaches()
    {
        Interlocked.Increment(ref _resultCacheGeneration);
        _queryEmbeddingCache.Compact(1.0);
        _queryResultCache.Compact(1.0);
        Interlocked.Exchange(ref _nextStalenessCheckUtcTicks, 0);
    }

    private bool ShouldSkipStalenessCheck()
    {
        var cooldownSeconds = Math.Max(0, _options.IndexStalenessCheckCooldownSeconds);
        if (cooldownSeconds <= 0)
        {
            return false;
        }

        var nextCheckTicks = Interlocked.Read(ref _nextStalenessCheckUtcTicks);
        return nextCheckTicks > DateTime.UtcNow.Ticks;
    }

    private void StampStalenessCooldownWindow()
    {
        var cooldownSeconds = Math.Max(0, _options.IndexStalenessCheckCooldownSeconds);
        if (cooldownSeconds <= 0)
        {
            return;
        }

        var nextCheckTicks = DateTime.UtcNow.AddSeconds(cooldownSeconds).Ticks;
        Interlocked.Exchange(ref _nextStalenessCheckUtcTicks, nextCheckTicks);
    }

    private CodeSearchBuildProgress GetBuildProgress()
    {
        lock (_buildProgressGate)
        {
            return _buildProgress;
        }
    }

    private void SetBuildProgress(Func<CodeSearchBuildProgress, CodeSearchBuildProgress> updater)
    {
        lock (_buildProgressGate)
        {
            _buildProgress = updater(_buildProgress);
        }
    }

    private void BeginBuild(int totalFileCount, CodeSearchBuildTimings timings)
    {
        var now = DateTime.UtcNow;
        lock (_buildProgressGate)
        {
            _buildProgress = new CodeSearchBuildProgress(
                "indexing",
                true,
                totalFileCount,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                timings,
                null,
                null,
                now,
                now,
                null,
                null);
        }
    }

    private void CompleteBuild(int processedFileCount, int reusedFileCount, int updatedFileCount, int skippedFileCount, int removedFileCount, int failedFileCount, string? lastError, CodeSearchBuildTimings timings) =>
        FinalizeBuild("completed", false, processedFileCount, reusedFileCount, updatedFileCount, skippedFileCount, removedFileCount, failedFileCount, 0, lastError, timings);

    private void CancelBuild(int processedFileCount, int reusedFileCount, int updatedFileCount, int skippedFileCount, int removedFileCount, int failedFileCount, string? lastError, CodeSearchBuildTimings timings) =>
        FinalizeBuild("canceled", false, processedFileCount, reusedFileCount, updatedFileCount, skippedFileCount, removedFileCount, failedFileCount, 0, lastError, timings);

    private void FailBuild(int processedFileCount, int reusedFileCount, int updatedFileCount, int skippedFileCount, int removedFileCount, int failedFileCount, string? lastError, CodeSearchBuildTimings timings) =>
        FinalizeBuild("failed", false, processedFileCount, reusedFileCount, updatedFileCount, skippedFileCount, removedFileCount, failedFileCount, 0, lastError, timings);

    private void FinalizeBuild(string state, bool isRunning, int processedFileCount, int reusedFileCount, int updatedFileCount, int skippedFileCount, int removedFileCount, int failedFileCount, int pendingWriteCount, string? lastError, CodeSearchBuildTimings timings)
    {
        var now = DateTime.UtcNow;
        lock (_buildProgressGate)
        {
            _buildProgress = _buildProgress with
            {
                State = state,
                IsRunning = isRunning,
                ProcessedFileCount = processedFileCount,
                ReusedFileCount = reusedFileCount,
                UpdatedFileCount = updatedFileCount,
                SkippedFileCount = skippedFileCount,
                RemovedFileCount = removedFileCount,
                FailedFileCount = failedFileCount,
                PendingWriteCount = pendingWriteCount,
                Timings = timings,
                UpdatedAtUtc = now,
                CompletedAtUtc = now,
                LastError = lastError,
                CurrentTarget = null,
                CurrentDocumentPath = null
            };
        }
    }

    private void LogBuildProgress(int processedFileCount, int reusedFileCount, int updatedFileCount, int skippedFileCount, int failedFileCount, int pendingWriteCount)
    {
        var progress = GetBuildProgress();
        _logger.LogInformation(
            "Code search index progress {ProcessedFileCount}/{TotalFileCount} ({ProgressPercentage}%). Reused {ReusedFileCount}, updated {UpdatedFileCount}, pending writes {PendingWriteCount}, skipped {SkippedFileCount}, failed {FailedFileCount}. Current document: {CurrentDocumentPath}.",
            processedFileCount,
            progress.TotalFileCount,
            progress.ProgressPercentage,
            reusedFileCount,
            updatedFileCount,
            pendingWriteCount,
            skippedFileCount,
            failedFileCount,
            progress.CurrentDocumentPath ?? "(none)");
    }

    private static string BuildSnippet(string content, string query)
    {
        const int maxLength = 280;
        if (string.IsNullOrWhiteSpace(content))
        {
            return string.Empty;
        }

        if (content.Length <= maxLength)
        {
            return content;
        }

        var matchIndex = content.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (matchIndex < 0)
        {
            matchIndex = 0;
        }

        var start = Math.Max(0, matchIndex - 80);
        var length = Math.Min(maxLength, content.Length - start);
        var prefix = start > 0 ? "..." : string.Empty;
        var suffix = start + length < content.Length ? "..." : string.Empty;
        return prefix + content.Substring(start, length).Trim() + suffix;
    }

    private static double Dot(float[] left, float[] right)
    {
        var sum = 0.0;
        for (var index = 0; index < left.Length; index++)
        {
            sum += left[index] * right[index];
        }

        return sum;
    }

    private double ScoreLexical(IndexedChunk chunk, IReadOnlySet<string> queryTokens)
    {
        if (queryTokens.Count == 0)
        {
            return 0;
        }

        var haystack = chunk.DocumentPath + "\n" + chunk.SearchText;
        var score = 0.0;
        foreach (var token in queryTokens)
        {
            var occurrences = CountOccurrences(haystack, token);
            if (occurrences == 0)
            {
                continue;
            }

            score += 1.0;
            if (occurrences > 1)
            {
                score += Math.Min(_maxLexicalFrequencyBonusPerToken, Math.Log2(occurrences) * _lexicalFrequencyBonusScale);
            }

            if (chunk.DocumentPath.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                score += 1.5;
            }
        }

        return score;
    }

    private static int CountMatchedTokens(IndexedChunk chunk, IReadOnlySet<string> queryTokens)
    {
        if (queryTokens.Count == 0)
        {
            return 0;
        }

        var haystack = chunk.DocumentPath + "\n" + chunk.SearchText;
        var matched = 0;
        foreach (var token in queryTokens)
        {
            if (CountOccurrences(haystack, token) > 0)
            {
                matched++;
            }
        }

        return matched;
    }

    private static IReadOnlyList<ScoredChunk> TakeBalancedByDocument(IEnumerable<ScoredChunk> sortedEntries, int limit, int maxPerDocument)
    {
        var effectiveLimit = Math.Max(1, limit);
        var perDocumentCap = Math.Max(1, maxPerDocument);
        var selected = new List<ScoredChunk>(effectiveLimit);
        var countsByDocument = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in sortedEntries)
        {
            if (!countsByDocument.TryGetValue(entry.Chunk.DocumentPath, out var count))
            {
                count = 0;
            }

            if (count >= perDocumentCap)
            {
                continue;
            }

            selected.Add(entry);
            countsByDocument[entry.Chunk.DocumentPath] = count + 1;
            if (selected.Count >= effectiveLimit)
            {
                break;
            }
        }

        return selected;
    }

    private double ScoreHybrid(double rawVectorScore, double lexicalScore)
    {
        if (lexicalScore <= 0)
        {
            return rawVectorScore * _zeroLexicalEvidencePenalty;
        }

        var normalizedLexical = lexicalScore / (lexicalScore + _lexicalScoreSaturation);
        return (rawVectorScore * _hybridVectorWeight) + (normalizedLexical * _hybridLexicalWeight);
    }

    private double ScoreTokenCoverageWeight(int matchedTokenCount, int totalTokenCount)
    {
        if (totalTokenCount <= 0)
        {
            return 1.0;
        }

        var ratio = Math.Clamp((double)matchedTokenCount / totalTokenCount, 0.0, 1.0);
        return _minTokenCoverageWeight + ((_maxTokenCoverageWeight - _minTokenCoverageWeight) * ratio);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        if (string.IsNullOrWhiteSpace(needle))
        {
            return 0;
        }

        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    private static IReadOnlySet<string> Tokenize(string query)
    {
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in TokenRegex.Matches(query ?? string.Empty))
        {
            AddTokenVariants(tokens, match.Value);
        }

        return tokens;
    }

    private static IReadOnlySet<string> ExpandQueryTokens(IReadOnlySet<string> queryTokens)
    {
        var expanded = new HashSet<string>(queryTokens, StringComparer.OrdinalIgnoreCase);
        foreach (var token in queryTokens)
        {
            if (QuerySynonyms.TryGetValue(token, out var synonyms))
            {
                foreach (var synonym in synonyms)
                {
                    AddTokenVariants(expanded, synonym);
                }
            }

            if (token.EndsWith("driver", StringComparison.OrdinalIgnoreCase) && token.Length > "driver".Length + 1)
            {
                var prefix = token[..^"driver".Length];
                AddTokenVariants(expanded, prefix);
                AddTokenVariants(expanded, "driver");
                AddTokenVariants(expanded, "tool");
            }
        }

        return expanded;
    }

    private static void AddTokenVariants(HashSet<string> tokens, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        var normalized = value.ToLowerInvariant();
        if (normalized.Length > 1)
        {
            tokens.Add(normalized);
        }

        foreach (var part in IdentifierSplitRegex.Split(value))
        {
            var segment = part.Trim();
            if (segment.Length <= 1)
            {
                continue;
            }

            tokens.Add(segment.ToLowerInvariant());
        }
    }

    private static string NormalizeExtension(string extension)
    {
        var trimmed = (extension ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return string.Empty;
        }

        return trimmed.StartsWith('.') ? trimmed.ToLowerInvariant() : "." + trimmed.ToLowerInvariant();
    }

    private IReadOnlySet<string> NormalizeTargets(IReadOnlyList<string>? targets)
    {
        if (targets is null || targets.Count == 0)
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        return targets
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static string NormalizeTargetKey(string configuredValue, string fullPath)
    {
        var trimmed = configuredValue.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (Path.IsPathFullyQualified(trimmed))
        {
            trimmed = Path.GetFileName(trimmed);
        }

        return string.IsNullOrWhiteSpace(trimmed)
            ? Path.GetFileName(fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            : trimmed.Replace('\\', '/');
    }

    private static string ComputeHash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text ?? string.Empty)));

    private static string ResolveDataDeploymentRoot(string dataPath)
    {
        var expanded = Environment.ExpandEnvironmentVariables(dataPath);
        var fullPath = Path.GetFullPath(expanded).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(Path.GetFileName(fullPath), "Memories", StringComparison.OrdinalIgnoreCase)
            ? Directory.GetParent(fullPath)?.FullName ?? fullPath
            : fullPath;
    }

    private static string ResolveRepositoryRoot(string dataRoot, string repositoryRootPath)
    {
        var expanded = Environment.ExpandEnvironmentVariables(repositoryRootPath ?? string.Empty);
        if (Path.IsPathFullyQualified(expanded))
        {
            return Path.GetFullPath(expanded);
        }

        return Path.GetFullPath(Path.Combine(dataRoot, NormalizeDataRelativePath(expanded)));
    }

    private static string NormalizeDataRelativePath(string path)
    {
        var normalized = path.Trim().Replace('\\', '/');
        foreach (var prefix in new[] { "../Data/", "./Data/", "Data/" })
        {
            if (normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                normalized = normalized[prefix.Length..];
                break;
            }
        }

        while (normalized.StartsWith("./", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }

        return normalized.Replace('/', Path.DirectorySeparatorChar);
    }

    private sealed record ResolvedTarget(string Key, string FullPath);

    private sealed record ResolvedFileCandidate(string TargetKey, string DocumentPath, string AbsolutePath);

    private sealed record ExistingDocumentState(
        string SourceHash,
        string ConfigurationHash,
        long? SourceLengthBytes,
        DateTime? SourceLastWriteUtc,
        bool HasEmbeddings);

    private sealed record PendingDocumentUpdate(string DocumentPath, IReadOnlyList<CodeChunkRow> Chunks);

    private sealed record CodeChunkRow(
        string Target,
        string DocumentPath,
        string AbsolutePath,
        int ChunkId,
        string SourceHash,
        long SourceLengthBytes,
        DateTime SourceLastWriteUtc,
        string ConfigurationHash,
        int StartLine,
        int EndLine,
        string Snippet,
        string SearchText,
        float[] Embedding,
        DateTime IndexedAtUtc);

    private sealed record IndexedChunk(
        string Target,
        string DocumentPath,
        string AbsolutePath,
        int StartLine,
        int EndLine,
        string Snippet,
        string SearchText,
        float[] Embedding,
        DateTime IndexedAtUtc);

    private sealed record ScoredChunk(
        IndexedChunk Chunk,
        double RawScore,
        double LexicalScore,
        double TargetWeight,
        double CoverageWeight,
        double WeightedScore,
        int MatchedTokenCount);


    private static async Task EnsureColumnAsync(SqliteConnection connection, string columnName, string columnDefinition, CancellationToken cancellationToken)
    {
        if (await HasColumnAsync(connection, columnName, cancellationToken))
        {
            return;
        }

        await using var command = connection.CreateCommand();
        command.CommandText = $"ALTER TABLE CodeSearchChunks ADD COLUMN {columnName} {columnDefinition};";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<bool> HasColumnAsync(SqliteConnection connection, string columnName, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(CodeSearchChunks);";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}

internal sealed class CodeSearchIgnoreMatcher
{
    private readonly IReadOnlyList<CodeSearchIgnoreRule> _gitIgnoreRules;
    private readonly IReadOnlyList<CodeSearchIgnoreRule> _includeRules;
    private readonly IReadOnlyList<CodeSearchIgnoreRule> _excludeRules;

    private CodeSearchIgnoreMatcher(
        string repositoryRoot,
        IReadOnlyList<CodeSearchIgnoreRule> gitIgnoreRules,
        IReadOnlyList<CodeSearchIgnoreRule> includeRules,
        IReadOnlyList<CodeSearchIgnoreRule> excludeRules)
    {
        _gitIgnoreRules = gitIgnoreRules;
        _includeRules = includeRules;
        _excludeRules = excludeRules;
    }

    public static CodeSearchIgnoreMatcher Create(string repositoryRoot, IReadOnlyList<string> includePatterns, IReadOnlyList<string> excludePatterns)
    {
        var gitIgnorePath = Path.Combine(repositoryRoot, ".gitignore");
        var gitIgnoreRules = File.Exists(gitIgnorePath)
            ? File.ReadAllLines(gitIgnorePath)
                .Select(CodeSearchIgnoreRule.TryParse)
                .Where(rule => rule is not null)
                .Select(rule => rule!)
                .ToList()
            : [];
        var includeRules = includePatterns
            .Select(pattern => CodeSearchIgnoreRule.FromPattern(pattern, isNegated: true))
            .Where(rule => rule is not null)
            .Select(rule => rule!)
            .ToList();
        var excludeRules = excludePatterns
            .Select(pattern => CodeSearchIgnoreRule.FromPattern(pattern, isNegated: false))
            .Where(rule => rule is not null)
            .Select(rule => rule!)
            .ToList();
        return new CodeSearchIgnoreMatcher(repositoryRoot, gitIgnoreRules, includeRules, excludeRules);
    }

    public bool IsIgnored(string documentPath)
    {
        var normalized = documentPath.Replace('\\', '/');
        var ignored = false;
        foreach (var rule in _gitIgnoreRules)
        {
            if (!rule.IsMatch(normalized))
            {
                continue;
            }

            ignored = !rule.IsNegated;
        }

        foreach (var rule in _excludeRules)
        {
            if (rule.IsMatch(normalized))
            {
                ignored = true;
            }
        }

        foreach (var rule in _includeRules)
        {
            if (rule.IsMatch(normalized))
            {
                ignored = false;
            }
        }

        return ignored;
    }
}

internal sealed class CodeSearchIgnoreRule
{
    private CodeSearchIgnoreRule(bool isNegated, Regex regex)
    {
        IsNegated = isNegated;
        Regex = regex;
    }

    public bool IsNegated { get; }

    private Regex Regex { get; }

    public static CodeSearchIgnoreRule? TryParse(string rawLine)
    {
        if (string.IsNullOrWhiteSpace(rawLine))
        {
            return null;
        }

        var trimmed = rawLine.Trim();
        if (trimmed.StartsWith('#'))
        {
            return null;
        }

        var isNegated = trimmed.StartsWith('!');
        var pattern = isNegated ? trimmed[1..] : trimmed;
        return FromPattern(pattern, isNegated);
    }

    public static CodeSearchIgnoreRule? FromPattern(string rawPattern, bool isNegated)
    {
        if (string.IsNullOrWhiteSpace(rawPattern))
        {
            return null;
        }

        var pattern = rawPattern.Trim().Replace('\\', '/');
        var directoryOnly = pattern.EndsWith('/');
        var anchored = pattern.StartsWith('/');
        if (directoryOnly)
        {
            pattern = pattern[..^1];
        }

        if (anchored)
        {
            pattern = pattern[1..];
        }

        if (string.IsNullOrWhiteSpace(pattern))
        {
            return null;
        }

        var hasSlash = pattern.Contains('/');
        var regexPattern = ConvertGlobToRegex(pattern);
        var prefix = anchored || hasSlash
            ? "^"
            : "(^|.*/)";
        var suffix = directoryOnly
            ? "(/.*)?$"
            : "$";
        return new CodeSearchIgnoreRule(
            isNegated,
            new Regex(prefix + regexPattern + suffix, RegexOptions.Compiled | RegexOptions.IgnoreCase));
    }

    public bool IsMatch(string normalizedPath) => Regex.IsMatch(normalizedPath);

    private static string ConvertGlobToRegex(string pattern)
    {
        var builder = new StringBuilder();
        for (var index = 0; index < pattern.Length; index++)
        {
            var current = pattern[index];
            if (current == '*')
            {
                if (index + 1 < pattern.Length && pattern[index + 1] == '*')
                {
                    builder.Append(".*");
                    index++;
                }
                else
                {
                    builder.Append("[^/]*");
                }

                continue;
            }

            if (current == '?')
            {
                builder.Append("[^/]");
                continue;
            }

            if (current == '[')
            {
                var end = pattern.IndexOf(']', index + 1);
                if (end > index)
                {
                    builder.Append(pattern[index..(end + 1)]);
                    index = end;
                    continue;
                }
            }

            if (".+()^$|{}".Contains(current, StringComparison.Ordinal))
            {
                builder.Append('\\');
            }

            builder.Append(current);
        }

        return builder.ToString();
    }
}