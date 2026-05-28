using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using MemorySmith.Core.Models;
using OnnxSessionOptions = Microsoft.ML.OnnxRuntime.SessionOptions;

namespace MemorySmith.App.Services;

public enum EmbeddingInputKind
{
    Query,
    Document
}

public sealed record EmbeddingProviderStatus(
    bool Available,
    string Reason,
    string? ModelPath,
    string? VocabularyPath,
    int? Dimension,
    string RequestedExecutionProvider,
    string ActiveExecutionProvider,
    string? RequestedExecutionDevice,
    string? ActiveExecutionDevice);

public interface ITextEmbeddingProvider
{
    EmbeddingProviderStatus GetStatus();
    bool TryEmbed(string text, EmbeddingInputKind kind, out float[] embedding, out string? reason);
}

public interface IBatchTextEmbeddingProvider : ITextEmbeddingProvider
{
    bool TryEmbedBatch(IReadOnlyList<string> texts, EmbeddingInputKind kind, out IReadOnlyList<float[]> embeddings, out string? reason);
}

internal static class OnnxEmbeddingModelConventions
{
    public static string CanonicalizeTokenizerKind(string? tokenizerKind)
    {
        var normalized = tokenizerKind?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return "wordpiece";
        }

        return normalized.ToLowerInvariant() switch
        {
            "wordpiece" or "bert" or "bert-wordpiece" => "wordpiece",
            _ => normalized.ToLowerInvariant()
        };
    }

    public static string CanonicalizePoolingMode(string? poolingMode)
    {
        var normalized = poolingMode?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return "mean";
        }

        return normalized.ToLowerInvariant() switch
        {
            "mean" or "meanpool" or "mean_pool" => "mean",
            "cls" or "class" or "class-token" or "class_token" => "cls",
            _ => normalized.ToLowerInvariant()
        };
    }

    public static string CanonicalizeExecutionProvider(string? executionProvider)
    {
        var normalized = executionProvider?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return "cpu";
        }

        return normalized.ToLowerInvariant() switch
        {
            "cpu" => "cpu",
            "cuda" or "gpu" => "cuda",
            "openvino" or "open-vino" or "open_vino" => "openvino",
            "none" => "none",
            _ => normalized.ToLowerInvariant()
        };
    }

    public static string DisplayExecutionProvider(string? executionProvider)
    {
        var normalized = executionProvider?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return "Cpu";
        }

        return CanonicalizeExecutionProvider(normalized) switch
        {
            "cpu" => "Cpu",
            "cuda" => "Cuda",
            "openvino" => "OpenVino",
            "none" => "None",
            _ => normalized
        };
    }

    public static string GetUnsupportedTokenizerMessage(string? tokenizerKind) =>
        $"Tokenizer kind '{tokenizerKind ?? string.Empty}' is not supported. Supported kinds: WordPiece.";

    public static string GetUnsupportedPoolingMessage(string? poolingMode) =>
        $"Pooling mode '{poolingMode ?? string.Empty}' is not supported. Supported modes: Mean, Cls.";

    public static string GetUnsupportedExecutionProviderMessage(string? executionProvider) =>
        $"Execution provider '{executionProvider ?? string.Empty}' is not supported. Supported providers: Cpu, Cuda, OpenVino.";
}

internal static class OnnxEmbeddingVectorProjector
{
    public static float[] ProjectSequenceOutput(float[] data, int tokenCount, int dimension, long[] attentionMask, string? poolingMode)
    {
        return OnnxEmbeddingModelConventions.CanonicalizePoolingMode(poolingMode) switch
        {
            "mean" => MeanPool(data, tokenCount, dimension, attentionMask),
            "cls" => ClsPool(data, tokenCount, dimension, attentionMask),
            _ => throw new NotSupportedException(OnnxEmbeddingModelConventions.GetUnsupportedPoolingMessage(poolingMode))
        };
    }

    private static float[] MeanPool(float[] data, int tokenCount, int dimension, long[] attentionMask)
    {
        var pooled = new float[dimension];
        var included = 0;
        for (var tokenIndex = 0; tokenIndex < tokenCount && tokenIndex < attentionMask.Length; tokenIndex++)
        {
            if (attentionMask[tokenIndex] == 0)
            {
                continue;
            }

            included++;
            var offset = tokenIndex * dimension;
            for (var dimensionIndex = 0; dimensionIndex < dimension; dimensionIndex++)
            {
                pooled[dimensionIndex] += data[offset + dimensionIndex];
            }
        }

        if (included == 0)
        {
            return pooled;
        }

        for (var dimensionIndex = 0; dimensionIndex < pooled.Length; dimensionIndex++)
        {
            pooled[dimensionIndex] /= included;
        }

        return pooled;
    }

    private static float[] ClsPool(float[] data, int tokenCount, int dimension, long[] attentionMask)
    {
        for (var tokenIndex = 0; tokenIndex < tokenCount && tokenIndex < attentionMask.Length; tokenIndex++)
        {
            if (attentionMask[tokenIndex] == 0)
            {
                continue;
            }

            var offset = tokenIndex * dimension;
            return data.Skip(offset).Take(dimension).ToArray();
        }

        return new float[dimension];
    }
}

public sealed class SemanticEmbeddingSearchService : IDisposable
{
    private const int MaxCachedQueryEmbeddings = 256;
    private const int MaxCachedDocumentEmbeddings = 4096;
    private static readonly JsonSerializerOptions PersistentEmbeddingJsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ITextEmbeddingProvider _embeddingProvider;
    private readonly MemorySmithOptions _settings;
    private readonly SemanticSearchOptions _options;
    private readonly MemoryCache _queryEmbeddingCache = new(new MemoryCacheOptions { SizeLimit = MaxCachedQueryEmbeddings });
    private readonly MemoryCache _documentEmbeddingCache = new(new MemoryCacheOptions { SizeLimit = MaxCachedDocumentEmbeddings });
    private readonly ConcurrentDictionary<string, object> _persistentDocumentLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _persistentDocumentCacheDirectory;
    private readonly string _embeddingConfigurationHash;

    public SemanticEmbeddingSearchService(ITextEmbeddingProvider embeddingProvider, IOptions<MemorySmithOptions> options)
    {
        _embeddingProvider = embeddingProvider;
        _settings = options.Value;
        _options = _settings.SemanticSearch;
        _persistentDocumentCacheDirectory = Path.Combine(ResolveDataDeploymentRoot(_settings.DataPath), "Graph", "embeddings");
        _embeddingConfigurationHash = BuildEmbeddingConfigurationHash();
    }

    public RetrievalProviderMetadata GetProviderMetadata()
    {
        var status = _embeddingProvider.GetStatus();
        var mode = _options.EmbeddingsEnabled && status.Available ? "onnx-embedding" : "token-fallback";
        return new RetrievalProviderMetadata(
            "semantic",
            mode,
            status.Available,
            status.Reason,
            status.ModelPath,
            status.VocabularyPath,
            status.Dimension);
    }

    public bool TryRank(
        IReadOnlyList<MemoryRecord> records,
        string? query,
        IReadOnlySet<string> queryTokens,
        out IReadOnlyList<MemorySearchResult> results)
    {
        results = [];
        if (!_options.EmbeddingsEnabled || string.IsNullOrWhiteSpace(query))
        {
            return false;
        }

        if (!TryGetQueryEmbedding(query, out var queryEmbedding))
        {
            return false;
        }

        var ranked = new List<MemorySearchResult>();
        foreach (var record in records)
        {
            var text = BuildEmbeddingText(record, _options.MaxIndexedTextCharacters);
            if (!TryGetDocumentEmbedding(record.Id, text, queryEmbedding.Length, out var documentEmbedding))
            {
                continue;
            }

            var score = Dot(queryEmbedding, documentEmbedding);
            if (double.IsNaN(score) || score <= 0)
            {
                continue;
            }

            ranked.Add(new MemorySearchResult(
                record.Id,
                record.Title,
                record.Status,
                record.Confidence,
                Math.Round(score, 6),
                record.Tags,
                record.UsageCount,
                BuildSnippet(record.Content, queryTokens),
                $"Embedding cosine similarity {score:0.###} using ONNX semantic search.",
                record.LastUpdated));
        }

        results = ranked
            .OrderByDescending(result => result.Score)
            .ThenByDescending(result => result.LastUpdated)
            .ThenBy(result => result.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(result => result.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return results.Count > 0;
    }

    public void Dispose()
    {
        _queryEmbeddingCache.Dispose();
        _documentEmbeddingCache.Dispose();
    }

    private bool TryGetQueryEmbedding(string query, out float[] embedding)
    {
        if (_queryEmbeddingCache.TryGetValue(query, out float[]? cached) && cached is not null)
        {
            embedding = cached;
            return true;
        }

        if (!_embeddingProvider.TryEmbed(query, EmbeddingInputKind.Query, out embedding, out _))
        {
            return false;
        }

        _queryEmbeddingCache.Set(query, embedding, new MemoryCacheEntryOptions { Size = 1 });
        return true;
    }

    private bool TryGetDocumentEmbedding(string recordId, string text, int expectedLength, out float[] embedding)
    {
        if (_documentEmbeddingCache.TryGetValue(recordId, out CachedEmbeddingEntry? cached) &&
            cached is not null &&
            string.Equals(cached.SourceText, text, StringComparison.Ordinal) &&
            cached.Embedding.Length == expectedLength)
        {
            embedding = cached.Embedding;
            return true;
        }

        var persistentLock = _persistentDocumentLocks.GetOrAdd(recordId, static _ => new object());
        lock (persistentLock)
        {
            if (_documentEmbeddingCache.TryGetValue(recordId, out cached) &&
                cached is not null &&
                string.Equals(cached.SourceText, text, StringComparison.Ordinal) &&
                cached.Embedding.Length == expectedLength)
            {
                embedding = cached.Embedding;
                return true;
            }

            if (TryLoadPersistedDocumentEmbedding(recordId, text, expectedLength, out embedding))
            {
                _documentEmbeddingCache.Set(recordId, new CachedEmbeddingEntry(text, embedding), new MemoryCacheEntryOptions { Size = 1 });
                return true;
            }

            if (!_embeddingProvider.TryEmbed(text, EmbeddingInputKind.Document, out embedding, out _) || embedding.Length != expectedLength)
            {
                return false;
            }

            _documentEmbeddingCache.Set(recordId, new CachedEmbeddingEntry(text, embedding), new MemoryCacheEntryOptions { Size = 1 });
            TryPersistDocumentEmbedding(recordId, text, embedding);
            return true;
        }
    }

    private bool TryLoadPersistedDocumentEmbedding(string recordId, string sourceText, int expectedLength, out float[] embedding)
    {
        embedding = [];
        var path = GetPersistentEmbeddingPath(recordId);
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            var persisted = JsonSerializer.Deserialize<PersistedEmbeddingEntry>(File.ReadAllText(path), PersistentEmbeddingJsonOptions);
            if (persisted is null ||
                !string.Equals(persisted.ConfigurationHash, _embeddingConfigurationHash, StringComparison.Ordinal) ||
                !string.Equals(persisted.SourceTextHash, ComputeHash(sourceText), StringComparison.Ordinal) ||
                persisted.Embedding.Length != expectedLength)
            {
                return false;
            }

            embedding = persisted.Embedding;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void TryPersistDocumentEmbedding(string recordId, string sourceText, float[] embedding)
    {
        try
        {
            Directory.CreateDirectory(_persistentDocumentCacheDirectory);
            var path = GetPersistentEmbeddingPath(recordId);
            var tempPath = path + ".tmp";
            var entry = new PersistedEmbeddingEntry(
                recordId,
                ComputeHash(sourceText),
                _embeddingConfigurationHash,
                embedding,
                DateTime.UtcNow);

            File.WriteAllText(tempPath, JsonSerializer.Serialize(entry, PersistentEmbeddingJsonOptions));
            File.Move(tempPath, path, overwrite: true);
        }
        catch
        {
            // Persistence is an optimization; on write failure the in-memory caches still serve the current process.
        }
    }

    private string GetPersistentEmbeddingPath(string recordId)
    {
        var fileName = IsSafeEmbeddingId(recordId) ? recordId : ComputeHash(recordId);
        return Path.Combine(_persistentDocumentCacheDirectory, fileName + ".json");
    }

    private string BuildEmbeddingConfigurationHash()
    {
        var modelPath = ResolveSemanticPath(_options.ModelPath);
        var vocabularyPath = ResolveSemanticPath(_options.VocabularyPath);
        var onnxRuntimeVersion = typeof(InferenceSession).Assembly.GetName().Version?.ToString() ?? "unknown";
        var payload = string.Join('|',
            modelPath,
            File.Exists(modelPath) ? File.GetLastWriteTimeUtc(modelPath).Ticks.ToString() : "missing",
            vocabularyPath,
            File.Exists(vocabularyPath) ? File.GetLastWriteTimeUtc(vocabularyPath).Ticks.ToString() : "missing",
            onnxRuntimeVersion,
            OnnxEmbeddingModelConventions.CanonicalizeTokenizerKind(_options.TokenizerKind),
            OnnxEmbeddingModelConventions.CanonicalizePoolingMode(_options.PoolingMode),
            OnnxEmbeddingModelConventions.CanonicalizeExecutionProvider(_options.ExecutionProvider),
            _options.CpuFallbackEnabled,
            _options.CudaDeviceId,
            _options.OpenVinoDeviceId,
            _options.QueryPrefix,
            _options.DocumentPrefix,
            _options.MaxInputTokens,
            _options.MaxIndexedTextCharacters);

        return ComputeHash(payload);
    }

    private string ResolveSemanticPath(string path)
    {
        var expanded = Environment.ExpandEnvironmentVariables(path);
        if (Path.IsPathFullyQualified(expanded))
        {
            return Path.GetFullPath(expanded);
        }

        return Path.GetFullPath(Path.Combine(ResolveDataDeploymentRoot(_settings.DataPath), NormalizeDataRelativePath(expanded)));
    }

    private static string ResolveDataDeploymentRoot(string dataPath)
    {
        var expanded = Environment.ExpandEnvironmentVariables(dataPath);
        var fullPath = Path.GetFullPath(expanded).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(Path.GetFileName(fullPath), "Memories", StringComparison.OrdinalIgnoreCase)
            ? Directory.GetParent(fullPath)?.FullName ?? fullPath
            : fullPath;
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

    private static string ComputeHash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text ?? string.Empty)));

    private static bool IsSafeEmbeddingId(string recordId)
    {
        foreach (var character in recordId)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character != '-' && character != '_')
            {
                return false;
            }
        }

        return true;
    }

    private static string BuildEmbeddingText(MemoryRecord record, int maxCharacters)
    {
        var content = string.Join('\n', new[]
        {
            record.Title,
            record.Tags.Count == 0 ? string.Empty : "Tags: " + string.Join(", ", record.Tags),
            record.References.Count == 0 ? string.Empty : "References: " + string.Join(", ", record.References),
            record.Content
        }.Where(part => !string.IsNullOrWhiteSpace(part)));

        return content.Length <= maxCharacters ? content : content[..Math.Max(1, maxCharacters)].TrimEnd();
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

    private sealed record CachedEmbeddingEntry(string SourceText, float[] Embedding);

    private sealed record PersistedEmbeddingEntry(
        string RecordId,
        string SourceTextHash,
        string ConfigurationHash,
        float[] Embedding,
        DateTime UpdatedUtc);
}

public sealed class OnnxTextEmbeddingProvider : IBatchTextEmbeddingProvider, IDisposable
{
    private readonly MemorySmithOptions _settings;
    private readonly SemanticSearchOptions _options;
    private readonly object _lock = new();
    private InferenceSession? _session;
    private ITokenizer? _tokenizer;
    private bool _initialized;
    private string? _modelPath;
    private string? _vocabularyPath;
    private string _statusReason = "Embedding provider has not been initialized.";
    private int? _dimension;
    private string _requestedExecutionProvider = "cpu";
    private string _activeExecutionProvider = "none";
    private string? _requestedExecutionDevice;
    private string? _activeExecutionDevice;

    public OnnxTextEmbeddingProvider(IOptions<MemorySmithOptions> options)
    {
        _settings = options.Value;
        _options = _settings.SemanticSearch;
    }

    public EmbeddingProviderStatus GetStatus()
    {
        _requestedExecutionProvider = OnnxEmbeddingModelConventions.CanonicalizeExecutionProvider(_options.ExecutionProvider);
        _requestedExecutionDevice = GetExecutionDevice(_requestedExecutionProvider);
        var available = EnsureInitialized();
        return new EmbeddingProviderStatus(
            available,
            _statusReason,
            _modelPath,
            _vocabularyPath,
            _dimension,
            OnnxEmbeddingModelConventions.DisplayExecutionProvider(_options.ExecutionProvider),
            OnnxEmbeddingModelConventions.DisplayExecutionProvider(_activeExecutionProvider),
            _requestedExecutionDevice,
            _activeExecutionDevice);
    }

    public bool TryEmbed(string text, EmbeddingInputKind kind, out float[] embedding, out string? reason)
    {
        embedding = [];
        if (!EnsureInitialized())
        {
            reason = _statusReason;
            return false;
        }

        try
        {
            var session = _session!;
            var prefix = kind == EmbeddingInputKind.Query ? _options.QueryPrefix : _options.DocumentPrefix;
            var tokenized = _tokenizer!.Encode(prefix + (text ?? string.Empty), _options.MaxInputTokens);
            using var results = session.Run(CreateInputs(session, tokenized));
            embedding = ExtractEmbeddings(results, [tokenized], _options.PoolingMode)[0];
            Normalize(embedding);
            _dimension = embedding.Length;
            reason = null;
            return true;
        }
        catch (Exception ex)
        {
            reason = ex.Message;
            return false;
        }
    }

    public bool TryEmbedBatch(IReadOnlyList<string> texts, EmbeddingInputKind kind, out IReadOnlyList<float[]> embeddings, out string? reason)
    {
        embeddings = [];
        if (texts.Count == 0)
        {
            reason = null;
            return true;
        }

        if (texts.Count == 1)
        {
            var singleSuccess = TryEmbed(texts[0], kind, out var singleEmbedding, out reason);
            embeddings = singleSuccess ? [singleEmbedding] : [];
            return singleSuccess;
        }

        if (!EnsureInitialized())
        {
            reason = _statusReason;
            return false;
        }

        try
        {
            var session = _session!;
            var prefix = kind == EmbeddingInputKind.Query ? _options.QueryPrefix : _options.DocumentPrefix;
            var tokenized = texts
                .Select(text => _tokenizer!.Encode(prefix + (text ?? string.Empty), _options.MaxInputTokens))
                .ToArray();

            using var results = session.Run(CreateInputs(session, tokenized));
            var extracted = ExtractEmbeddings(results, tokenized, _options.PoolingMode).ToArray();
            foreach (var vector in extracted)
            {
                Normalize(vector);
            }

            if (extracted.Length > 0)
            {
                _dimension = extracted[0].Length;
            }

            embeddings = extracted;
            reason = null;
            return true;
        }
        catch (Exception ex)
        {
            reason = ex.Message;
            return false;
        }
    }

    private bool EnsureInitialized()
    {
        if (_initialized)
        {
            return _session is not null && _tokenizer is not null;
        }

        lock (_lock)
        {
            if (_initialized)
            {
                return _session is not null && _tokenizer is not null;
            }

            _initialized = true;
            _modelPath = ResolvePath(_options.ModelPath);
            _vocabularyPath = ResolvePath(_options.VocabularyPath);
            _requestedExecutionProvider = OnnxEmbeddingModelConventions.CanonicalizeExecutionProvider(_options.ExecutionProvider);
            _requestedExecutionDevice = GetExecutionDevice(_requestedExecutionProvider);
            _activeExecutionProvider = "none";
            _activeExecutionDevice = null;

            if (!_options.EmbeddingsEnabled)
            {
                _statusReason = "Embeddings are disabled by configuration.";
                return false;
            }

            if (!IsSupportedExecutionProvider(_requestedExecutionProvider))
            {
                _statusReason = OnnxEmbeddingModelConventions.GetUnsupportedExecutionProviderMessage(_options.ExecutionProvider);
                return false;
            }

            if (!File.Exists(_modelPath))
            {
                _statusReason = $"ONNX embedding model was not found at '{_modelPath}'.";
                return false;
            }

            if (!File.Exists(_vocabularyPath))
            {
                _statusReason = $"WordPiece vocabulary was not found at '{_vocabularyPath}'.";
                return false;
            }

            try
            {
                _tokenizer = CreateTokenizer(_options.TokenizerKind, _vocabularyPath);
                _session = CreateSession(_modelPath);
                return true;
            }
            catch (Exception ex)
            {
                _session?.Dispose();
                _session = null;
                _tokenizer = null;
                _activeExecutionProvider = "none";
                _activeExecutionDevice = null;
                _statusReason = $"ONNX embedding provider failed to initialize: {ex.Message}";
                return false;
            }
        }
    }

    private InferenceSession CreateSession(string modelPath)
    {
        if (!IsSupportedExecutionProvider(_requestedExecutionProvider))
        {
            throw new NotSupportedException(OnnxEmbeddingModelConventions.GetUnsupportedExecutionProviderMessage(_options.ExecutionProvider));
        }

        var allowCpuFallback = _options.CpuFallbackEnabled && _requestedExecutionProvider is "cuda" or "openvino";
        if (TryCreateSession(modelPath, _requestedExecutionProvider, out var session, out var failure))
        {
            return session!;
        }

        if (!allowCpuFallback)
        {
            throw failure ?? new InvalidOperationException($"Execution provider '{OnnxEmbeddingModelConventions.DisplayExecutionProvider(_requestedExecutionProvider)}' failed to initialize.");
        }

        var requestedProviderLabel = OnnxEmbeddingModelConventions.DisplayExecutionProvider(_requestedExecutionProvider);
        var requestedDevice = FormatExecutionProviderWithDevice(_requestedExecutionProvider, _requestedExecutionDevice);
        var requestedFailure = failure?.Message ?? "unknown error";

        if (TryCreateSession(modelPath, "cpu", out session, out var cpuFailure))
        {
            _statusReason = $"Requested {requestedDevice} was unavailable; fell back to CPU. Requested provider error: {requestedFailure}";
            return session!;
        }

        throw new InvalidOperationException(
            $"Requested {requestedProviderLabel} execution provider failed to initialize ({requestedFailure}) and CPU fallback also failed ({cpuFailure?.Message ?? "unknown error"}).");
    }

    private bool TryCreateSession(string modelPath, string executionProvider, out InferenceSession? session, out Exception? failure)
    {
        try
        {
            using var sessionOptions = CreateSessionOptions(executionProvider);
            session = new InferenceSession(modelPath, sessionOptions);
            _activeExecutionProvider = executionProvider;
            _activeExecutionDevice = GetExecutionDevice(executionProvider);
            if (string.IsNullOrWhiteSpace(_statusReason) || string.Equals(_statusReason, "Embedding provider has not been initialized.", StringComparison.Ordinal))
            {
                _statusReason = $"ONNX embedding provider is available via {FormatExecutionProviderWithDevice(executionProvider, _activeExecutionDevice)}.";
            }

            failure = null;
            return true;
        }
        catch (Exception ex)
        {
            session = null;
            failure = ex;
            return false;
        }
    }

    private OnnxSessionOptions CreateSessionOptions(string executionProvider)
    {
        var sessionOptions = new OnnxSessionOptions();
        try
        {
            ConfigureExecutionProvider(sessionOptions, executionProvider);
            return sessionOptions;
        }
        catch
        {
            sessionOptions.Dispose();
            throw;
        }
    }

    private void ConfigureExecutionProvider(OnnxSessionOptions sessionOptions, string executionProvider)
    {
        switch (executionProvider)
        {
            case "cpu":
                return;
            case "cuda":
                sessionOptions.AppendExecutionProvider_CUDA(_options.CudaDeviceId);
                return;
            case "openvino":
                if (!OperatingSystem.IsWindows())
                {
                    throw new PlatformNotSupportedException("OpenVino execution provider requires a supported Windows host.");
                }

                sessionOptions.AppendExecutionProvider_OpenVINO(_options.OpenVinoDeviceId ?? string.Empty);
                return;
            default:
                throw new NotSupportedException(OnnxEmbeddingModelConventions.GetUnsupportedExecutionProviderMessage(_options.ExecutionProvider));
        }
    }

    private string? GetExecutionDevice(string executionProvider)
    {
        return executionProvider switch
        {
            "cuda" => _options.CudaDeviceId.ToString(CultureInfo.InvariantCulture),
            "openvino" => string.IsNullOrWhiteSpace(_options.OpenVinoDeviceId) ? null : _options.OpenVinoDeviceId.Trim(),
            _ => null
        };
    }

    private static bool IsSupportedExecutionProvider(string executionProvider) => executionProvider is "cpu" or "cuda" or "openvino";

    private static string FormatExecutionProviderWithDevice(string executionProvider, string? executionDevice)
    {
        var provider = OnnxEmbeddingModelConventions.DisplayExecutionProvider(executionProvider);
        return string.IsNullOrWhiteSpace(executionDevice)
            ? provider
            : $"{provider} ({executionDevice})";
    }

    private string ResolvePath(string path)
    {
        var expanded = Environment.ExpandEnvironmentVariables(path);
        if (Path.IsPathFullyQualified(expanded))
        {
            return Path.GetFullPath(expanded);
        }

        var dataRoot = ResolveDataDeploymentRoot(_settings.DataPath);
        return Path.GetFullPath(Path.Combine(dataRoot, NormalizeDataRelativePath(expanded)));
    }

    private static string ResolveDataDeploymentRoot(string dataPath)
    {
        var expanded = Environment.ExpandEnvironmentVariables(dataPath);
        var fullPath = Path.GetFullPath(expanded).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(Path.GetFileName(fullPath), "Memories", StringComparison.OrdinalIgnoreCase)
            ? Directory.GetParent(fullPath)?.FullName ?? fullPath
            : fullPath;
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

    private static List<NamedOnnxValue> CreateInputs(InferenceSession session, TokenizedText tokenized)
    {
        var inputs = new List<NamedOnnxValue>();
        foreach (var input in session.InputMetadata)
        {
            var normalized = NormalizeInputName(input.Key);
            var values = normalized switch
            {
                "input_ids" => tokenized.InputIds,
                "attention_mask" or "input_mask" => tokenized.AttentionMask,
                "token_type_ids" or "segment_ids" => tokenized.TokenTypeIds,
                _ => throw new NotSupportedException($"Unsupported ONNX embedding input '{input.Key}'.")
            };

            inputs.Add(CreateTokenInput(input.Key, values, [1, values.Length], input.Value.ElementType));
        }

        if (inputs.Count == 0)
        {
            throw new InvalidOperationException("The ONNX embedding model did not expose any supported inputs.");
        }

        return inputs;
    }

    private static List<NamedOnnxValue> CreateInputs(InferenceSession session, IReadOnlyList<TokenizedText> tokenizedBatch)
    {
        if (tokenizedBatch.Count == 1)
        {
            return CreateInputs(session, tokenizedBatch[0]);
        }

        var maxTokenCount = tokenizedBatch.Max(tokenized => tokenized.InputIds.Length);
        var inputIds = new long[tokenizedBatch.Count * maxTokenCount];
        var attentionMask = new long[tokenizedBatch.Count * maxTokenCount];
        var tokenTypeIds = new long[tokenizedBatch.Count * maxTokenCount];

        for (var batchIndex = 0; batchIndex < tokenizedBatch.Count; batchIndex++)
        {
            var tokenized = tokenizedBatch[batchIndex];
            var offset = batchIndex * maxTokenCount;
            Array.Copy(tokenized.InputIds, 0, inputIds, offset, tokenized.InputIds.Length);
            Array.Copy(tokenized.AttentionMask, 0, attentionMask, offset, tokenized.AttentionMask.Length);
            Array.Copy(tokenized.TokenTypeIds, 0, tokenTypeIds, offset, tokenized.TokenTypeIds.Length);
        }

        var inputs = new List<NamedOnnxValue>();
        foreach (var input in session.InputMetadata)
        {
            var normalized = NormalizeInputName(input.Key);
            var values = normalized switch
            {
                "input_ids" => inputIds,
                "attention_mask" or "input_mask" => attentionMask,
                "token_type_ids" or "segment_ids" => tokenTypeIds,
                _ => throw new NotSupportedException($"Unsupported ONNX embedding input '{input.Key}'.")
            };

            inputs.Add(CreateTokenInput(input.Key, values, [tokenizedBatch.Count, maxTokenCount], input.Value.ElementType));
        }

        if (inputs.Count == 0)
        {
            throw new InvalidOperationException("The ONNX embedding model did not expose any supported inputs.");
        }

        return inputs;
    }

    private static NamedOnnxValue CreateTokenInput(string name, long[] values, int[] dimensions, Type? elementType)
    {
        if (elementType == typeof(int))
        {
            var intValues = new int[values.Length];
            for (var index = 0; index < values.Length; index++)
            {
                intValues[index] = (int)values[index];
            }

            return NamedOnnxValue.CreateFromTensor(name, new DenseTensor<int>(intValues, dimensions));
        }

        return NamedOnnxValue.CreateFromTensor(name, new DenseTensor<long>(values, dimensions));
    }

    private static string NormalizeInputName(string name) =>
        name.Replace('-', '_').ToLowerInvariant();

    private static IReadOnlyList<float[]> ExtractEmbeddings(IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results, IReadOnlyList<TokenizedText> tokenizedBatch, string? poolingMode)
    {
        var batchCount = tokenizedBatch.Count;
        foreach (var result in results.OrderByDescending(result => result.Name.Contains("embedding", StringComparison.OrdinalIgnoreCase)))
        {
            Tensor<float> tensor;
            try
            {
                tensor = result.AsTensor<float>();
            }
            catch
            {
                continue;
            }

            var dimensions = tensor.Dimensions.ToArray();
            var data = tensor.ToArray();
            if (dimensions.Length == 1)
            {
                if (batchCount != 1)
                {
                    continue;
                }

                return [data];
            }

            if (dimensions.Length == 2 && dimensions[0] == batchCount)
            {
                var dimension = dimensions[1];
                var embeddings = new List<float[]>(batchCount);
                for (var batchIndex = 0; batchIndex < batchCount; batchIndex++)
                {
                    var offset = batchIndex * dimension;
                    embeddings.Add(data.AsSpan(offset, dimension).ToArray());
                }

                return embeddings;
            }

            if (dimensions.Length == 3 && dimensions[0] == batchCount)
            {
                var tokenCount = dimensions[1];
                var dimension = dimensions[2];
                var stride = tokenCount * dimension;
                var embeddings = new List<float[]>(batchCount);
                for (var batchIndex = 0; batchIndex < batchCount; batchIndex++)
                {
                    var offset = batchIndex * stride;
                    embeddings.Add(OnnxEmbeddingVectorProjector.ProjectSequenceOutput(
                        data.AsSpan(offset, stride).ToArray(),
                        tokenCount,
                        dimension,
                        tokenizedBatch[batchIndex].AttentionMask,
                        poolingMode));
                }

                return embeddings;
            }
        }

        throw new InvalidOperationException("The ONNX embedding model did not return a supported float tensor output.");
    }

    private static ITokenizer CreateTokenizer(string? tokenizerKind, string vocabularyPath)
    {
        return OnnxEmbeddingModelConventions.CanonicalizeTokenizerKind(tokenizerKind) switch
        {
            "wordpiece" => WordPieceTokenizer.Load(vocabularyPath),
            _ => throw new NotSupportedException(OnnxEmbeddingModelConventions.GetUnsupportedTokenizerMessage(tokenizerKind))
        };
    }

    private static void Normalize(float[] vector)
    {
        var norm = MathF.Sqrt(vector.Sum(value => value * value));
        if (norm <= 0)
        {
            return;
        }

        for (var index = 0; index < vector.Length; index++)
        {
            vector[index] /= norm;
        }
    }

    public void Dispose()
    {
        _session?.Dispose();
    }

    private sealed record TokenizedText(long[] InputIds, long[] AttentionMask, long[] TokenTypeIds);

    private interface ITokenizer
    {
        TokenizedText Encode(string text, int maxInputTokens);
    }

    private sealed class WordPieceTokenizer : ITokenizer
    {
        private readonly IReadOnlyDictionary<string, long> _vocabulary;
        private readonly long _unknownId;
        private readonly long _classId;
        private readonly long _separatorId;

        private WordPieceTokenizer(IReadOnlyDictionary<string, long> vocabulary)
        {
            _vocabulary = vocabulary;
            _unknownId = GetTokenId("[UNK]", 100);
            _classId = GetTokenId("[CLS]", 101);
            _separatorId = GetTokenId("[SEP]", 102);
        }

        public static WordPieceTokenizer Load(string vocabularyPath)
        {
            var vocabulary = new Dictionary<string, long>(StringComparer.Ordinal);
            var index = 0L;
            foreach (var line in File.ReadLines(vocabularyPath))
            {
                var token = line.Trim();
                if (!string.IsNullOrWhiteSpace(token))
                {
                    vocabulary.TryAdd(token, index);
                }

                index++;
            }

            if (vocabulary.Count == 0)
            {
                throw new InvalidOperationException("The WordPiece vocabulary is empty.");
            }

            return new WordPieceTokenizer(vocabulary);
        }

        public TokenizedText Encode(string text, int maxInputTokens)
        {
            var tokenBudget = Math.Max(4, maxInputTokens);
            var inputIds = new List<long> { _classId };
            foreach (var token in BasicTokenize(text))
            {
                foreach (var pieceId in WordPieceTokenize(token))
                {
                    if (inputIds.Count >= tokenBudget - 1)
                    {
                        break;
                    }

                    inputIds.Add(pieceId);
                }

                if (inputIds.Count >= tokenBudget - 1)
                {
                    break;
                }
            }

            inputIds.Add(_separatorId);
            var attentionMask = Enumerable.Repeat(1L, inputIds.Count).ToArray();
            var tokenTypeIds = new long[inputIds.Count];
            return new TokenizedText(inputIds.ToArray(), attentionMask, tokenTypeIds);
        }

        private long GetTokenId(string token, long fallback) =>
            _vocabulary.TryGetValue(token, out var id) ? id : fallback;

        private IEnumerable<long> WordPieceTokenize(string token)
        {
            if (_vocabulary.TryGetValue(token, out var id))
            {
                yield return id;
                yield break;
            }

            var start = 0;
            var pieces = new List<long>();
            while (start < token.Length)
            {
                var end = token.Length;
                long? pieceId = null;
                while (start < end)
                {
                    var candidate = token[start..end];
                    if (start > 0)
                    {
                        candidate = "##" + candidate;
                    }

                    if (_vocabulary.TryGetValue(candidate, out var candidateId))
                    {
                        pieceId = candidateId;
                        break;
                    }

                    end--;
                }

                if (!pieceId.HasValue)
                {
                    yield return _unknownId;
                    yield break;
                }

                pieces.Add(pieceId.Value);
                start = end;
            }

            foreach (var piece in pieces)
            {
                yield return piece;
            }
        }

        private static IEnumerable<string> BasicTokenize(string text)
        {
            var current = new List<char>();
            foreach (var character in text.ToLowerInvariant())
            {
                if (char.IsWhiteSpace(character))
                {
                    var token = FlushCurrent();
                    if (token is not null)
                    {
                        yield return token;
                    }

                    continue;
                }

                if (char.IsLetterOrDigit(character))
                {
                    current.Add(character);
                    continue;
                }

                var flushed = FlushCurrent();
                if (flushed is not null)
                {
                    yield return flushed;
                }

                yield return character.ToString();
            }

            var finalToken = FlushCurrent();
            if (finalToken is not null)
            {
                yield return finalToken;
            }

            string? FlushCurrent()
            {
                if (current.Count == 0)
                {
                    return null;
                }

                var token = new string(current.ToArray());
                current.Clear();
                return token;
            }
        }
    }
}