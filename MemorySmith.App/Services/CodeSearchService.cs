using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
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
    private CodeSearchBuildProgress _buildProgress = CodeSearchBuildProgress.Idle;
    private long _resultCacheGeneration;
    private long _queryTelemetryCounter;

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

        IReadOnlyList<CodeSearchResult> CompleteSearch(string mode, IReadOnlyList<CodeSearchResult> results)
        {
            if (queryTelemetryEnabled)
            {
                LogQueryTiming(query.Query!, normalizedTargets, limit, mode, results.Count, queryStartTimestamp);
            }

            return results;
        }

        var resultCacheKey = BuildResultCacheKey(query.Query!, normalizedTargets, limit);
        if (_queryResultCache.TryGetValue(resultCacheKey, out IReadOnlyList<CodeSearchResult>? cachedResults) && cachedResults is not null)
        {
            return CompleteSearch("cache", cachedResults);
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        var chunks = await LoadChunksAsync(connection, normalizedTargets, cancellationToken);
        if (chunks.Count == 0)
        {
            return CompleteSearch("empty-index", []);
        }

        if (TryGetQueryEmbedding(query.Query!, out var queryEmbedding))
        {
            var vectorResults = chunks
                .Where(chunk => chunk.Embedding.Length == queryEmbedding.Length)
                .Select(chunk => (Chunk: chunk, Score: Dot(queryEmbedding, chunk.Embedding)))
                .Where(entry => entry.Score > 0)
                .OrderByDescending(entry => entry.Score)
                .ThenBy(entry => entry.Chunk.DocumentPath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => entry.Chunk.StartLine)
                .Take(limit)
                .Select(entry => new CodeSearchResult(
                    entry.Chunk.Target,
                    entry.Chunk.DocumentPath,
                    entry.Chunk.AbsolutePath,
                    entry.Chunk.StartLine,
                    entry.Chunk.EndLine,
                    Math.Round(entry.Score, 6),
                    BuildSnippet(entry.Chunk.Snippet, query.Query!),
                    $"Code embedding cosine similarity {entry.Score:0.###}.",
                    entry.Chunk.IndexedAtUtc))
                .ToList();

            if (vectorResults.Count > 0)
            {
                CacheResults(resultCacheKey, vectorResults);
                return CompleteSearch("vector", vectorResults);
            }
        }

        var queryTokens = Tokenize(query.Query!);
        var lexicalResults = chunks
            .Select(chunk => (Chunk: chunk, Score: ScoreLexical(chunk, queryTokens)))
            .Where(entry => entry.Score > 0)
            .OrderByDescending(entry => entry.Score)
            .ThenBy(entry => entry.Chunk.DocumentPath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.Chunk.StartLine)
            .Take(limit)
            .Select(entry => new CodeSearchResult(
                entry.Chunk.Target,
                entry.Chunk.DocumentPath,
                entry.Chunk.AbsolutePath,
                entry.Chunk.StartLine,
                entry.Chunk.EndLine,
                entry.Score,
                BuildSnippet(entry.Chunk.Snippet, query.Query!),
                $"Lexical fallback matched {queryTokens.Count} query token(s) in indexed code.",
                entry.Chunk.IndexedAtUtc))
            .ToList();

            CacheResults(resultCacheKey, lexicalResults);
            return CompleteSearch("lexical", lexicalResults);
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

        await _indexLock.WaitAsync(cancellationToken);
        try
        {
            await EnsureDatabaseAsync(cancellationToken);
            await BuildIndexCoreAsync(forceRebuild, cancellationToken);
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
                                pendingDocuments.Clear();
                            }
                        }
                    }
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
        var chunkLineCount = Math.Max(5, _options.ChunkLineCount);
        var overlapLineCount = Math.Clamp(_options.ChunkOverlapLineCount, 0, Math.Max(0, chunkLineCount - 1));
        var chunkingStopwatch = Stopwatch.StartNew();
        var lines = sourceText.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        var preparedChunks = new List<PreparedChunk>();
        var step = Math.Max(1, chunkLineCount - overlapLineCount);
        var chunkIndex = 0;

        for (var startLineIndex = 0; startLineIndex < lines.Length; startLineIndex += step)
        {
            var startLine = startLineIndex + 1;
            var endLine = Math.Min(lines.Length, startLineIndex + chunkLineCount);
            var chunkLines = lines.Skip(startLineIndex).Take(endLine - startLineIndex).ToArray();
            var chunkText = string.Join('\n', chunkLines).Trim();
            if (string.IsNullOrWhiteSpace(chunkText))
            {
                continue;
            }

            if (chunkText.Length > _options.MaxChunkCharacters)
            {
                chunkText = chunkText[..Math.Max(1, _options.MaxChunkCharacters)].TrimEnd();
            }

            preparedChunks.Add(new PreparedChunk(
                target,
                documentPath,
                absolutePath,
                chunkIndex++,
                sourceHash,
                sourceLengthBytes,
                sourceLastWriteUtc,
                configurationHash,
                startLine,
                endLine,
                BuildSnippet(chunkText, chunkText),
                chunkText,
                BuildEmbeddingText(documentPath, chunkText)));
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
        var chunks = new List<IndexedChunk>();
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
CREATE INDEX IF NOT EXISTS IX_CodeSearchChunks_DocumentPath ON CodeSearchChunks(DocumentPath);";
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

    private void LogQueryTiming(string queryText, IReadOnlySet<string> targets, int limit, string mode, int resultCount, long queryStartTimestamp)
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
                "Code-search query timing slow path: {ElapsedMs:0.###} ms (mode={Mode}, results={ResultCount}, targets={TargetCount}, limit={Limit}, query=\"{QueryPreview}\").",
                elapsedMs,
                mode,
                resultCount,
                targets.Count,
                limit,
                queryPreview);
            return;
        }

        _logger.LogDebug(
            "Code-search query timing sample #{QueryNumber}: {ElapsedMs:0.###} ms (mode={Mode}, results={ResultCount}, targets={TargetCount}, limit={Limit}, query=\"{QueryPreview}\").",
            queryNumber,
            elapsedMs,
            mode,
            resultCount,
            targets.Count,
            limit,
            queryPreview);
    }

    private void InvalidateQueryCaches()
    {
        Interlocked.Increment(ref _resultCacheGeneration);
        _queryEmbeddingCache.Compact(1.0);
        _queryResultCache.Compact(1.0);
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

    private static double ScoreLexical(IndexedChunk chunk, IReadOnlySet<string> queryTokens)
    {
        if (queryTokens.Count == 0)
        {
            return 0;
        }

        var haystack = (chunk.DocumentPath + "\n" + chunk.SearchText).ToLowerInvariant();
        var score = 0.0;
        foreach (var token in queryTokens)
        {
            var occurrences = CountOccurrences(haystack, token);
            if (occurrences == 0)
            {
                continue;
            }

            score += occurrences;
            if (chunk.DocumentPath.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                score += 1.5;
            }
        }

        return score;
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        if (string.IsNullOrWhiteSpace(needle))
        {
            return 0;
        }

        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    private static IReadOnlySet<string> Tokenize(string query) =>
        TokenRegex.Matches(query ?? string.Empty)
            .Select(match => match.Value.ToLowerInvariant())
            .Where(value => value.Length > 1)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

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