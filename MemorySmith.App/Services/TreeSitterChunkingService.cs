using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TreeSitter;

namespace MemorySmith.App.Services;

/// <summary>
/// Multi-language AST chunking using TreeSitter.DotNet native bindings.
/// Handles .razor, .ts/.tsx, .js/.jsx, .py, .json, and other file types
/// by parsing into ASTs and creating chunks at declaration/section boundaries.
/// </summary>
public sealed class TreeSitterChunkingService : IDisposable
{
    private readonly ILogger<TreeSitterChunkingService> _logger;
    private readonly ConcurrentDictionary<string, Lazy<Language>> _languages = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _parserLock = new();
    private Parser? _sharedParser;
    private bool _disposed;

    /// <summary>
    /// Maps file extensions to tree-sitter language names (as known by the native grammars).
    /// </summary>
    private static readonly Dictionary<string, string> ExtensionToLanguage = new(StringComparer.OrdinalIgnoreCase)
    {
        [".razor"] = "Razor",
        [".ts"] = "TypeScript",
        [".tsx"] = "TypeScript",
        [".js"] = "JavaScript",
        [".jsx"] = "JavaScript",
        [".mjs"] = "JavaScript",
        [".cjs"] = "JavaScript",
        [".py"] = "Python",
        [".json"] = "JSON",
        [".css"] = "CSS",
        [".html"] = "HTML",
        [".htm"] = "HTML",
        [".go"] = "Go",
        [".rs"] = "Rust",
        [".java"] = "Java",
        [".rb"] = "Ruby",
        [".swift"] = "Swift",
        [".scala"] = "Scala",
        [".sh"] = "Bash",
        [".bash"] = "Bash",
        [".c"] = "C",
        [".cpp"] = "Cpp",
        [".h"] = "C",
        [".hpp"] = "Cpp",
        [".cs"] = "CSharp",   // We use Roslyn for .cs, but make it available
        [".toml"] = "TOML",
    };

    /// <summary>
    /// Node type names that represent chunkable declarations, keyed by language name (lowercase).
    /// If a language is not listed, all top-level named children are chunked.
    /// </summary>
    private static readonly Dictionary<string, HashSet<string>> ChunkableTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["razor"] = ["code_block", "markup_block", "directive"],
        ["typescript"] = [
            "function_declaration", "class_declaration", "interface_declaration",
            "enum_declaration", "type_alias_declaration", "method_definition",
            "lexical_declaration", "module_declaration"
        ],
        ["javascript"] = [
            "function_declaration", "class_declaration", "method_definition",
            "lexical_declaration", "export_statement"
        ],
        ["python"] = [
            "function_definition", "class_definition", "decorated_definition"
        ],
        ["json"] = ["object", "array"],
        ["CSharp"] = [
            "class_declaration", "struct_declaration", "interface_declaration",
            "enum_declaration", "method_declaration", "property_declaration",
            "field_declaration", "record_declaration"
        ],
        ["css"] = ["rule_set", "at_rule"],
        ["go"] = [
            "function_declaration", "method_declaration", "type_declaration"
        ],
        ["rust"] = [
            "function_item", "struct_item", "enum_item", "impl_item",
            "trait_item", "type_item", "const_item", "static_item"
        ],
        ["java"] = [
            "class_declaration", "interface_declaration", "method_declaration",
            "enum_declaration", "record_declaration"
        ],
        ["ruby"] = [
            "method", "singleton_method", "class", "module"
        ],
        ["bash"] = [
            "function_definition"
        ],
    };

    public TreeSitterChunkingService(ILogger<TreeSitterChunkingService>? logger = null)
    {
        _logger = logger ?? NullLogger<TreeSitterChunkingService>.Instance;
    }

    /// <summary>
    /// Returns true if this service can handle the given file extension.
    /// </summary>
    public bool CanHandle(string extension) =>
        ExtensionToLanguage.ContainsKey(extension);

    /// <summary>
    /// Attempts to parse the given file into AST-aware chunks using tree-sitter.
    /// Returns false if the file type is unsupported, parsing fails, or the AST
    /// yields no meaningful chunk boundaries.
    /// </summary>
    public bool TryChunk(string documentPath, string sourceText, int maxChunkCharacters, out List<ParsedChunk> chunks)
    {
        chunks = [];

        var extension = Path.GetExtension(documentPath);
        if (!ExtensionToLanguage.TryGetValue(extension, out var languageName))
        {
            return false;
        }

        try
        {
            var language = GetOrCreateLanguage(languageName);
            Tree tree;

            lock (_parserLock)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_sharedParser is null)
                {
                    _sharedParser = new Parser(language);
                }
                else if (_sharedParser.Language != language)
                {
                    _sharedParser.Language = language;
                }

                tree = _sharedParser.Parse(sourceText) ?? throw new InvalidOperationException(
                    $"Tree-sitter returned null tree for {documentPath}.");
            }

            var rootNode = tree.RootNode;
            if (rootNode is null || rootNode.NamedChildren.Count == 0)
            {
                _logger.LogDebug(
                    "Tree-sitter produced no named children for {DocumentPath}; falling through.",
                    documentPath);
                return false;
            }

            var hasChunkableType = ChunkableTypes.TryGetValue(languageName, out var allowedTypes);
            var rawChunks = new List<(int StartLine, int EndLine, string Text)>();

            for (var i = 0; i < rootNode.NamedChildren.Count; i++)
            {
                var child = rootNode.NamedChildren[i];
                if (child is null)
                {
                    continue;
                }

                // Skip trivial single-line nodes (imports, expressions, comments)
                if (child.StartPosition.Row == child.EndPosition.Row)
                {
                    continue;
                }

                // Skip nodes that aren't in the allowed set for this language
                if (hasChunkableType && allowedTypes is not null && !allowedTypes.Contains(child.Type))
                {
                    continue;
                }

                var startLine = child.StartPosition.Row + 1;  // 1-based for our pipeline
                var endLine = child.EndPosition.Row + 1;
                var nodeText = child.Text;
                if (string.IsNullOrWhiteSpace(nodeText))
                {
                    continue;
                }

                rawChunks.Add((startLine, endLine, nodeText));
            }

            if (rawChunks.Count == 0)
            {
                _logger.LogDebug(
                    "Tree-sitter parsed {DocumentPath} but found no chunkable declarations; " +
                    "falling through to next parser strategy.",
                    documentPath);
                return false;
            }

            // Build the final parsed chunks with size limits
            foreach (var (startLine, endLine, text) in rawChunks)
            {
                var chunkText = text;
                if (chunkText.Length > maxChunkCharacters)
                {
                    chunkText = chunkText[..Math.Max(1, maxChunkCharacters)].TrimEnd();
                }

                if (string.IsNullOrWhiteSpace(chunkText))
                {
                    continue;
                }

                chunks.Add(new ParsedChunk(startLine, endLine, chunkText));
            }

            return chunks.Count > 0;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex,
                "Tree-sitter chunking failed for {DocumentPath}; parser pipeline will continue.",
                documentPath);
            chunks = [];
            return false;
        }
    }

    /// <summary>
    /// Gets the chunkable declaration kinds for a given language (for diagnostics/logging).
    /// </summary>
    public IReadOnlySet<string>? GetAllowedTypes(string languageName) =>
        ChunkableTypes.TryGetValue(languageName, out var types) ? types : null;

    private Language GetOrCreateLanguage(string languageName)
    {
        return _languages.GetOrAdd(languageName, name =>
            new Lazy<Language>(() =>
            {
                _logger.LogDebug("Loading tree-sitter language grammar: {LanguageName}", name);
                return new Language(name);
            })).Value;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        lock (_parserLock)
        {
            _sharedParser?.Dispose();
            _sharedParser = null;
        }

        // Languages don't need disposal in TreeSitter.DotNet, they're cached natively
        _languages.Clear();
    }
}
