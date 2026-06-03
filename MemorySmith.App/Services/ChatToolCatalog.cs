using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using MemorySmith.Core.Models;

namespace MemorySmith.App.Services;

/// <summary>
/// Risk classification for a tool. Used by chat and MCP to gate/audit calls.
/// </summary>
public enum ChatToolRisk
{
    ReadOnly,
    SensitiveRead,
    Write
}

/// <summary>
/// A schema-bearing, executable tool shared by /mcp tools/call and the in-app chat tool intercept.
/// </summary>
public sealed record ChatToolDescriptor(
    string Name,
    string Description,
    JsonObject InputSchema,
    ChatToolRisk Risk,
    bool AvailableInChat,
    bool AvailableInMcp,
    Func<JsonObject, ChatToolExecutionContext, CancellationToken, Task<ChatToolExecutionResult>> Execute,
    bool EnabledByDefaultInMcp = true);

public sealed record ChatToolExecutionContext(
    MemoryApplicationService Memories,
    IPageService Pages,
    string Transport,
    ClaimsPrincipal? User = null,
    ICurrentUserContext? CurrentUser = null,
    AuthOptions? Auth = null,
    string? DefaultPageMinimumRole = null,
    /// <summary>
    bool AgentWritesEnabled = false,
    bool AgentWriteAutoAccept = false,
    CodeSearchService? CodeSearch = null,
    /// <summary>
    /// Nesting depth in the agent delegation chain.
    /// 0 for all direct MCP callers. 1+ for internal sub-agent delegation (Phase 3).
    /// Used to enforce MaxNestingDepth and to exclude memorysmith_agent_invoke from sub-agent catalogs.
    /// </summary>
    int NestingDepth = 0,
    /// <summary>
    /// Session ID of the parent agent session that spawned this sub-agent call (Phase 3).
    /// Null for all external MCP callers in Phase 1-2.
    /// </summary>
    string? ParentSessionId = null,
    /// <summary>
    /// GPU slot handle held by the calling Athena session (Phase 3 only).
    /// AgentInvokeTool.Execute disposes this before acquiring the sub-agent slot to prevent deadlock.
    /// Always null in Phase 1-2 since AvailableInAgent is false.
    /// TODO (Phase 3): Populate this in MemoryChatAgent's agent-mode tool loop before calling Execute.
    /// </summary>
    IAsyncDisposable? InheritedGpuSlot = null)
{
    public bool CanViewPage(string minimumRole) =>
        CurrentUser is not null
            ? PageAccessLevels.CanView(minimumRole, CurrentUser, Auth)
            : PageAccessLevels.CanView(minimumRole, User, Auth);

    public bool CanSetPageMinimumRole(string minimumRole) =>
        CurrentUser is not null
            ? PageAccessLevels.CanSetMinimumRole(minimumRole, CurrentUser, Auth)
            : PageAccessLevels.CanSetMinimumRole(minimumRole, User, Auth);
}

public sealed record ChatToolExecutionResult(
    string Text,
    bool IsError = false,
    IReadOnlyList<ChatContextItem>? ContextItems = null,
    JsonNode? Structured = null);

/// <summary>
/// Single source of truth for the read-only tool surface used by both the JSON-RPC /mcp endpoint
/// and the in-chat application-intercepted tool protocol.
/// </summary>
public sealed class ChatToolCatalog
{
    public static readonly JsonSerializerOptions ToolJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly Dictionary<string, ChatToolDescriptor> _tools;

    /// <summary>Default constructor — builds the full tool catalog from BuildTools().</summary>
    public ChatToolCatalog()
    {
        _tools = BuildTools().ToDictionary(tool => tool.Name, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Filtered constructor — builds a catalog from a pre-filtered set of descriptors.
    /// Used by <c>AgentSessionService</c> to create scoped sub-agent catalogs that only
    /// expose tools within a session's computed effective scope.
    /// </summary>
    public ChatToolCatalog(IEnumerable<ChatToolDescriptor> allowedTools)
    {
        _tools = allowedTools.ToDictionary(tool => tool.Name, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<ChatToolDescriptor> All => _tools.Values.ToList();

    public IReadOnlyList<ChatToolDescriptor> ChatTools =>
        _tools.Values.Where(tool => tool.AvailableInChat).ToList();

    public IReadOnlyList<ChatToolDescriptor> McpTools =>
        _tools.Values.Where(tool => tool.AvailableInMcp).ToList();

    public IReadOnlyList<ChatToolDescriptor> AgentTools =>
        _tools.Values.Where(tool => tool.AvailableInMcp).ToList();

    public bool TryGet(string name, out ChatToolDescriptor tool)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            tool = null!;
            return false;
        }
        return _tools.TryGetValue(name, out tool!);
    }

    public bool IsChatEnabled(string name) =>
        TryGet(name, out var tool) && tool.AvailableInChat;

    public bool IsMcpEnabled(string name) =>
        TryGet(name, out var tool) && tool.AvailableInMcp;

    private static IEnumerable<ChatToolDescriptor> BuildTools()
    {
        yield return new ChatToolDescriptor(
            "memorysmith_search",
            "Search MemorySmith wiki records with Lucene-style lexical ranking plus optional tag and status filters.",
            BuildSearchSchema(),
            ChatToolRisk.ReadOnly,
            AvailableInChat: true,
            AvailableInMcp: true,
            Execute: async (args, ctx, ct) =>
            {
                var results = await ctx.Memories.SearchAsync(ReadLexicalQuery(args), ct);
                return new ChatToolExecutionResult(FormatLexicalResults(results), ContextItems: results.Select(ToMemoryContextItem).ToList());
            });

        yield return new ChatToolDescriptor(
            "memorysmith_semantic_search",
            "Search MemorySmith wiki records with ONNX embeddings when configured, falling back to local semantic token scoring with match explanations.",
            BuildSearchSchema(),
            ChatToolRisk.ReadOnly,
            AvailableInChat: true,
            AvailableInMcp: true,
            Execute: async (args, ctx, ct) =>
            {
                var results = await ctx.Memories.SemanticSearchAsync(ReadSemanticQuery(args), ct);
                return new ChatToolExecutionResult(FormatSemanticResults(results), ContextItems: results.Select(ToMemoryContextItem).ToList());
            });

        yield return new ChatToolDescriptor(
            "memorysmith_hybrid_search",
            "Search MemorySmith wiki records by fusing Lucene-style lexical rank and the active semantic ranker with reciprocal rank fusion.",
            BuildSearchSchema(),
            ChatToolRisk.ReadOnly,
            AvailableInChat: true,
            AvailableInMcp: true,
            Execute: async (args, ctx, ct) =>
            {
                var results = await ctx.Memories.HybridSearchAsync(ReadHybridQuery(args), ct);
                return new ChatToolExecutionResult(FormatHybridResults(results), ContextItems: results.Select(ToMemoryContextItem).ToList());
            });

        yield return new ChatToolDescriptor(
            "memorysmith_context_pack",
            "Build an agent-ready context pack from hybrid search results plus linked references, conflicts, and optional backlinks.",
            BuildContextPackSchema(),
            ChatToolRisk.ReadOnly,
            AvailableInChat: true,
            AvailableInMcp: true,
            Execute: async (args, ctx, ct) =>
            {
                var pack = await ctx.Memories.BuildContextPackAsync(ReadContextPackQuery(args), ct);
                return new ChatToolExecutionResult(FormatContextPack(pack, ReadString(args, "format")), ContextItems: pack.Records.Select(ToMemoryContextItem).ToList());
            });

        yield return new ChatToolDescriptor(
            "memorysmith_get",
            "Fetch a single MemorySmith wiki record by id.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["id"] = new JsonObject { ["type"] = "string", ["description"] = "Memory record id." }
                },
                ["required"] = new JsonArray { "id" }
            },
            ChatToolRisk.ReadOnly,
            AvailableInChat: true,
            AvailableInMcp: true,
            Execute: async (args, ctx, ct) =>
            {
                var id = ReadString(args, "id");
                if (string.IsNullOrWhiteSpace(id))
                {
                    return new ChatToolExecutionResult("The memorysmith_get tool requires an id argument.", IsError: true);
                }
                var record = await ctx.Memories.GetAsync(id, ct);
                return record is null
                    ? new ChatToolExecutionResult($"No memory record found for id '{id}'.")
                    : new ChatToolExecutionResult(JsonSerializer.Serialize(record, ToolJsonOptions), ContextItems: [ToMemoryContextItem(record)]);
            });

        // ---------- New tools (Phase 2 of ChatCapabilityImprovements plan) ----------

        yield return new ChatToolDescriptor(
            "memorysmith_page_search",
            "Search markdown pages in the MemorySmith project wiki. Returns slug, title and short snippets.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["query"] = new JsonObject { ["type"] = "string", ["description"] = "Optional search text. When empty, returns most-recently-updated pages." },
                    ["limit"] = new JsonObject { ["type"] = "integer", ["description"] = "Maximum number of pages to return. Clamped 1-50, default 10." }
                }
            },
            ChatToolRisk.ReadOnly,
            AvailableInChat: true,
            AvailableInMcp: true,
            Execute: async (args, ctx, ct) =>
            {
                var query = ReadString(args, "query");
                var limit = Math.Clamp(ReadInt(args, "limit", 10), 1, 50);
                var pages = await ctx.Pages.SearchVisibleAsync(
                    query,
                    limit,
                    page => ctx.CanViewPage(page.MinimumRole),
                    ct);
                if (pages.Count == 0)
                {
                    return new ChatToolExecutionResult("No matching pages.");
                }
                var text = string.Join(Environment.NewLine + Environment.NewLine, pages.Select(page =>
                    $"- {page.Slug}: {page.Title}{Environment.NewLine}  Updated: {page.LastUpdatedUtc:O}{Environment.NewLine}  {page.Snippet}"));
                return new ChatToolExecutionResult(text, ContextItems: pages.Select(ToPageContextItem).ToList());
            });

        yield return new ChatToolDescriptor(
            "memorysmith_page_get",
            "Read one markdown page by slug, bounded by maxCharacters (default 4000, clamped 200-20000) for safe context inclusion.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["slug"] = new JsonObject { ["type"] = "string", ["description"] = "Page slug (path/segments, no extension)." },
                    ["maxCharacters"] = new JsonObject { ["type"] = "integer", ["description"] = "Max markdown characters to return. Clamped 200-20000, default 4000." }
                },
                ["required"] = new JsonArray { "slug" }
            },
            ChatToolRisk.ReadOnly,
            AvailableInChat: true,
            AvailableInMcp: true,
            Execute: async (args, ctx, ct) =>
            {
                var slug = ReadString(args, "slug");
                if (string.IsNullOrWhiteSpace(slug))
                {
                    return new ChatToolExecutionResult("The memorysmith_page_get tool requires a slug argument.", IsError: true);
                }
                PageDocument? page;
                try
                {
                    page = await ctx.Pages.GetAsync(slug, ct);
                }
                catch (ArgumentException ex)
                {
                    return new ChatToolExecutionResult(ex.Message, IsError: true);
                }
                if (page is null)
                {
                    return new ChatToolExecutionResult($"No page found for slug '{slug}'.");
                }
                if (!ctx.CanViewPage(page.MinimumRole))
                {
                    return new ChatToolExecutionResult($"No page found for slug '{slug}'.");
                }

                var maxChars = Math.Clamp(ReadInt(args, "maxCharacters", 4000), 200, 20000);
                var markdown = page.Markdown.Length <= maxChars
                    ? page.Markdown
                    : page.Markdown[..maxChars] + "...";
                var truncatedNote = page.Markdown.Length > maxChars
                    ? $"{Environment.NewLine}(truncated from {page.Markdown.Length} chars at {maxChars} char limit)"
                    : string.Empty;
                var text = $"# {page.Title}{Environment.NewLine}Slug: {page.Slug}{Environment.NewLine}Updated: {page.LastUpdatedUtc:O}{truncatedNote}{Environment.NewLine}{Environment.NewLine}{markdown}";
                return new ChatToolExecutionResult(text, ContextItems: [ToPageContextItem(page, markdown)]);
            });

        yield return new ChatToolDescriptor(
            "memorysmith_unified_search",
            "Combined search across memories (hybrid) and markdown pages. Recommended for natural-language 'search the wiki' requests.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["query"] = new JsonObject { ["type"] = "string", ["description"] = "Search text." },
                    ["memoryLimit"] = new JsonObject { ["type"] = "integer", ["description"] = "Max memory results. Clamped 0-20, default 5." },
                    ["pageLimit"] = new JsonObject { ["type"] = "integer", ["description"] = "Max page results. Clamped 0-20, default 5." },
                    ["tags"] = new JsonObject { ["type"] = "string", ["description"] = "Optional comma-separated tag filter (memories only)." },
                    ["status"] = new JsonObject { ["type"] = "string", ["description"] = "Optional memory status name." }
                },
                ["required"] = new JsonArray { "query" }
            },
            ChatToolRisk.ReadOnly,
            AvailableInChat: true,
            AvailableInMcp: true,
            Execute: async (args, ctx, ct) =>
            {
                var query = ReadString(args, "query");
                if (string.IsNullOrWhiteSpace(query))
                {
                    return new ChatToolExecutionResult("The memorysmith_unified_search tool requires a query argument.", IsError: true);
                }
                var memoryLimit = Math.Clamp(ReadInt(args, "memoryLimit", 5), 0, 20);
                var pageLimit = Math.Clamp(ReadInt(args, "pageLimit", 5), 0, 20);
                var memoryTask = memoryLimit == 0
                    ? Task.FromResult<IReadOnlyList<MemorySearchResult>>(Array.Empty<MemorySearchResult>())
                    : ctx.Memories.HybridSearchAsync(new HybridMemorySearchQuery(query, ReadStatus(args), ReadString(args, "tags"), memoryLimit), ct);
                var pageTask = pageLimit == 0
                    ? Task.FromResult<IReadOnlyList<PageSummary>>(Array.Empty<PageSummary>())
                    : ctx.Pages.SearchVisibleAsync(query, pageLimit, page => ctx.CanViewPage(page.MinimumRole), ct);
                await Task.WhenAll(memoryTask, pageTask);
                var memoryResults = await memoryTask;
                var pageResults = (await pageTask).ToList();
                var sb = new System.Text.StringBuilder();
                sb.Append("Unified MemorySmith search results for: ").AppendLine(query);
                sb.AppendLine();
                sb.Append("Memories (").Append(memoryResults.Count).AppendLine("):");
                if (memoryResults.Count == 0)
                {
                    sb.AppendLine("- (none)");
                }
                else
                {
                    foreach (var memory in memoryResults)
                    {
                        sb.Append("- ").Append(memory.Id).Append(": ").AppendLine(memory.Title);
                        sb.Append("  Score: ").Append(memory.Score.ToString("0.######")).Append("  Tags: ").AppendLine(string.Join(", ", memory.Tags));
                        sb.Append("  ").AppendLine(memory.Snippet);
                    }
                }
                sb.AppendLine();
                sb.Append("Pages (").Append(pageResults.Count).AppendLine("):");
                if (pageResults.Count == 0)
                {
                    sb.AppendLine("- (none)");
                }
                else
                {
                    foreach (var page in pageResults)
                    {
                        sb.Append("- ").Append(page.Slug).Append(": ").AppendLine(page.Title);
                        sb.Append("  Updated: ").AppendLine(page.LastUpdatedUtc.ToString("O"));
                        sb.Append("  ").AppendLine(page.Snippet);
                    }
                }
                var contextItems = memoryResults.Select(ToMemoryContextItem)
                    .Concat(pageResults.Select(ToPageContextItem))
                    .ToList();
                return new ChatToolExecutionResult(sb.ToString().TrimEnd(), ContextItems: contextItems);
            });

        yield return new ChatToolDescriptor(
            "memorysmith_page_save",
            "Create or update a wiki page. Slug is derived from the title if omitted. Requires edit permission.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["markdown"] = new JsonObject { ["type"] = "string", ["description"] = "Full markdown content of the page." },
                    ["slug"] = new JsonObject { ["type"] = "string", ["description"] = "Optional slug. Omit to auto-derive from title or first heading." },
                    ["title"] = new JsonObject { ["type"] = "string", ["description"] = "Optional explicit title. Overrides the first heading in the markdown." },
                    ["minimumRole"] = new JsonObject { ["type"] = "string", ["description"] = "Optional page visibility: Anonymous, Authenticated, or Admin." }
                },
                ["required"] = new JsonArray { "markdown" }
            },
            ChatToolRisk.Write,
            AvailableInChat: false,
            AvailableInMcp: true,
            Execute: async (args, ctx, ct) =>
            {
                var markdown = ReadString(args, "markdown");
                if (string.IsNullOrWhiteSpace(markdown))
                {
                    return new ChatToolExecutionResult("The memorysmith_page_save tool requires a markdown argument.", IsError: true);
                }
                var slug = ReadString(args, "slug");
                var title = ReadString(args, "title");
                var minimumRole = ReadString(args, "minimumRole");
                string? normalizedMinimumRole = null;
                if (!string.IsNullOrWhiteSpace(minimumRole))
                {
                    if (!PageAccessLevels.TryNormalize(minimumRole, out normalizedMinimumRole))
                    {
                        return new ChatToolExecutionResult("Choose Anonymous, Authenticated, or Admin for page visibility.", IsError: true);
                    }
                }

                PageDocument? existing = null;
                if (!string.IsNullOrWhiteSpace(slug))
                {
                    existing = await ctx.Pages.GetAsync(slug, ct);
                    if (existing is not null && !ctx.CanViewPage(existing.MinimumRole))
                    {
                        return new ChatToolExecutionResult($"No page found for slug '{slug}'.", IsError: true);
                    }
                }

                var resolvedMinimumRole = PageAccessLevels.ResolveStoredMinimumRole(
                    normalizedMinimumRole,
                    existing?.MinimumRole,
                    ctx.DefaultPageMinimumRole ?? PageAccessLevels.Anonymous);

                if ((existing is null || !string.Equals(existing.MinimumRole, resolvedMinimumRole, StringComparison.OrdinalIgnoreCase))
                    && string.Equals(resolvedMinimumRole, PageAccessLevels.Admin, StringComparison.OrdinalIgnoreCase)
                    && !ctx.CanSetPageMinimumRole(PageAccessLevels.Admin))
                {
                    return new ChatToolExecutionResult("The caller is not authorized to set that page visibility.", IsError: true);
                }

                var saved = await ctx.Pages.SaveAsync(new PageSaveRequest(slug, title, markdown, resolvedMinimumRole), ct);
                return new ChatToolExecutionResult($"Page saved. Slug: {saved.Slug}  Title: {saved.Title}  Updated: {saved.LastUpdatedUtc:O}");
            });

        yield return new ChatToolDescriptor(
            "memorysmith_page_delete",
            "Delete a wiki page by slug. Requires edit permission.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["slug"] = new JsonObject { ["type"] = "string", ["description"] = "Slug of the page to delete." }
                },
                ["required"] = new JsonArray { "slug" }
            },
            ChatToolRisk.Write,
            AvailableInChat: false,
            AvailableInMcp: true,
            Execute: async (args, ctx, ct) =>
            {
                var slug = ReadString(args, "slug");
                if (string.IsNullOrWhiteSpace(slug))
                {
                    return new ChatToolExecutionResult("The memorysmith_page_delete tool requires a slug argument.", IsError: true);
                }
                var existing = await ctx.Pages.GetAsync(slug, ct);
                if (existing is not null && !ctx.CanViewPage(existing.MinimumRole))
                {
                    return new ChatToolExecutionResult($"No page found with slug '{slug}'.", IsError: true);
                }

                var deleted = await ctx.Pages.DeleteAsync(slug, ct);
                return new ChatToolExecutionResult(deleted
                    ? $"Page '{slug}' deleted."
                    : $"No page found with slug '{slug}'.");
            });
    }

    // ---------- Schema builders ----------

    private static JsonObject BuildSearchSchema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["query"] = new JsonObject { ["type"] = "string", ["description"] = "Search text." },
            ["tags"] = new JsonObject { ["type"] = "string", ["description"] = "Optional comma-separated tag filter." },
            ["status"] = new JsonObject { ["type"] = "string", ["description"] = "Optional memory status name." },
            ["limit"] = new JsonObject { ["type"] = "integer", ["description"] = "Maximum number of results." }
        }
    };

    private static JsonObject BuildContextPackSchema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["query"] = new JsonObject { ["type"] = "string", ["description"] = "Search text used to seed the context pack with hybrid search." },
            ["ids"] = new JsonObject { ["type"] = "string", ["description"] = "Optional comma-separated root memory ids to include before search results." },
            ["tags"] = new JsonObject { ["type"] = "string", ["description"] = "Optional comma-separated tag filter." },
            ["status"] = new JsonObject { ["type"] = "string", ["description"] = "Optional memory status name." },
            ["limit"] = new JsonObject { ["type"] = "integer", ["description"] = "Maximum number of hybrid root results." },
            ["referenceDepth"] = new JsonObject { ["type"] = "integer", ["description"] = "How many levels of references/conflicts to include. Clamped to 0-2." },
            ["maxContentChars"] = new JsonObject { ["type"] = "integer", ["description"] = "Maximum content characters per record. Clamped to 200-6000." },
            ["maxRecords"] = new JsonObject { ["type"] = "integer", ["description"] = "Maximum total records in the context pack. Clamped to 1-100." },
            ["includeBacklinks"] = new JsonObject { ["type"] = "boolean", ["description"] = "Include records that reference or conflict with packed records." },
            ["format"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "Output format. Use json for structured agent parsing; defaults to markdown.",
                ["enum"] = new JsonArray { "markdown", "json" }
            }
        }
    };

    // ---------- Argument readers ----------

    public static MemorySearchQuery ReadLexicalQuery(JsonObject arguments) => new(
        Query: ReadString(arguments, "query"),
        Status: ReadStatus(arguments),
        Tags: ReadString(arguments, "tags"),
        Limit: ReadInt(arguments, "limit", 20));

    public static SemanticMemorySearchQuery ReadSemanticQuery(JsonObject arguments) => new(
        Query: ReadString(arguments, "query"),
        Status: ReadStatus(arguments),
        Tags: ReadString(arguments, "tags"),
        Limit: ReadInt(arguments, "limit", 20));

    public static HybridMemorySearchQuery ReadHybridQuery(JsonObject arguments) => new(
        Query: ReadString(arguments, "query"),
        Status: ReadStatus(arguments),
        Tags: ReadString(arguments, "tags"),
        Limit: ReadInt(arguments, "limit", 20));

    public static MemoryContextPackQuery ReadContextPackQuery(JsonObject arguments) => new(
        Query: ReadString(arguments, "query"),
        Status: ReadStatus(arguments),
        Tags: ReadString(arguments, "tags"),
        Limit: ReadInt(arguments, "limit", 5),
        ReferenceDepth: ReadInt(arguments, "referenceDepth", 1),
        MaxContentChars: ReadInt(arguments, "maxContentChars", 1200),
        MaxRecords: ReadInt(arguments, "maxRecords", 20),
        Ids: ReadString(arguments, "ids"),
        IncludeBacklinks: ReadBool(arguments, "includeBacklinks", false));

    public static string? ReadString(JsonObject item, string name) =>
        GetProperty(item, name) is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    public static int ReadInt(JsonObject item, string name, int fallback)
    {
        if (GetProperty(item, name) is not JsonValue value) return fallback;
        if (value.TryGetValue<int>(out var number)) return number;
        return value.TryGetValue<string>(out var text) && int.TryParse(text, out number) ? number : fallback;
    }

    public static bool ReadBool(JsonObject item, string name, bool fallback)
    {
        if (GetProperty(item, name) is not JsonValue value) return fallback;
        if (value.TryGetValue<bool>(out var boolean)) return boolean;
        return value.TryGetValue<string>(out var text) && bool.TryParse(text, out boolean) ? boolean : fallback;
    }

    public static MemoryStatus? ReadStatus(JsonObject item) =>
        Enum.TryParse<MemoryStatus>(ReadString(item, "status"), ignoreCase: true, out var status) ? status : null;

    public static JsonNode? GetProperty(JsonObject item, string name)
    {
        foreach (var property in item)
        {
            if (string.Equals(property.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value;
            }
        }
        return null;
    }

    // ---------- Result formatters ----------

    private static ChatContextItem ToMemoryContextItem(MemoryRecord record) =>
        new("memory", record.Id, record.Title, Truncate(record.Content, 320), ChatContextOrigins.Tool);

    private static ChatContextItem ToMemoryContextItem(MemorySearchResult result) =>
        new("memory", result.Id, result.Title, Truncate(result.Snippet, 320), ChatContextOrigins.Tool);

    private static ChatContextItem ToMemoryContextItem(MemoryContextPackRecord record) =>
        new("memory", record.Id, record.Title, Truncate(record.Content, 320), ChatContextOrigins.Tool);

    private static ChatContextItem ToPageContextItem(PageSummary page) =>
        new("page", page.Slug, page.Title, Truncate(page.Snippet, 320), ChatContextOrigins.Tool);

    private static ChatContextItem ToPageContextItem(PageDocument page, string markdown) =>
        new("page", page.Slug, page.Title, Truncate(markdown, 320), ChatContextOrigins.Tool);

    public static string FormatLexicalResults(IReadOnlyList<MemoryRecord> records)
    {
        if (records.Count == 0) return "No lexical search results.";
        return string.Join(Environment.NewLine + Environment.NewLine, records.Select(record =>
            $"- {record.Id}: {record.Title}{Environment.NewLine}  Tags: {string.Join(", ", record.Tags)}{Environment.NewLine}  {Truncate(record.Content, 320)}"));
    }

    public static string FormatSemanticResults(IReadOnlyList<MemorySearchResult> results)
    {
        if (results.Count == 0) return "No semantic search results.";
        return string.Join(Environment.NewLine + Environment.NewLine, results.Select(result =>
            $"- {result.Id}: {result.Title}{Environment.NewLine}  Score: {result.Score:0.###}{Environment.NewLine}  Match: {result.MatchReason}{Environment.NewLine}  Tags: {string.Join(", ", result.Tags)}{Environment.NewLine}  {result.Snippet}"));
    }

    public static string FormatHybridResults(IReadOnlyList<MemorySearchResult> results)
    {
        if (results.Count == 0) return "No hybrid search results.";
        return string.Join(Environment.NewLine + Environment.NewLine, results.Select(result =>
            $"- {result.Id}: {result.Title}{Environment.NewLine}  RRF Score: {result.Score:0.######}{Environment.NewLine}  Match: {result.MatchReason}{Environment.NewLine}  Tags: {string.Join(", ", result.Tags)}{Environment.NewLine}  {result.Snippet}"));
    }

    public static string FormatContextPack(MemoryContextPack pack, string? format)
    {
        if (string.Equals(format, "json", StringComparison.OrdinalIgnoreCase))
        {
            return JsonSerializer.Serialize(pack, ToolJsonOptions);
        }
        var warnings = pack.Warnings.Count == 0
            ? string.Empty
            : $"{Environment.NewLine}Warnings:{Environment.NewLine}" + string.Join(Environment.NewLine, pack.Warnings.Select(warning => $"- {warning}")) + Environment.NewLine;
        if (pack.Records.Count == 0)
        {
            return $"# Context Pack{Environment.NewLine}Query: {pack.Query ?? string.Empty}{Environment.NewLine}Generated: {pack.GeneratedAt:O}{warnings}{Environment.NewLine}No context pack records.";
        }
        var sections = pack.Records.Select(record =>
        {
            var scoreLine = record.Score.HasValue ? $"Score: {record.Score:0.######}" : "Score: linked context";
            var matchLine = string.IsNullOrWhiteSpace(record.MatchReason) ? string.Empty : $"Match: {record.MatchReason}{Environment.NewLine}";
            return $"## {record.Id}: {record.Title}{Environment.NewLine}" +
                   $"Relationship: {record.Relationship}{Environment.NewLine}" +
                   $"Status: {record.Status}; Confidence: {record.Confidence:P0}; Uses: {record.UsageCount}{Environment.NewLine}" +
                   $"Tags: {string.Join(", ", record.Tags)}{Environment.NewLine}" +
                   $"References: {FormatLinks(record.References)}{Environment.NewLine}" +
                   $"Conflicts: {FormatLinks(record.Conflicts)}{Environment.NewLine}" +
                   $"{scoreLine}{Environment.NewLine}" +
                   matchLine +
                   record.Content;
        });
        return $"# Context Pack{Environment.NewLine}Query: {pack.Query ?? string.Empty}{Environment.NewLine}Generated: {pack.GeneratedAt:O}{warnings}{Environment.NewLine}" +
            string.Join(Environment.NewLine + Environment.NewLine, sections);
    }

    private static string FormatLinks(IReadOnlyList<string> values) =>
        values.Count == 0 ? "none" : string.Join(", ", values);

    public static string Truncate(string? value, int maxCharacters)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var text = value.Trim();
        return text.Length <= maxCharacters ? text : text[..maxCharacters].TrimEnd() + "...";
    }
}
