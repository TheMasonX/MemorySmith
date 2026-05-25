using System.Globalization;
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
    bool EnabledByDefaultInMcp = true,
    bool AvailableInAgent = false);

public sealed record ChatToolExecutionContext(
    MemoryApplicationService Memories,
    IPageService Pages,
    string Transport,
    ClaimsPrincipal? User = null,
    ICurrentUserContext? CurrentUser = null,
    AuthOptions? Auth = null,
    string? DefaultPageMinimumRole = null,
    VarResolver? Vars = null,
    ITaskService? Tasks = null)
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
/// Single source of truth for the tool surface used by both the JSON-RPC /mcp endpoint
/// and the in-chat application-intercepted tool protocol.
/// </summary>
public sealed class ChatToolCatalog
{
    public static readonly JsonSerializerOptions ToolJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly Dictionary<string, ChatToolDescriptor> _tools;

    public ChatToolCatalog()
    {
        _tools = BuildTools().ToDictionary(tool => tool.Name, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<ChatToolDescriptor> All => _tools.Values.ToList();

    public IReadOnlyList<ChatToolDescriptor> ChatTools =>
        ToolsForMode(MemoryChatMode.Chat);

    public IReadOnlyList<ChatToolDescriptor> AgentTools =>
        ToolsForMode(MemoryChatMode.Agent);

    public IReadOnlyList<ChatToolDescriptor> McpTools =>
        _tools.Values.Where(tool => tool.AvailableInMcp).ToList();

    public IReadOnlyList<ChatToolDescriptor> ToolsForMode(MemoryChatMode mode) =>
        _tools.Values.Where(tool => IsAvailableInMode(tool, mode)).ToList();

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
        IsAvailableInMode(name, MemoryChatMode.Chat);

    public bool IsAvailableInMode(string name, MemoryChatMode mode) =>
        TryGet(name, out var tool) && IsAvailableInMode(tool, mode);

    public static bool IsAvailableInMode(ChatToolDescriptor tool, MemoryChatMode mode) =>
        mode == MemoryChatMode.Agent
            ? tool.AvailableInChat || tool.AvailableInAgent
            : tool.AvailableInChat;

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
                var structuredFormat = IsStructuredFormat(ReadString(args, "format"));
                var results = await ctx.Memories.LexicalSearchAsync(ReadLexicalQuery(args), ct);
                var envelope = ctx.Memories.BuildRetrievalEnvelope("lexical", MemoryApplicationService.GetLexicalProviderMetadata(), results);
                if (structuredFormat)
                {
                    return BuildRetrievalToolResult(envelope);
                }

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
                var structuredFormat = IsStructuredFormat(ReadString(args, "format"));
                var results = await ctx.Memories.SemanticSearchAsync(ReadSemanticQuery(args), ct);
                var envelope = ctx.Memories.BuildRetrievalEnvelope("semantic", ctx.Memories.GetSemanticProviderMetadata(), results);
                if (structuredFormat)
                {
                    return BuildRetrievalToolResult(envelope);
                }

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
                var structuredFormat = IsStructuredFormat(ReadString(args, "format"));
                var results = await ctx.Memories.HybridSearchAsync(ReadHybridQuery(args), ct);
                var envelope = ctx.Memories.BuildRetrievalEnvelope("hybrid", ctx.Memories.GetSemanticProviderMetadata(), results);
                if (structuredFormat)
                {
                    return BuildRetrievalToolResult(envelope);
                }

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

        yield return new ChatToolDescriptor(
            "memorysmith_source_bundle",
            "Read the source file content for all source links attached to the specified memory records. Useful for fetching the exact code or document sections that KB entries reference. Returns URL references as-is (unfetchable server-side). Use format=jsonl for streaming-friendly large bundles.",
            BuildSourceBundleSchema(),
            ChatToolRisk.SensitiveRead,
            AvailableInChat: false,
            AvailableInMcp: true,
            Execute: async (args, ctx, ct) =>
            {
                if (ctx.Vars is null)
                {
                    return new ChatToolExecutionResult("The memorysmith_source_bundle tool requires a source resolver.", IsError: true);
                }

                var maxFileBytes = Math.Clamp(ReadInt(args, "maxFileBytes", 16384), 1, 1048576);
                var format = ReadString(args, "format") ?? "json";
                var records = new List<MemoryRecord>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                var ids = ReadString(args, "ids");
                if (!string.IsNullOrWhiteSpace(ids))
                {
                    foreach (var id in ids.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (!seen.Add(id))
                        {
                            continue;
                        }

                        var record = await ctx.Memories.GetAsync(id, ct);
                        if (record is not null)
                        {
                            records.Add(record);
                        }
                    }
                }

                var query = ReadString(args, "query");
                if (!string.IsNullOrWhiteSpace(query))
                {
                    var limit = ReadInt(args, "limit", 10);
                    var searchResults = await ctx.Memories.HybridSearchAsync(
                        new HybridMemorySearchQuery(query, ReadStatus(args), ReadString(args, "tags"), limit), ct);

                    foreach (var result in searchResults)
                    {
                        if (!seen.Add(result.Id))
                        {
                            continue;
                        }

                        var record = await ctx.Memories.GetAsync(result.Id, ct);
                        if (record is not null)
                        {
                            records.Add(record);
                        }
                    }
                }

                if (records.Count == 0)
                {
                    return new ChatToolExecutionResult("No records found. Provide ids or a query.");
                }

                var entries = new List<object>();
                foreach (var record in records)
                {
                    foreach (var sl in record.SourceLinks)
                    {
                        var content = await ctx.Vars.ReadSourceAsync(sl, maxFileBytes);
                        entries.Add(new
                        {
                            MemoryId = record.Id,
                            MemoryTitle = record.Title,
                            Label = string.IsNullOrWhiteSpace(sl.Label) ? content.ResolvedUri : sl.Label,
                            RawUri = sl.Uri,
                            ResolvedUri = content.ResolvedUri,
                            ContentType = content.ContentType,
                            StartLine = content.StartLine,
                            EndLine = content.EndLine,
                            Exists = content.Exists,
                            Content = content.Content
                        });
                    }
                }

                if (entries.Count == 0)
                {
                    return new ChatToolExecutionResult($"Found {records.Count} record(s) but none have source links.", ContextItems: records.Select(ToMemoryContextItem).ToList());
                }

                if (string.Equals(format, "jsonl", StringComparison.OrdinalIgnoreCase))
                {
                    return new ChatToolExecutionResult(string.Join('\n', entries.Select(e => JsonSerializer.Serialize(e, ToolJsonOptions))), ContextItems: records.Select(ToMemoryContextItem).ToList());
                }

                return new ChatToolExecutionResult(JsonSerializer.Serialize(new
                {
                    MemoryCount = records.Count,
                    SourceCount = entries.Count,
                    Entries = entries
                }, ToolJsonOptions), ContextItems: records.Select(ToMemoryContextItem).ToList());
            });

        yield return new ChatToolDescriptor(
            "memorysmith_find_by_source",
            "Back-map a source path or URL fragment to every KB entry that references it. Matches against both raw and resolved (variable-expanded) URIs.",
            BuildFindBySourceSchema(),
            ChatToolRisk.SensitiveRead,
            AvailableInChat: false,
            AvailableInMcp: true,
            Execute: async (args, ctx, ct) =>
            {
                if (ctx.Vars is null)
                {
                    return new ChatToolExecutionResult("The memorysmith_find_by_source tool requires a source resolver.", IsError: true);
                }

                var pattern = ReadString(args, "pattern");
                if (string.IsNullOrWhiteSpace(pattern))
                {
                    return new ChatToolExecutionResult("The memorysmith_find_by_source tool requires a pattern argument.", IsError: true);
                }

                var matches = await ctx.Memories.FindBySourceAsync(pattern, ctx.Vars.Resolve, ct);
                if (matches.Count == 0)
                {
                    return new ChatToolExecutionResult($"No memory records found with source links matching '{pattern}'.");
                }

                var result = matches.Select(r => new
                {
                    r.Id,
                    r.Title,
                    r.Status,
                    MatchingLinks = r.SourceLinks
                        .Where(sl =>
                            sl.Uri.Contains(pattern, StringComparison.OrdinalIgnoreCase) ||
                            ctx.Vars.Resolve(sl.Uri).Contains(pattern, StringComparison.OrdinalIgnoreCase))
                        .Select(sl => new
                        {
                            sl.Label,
                            sl.Uri,
                            ResolvedUri = ctx.Vars.Resolve(sl.Uri),
                            sl.StartLine,
                            sl.EndLine
                        })
                        .ToList()
                }).ToList();

                return new ChatToolExecutionResult(JsonSerializer.Serialize(result, ToolJsonOptions), ContextItems: matches.Select(ToMemoryContextItem).ToList());
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
                    ["status"] = new JsonObject { ["type"] = "string", ["description"] = "Optional memory status name." },
                    ["format"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Output format. Defaults to markdown; use json or envelope for structured agent parsing.",
                        ["enum"] = new JsonArray { "markdown", "json", "envelope" }
                    }
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
                        if (memory.Diagnostics.Count > 0)
                        {
                            sb.Append("  Diagnostics: ").AppendLine(string.Join("; ", memory.Diagnostics.Take(3).Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")));
                        }
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
                if (IsStructuredFormat(ReadString(args, "format")))
                {
                    var payload = new
                    {
                        SchemaVersion = "memorysmith.unified-search.v1",
                        Query = query,
                        MemoryProvider = ctx.Memories.GetSemanticProviderMetadata(),
                        PageProvider = new RetrievalProviderMetadata("page", "markdown-lexical", true, "Markdown page metadata and body search."),
                        Memories = memoryResults,
                        Pages = pageResults,
                        Warnings = MemoryDiagnosticFormatting.ToWarningSummaries(memoryResults)
                    };
                    var node = JsonSerializer.SerializeToNode(payload, ToolJsonOptions);
                    return new ChatToolExecutionResult(node!.ToJsonString(ToolJsonOptions), ContextItems: contextItems, Structured: node);
                }
                return new ChatToolExecutionResult(sb.ToString().TrimEnd(), ContextItems: contextItems);
            });

        yield return new ChatToolDescriptor(
            "memorysmith_task_list",
            "List MemorySmith tasks by query, status, assignee, and limit.",
            BuildTaskListSchema(),
            ChatToolRisk.ReadOnly,
            AvailableInChat: true,
            AvailableInMcp: true,
            Execute: async (args, ctx, ct) =>
            {
                if (ctx.Tasks is null)
                {
                    return MissingTaskServiceResult("memorysmith_task_list");
                }

                var limit = Math.Clamp(ReadInt(args, "limit", 25), 1, 100);
                var tasks = await ctx.Tasks.ListAsync(ReadString(args, "query"), ReadString(args, "status"), ReadString(args, "assignee"), limit, ct);
                return JsonToolResult(new { Tasks = tasks });
            });

        yield return new ChatToolDescriptor(
            "memorysmith_task_get",
            "Fetch one MemorySmith task by id or key.",
            BuildTaskIdSchema(),
            ChatToolRisk.ReadOnly,
            AvailableInChat: true,
            AvailableInMcp: true,
            Execute: async (args, ctx, ct) =>
            {
                if (ctx.Tasks is null)
                {
                    return MissingTaskServiceResult("memorysmith_task_get");
                }

                var idOrKey = ReadString(args, "idOrKey");
                if (string.IsNullOrWhiteSpace(idOrKey))
                {
                    return new ChatToolExecutionResult("The memorysmith_task_get tool requires an idOrKey argument.", IsError: true);
                }

                var task = await ctx.Tasks.GetAsync(idOrKey, ct);
                return task is null
                    ? new ChatToolExecutionResult($"No task found for id or key '{idOrKey}'.")
                    : JsonToolResult(new { Task = task });
            });

        yield return new ChatToolDescriptor(
            "memorysmith_task_create",
            "Create a MemorySmith task. Requires edit permission.",
            BuildTaskCreateSchema(),
            ChatToolRisk.Write,
            AvailableInChat: false,
            AvailableInMcp: true,
            Execute: async (args, ctx, ct) =>
            {
                if (ctx.Tasks is null)
                {
                    return MissingTaskServiceResult("memorysmith_task_create");
                }

                try
                {
                    var actor = Actor(ctx);
                    var task = await ctx.Tasks.CreateAsync(new TaskCreateRequest(
                        Title: ReadString(args, "title") ?? string.Empty,
                        Description: ReadString(args, "description") ?? string.Empty,
                        Type: ReadString(args, "type") ?? "Task",
                        Status: ReadString(args, "status") ?? TaskStatuses.Backlog,
                        Priority: ReadString(args, "priority") ?? TaskPriorities.Medium,
                        AssigneeMode: ReadString(args, "assigneeMode") ?? TaskAssigneeModes.Custom,
                        AssigneeDirectoryId: ReadString(args, "assigneeDirectoryId"),
                        AssigneeCustomText: ReadString(args, "assigneeCustomText") ?? "Agent",
                        Reporter: ReadString(args, "reporter") ?? actor,
                        Labels: ReadStringList(args, "labels"),
                        DueDateUtc: ReadNullableDateTime(args, "dueDateUtc", null),
                        EpicId: ReadString(args, "epicId"),
                        ParentId: ReadString(args, "parentId"),
                        Slug: ReadString(args, "slug")), actor, ct);
                    return JsonToolResult(new { Message = "Task created.", Task = task });
                }
                catch (ArgumentException ex)
                {
                    return new ChatToolExecutionResult(ex.Message, IsError: true);
                }
            },
            AvailableInAgent: true);

        yield return new ChatToolDescriptor(
            "memorysmith_task_update",
            "Update editable MemorySmith task fields, including priority and assignment. Requires edit permission.",
            BuildTaskUpdateSchema(),
            ChatToolRisk.Write,
            AvailableInChat: false,
            AvailableInMcp: true,
            Execute: async (args, ctx, ct) =>
            {
                if (ctx.Tasks is null)
                {
                    return MissingTaskServiceResult("memorysmith_task_update");
                }

                var idOrKey = ReadString(args, "idOrKey");
                if (string.IsNullOrWhiteSpace(idOrKey))
                {
                    return new ChatToolExecutionResult("The memorysmith_task_update tool requires an idOrKey argument.", IsError: true);
                }

                try
                {
                    var existing = await ctx.Tasks.GetAsync(idOrKey, ct);
                    if (existing is null)
                    {
                        return new ChatToolExecutionResult($"No task found for id or key '{idOrKey}'.");
                    }

                    var updated = await ctx.Tasks.UpdateAsync(idOrKey, new TaskUpdateRequest(
                        Title: ReadString(args, "title"),
                        Description: ReadString(args, "description"),
                        Type: ReadString(args, "type"),
                        Priority: ReadString(args, "priority"),
                        AssigneeMode: ReadString(args, "assigneeMode"),
                        AssigneeDirectoryId: ReadString(args, "assigneeDirectoryId"),
                        AssigneeCustomText: ReadString(args, "assigneeCustomText"),
                        Reporter: ReadString(args, "reporter"),
                        Labels: ReadStringList(args, "labels"),
                        DueDateUtc: ReadNullableDateTime(args, "dueDateUtc", existing.DueDateUtc),
                        EpicId: ReadString(args, "epicId"),
                        ParentId: ReadString(args, "parentId")), Actor(ctx), ct);
                    return updated is null
                        ? new ChatToolExecutionResult($"No task found for id or key '{idOrKey}'.")
                        : JsonToolResult(new { Message = "Task updated.", Task = updated });
                }
                catch (ArgumentException ex)
                {
                    return new ChatToolExecutionResult(ex.Message, IsError: true);
                }
            },
            AvailableInAgent: true);

        yield return new ChatToolDescriptor(
            "memorysmith_task_set_status",
            "Change a MemorySmith task status and optionally record a note. Requires edit permission.",
            BuildTaskSetStatusSchema(),
            ChatToolRisk.Write,
            AvailableInChat: false,
            AvailableInMcp: true,
            Execute: async (args, ctx, ct) =>
            {
                if (ctx.Tasks is null)
                {
                    return MissingTaskServiceResult("memorysmith_task_set_status");
                }

                var idOrKey = ReadString(args, "idOrKey");
                if (string.IsNullOrWhiteSpace(idOrKey))
                {
                    return new ChatToolExecutionResult("The memorysmith_task_set_status tool requires an idOrKey argument.", IsError: true);
                }

                try
                {
                    var updated = await ctx.Tasks.SetStatusAsync(idOrKey, new TaskStatusUpdateRequest(ReadString(args, "status") ?? string.Empty, ReadString(args, "note")), Actor(ctx), ct);
                    return updated is null
                        ? new ChatToolExecutionResult($"No task found for id or key '{idOrKey}'.")
                        : JsonToolResult(new { Message = "Task status updated.", Task = updated });
                }
                catch (ArgumentException ex)
                {
                    return new ChatToolExecutionResult(ex.Message, IsError: true);
                }
            },
            AvailableInAgent: true);

        yield return new ChatToolDescriptor(
            "memorysmith_task_add_comment",
            "Add a comment to a MemorySmith task. Requires edit permission.",
            BuildTaskCommentSchema(),
            ChatToolRisk.Write,
            AvailableInChat: false,
            AvailableInMcp: true,
            Execute: async (args, ctx, ct) =>
            {
                if (ctx.Tasks is null)
                {
                    return MissingTaskServiceResult("memorysmith_task_add_comment");
                }

                var idOrKey = ReadString(args, "idOrKey");
                if (string.IsNullOrWhiteSpace(idOrKey))
                {
                    return new ChatToolExecutionResult("The memorysmith_task_add_comment tool requires an idOrKey argument.", IsError: true);
                }

                try
                {
                    var updated = await ctx.Tasks.AddCommentAsync(idOrKey, new TaskCommentRequest(ReadString(args, "body") ?? string.Empty), Actor(ctx), ct);
                    return updated is null
                        ? new ChatToolExecutionResult($"No task found for id or key '{idOrKey}'.")
                        : JsonToolResult(new { Message = "Task comment added.", Task = updated });
                }
                catch (ArgumentException ex)
                {
                    return new ChatToolExecutionResult(ex.Message, IsError: true);
                }
            },
            AvailableInAgent: true);

        yield return new ChatToolDescriptor(
            "memorysmith_task_add_attachment",
            "Attach an absolute http/https artifact URI to a MemorySmith task. Requires edit permission.",
            BuildTaskAttachmentSchema(),
            ChatToolRisk.Write,
            AvailableInChat: false,
            AvailableInMcp: true,
            Execute: async (args, ctx, ct) =>
            {
                if (ctx.Tasks is null)
                {
                    return MissingTaskServiceResult("memorysmith_task_add_attachment");
                }

                var idOrKey = ReadString(args, "idOrKey");
                if (string.IsNullOrWhiteSpace(idOrKey))
                {
                    return new ChatToolExecutionResult("The memorysmith_task_add_attachment tool requires an idOrKey argument.", IsError: true);
                }

                try
                {
                    var updated = await ctx.Tasks.AddAttachmentAsync(idOrKey, new TaskAttachmentRequest(
                        ReadString(args, "name") ?? string.Empty,
                        ReadString(args, "kind") ?? "file",
                        ReadString(args, "uri") ?? string.Empty), Actor(ctx), ct);
                    return updated is null
                        ? new ChatToolExecutionResult($"No task found for id or key '{idOrKey}'.")
                        : JsonToolResult(new { Message = "Task attachment added.", Task = updated });
                }
                catch (ArgumentException ex)
                {
                    return new ChatToolExecutionResult(ex.Message, IsError: true);
                }
            },
            AvailableInAgent: true);

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
            },
            AvailableInAgent: true);

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
            },
            AvailableInAgent: true);
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
            ["limit"] = new JsonObject { ["type"] = "integer", ["description"] = "Maximum number of results." },
            ["format"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "Output format. Defaults to markdown; use json or envelope for structured agent parsing.",
                ["enum"] = new JsonArray { "markdown", "json", "envelope" }
            }
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

    private static JsonObject BuildSourceBundleSchema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["ids"] = new JsonObject { ["type"] = "string", ["description"] = "Comma-separated memory record ids to fetch sources for." },
            ["query"] = new JsonObject { ["type"] = "string", ["description"] = "Optional search query; matching records' sources are included." },
            ["tags"] = new JsonObject { ["type"] = "string", ["description"] = "Optional comma-separated tag filter for the query." },
            ["limit"] = new JsonObject { ["type"] = "integer", ["description"] = "Max hybrid-search results when query is provided. Default 10." },
            ["maxFileBytes"] = new JsonObject { ["type"] = "integer", ["description"] = "Max bytes per file content entry. Default 16384; clamped by source-link policy." },
            ["format"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "Output format. json (default) or jsonl for one JSON object per line.",
                ["enum"] = new JsonArray { "json", "jsonl" }
            }
        }
    };

    private static JsonObject BuildFindBySourceSchema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["pattern"] = new JsonObject { ["type"] = "string", ["description"] = "Substring to match against source link URIs (raw and variable-expanded). Case-insensitive." }
        },
        ["required"] = new JsonArray { "pattern" }
    };

    private static JsonObject BuildTaskListSchema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["query"] = new JsonObject { ["type"] = "string", ["description"] = "Optional text matched against task title, description, key, labels, and assignee." },
            ["status"] = new JsonObject { ["type"] = "string", ["description"] = "Optional task status filter." },
            ["assignee"] = new JsonObject { ["type"] = "string", ["description"] = "Optional assignee text or directory id filter." },
            ["limit"] = new JsonObject { ["type"] = "integer", ["description"] = "Maximum tasks to return. Clamped 1-100, default 25." }
        }
    };

    private static JsonObject BuildTaskIdSchema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["idOrKey"] = new JsonObject { ["type"] = "string", ["description"] = "Task id or key, such as tsk-0171-agent-task-tools or TSK-0171." }
        },
        ["required"] = new JsonArray { "idOrKey" }
    };

    private static JsonObject BuildTaskCreateSchema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["title"] = new JsonObject { ["type"] = "string", ["description"] = "Task title." },
            ["description"] = new JsonObject { ["type"] = "string", ["description"] = "Task description." },
            ["type"] = new JsonObject { ["type"] = "string", ["description"] = "Task type. Defaults to Task." },
            ["status"] = new JsonObject { ["type"] = "string", ["description"] = "Initial status. Defaults to Backlog." },
            ["priority"] = new JsonObject { ["type"] = "string", ["description"] = "Priority: Critical, High, Medium, or Low. Defaults to Medium." },
            ["assigneeMode"] = new JsonObject { ["type"] = "string", ["description"] = "Assignee mode: Directory or Custom. Defaults to Custom." },
            ["assigneeDirectoryId"] = new JsonObject { ["type"] = "string", ["description"] = "Directory user id when assigneeMode is Directory." },
            ["assigneeCustomText"] = new JsonObject { ["type"] = "string", ["description"] = "Custom assignee label. Defaults to Agent." },
            ["reporter"] = new JsonObject { ["type"] = "string", ["description"] = "Reporter label. Defaults to the caller." },
            ["labels"] = new JsonObject { ["description"] = "Labels as an array, comma-separated string, or newline-separated string." },
            ["dueDateUtc"] = new JsonObject { ["type"] = "string", ["description"] = "Optional ISO-8601 UTC due date." },
            ["epicId"] = new JsonObject { ["type"] = "string", ["description"] = "Optional epic task id." },
            ["parentId"] = new JsonObject { ["type"] = "string", ["description"] = "Optional parent task id." },
            ["slug"] = new JsonObject { ["type"] = "string", ["description"] = "Optional id slug suffix." }
        },
        ["required"] = new JsonArray { "title" }
    };

    private static JsonObject BuildTaskUpdateSchema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["idOrKey"] = new JsonObject { ["type"] = "string", ["description"] = "Task id or key." },
            ["title"] = new JsonObject { ["type"] = "string", ["description"] = "Replacement title." },
            ["description"] = new JsonObject { ["type"] = "string", ["description"] = "Replacement description." },
            ["type"] = new JsonObject { ["type"] = "string", ["description"] = "Replacement type." },
            ["priority"] = new JsonObject { ["type"] = "string", ["description"] = "Replacement priority: Critical, High, Medium, or Low." },
            ["assigneeMode"] = new JsonObject { ["type"] = "string", ["description"] = "Replacement assignee mode: Directory or Custom." },
            ["assigneeDirectoryId"] = new JsonObject { ["type"] = "string", ["description"] = "Replacement directory assignee id." },
            ["assigneeCustomText"] = new JsonObject { ["type"] = "string", ["description"] = "Replacement custom assignee text." },
            ["reporter"] = new JsonObject { ["type"] = "string", ["description"] = "Replacement reporter." },
            ["labels"] = new JsonObject { ["description"] = "Replacement labels as an array, comma-separated string, or newline-separated string." },
            ["dueDateUtc"] = new JsonObject { ["type"] = "string", ["description"] = "Replacement ISO-8601 UTC due date. Omit to preserve the existing due date." },
            ["epicId"] = new JsonObject { ["type"] = "string", ["description"] = "Replacement epic task id." },
            ["parentId"] = new JsonObject { ["type"] = "string", ["description"] = "Replacement parent task id." }
        },
        ["required"] = new JsonArray { "idOrKey" }
    };

    private static JsonObject BuildTaskSetStatusSchema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["idOrKey"] = new JsonObject { ["type"] = "string", ["description"] = "Task id or key." },
            ["status"] = new JsonObject { ["type"] = "string", ["description"] = "New status: Backlog, Ready, InProgress, Blocked, Rejected, Done, or Archived." },
            ["note"] = new JsonObject { ["type"] = "string", ["description"] = "Optional history note." }
        },
        ["required"] = new JsonArray { "idOrKey", "status" }
    };

    private static JsonObject BuildTaskCommentSchema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["idOrKey"] = new JsonObject { ["type"] = "string", ["description"] = "Task id or key." },
            ["body"] = new JsonObject { ["type"] = "string", ["description"] = "Comment body." }
        },
        ["required"] = new JsonArray { "idOrKey", "body" }
    };

    private static JsonObject BuildTaskAttachmentSchema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["idOrKey"] = new JsonObject { ["type"] = "string", ["description"] = "Task id or key." },
            ["name"] = new JsonObject { ["type"] = "string", ["description"] = "Attachment display name." },
            ["kind"] = new JsonObject { ["type"] = "string", ["description"] = "Attachment kind, such as file, image, report, or trace. Defaults to file." },
            ["uri"] = new JsonObject { ["type"] = "string", ["description"] = "Absolute http/https attachment URI." }
        },
        ["required"] = new JsonArray { "idOrKey", "name", "uri" }
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

    public static string? ReadString(JsonObject item, string name)
    {
        var node = GetProperty(item, name);
        if (node is JsonValue value && value.TryGetValue<string>(out var text))
        {
            return text;
        }

        return node?.ToString();
    }

    public static IReadOnlyList<string>? ReadStringList(JsonObject item, string name)
    {
        var node = GetProperty(item, name);
        if (node is null)
        {
            return null;
        }

        if (node is JsonArray array)
        {
            return array
                .Select(value => value is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var text) ? text : value?.ToString())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value!.Trim())
                .ToList();
        }

        var raw = ReadString(item, name);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        return raw.Split([',', '\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .ToList();
    }

    public static DateTime? ReadNullableDateTime(JsonObject item, string name, DateTime? fallback)
    {
        var node = GetProperty(item, name);
        if (node is null)
        {
            return fallback;
        }

        var text = ReadString(item, name);
        if (string.IsNullOrWhiteSpace(text))
        {
            return fallback;
        }

        if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var value))
        {
            return value;
        }

        throw new ArgumentException($"{name} must be an ISO-8601 date/time value.");
    }

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
        new("memory", result.Id, result.Title, Truncate(result.Snippet, 320), ChatContextOrigins.Tool, result.Diagnostics);

    private static ChatContextItem ToMemoryContextItem(MemoryContextPackRecord record) =>
        new("memory", record.Id, record.Title, Truncate(record.Content, 320), ChatContextOrigins.Tool, record.Diagnostics);

    private static ChatContextItem ToPageContextItem(PageSummary page) =>
        new("page", page.Slug, page.Title, Truncate(page.Snippet, 320), ChatContextOrigins.Tool);

    private static ChatContextItem ToPageContextItem(PageDocument page, string markdown) =>
        new("page", page.Slug, page.Title, Truncate(markdown, 320), ChatContextOrigins.Tool);

    private static ChatToolExecutionResult BuildRetrievalToolResult(RetrievalResultEnvelope<MemorySearchResult> envelope)
    {
        var contextItems = envelope.Results.Select(ToMemoryContextItem).ToList();
        var structured = JsonSerializer.SerializeToNode(envelope, ToolJsonOptions);
        return new ChatToolExecutionResult(structured!.ToJsonString(ToolJsonOptions), ContextItems: contextItems, Structured: structured);
    }

    private static ChatToolExecutionResult JsonToolResult(object payload)
    {
        var structured = JsonSerializer.SerializeToNode(payload, ToolJsonOptions);
        return new ChatToolExecutionResult(structured!.ToJsonString(ToolJsonOptions), Structured: structured);
    }

    private static ChatToolExecutionResult MissingTaskServiceResult(string toolName) =>
        new($"The {toolName} tool requires the task service.", IsError: true);

    private static string Actor(ChatToolExecutionContext ctx)
    {
        if (ctx.CurrentUser is { IsAuthenticated: true } && !string.IsNullOrWhiteSpace(ctx.CurrentUser.DisplayName))
        {
            return ctx.CurrentUser.DisplayName;
        }

        if (ctx.User?.Identity?.IsAuthenticated != true)
        {
            return "anonymous";
        }

        return ctx.User.Identity?.Name
            ?? ctx.User.FindFirst("name")?.Value
            ?? ctx.User.FindFirst("sub")?.Value
            ?? "authenticated-user";
    }

    public static string FormatLexicalResults(IReadOnlyList<MemorySearchResult> results)
    {
        if (results.Count == 0) return "No lexical search results.";
        return string.Join(Environment.NewLine + Environment.NewLine, results.Select(result =>
            $"- {result.Id}: {result.Title}{Environment.NewLine}  Score: {result.Score:0.###}{Environment.NewLine}  Match: {result.MatchReason}{Environment.NewLine}  Tags: {string.Join(", ", result.Tags)}{Environment.NewLine}{FormatInlineDiagnostics(result.Diagnostics)}  {result.Snippet}"));
    }

    public static string FormatSemanticResults(IReadOnlyList<MemorySearchResult> results)
    {
        if (results.Count == 0) return "No semantic search results.";
        return string.Join(Environment.NewLine + Environment.NewLine, results.Select(result =>
            $"- {result.Id}: {result.Title}{Environment.NewLine}  Score: {result.Score:0.###}{Environment.NewLine}  Match: {result.MatchReason}{Environment.NewLine}  Tags: {string.Join(", ", result.Tags)}{Environment.NewLine}{FormatInlineDiagnostics(result.Diagnostics)}  {result.Snippet}"));
    }

    public static string FormatHybridResults(IReadOnlyList<MemorySearchResult> results)
    {
        if (results.Count == 0) return "No hybrid search results.";
        return string.Join(Environment.NewLine + Environment.NewLine, results.Select(result =>
            $"- {result.Id}: {result.Title}{Environment.NewLine}  RRF Score: {result.Score:0.######}{Environment.NewLine}  Match: {result.MatchReason}{Environment.NewLine}  Tags: {string.Join(", ", result.Tags)}{Environment.NewLine}{FormatInlineDiagnostics(result.Diagnostics)}  {result.Snippet}"));
    }

    public static string FormatContextPack(MemoryContextPack pack, string? format) =>
        MemoryContextPackFormatter.Format(pack, format);

    private static string FormatInlineDiagnostics(IReadOnlyList<MemoryDiagnostic> diagnostics) =>
        diagnostics.Count == 0
            ? string.Empty
            : $"  Diagnostics: {string.Join("; ", diagnostics.Take(3).Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Message}"))}{Environment.NewLine}";

    private static bool IsStructuredFormat(string? format) =>
        string.Equals(format, "json", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(format, "envelope", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(format, "json-v2", StringComparison.OrdinalIgnoreCase);

    public static string Truncate(string? value, int maxCharacters)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var text = value.Trim();
        return text.Length <= maxCharacters ? text : text[..maxCharacters].TrimEnd() + "...";
    }
}
