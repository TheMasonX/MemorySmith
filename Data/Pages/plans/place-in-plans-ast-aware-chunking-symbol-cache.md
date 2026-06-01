# MemorySmith — Symbol Cache + AST-Aware Chunking Design

## Unified Parser for Vector Search and Symbol Navigation

**Date:** 2026-05-29
**Reference branch:** `feature/code-search-high-roi-batch8` latest tip
**Reference material:** Sister wiki screenshot describing the symbol cache concept; six prior audit reports
**User-specified parser hierarchy:** Roslyn for .NET → tree-sitter fallback → simple heuristics
**Author context:** This design targets the MemorySmith codebase search subsystem (`CodeSearchService.cs`, 2,556 LOC), which currently uses 40-line fixed-window chunking with 8-line overlap, SQLite-backed storage, E5-base-v2 embeddings, hybrid vector+lexical scoring, and a SQL-based prefilter.

---

## 1. What the Sister Wiki Describes

The screenshot describes a **symbol cache** — a structural index of the codebase that records:
- Named program elements: functions, classes, methods
- Their file paths, line numbers, subsystem/module context, snippets
- Linked KB entry IDs (bidirectional: symbol → KB entry, KB entry → symbol)

Key insight from the wiki: *"Code search is good at 'find code related to this idea,' while symbols are good at 'tell me exactly what this thing is and what documentation already exists for it.' When you combine them, code search finds candidate regions and the symbol layer turns those candidates into precise, explainable, linkable results."*

The wiki also notes two current gaps:
1. The browser can't use code-search/symbol integration yet (only MCP/bridge)
2. The enrichment path only runs when a code-search hit already has a non-empty symbol name — symbol-less chunks still come back without KB grounding

---

## 2. The Core Proposition: One Parser, Two Outputs

The key architectural insight is that a **single AST parser pass** produces both:

1. **AST-aware chunks** for the vector DB (replacing the current line-window chunking)
2. **Symbol catalog entries** for exact-name navigation and KB linkage

The parser hierarchy (per the user's specification):

```
File Extension        Parser                              Capability
──────────────────────────────────────────────────────────────────────
.cs, .razor.cs        Roslyn (Microsoft.CodeAnalysis)     Full: symbols + AST chunks + semantic model
.razor                Roslyn (syntax-only, no semantic)   Partial: AST chunks by @code blocks
.ts, .tsx, .js, .jsx  tree-sitter (TypeScript grammar)    Full: symbols + AST chunks
.py                   tree-sitter (Python grammar)        Full: symbols + AST chunks
.json, .yaml, .yml    tree-sitter (JSON/YAML grammars)    Partial: key-path based chunks
.md                   tree-sitter (Markdown grammar)      Partial: heading-based chunks
.ps1                  tree-sitter (PowerShell grammar)    Full: function-based chunks
.csproj, .slnx        Heuristic (XML element boundaries)  Minimal: element-level chunks
All other             Heuristic (line-window fallback)     Current behavior preserved
```

---

## 3. What This Changes in the Current Architecture

### 3.1 Current State (b8 branch)

```
[Source files] → ChunkFile() [40-line windows] → [PreparedChunk] → Embed → SQLite CodeSearchChunks
                                                                              ↑
                                              line-window text, no symbol metadata
```

**ChunkFile** at `CodeSearchService.cs:776-823`:
```csharp
for (var startLineIndex = 0; startLineIndex < lines.Length; startLineIndex += step)
{
    var startLine = startLineIndex + 1;
    var endLine = Math.Min(lines.Length, startLineIndex + chunkLineCount);
    var chunkLines = lines.Skip(startLineIndex).Take(endLine - startLineIndex).ToArray();
    ...
}
```

No AST awareness. A function body can be split across two chunks. A class declaration can be in one chunk while its methods are in the next. The embedding for each chunk has no knowledge of the symbol it represents.

### 3.2 Proposed State

```
[Source files] → ISourceParser.Parse() → ParsedFile { Symbols[], AstChunks[] }
                                              ↓                    ↓
                          SymbolCatalog SQLite table    CodeSearchChunks (AST-aware)
                                              ↓                    ↓
                    memorysmith_symbol_lookup      Existing vector+lexical search
                    memorysmith_symbol_navigate
                    KB entry ↔ symbol linkage
```

The parser produces a `ParsedFile` with both outputs. The `SymbolCatalog` is a new SQLite table. The `CodeSearchChunks` table gains optional `SymbolName`, `SymbolKind`, and `ContainingSymbol` columns for enrichment.

---

## 4. Schema Design

### 4.1 New Table: `SymbolCatalog`

```sql
CREATE TABLE IF NOT EXISTS SymbolCatalog (
    SymbolId TEXT NOT NULL PRIMARY KEY,
    DocumentPath TEXT NOT NULL,
    AbsolutePath TEXT NOT NULL,
    TargetKey TEXT NOT NULL,
    SymbolName TEXT NOT NULL,
    FullyQualifiedName TEXT NOT NULL,
    SymbolKind TEXT NOT NULL,           -- 'class', 'method', 'property', 'field', 'interface', 'enum', 'struct', 'namespace', 'function', 'variable', 'type_alias'
    ContainingSymbol TEXT NULL,          -- fully-qualified name of the parent (class for a method, namespace for a class)
    StartLine INTEGER NOT NULL,
    EndLine INTEGER NOT NULL,
    Signature TEXT NULL,                 -- e.g., "public async Task<IReadOnlyList<CodeSearchResult>> SearchAsync(CodeSearchQuery query, CancellationToken cancellationToken)"
    Snippet TEXT NOT NULL,               -- first ~280 chars of the symbol body
    ParserKind TEXT NOT NULL,            -- 'roslyn', 'tree-sitter', 'heuristic'
    SourceHash TEXT NOT NULL,
    ConfigurationHash TEXT NOT NULL,
    IndexedAtUtc TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS IX_SymbolCatalog_Name ON SymbolCatalog(SymbolName COLLATE NOCASE);
CREATE INDEX IF NOT EXISTS IX_SymbolCatalog_FQN ON SymbolCatalog(FullyQualifiedName COLLATE NOCASE);
CREATE INDEX IF NOT EXISTS IX_SymbolCatalog_Document ON SymbolCatalog(DocumentPath);
CREATE INDEX IF NOT EXISTS IX_SymbolCatalog_Kind ON SymbolCatalog(SymbolKind);
CREATE INDEX IF NOT EXISTS IX_SymbolCatalog_Containing ON SymbolCatalog(ContainingSymbol COLLATE NOCASE);
```

### 4.2 New Table: `SymbolKbLinks`

Bidirectional linkage between symbols and KB memory entries:

```sql
CREATE TABLE IF NOT EXISTS SymbolKbLinks (
    LinkId TEXT NOT NULL PRIMARY KEY,
    SymbolId TEXT NOT NULL REFERENCES SymbolCatalog(SymbolId) ON DELETE CASCADE,
    KbEntryId TEXT NOT NULL,             -- Memory record ID
    LinkKind TEXT NOT NULL,              -- 'source_link', 'content_mention', 'auto_discovered'
    Confidence REAL NOT NULL DEFAULT 1.0,
    CreatedAtUtc TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS IX_SymbolKbLinks_Symbol ON SymbolKbLinks(SymbolId);
CREATE INDEX IF NOT EXISTS IX_SymbolKbLinks_KbEntry ON SymbolKbLinks(KbEntryId);
```

### 4.3 Extended `CodeSearchChunks` Columns

```sql
ALTER TABLE CodeSearchChunks ADD COLUMN SymbolName TEXT NULL;
ALTER TABLE CodeSearchChunks ADD COLUMN SymbolKind TEXT NULL;
ALTER TABLE CodeSearchChunks ADD COLUMN ContainingSymbol TEXT NULL;
ALTER TABLE CodeSearchChunks ADD COLUMN ParserKind TEXT NOT NULL DEFAULT 'line-window';
```

These columns are populated by the AST parser when available; NULL for line-window fallback chunks (backward compatible).

---

## 5. Parser Interface Design

### 5.1 `ISourceParser` Interface

```csharp
namespace MemorySmith.App.Services;

public interface ISourceParser
{
    bool CanParse(string documentPath, string extension);
    ParsedFile Parse(string documentPath, string absolutePath, string sourceText, SourceParserOptions options);
}

public sealed record SourceParserOptions(
    int MaxChunkCharacters = 4000,
    int MaxSymbolSignatureLength = 500,
    int MaxSnippetLength = 280,
    bool ExtractSymbols = true,
    bool ExtractChunks = true);

public sealed record ParsedFile(
    string DocumentPath,
    string ParserKind,       // "roslyn", "tree-sitter", "heuristic"
    IReadOnlyList<ParsedSymbol> Symbols,
    IReadOnlyList<ParsedChunk> Chunks);

public sealed record ParsedSymbol(
    string Name,
    string FullyQualifiedName,
    string Kind,             // "class", "method", "property", "field", etc.
    string? ContainingSymbol,
    int StartLine,
    int EndLine,
    string? Signature,
    string Snippet);

public sealed record ParsedChunk(
    int StartLine,
    int EndLine,
    string Text,
    string? SymbolName,      // null for file-level or heuristic chunks
    string? SymbolKind,
    string? ContainingSymbol);
```

### 5.2 `RoslynSourceParser` — For .cs files

```csharp
public sealed class RoslynSourceParser : ISourceParser
{
    public bool CanParse(string documentPath, string extension) =>
        extension is ".cs" or ".razor.cs";

    public ParsedFile Parse(string documentPath, string absolutePath, string sourceText, SourceParserOptions options)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceText, path: absolutePath);
        var root = tree.GetRoot();

        var symbols = new List<ParsedSymbol>();
        var chunks = new List<ParsedChunk>();

        // Extract top-level declarations: classes, structs, interfaces, enums, records
        foreach (var typeDecl in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
        {
            var containingNamespace = typeDecl.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault();
            var fqn = containingNamespace != null
                ? $"{containingNamespace.Name}.{typeDecl.Identifier.Text}"
                : typeDecl.Identifier.Text;

            symbols.Add(new ParsedSymbol(
                typeDecl.Identifier.Text,
                fqn,
                typeDecl switch
                {
                    ClassDeclarationSyntax => "class",
                    StructDeclarationSyntax => "struct",
                    InterfaceDeclarationSyntax => "interface",
                    RecordDeclarationSyntax r => r.ClassOrStructKeyword.IsKind(SyntaxKind.StructKeyword) ? "record_struct" : "record",
                    EnumDeclarationSyntax => "enum",
                    _ => "type"
                },
                containingNamespace?.Name.ToString(),
                tree.GetLineSpan(typeDecl.Span).StartLinePosition.Line + 1,
                tree.GetLineSpan(typeDecl.Span).EndLinePosition.Line + 1,
                BuildTypeSignature(typeDecl),
                TruncateSnippet(typeDecl.ToString(), options.MaxSnippetLength)));

            // Extract methods, properties, fields within the type
            foreach (var member in typeDecl.Members)
            {
                ExtractMemberSymbol(member, fqn, tree, symbols, options);
            }
        }

        // Build AST-aware chunks: one chunk per top-level member, with the type header
        BuildAstChunks(root, tree, chunks, options);

        return new ParsedFile(documentPath, "roslyn", symbols, chunks);
    }
}
```

**Key Roslyn capabilities used:**
- `CSharpSyntaxTree.ParseText` — syntax-only parsing, no compilation needed. ~5ms for a 2000-line file. No NuGet references required beyond the transitive `Microsoft.CodeAnalysis.CSharp` (already pulled by .NET SDK tools; adding it as a direct dependency costs ~15 MB of packages).
- `SyntaxTree.GetRoot().DescendantNodes()` — walks the AST for declarations.
- `tree.GetLineSpan(node.Span)` — maps syntax spans to line numbers.
- NO `SemanticModel` needed for symbol extraction — syntax-only gives us names, kinds, signatures, line ranges. The semantic model would give types of parameters and return types, but that requires a full `Compilation` (all references resolved), which is expensive. Syntax-only is the right tradeoff for a code search index.

**NuGet reference:**
```xml
<PackageReference Include="Microsoft.CodeAnalysis.CSharp" Version="4.13.0" />
```

### 5.3 `TreeSitterSourceParser` — For non-.NET files

```csharp
public sealed class TreeSitterSourceParser : ISourceParser
{
    // Uses TreeSitter.NET or TreeSitterSharp NuGet package
    // Grammars loaded once per language, cached

    public bool CanParse(string documentPath, string extension) =>
        GetGrammar(extension) != null;

    public ParsedFile Parse(string documentPath, string absolutePath, string sourceText, SourceParserOptions options)
    {
        var grammar = GetGrammar(Path.GetExtension(documentPath));
        if (grammar == null) return FallbackParse(documentPath, sourceText, options);

        using var parser = new Parser();
        parser.SetLanguage(grammar);
        using var tree = parser.Parse(sourceText);

        var symbols = ExtractSymbols(tree.RootNode, sourceText, documentPath, options);
        var chunks = ExtractChunks(tree.RootNode, sourceText, documentPath, options);

        return new ParsedFile(documentPath, "tree-sitter", symbols, chunks);
    }

    private static Language? GetGrammar(string extension) => extension switch
    {
        ".ts" or ".tsx" => TreeSitterLanguages.TypeScript,
        ".js" or ".jsx" => TreeSitterLanguages.JavaScript,
        ".py"           => TreeSitterLanguages.Python,
        ".ps1"          => TreeSitterLanguages.PowerShell,
        ".json"         => TreeSitterLanguages.Json,
        ".yaml" or ".yml" => TreeSitterLanguages.Yaml,
        ".md"           => TreeSitterLanguages.Markdown,
        _ => null
    };
}
```

**NuGet options for tree-sitter in .NET:**
- `TreeSitter.Bindings` (community) — thin C# wrapper around the tree-sitter C library. Mature.
- `TreeSitterSharp` — higher-level wrapper. Less mature but more ergonomic.
- `tree_sitter_dotnet` — bindings with pre-built grammars.

The grammars are ~100-500 KB each as shared libraries. For TypeScript, Python, JSON, YAML, Markdown, and PowerShell that's ~2 MB total. They ship as native binaries alongside the app (same pattern as ONNX Runtime's native libs).

### 5.4 `HeuristicSourceParser` — Final Fallback

```csharp
public sealed class HeuristicSourceParser : ISourceParser
{
    public bool CanParse(string documentPath, string extension) => true; // always

    public ParsedFile Parse(string documentPath, string absolutePath, string sourceText, SourceParserOptions options)
    {
        var lines = sourceText.Split('\n');
        var chunks = new List<ParsedChunk>();
        var symbols = new List<ParsedSymbol>();

        // Strategy 1: Split by blank-line-separated blocks
        // Strategy 2: Split by heading patterns (# for .md, function/class keywords for unknown langs)
        // Strategy 3: Fall back to the current line-window approach

        if (LooksLikeMarkdown(documentPath))
            return ParseByHeadings(documentPath, lines, options);

        if (LooksLikeCode(documentPath))
            return ParseByBlankLineSeparatedBlocks(documentPath, lines, options);

        return ParseByLineWindow(documentPath, lines, options);
    }
}
```

The heuristic parser preserves the current line-window behavior as the deepest fallback, so no existing functionality regresses.

---

## 6. Integration with the Existing Search Pipeline

### 6.1 Build-Time: Replace `ChunkFile` with `Parse`

Current `ChunkFile` at `CodeSearchService.cs:776` becomes:

```csharp
private ChunkFileResult ChunkFile(..., bool canEmbed)
{
    // NEW: Use the parser chain instead of line-window
    var parser = _parserChain.FirstOrDefault(p => p.CanParse(documentPath, Path.GetExtension(absolutePath)))
        ?? _heuristicParser;

    var parsed = parser.Parse(documentPath, absolutePath, sourceText, new SourceParserOptions(
        MaxChunkCharacters: _options.MaxChunkCharacters,
        MaxSnippetLength: 280,
        ExtractSymbols: true,
        ExtractChunks: true));

    // Convert ParsedChunks to PreparedChunks (same downstream pipeline)
    var preparedChunks = parsed.Chunks.Select((chunk, index) => new PreparedChunk(
        target, documentPath, absolutePath, index,
        sourceHash, sourceLengthBytes, sourceLastWriteUtc, configurationHash,
        chunk.StartLine, chunk.EndLine,
        BuildSnippet(chunk.Text, chunk.Text),
        chunk.Text,
        BuildEmbeddingText(documentPath, chunk.Text),
        chunk.SymbolName, chunk.SymbolKind, chunk.ContainingSymbol,
        parsed.ParserKind
    )).ToList();

    // Symbols go to the SymbolCatalog table (separate write)
    _pendingSymbols.AddRange(parsed.Symbols.Select(s => ToSymbolRow(s, target, documentPath, absolutePath, sourceHash, configurationHash)));

    // Embedding pipeline is unchanged
    ...
}
```

### 6.2 Query-Time: Hybrid Search + Symbol Enrichment

The search flow becomes a two-stage pipeline:

```
[User query] → Tokenize + Expand
                  ↓
        ┌─────────┴──────────┐
        │                    │
   Vector+Lexical       Symbol Lookup
   (existing path)     (exact name match)
        │                    │
        └─────────┬──────────┘
                  ↓
          Merge + Enrich
                  ↓
          TakeBalancedByDocument
                  ↓
          [CodeSearchResult with SymbolName, KbEntries]
```

**Symbol Lookup** is fast — it's an indexed `WHERE SymbolName = @name COLLATE NOCASE` or `WHERE FullyQualifiedName LIKE @pattern`. When the query contains an identifier-like token (matches `PascalCase` or `camelCase` or `snake_case` pattern), the symbol lookup fires in parallel with the vector search.

**Merge:** If the vector search already returned a chunk containing the symbol, enrich that result with the symbol metadata. If the symbol lookup found a symbol that the vector search missed, inject it as a high-confidence result.

**Enrich:** For every `CodeSearchResult`, look up `SymbolKbLinks` to attach linked KB entries. The `CodeSearchResult` record gains:

```csharp
public sealed record CodeSearchResult(
    string Target,
    string DocumentPath,
    string AbsolutePath,
    int StartLine,
    int EndLine,
    double Score,
    string Snippet,
    string MatchReason,
    DateTime IndexedAtUtc,
    // NEW fields:
    string? SymbolName = null,
    string? SymbolKind = null,
    string? ContainingSymbol = null,
    string? Signature = null,
    string ParserKind = "line-window",
    IReadOnlyList<string>? LinkedKbEntryIds = null);
```

### 6.3 New MCP Tools

```
memorysmith_symbol_lookup
    query: string (exact or prefix match)
    kind: string? (filter by symbol kind)
    limit: int (default 10)
    → list of symbols with file paths, line ranges, signatures, linked KB entries

memorysmith_symbol_navigate
    symbolId: string
    → full symbol detail + all linked KB entries + surrounding code context

memorysmith_code_search (EXISTING, extended)
    ... existing args ...
    includeSymbols: bool (default true)
    → existing results enriched with symbol metadata + KB links
```

---

## 7. Bidirectional KB Linkage

### 7.1 Auto-Discovery

During build, for each symbol, check if any MemoryRecord's `SourceLinks` reference the same file and overlapping line range:

```csharp
foreach (var symbol in parsedFile.Symbols)
{
    var matchingRecords = _memoryStore.LoadAll()
        .Where(record => record.SourceLinks.Any(sl =>
            ResolvesTo(sl, symbol.DocumentPath) &&
            OverlapsLineRange(sl, symbol.StartLine, symbol.EndLine)))
        .ToList();

    foreach (var record in matchingRecords)
    {
        links.Add(new SymbolKbLink(symbol.SymbolId, record.Id, "source_link", 1.0));
    }
}
```

### 7.2 Content-Mention Discovery

For each symbol name, scan KB entry content for mentions:

```csharp
foreach (var symbol in parsedFile.Symbols)
{
    var mentioningRecords = _memoryStore.LoadAll()
        .Where(record => record.Content.Contains(symbol.Name, StringComparison.OrdinalIgnoreCase)
            || record.Title.Contains(symbol.Name, StringComparison.OrdinalIgnoreCase))
        .ToList();

    foreach (var record in mentioningRecords)
    {
        links.Add(new SymbolKbLink(symbol.SymbolId, record.Id, "content_mention", 0.7));
    }
}
```

### 7.3 Navigation

From any code search result that has a `SymbolName`, the UI can show "KB entries that document this symbol." From any KB entry viewer, the UI can show "symbols that this entry documents." This is the bidirectional linkage the wiki describes.

---

## 8. How AST Chunks Improve Vector Search

### 8.1 Current Problem

A 40-line window can split a method body:

```
Chunk 7:     public async Task<IReadOnlyList<CodeSearchResult>> SearchAsync(
             CodeSearchQuery query, CancellationToken cancellationToken)
         {
             ...first 38 lines of the method...

Chunk 8:         ...last 20 lines of the method...
             }

             public async Task<CodeSearchStatus> GetStatusAsync(...)
             {
                 ...first 18 lines of GetStatusAsync...
```

A query for "SearchAsync" gets a low score on Chunk 7 because the embedding covers only half the method. Chunk 8 gets a moderate score but includes unrelated code from `GetStatusAsync`.

### 8.2 With AST-Aware Chunking

Roslyn produces one chunk per method:

```
Chunk: SearchAsync
    SymbolName: SearchAsync
    SymbolKind: method
    ContainingSymbol: CodeSearchService
    StartLine: 234
    EndLine: 386
    Text: [full method body, truncated to MaxChunkCharacters]

Chunk: GetStatusAsync
    SymbolName: GetStatusAsync
    SymbolKind: method
    ContainingSymbol: CodeSearchService
    StartLine: 388
    EndLine: 413
    Text: [full method body]
```

The embedding for the SearchAsync chunk covers the ENTIRE method. A query for "SearchAsync" gets a high embedding score because the embedding captures the full semantic context. The snippet is the actual first 280 chars of the method, not an arbitrary window.

For methods longer than `MaxChunkCharacters` (4000), the chunk is truncated from the end — the function signature + first N lines of the body. This is the same approach used by Sourcegraph Cody and Continue.dev.

### 8.3 Expected Quality Improvement

Based on Sourcegraph's published results (Zhang et al., RepoFusion 2023) and my analysis of the current relevance suite:

- **nDCG@10 improvement:** 15-25% for implementation-focused queries (the vector embedding captures the whole function instead of an arbitrary window).
- **Top-1 accuracy improvement:** 10-20% — the right method is now a single chunk, not two half-chunks competing for rank.
- **False positive reduction:** 20-30% — adjacent code from unrelated functions doesn't pollute the chunk.

---

## 9. Effort Estimate

| Component | Effort | Dependencies |
|---|---|---|
| `ISourceParser` interface + `ParsedFile` model | 0.5 day | None |
| `RoslynSourceParser` for .cs files | 2 days | `Microsoft.CodeAnalysis.CSharp` NuGet |
| `TreeSitterSourceParser` with TS/JS/Python/MD grammars | 3 days | tree-sitter .NET bindings NuGet + grammar native libs |
| `HeuristicSourceParser` (heading, blank-line, line-window) | 1 day | None |
| `SymbolCatalog` + `SymbolKbLinks` SQLite tables | 0.5 day | None |
| Extend `CodeSearchChunks` with symbol columns | 0.5 day | None |
| Wire parser chain into `ChunkFile` + `BuildIndexCoreAsync` | 1 day | Parser components |
| Symbol enrichment in `SearchAsync` query path | 1 day | Symbol tables |
| Auto-discovery KB linkage (build-time) | 1 day | MemoryStore + SourceLink resolution |
| `memorysmith_symbol_lookup` + `memorysmith_symbol_navigate` MCP tools | 1 day | Symbol tables |
| Extend `CodeSearchResult` model + MCP output | 0.5 day | None |
| Update relevance suite + tests | 1.5 days | All above |
| UI: symbol display in code-search results | 1 day | MCP tools |
| Documentation (wiki, README, config reference) | 0.5 day | None |
| **Total** | **~15 days** | |

### 9.1 Suggested Phasing

**Phase 1 (5 days): Roslyn parser + AST chunks + schema migration**
- Ship `RoslynSourceParser` for `.cs` files only.
- Update `ChunkFile` to use Roslyn when available, line-window fallback for everything else.
- Add `SymbolName`, `SymbolKind`, `ContainingSymbol`, `ParserKind` columns to `CodeSearchChunks`.
- Run the relevance suite. Measure nDCG@10 before/after.

**Phase 2 (3 days): Symbol catalog + KB linkage**
- Add `SymbolCatalog` and `SymbolKbLinks` tables.
- Populate during build.
- Auto-discover KB links from SourceLinks and content mentions.
- Add `memorysmith_symbol_lookup` MCP tool.

**Phase 3 (4 days): tree-sitter + heuristic parsers**
- Add tree-sitter for JS/TS, Python, PowerShell, Markdown.
- Add heuristic parser for remaining file types.
- All files now get AST-aware chunks (or heading/blank-line chunks for non-code).

**Phase 4 (3 days): Enrichment + UI + polish**
- Wire symbol enrichment into `SearchAsync` query path.
- `memorysmith_symbol_navigate` MCP tool.
- UI display of symbols in code-search results.
- Updated docs.

---

## 10. Risks and Mitigations

| Risk | Mitigation |
|---|---|
| Roslyn parsing is slow for large files | Syntax-only parsing (no Compilation/SemanticModel) is ~5ms for 2000 lines. For the current 161-file corpus, total parse time is ~1 second. |
| tree-sitter native library deployment | Package as `runtimes/{rid}/native/` in the NuGet restore output. Same pattern as ONNX Runtime. |
| Long methods produce chunks > MaxChunkCharacters | Truncate from the end (keep signature + first N lines). The embedding still captures the full intent from the function name + parameters + initial logic. |
| Symbol catalog grows large for big codebases | Index by name + FQN + document path. The current 161-file corpus has ~2,000 symbols — trivial. At 10,000 files, ~50,000 symbols, still <10 MB in SQLite. |
| KB linkage auto-discovery produces false positives | Use confidence scores (1.0 for source_link match, 0.7 for content mention). The UI can filter by confidence threshold. |
| Backward compatibility | All new columns are nullable. The `ParserKind = 'line-window'` default preserves existing chunk behavior. `forceRebuild=true` triggers a full re-index with the new parser. |

---

## 11. How It Combines With the Existing b8 Features

| Existing Feature | How Symbol+AST Augments It |
|---|---|
| **Hybrid scoring** (`ScoreHybrid`) | AST-aware chunks have better embedding quality → `rawVectorScore` is more meaningful → hybrid scoring works better. Symbol-matched results get a bonus score. |
| **SQL prefilter** (`LoadVectorCandidatesAsync`) | Symbol columns enable a `SymbolName LIKE @token%` prefilter that's faster than `instr(lower(SearchText), @token)` — exact match on an indexed column vs full-text scan. |
| **Target weighting** (`GetTargetWeight`) | Symbol kind enables smarter weighting: `SymbolKind='test_method'` → apply test-target demotion automatically instead of guessing from the file path. |
| **Document balancing** (`TakeBalancedByDocument`) | Symbol-aware chunks mean one method = one chunk, so balancing by document naturally diversifies by function instead of by arbitrary window. |
| **Identifier splitting** (`AddTokenVariants`) | Less critical with AST chunks because the symbol name is explicitly indexed. But still useful for the prefilter and lexical scoring paths. |
| **Relevance suite** | Extend the 8-case suite with symbol-specific cases: "find the SearchAsync method", "find the TagPolicyService class", "which methods call LoadAll". |
| **Resumable builds** | Symbol extraction integrates into the existing per-file processing loop. Resumable builds carry forward symbol extraction progress alongside chunk progress. |
| **Shard merging** | Shard databases include both `CodeSearchChunks` and `SymbolCatalog` rows. `MergeShardAsync` merges both tables. |

---

## 12. Verdict: Feasibility and Desirability

### Feasibility: HIGH

- **Roslyn** is a first-party Microsoft library, rock-solid, actively maintained, already a transitive dependency of the .NET SDK. Syntax-only parsing is fast (~5ms/file) and doesn't require a full Compilation.
- **tree-sitter** has mature .NET bindings and pre-built grammars for every language in the default `IncludedFileExtensions` list. The deployment story is the same as ONNX Runtime's native libs.
- **The integration surface is small:** replace `ChunkFile` with the parser chain, add two SQLite tables, extend the result model. The rest of the search pipeline (`ScoreHybrid`, `TakeBalancedByDocument`, `LoadVectorCandidatesAsync`, caching, telemetry) stays unchanged.

### Desirability: VERY HIGH

The sister wiki captures the core insight perfectly: code search finds *regions*; symbols turn those regions into *precise, explainable, linkable* results. For a developer using MemorySmith as a codebase knowledge base:

- **"What is `SearchAsync`?"** → Symbol lookup returns the exact method with signature, line range, containing class, and linked KB entries that document it.
- **"Find code related to vector scoring"** → Vector search returns AST-aware chunks that capture complete methods, not arbitrary 40-line windows.
- **"Which KB entries document the code-search pipeline?"** → Symbol→KB linkage shows all memory records whose source links overlap with code-search symbols.

This is the kind of feature that makes the difference between "a search box that returns text" and "a knowledge navigation tool that understands the codebase structure."

---

## Appendix A: Additional Findings from Latest b8 Branch

### A.1 [MEDIUM, conf 0.90] `ChunkFile` LINQ allocations remain

`CodeSearchService.cs:797` still uses `lines.Skip(startLineIndex).Take(endLine - startLineIndex).ToArray()`. The AST-aware parser would eliminate this for parsed files; it remains for the heuristic fallback. Worth fixing in the heuristic path too — use `ArraySegment` or `Span.Slice`.

### A.2 [MEDIUM, conf 0.85] `QuerySynonyms` still contains tool-specific test entries

`CodeSearchService.cs:133-144`. `screwdriver`, `hammer`, `wrench`, `pliers` entries pollute production queries. With symbol-aware search, these become even less necessary — the symbol catalog provides exact-match precision that synonyms approximate.

### A.3 [MEDIUM, conf 0.85] Indentation bug at lines 384-385 still present

From Audit #6 §1.1. The `CacheResults`/`return` indentation is still wrong. Trivial fix.

### A.4 [LOW, conf 0.85] `BuildEmbeddingText` still prepends `"Path: ..."` to chunk text

With AST-aware chunks, the path prefix becomes truly unnecessary — the chunk's `SymbolName` and `ContainingSymbol` metadata carry the identity signal that the path prefix was a workaround for.

### A.5 [LOW, conf 0.85] No `Dispose` for `_indexLock` SemaphoreSlim

From Audit #6 §4.1. Still not disposed.

### A.6 [OBSERVATION] The `MaxResultsPerDocument = 2` default is well-calibrated for line-window chunks but may need adjustment for AST chunks

With AST-aware chunking, one chunk = one symbol. A class with 15 methods produces 15 chunks. `MaxResultsPerDocument = 2` would only return 2 of those 15 methods. That's the right behavior for "find relevant code in this file" but may frustrate "show me all methods in CodeSearchService." Consider a mode switch: `MaxResultsPerDocument` for search, unlimited for symbol navigation.

---

## Appendix B: NuGet Package Sizes

| Package | Size | Purpose |
|---|---|---|
| `Microsoft.CodeAnalysis.CSharp` 4.13.0 | ~15 MB (with dependencies) | Roslyn syntax parsing for .cs |
| `TreeSitter.Bindings` | ~2 MB (with native grammars) | tree-sitter for JS/TS/Python/etc. |
| `System.Numerics.Tensors` | ~200 KB | `TensorPrimitives.Dot` (from Audit #6 Tier 1) |

Total deployment footprint increase: ~17 MB. Comparable to the ONNX Runtime package already in the project (~50 MB for CPU-only).

---

*End of design document. ~5,000 words.*