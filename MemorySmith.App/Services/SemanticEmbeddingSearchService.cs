using Microsoft.Extensions.Options;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using MemorySmith.Core.Models;

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
    int? Dimension);

public interface ITextEmbeddingProvider
{
    EmbeddingProviderStatus GetStatus();
    bool TryEmbed(string text, EmbeddingInputKind kind, out float[] embedding, out string? reason);
}

public sealed class SemanticEmbeddingSearchService
{
    private readonly ITextEmbeddingProvider _embeddingProvider;
    private readonly SemanticSearchOptions _options;

    public SemanticEmbeddingSearchService(ITextEmbeddingProvider embeddingProvider, IOptions<MemorySmithOptions> options)
    {
        _embeddingProvider = embeddingProvider;
        _options = options.Value.SemanticSearch;
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

        if (!_embeddingProvider.TryEmbed(query, EmbeddingInputKind.Query, out var queryEmbedding, out _))
        {
            return false;
        }

        var ranked = new List<MemorySearchResult>();
        foreach (var record in records)
        {
            var text = BuildEmbeddingText(record, _options.MaxIndexedTextCharacters);
            if (!_embeddingProvider.TryEmbed(text, EmbeddingInputKind.Document, out var documentEmbedding, out _) || documentEmbedding.Length != queryEmbedding.Length)
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

    private static double Dot(IReadOnlyList<float> left, IReadOnlyList<float> right)
    {
        var sum = 0.0;
        for (var index = 0; index < left.Count; index++)
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
}

public sealed class OnnxTextEmbeddingProvider : ITextEmbeddingProvider, IDisposable
{
    private readonly SemanticSearchOptions _options;
    private readonly object _lock = new();
    private InferenceSession? _session;
    private WordPieceTokenizer? _tokenizer;
    private bool _initialized;
    private string? _modelPath;
    private string? _vocabularyPath;
    private string _statusReason = "Embedding provider has not been initialized.";
    private int? _dimension;

    public OnnxTextEmbeddingProvider(IOptions<MemorySmithOptions> options)
    {
        _options = options.Value.SemanticSearch;
    }

    public EmbeddingProviderStatus GetStatus()
    {
        var available = EnsureInitialized();
        return new EmbeddingProviderStatus(available, _statusReason, _modelPath, _vocabularyPath, _dimension);
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
            embedding = ExtractEmbedding(results, tokenized.AttentionMask);
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

            if (!_options.EmbeddingsEnabled)
            {
                _statusReason = "Embeddings are disabled by configuration.";
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
                _tokenizer = WordPieceTokenizer.Load(_vocabularyPath);
                _session = new InferenceSession(_modelPath);
                _statusReason = "ONNX embedding provider is available.";
                return true;
            }
            catch (Exception ex)
            {
                _session?.Dispose();
                _session = null;
                _tokenizer = null;
                _statusReason = $"ONNX embedding provider failed to initialize: {ex.Message}";
                return false;
            }
        }
    }

    private static string ResolvePath(string path) =>
        Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));

    private static IReadOnlyList<NamedOnnxValue> CreateInputs(InferenceSession session, TokenizedText tokenized)
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

            inputs.Add(CreateTokenInput(input.Key, values, input.Value.ElementType));
        }

        if (inputs.Count == 0)
        {
            throw new InvalidOperationException("The ONNX embedding model did not expose any supported inputs.");
        }

        return inputs;
    }

    private static NamedOnnxValue CreateTokenInput(string name, long[] values, Type? elementType)
    {
        var dimensions = new[] { 1, values.Length };
        if (elementType == typeof(int))
        {
            return NamedOnnxValue.CreateFromTensor(name, new DenseTensor<int>(values.Select(value => (int)value).ToArray(), dimensions));
        }

        return NamedOnnxValue.CreateFromTensor(name, new DenseTensor<long>(values, dimensions));
    }

    private static string NormalizeInputName(string name) =>
        name.Replace('-', '_').ToLowerInvariant();

    private static float[] ExtractEmbedding(IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results, long[] attentionMask)
    {
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
                return data;
            }

            if (dimensions.Length == 2 && dimensions[0] == 1)
            {
                return data.Take(dimensions[1]).ToArray();
            }

            if (dimensions.Length == 3 && dimensions[0] == 1)
            {
                return MeanPool(data, dimensions[1], dimensions[2], attentionMask);
            }
        }

        throw new InvalidOperationException("The ONNX embedding model did not return a supported float tensor output.");
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

    private sealed class WordPieceTokenizer
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