using System.Text.Json;
using System.Text.Json.Nodes;
using MemorySmith.App.Services;
using MemorySmith.Core.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace MemorySmith.App.Controllers;

[ApiController]
[Route("mcp")]
[Authorize(Policy = MemorySmithPolicies.CanViewMemorySmith)]
public class McpController : ControllerBase
{
    private static readonly JsonSerializerOptions ToolJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private static readonly JsonSerializerOptions CompactJsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly string[] ToolNames =
    {
        "memorysmith_search",
        "memorysmith_semantic_search",
        "memorysmith_hybrid_search",
        "memorysmith_context_pack",
        "memorysmith_get",
        "memorysmith_page_search",
        "memorysmith_page_get",
        "memorysmith_unified_search",
        "memorysmith_page_save",
        "memorysmith_page_delete",
        "memorysmith_source_bundle",
        "memorysmith_find_by_source"
    };

    private readonly MemoryApplicationService _memories;
    private readonly VarResolver _vars;
    private readonly MemorySmithOptions _options;
    private readonly IAuthorizationService _authorization;
    private readonly ChatToolCatalog _toolCatalog;
    private readonly IPageService _pages;

    public McpController(
        MemoryApplicationService memories,
        VarResolver vars,
        IOptions<MemorySmithOptions> options,
        IAuthorizationService authorization,
        ChatToolCatalog toolCatalog,
        IPageService pages)
    {
        _memories = memories;
        _vars = vars;
        _options = options.Value;
        _authorization = authorization;
        _toolCatalog = toolCatalog;
        _pages = pages;
    }

    [HttpGet]
    public IActionResult GetInfo()
    {
        return Ok(new
        {
            Name = "MemorySmithWiki",
            Endpoint = "/mcp",
            Transport = "HTTP JSON-RPC",
            Tools = ToolNames
        });
    }

    [HttpPost]
    public async Task<IActionResult> Post([FromBody] JsonElement message, CancellationToken cancellationToken)
    {
        if (message.ValueKind == JsonValueKind.Array)
        {
            var responses = new JsonArray();
            foreach (var item in message.EnumerateArray())
            {
                var response = await HandleMessageAsync(item, cancellationToken);
                if (response is not null)
                {
                    responses.Add(response);
                }
            }

            return responses.Count == 0 ? Accepted() : new JsonResult(responses);
        }

        var singleResponse = await HandleMessageAsync(message, cancellationToken);
        return singleResponse is null ? Accepted() : new JsonResult(singleResponse);
    }

    private async Task<JsonObject?> HandleMessageAsync(JsonElement message, CancellationToken cancellationToken)
    {
        if (!TryGetProperty(message, "method", out var methodElement) || methodElement.ValueKind != JsonValueKind.String)
        {
            return Error(message, -32600, "Invalid JSON-RPC request.");
        }

        var method = methodElement.GetString();
        var hasId = TryGetProperty(message, "id", out var idElement);
        if (!hasId)
        {
            return null;
        }

        return method switch
        {
            "initialize" => Success(idElement, BuildInitializeResult()),
            "ping" => Success(idElement, new JsonObject()),
            "tools/list" => Success(idElement, BuildToolsListResult()),
            "tools/call" => Success(idElement, await HandleToolCallAsync(message, cancellationToken)),
            _ => Error(message, -32601, $"Method '{method}' is not supported.")
        };
    }

    private async Task<JsonObject> HandleToolCallAsync(JsonElement message, CancellationToken cancellationToken)
    {
        if (!TryGetProperty(message, "params", out var paramsElement) ||
            !TryGetProperty(paramsElement, "name", out var nameElement) ||
            nameElement.ValueKind != JsonValueKind.String)
        {
            return ToolText("Tool call is missing params.name.", isError: true);
        }

        TryGetProperty(paramsElement, "arguments", out var argumentsElement);
        var toolName = nameElement.GetString();

        return toolName switch
        {
            "memorysmith_search" => ToolText(FormatLexicalResults(await _memories.SearchAsync(ReadLexicalQuery(argumentsElement), cancellationToken))),
            "memorysmith_semantic_search" => ToolText(FormatSemanticResults(await _memories.SemanticSearchAsync(ReadSemanticQuery(argumentsElement), cancellationToken))),
            "memorysmith_hybrid_search" => ToolText(FormatHybridResults(await _memories.HybridSearchAsync(ReadHybridQuery(argumentsElement), cancellationToken))),
            "memorysmith_context_pack" => ToolText(FormatContextPack(
                await _memories.BuildContextPackAsync(ReadContextPackQuery(argumentsElement), cancellationToken),
                GetString(argumentsElement, "format"))),
            "memorysmith_get" => ToolText(await FormatRecordAsync(argumentsElement, cancellationToken)),
            "memorysmith_source_bundle" => await CanReadSourceBundleAsync()
                ? ToolText(await FormatSourceBundleAsync(argumentsElement, cancellationToken))
                : ToolText("The caller is not authorized to read source bundles.", isError: true),
            "memorysmith_find_by_source" => ToolText(await FormatFindBySourceAsync(argumentsElement, cancellationToken)),
            "memorysmith_page_search" or "memorysmith_page_get" or "memorysmith_unified_search"
            or "memorysmith_page_save" or "memorysmith_page_delete"
                => await DelegateToCatalogAsync(toolName, argumentsElement, cancellationToken),
            _ => ToolText($"Unknown MemorySmith tool '{toolName}'.", isError: true)
        };
    }

    private async Task<JsonObject> DelegateToCatalogAsync(string toolName, JsonElement argumentsElement, CancellationToken cancellationToken)
    {
        if (!_toolCatalog.TryGet(toolName, out var tool) || !tool.AvailableInMcp)
        {
            return ToolText($"Unknown MemorySmith tool '{toolName}'.", isError: true);
        }
        if (tool.Risk == ChatToolRisk.Write && !await CanEditMemorySmithAsync())
        {
            return ToolText("The caller is not authorized to perform write operations.", isError: true);
        }
        var args = argumentsElement.ValueKind == JsonValueKind.Object
            ? JsonNode.Parse(argumentsElement.GetRawText()) as JsonObject ?? new JsonObject()
            : new JsonObject();
        var ctx = new ChatToolExecutionContext(_memories, _pages, Transport: "mcp");
        var result = await tool.Execute(args, ctx, cancellationToken);
        return ToolText(result.Text, isError: result.IsError);
    }

    private async Task<bool> CanReadSourceBundleAsync() =>
        (await _authorization.AuthorizeAsync(User, null, MemorySmithPolicies.CanReadSourceBundle)).Succeeded;

    private async Task<bool> CanEditMemorySmithAsync() =>
        (await _authorization.AuthorizeAsync(User, null, MemorySmithPolicies.CanEditMemorySmith)).Succeeded;

    private async Task<string> FormatSourceBundleAsync(JsonElement args, CancellationToken ct)
    {
        var ids = GetString(args, "ids");
        var maxFileBytes = Clamp(GetInt(args, "maxFileBytes", 16384), 1, Math.Max(1, _options.SourceLinks.MaxReadBytes), 16384);
        var format = GetString(args, "format") ?? "json";

        var records = new List<MemoryRecord>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(ids))
        {
            foreach (var id in ids.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                if (!seen.Add(id)) continue;
                var r = await _memories.GetAsync(id, ct);
                if (r is not null) records.Add(r);
            }
        }

        var query = GetString(args, "query");
        if (!string.IsNullOrWhiteSpace(query))
        {
            var limit = GetInt(args, "limit", 10);
            var searchResults = await _memories.HybridSearchAsync(
                new HybridMemorySearchQuery(query, GetStatus(args), GetString(args, "tags"), limit), ct);
            foreach (var result in searchResults)
            {
                if (!seen.Add(result.Id)) continue;
                var r = await _memories.GetAsync(result.Id, ct);
                if (r is not null) records.Add(r);
            }
        }

        if (records.Count == 0)
            return "No records found. Provide ids or a query.";

        var entries = new List<object>();
        foreach (var record in records)
        {
            foreach (var sl in record.SourceLinks)
            {
                var content = await _vars.ReadSourceAsync(sl, maxFileBytes);
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
            return $"Found {records.Count} record(s) but none have source links.";

        if (string.Equals(format, "jsonl", StringComparison.OrdinalIgnoreCase))
            return string.Join('\n', entries.Select(e => JsonSerializer.Serialize(e, CompactJsonOptions)));

        return JsonSerializer.Serialize(new
        {
            MemoryCount = records.Count,
            SourceCount = entries.Count,
            Entries = entries
        }, ToolJsonOptions);
    }

    private async Task<string> FormatFindBySourceAsync(JsonElement args, CancellationToken ct)
    {
        var pattern = GetString(args, "pattern");
        if (string.IsNullOrWhiteSpace(pattern))
            return "The memorysmith_find_by_source tool requires a pattern argument.";

        var matches = await _memories.FindBySourceAsync(pattern, _vars.Resolve, ct);

        if (matches.Count == 0)
            return $"No memory records found with source links matching '{pattern}'.";

        var result = matches.Select(r => new
        {
            r.Id,
            r.Title,
            r.Status,
            MatchingLinks = r.SourceLinks
                .Where(sl =>
                    sl.Uri.Contains(pattern, StringComparison.OrdinalIgnoreCase) ||
                    _vars.Resolve(sl.Uri).Contains(pattern, StringComparison.OrdinalIgnoreCase))
                .Select(sl => new
                {
                    sl.Label,
                    sl.Uri,
                    ResolvedUri = _vars.Resolve(sl.Uri),
                    sl.StartLine,
                    sl.EndLine
                })
                .ToList()
        });

        return JsonSerializer.Serialize(result, ToolJsonOptions);
    }

    private async Task<string> FormatRecordAsync(JsonElement argumentsElement, CancellationToken cancellationToken)
    {
        var id = GetString(argumentsElement, "id");
        if (string.IsNullOrWhiteSpace(id))
        {
            return "The memorysmith_get tool requires an id argument.";
        }

        var record = await _memories.GetAsync(id, cancellationToken);
        return record is null
            ? $"No memory record found for id '{id}'."
            : JsonSerializer.Serialize(record, ToolJsonOptions);
    }

    private static MemorySearchQuery ReadLexicalQuery(JsonElement argumentsElement) => new(
        Query: GetString(argumentsElement, "query"),
        Status: GetStatus(argumentsElement),
        Tags: GetString(argumentsElement, "tags"),
        Limit: GetInt(argumentsElement, "limit", 20));

    private static SemanticMemorySearchQuery ReadSemanticQuery(JsonElement argumentsElement) => new(
        Query: GetString(argumentsElement, "query"),
        Status: GetStatus(argumentsElement),
        Tags: GetString(argumentsElement, "tags"),
        Limit: GetInt(argumentsElement, "limit", 20));

    private static HybridMemorySearchQuery ReadHybridQuery(JsonElement argumentsElement) => new(
        Query: GetString(argumentsElement, "query"),
        Status: GetStatus(argumentsElement),
        Tags: GetString(argumentsElement, "tags"),
        Limit: GetInt(argumentsElement, "limit", 20));

    private static MemoryContextPackQuery ReadContextPackQuery(JsonElement argumentsElement) => new(
        Query: GetString(argumentsElement, "query"),
        Status: GetStatus(argumentsElement),
        Tags: GetString(argumentsElement, "tags"),
        Limit: GetInt(argumentsElement, "limit", 5),
        ReferenceDepth: GetInt(argumentsElement, "referenceDepth", 1),
        MaxContentChars: GetInt(argumentsElement, "maxContentChars", 1200),
        MaxRecords: GetInt(argumentsElement, "maxRecords", 20),
        Ids: GetString(argumentsElement, "ids"),
        IncludeBacklinks: GetBool(argumentsElement, "includeBacklinks", false));

    private static string FormatLexicalResults(IReadOnlyList<MemoryRecord> records)
    {
        if (records.Count == 0)
        {
            return "No lexical search results.";
        }

        return string.Join(Environment.NewLine + Environment.NewLine, records.Select(record =>
            $"- {record.Id}: {record.Title}{Environment.NewLine}  Tags: {string.Join(", ", record.Tags)}{Environment.NewLine}  {Truncate(record.Content, 260)}"));
    }

    private static string FormatSemanticResults(IReadOnlyList<MemorySearchResult> results)
    {
        if (results.Count == 0)
        {
            return "No semantic search results.";
        }

        return string.Join(Environment.NewLine + Environment.NewLine, results.Select(result =>
            $"- {result.Id}: {result.Title}{Environment.NewLine}  Score: {result.Score:0.###}{Environment.NewLine}  Match: {result.MatchReason}{Environment.NewLine}  Tags: {string.Join(", ", result.Tags)}{Environment.NewLine}  {result.Snippet}"));
    }

    private static string FormatHybridResults(IReadOnlyList<MemorySearchResult> results)
    {
        if (results.Count == 0)
        {
            return "No hybrid search results.";
        }

        return string.Join(Environment.NewLine + Environment.NewLine, results.Select(result =>
            $"- {result.Id}: {result.Title}{Environment.NewLine}  RRF Score: {result.Score:0.######}{Environment.NewLine}  Match: {result.MatchReason}{Environment.NewLine}  Tags: {string.Join(", ", result.Tags)}{Environment.NewLine}  {result.Snippet}"));
    }

    private string FormatContextPack(MemoryContextPack pack, string? format)
    {
        if (string.Equals(format, "json", StringComparison.OrdinalIgnoreCase))
        {
            // Serialize with resolved source link URIs so agents get actionable paths.
            var projected = new
            {
                pack.Query,
                pack.GeneratedAt,
                pack.Warnings,
                Records = pack.Records.Select(r => new
                {
                    r.Id, r.Title, r.Status, r.Confidence, r.Tags,
                    r.References, r.Conflicts,
                    SourceLinks = r.SourceLinks.Select(sl => new
                    {
                        sl.Label,
                        Uri = _vars.Resolve(sl.Uri),
                        sl.StartLine,
                        sl.EndLine
                    }),
                    r.UsageCount, r.LastUpdated, r.Relationship, r.Score, r.MatchReason, r.Content
                })
            };
            return JsonSerializer.Serialize(projected, ToolJsonOptions);
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
            var sourceLinks = record.SourceLinks.Count == 0
                ? string.Empty
                : $"Source Links: {string.Join(", ", record.SourceLinks.Select(sl => FormatSourceLink(sl)))}{Environment.NewLine}";
            return $"## {record.Id}: {record.Title}{Environment.NewLine}" +
                   $"Relationship: {record.Relationship}{Environment.NewLine}" +
                   $"Status: {record.Status}; Confidence: {record.Confidence:P0}; Uses: {record.UsageCount}{Environment.NewLine}" +
                   $"Tags: {string.Join(", ", record.Tags)}{Environment.NewLine}" +
                   $"References: {FormatLinks(record.References)}{Environment.NewLine}" +
                   $"Conflicts: {FormatLinks(record.Conflicts)}{Environment.NewLine}" +
                   sourceLinks +
                   $"{scoreLine}{Environment.NewLine}" +
                   matchLine +
                   record.Content;
        });

        return $"# Context Pack{Environment.NewLine}Query: {pack.Query ?? string.Empty}{Environment.NewLine}Generated: {pack.GeneratedAt:O}{warnings}{Environment.NewLine}" +
            string.Join(Environment.NewLine + Environment.NewLine, sections);
    }

    private string FormatSourceLink(SourceLink sl)
    {
        var resolved = _vars.Resolve(sl.Uri);
        var label = string.IsNullOrWhiteSpace(sl.Label) ? resolved : sl.Label;
        var lineHint = sl.StartLine.HasValue
            ? (sl.EndLine.HasValue ? $":{sl.StartLine}-{sl.EndLine}" : $":{sl.StartLine}")
            : string.Empty;
        var display = resolved == sl.Uri ? $"{label}{lineHint}" : $"{label}{lineHint} ({resolved}{lineHint})";
        return display;
    }

    private static JsonObject BuildInitializeResult() => new()
    {
        ["protocolVersion"] = "2025-06-18",
        ["capabilities"] = new JsonObject
        {
            ["tools"] = new JsonObject()
        },
        ["serverInfo"] = new JsonObject
        {
            ["name"] = "MemorySmithWiki",
            ["version"] = "0.1.0"
        }
    };

    private JsonObject BuildToolsListResult()
    {
        var array = new JsonArray
        {
            BuildTool(
                "memorysmith_search",
                "Search MemorySmith wiki records with Lucene-style lexical ranking plus optional tag and status filters.",
                BuildSearchSchema()),
            BuildTool(
                "memorysmith_semantic_search",
                "Search MemorySmith wiki records with ONNX embeddings when configured, falling back to local semantic token scoring with match explanations.",
                BuildSearchSchema()),
            BuildTool(
                "memorysmith_hybrid_search",
                "Search MemorySmith wiki records by fusing Lucene-style lexical rank and the active semantic ranker with reciprocal rank fusion.",
                BuildSearchSchema()),
            BuildTool(
                "memorysmith_context_pack",
                "Build an agent-ready context pack from hybrid search results plus linked references, conflicts, and optional backlinks.",
                BuildContextPackSchema()),
            BuildTool(
                "memorysmith_get",
                "Fetch a single MemorySmith wiki record by id.",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["id"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["description"] = "Memory record id."
                        }
                    },
                    ["required"] = new JsonArray { "id" }
                }),
            BuildTool(
                "memorysmith_source_bundle",
                "Read the source file content for all source links attached to the specified memory records. Useful for fetching the exact code or document sections that KB entries reference. Returns URL references as-is (unfetchable server-side). Use format=jsonl for streaming-friendly large bundles.",
                BuildSourceBundleSchema()),
            BuildTool(
                "memorysmith_find_by_source",
                "Back-map a source path or URL fragment to every KB entry that references it. Matches against both raw and resolved (variable-expanded) URIs.",
                BuildFindBySourceSchema())
        };

        // Add the page/unified/write tools that live in the shared ChatToolCatalog so MCP and chat stay in sync.
        var sharedToolNames = new[] { "memorysmith_page_search", "memorysmith_page_get", "memorysmith_unified_search", "memorysmith_page_save", "memorysmith_page_delete" };
        foreach (var name in sharedToolNames)
        {
            if (_toolCatalog.TryGet(name, out var tool))
            {
                // Clone the schema; JsonNodes cannot have two parents and BuildToolsListResult may run repeatedly.
                var clonedSchema = JsonNode.Parse(tool.InputSchema.ToJsonString()) as JsonObject ?? new JsonObject();
                array.Add(BuildTool(tool.Name, tool.Description, clonedSchema));
            }
        }
        return new JsonObject { ["tools"] = array };
    }

    private static JsonObject BuildTool(string name, string description, JsonObject inputSchema) => new()
    {
        ["name"] = name,
        ["description"] = description,
        ["inputSchema"] = inputSchema
    };

    private static JsonObject BuildSearchSchema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["query"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "Search text."
            },
            ["tags"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "Optional comma-separated tag filter."
            },
            ["status"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "Optional memory status name."
            },
            ["limit"] = new JsonObject
            {
                ["type"] = "integer",
                ["description"] = "Maximum number of results."
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
            ["maxFileBytes"] = new JsonObject { ["type"] = "integer", ["description"] = "Max bytes per file content entry. Default 16384; clamped by MemorySmith:SourceLinks:MaxReadBytes." },
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

    private static JsonObject BuildContextPackSchema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["query"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "Search text used to seed the context pack with hybrid search."
            },
            ["ids"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "Optional comma-separated root memory ids to include before search results."
            },
            ["tags"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "Optional comma-separated tag filter."
            },
            ["status"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "Optional memory status name."
            },
            ["limit"] = new JsonObject
            {
                ["type"] = "integer",
                ["description"] = "Maximum number of hybrid root results."
            },
            ["referenceDepth"] = new JsonObject
            {
                ["type"] = "integer",
                ["description"] = "How many levels of references/conflicts to include. Clamped to 0-2."
            },
            ["maxContentChars"] = new JsonObject
            {
                ["type"] = "integer",
                ["description"] = "Maximum content characters per record. Clamped to 200-6000."
            },
            ["maxRecords"] = new JsonObject
            {
                ["type"] = "integer",
                ["description"] = "Maximum total records in the context pack. Clamped to 1-100."
            },
            ["includeBacklinks"] = new JsonObject
            {
                ["type"] = "boolean",
                ["description"] = "Include records that reference or conflict with packed records."
            },
            ["format"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "Output format. Use json for structured agent parsing; defaults to markdown.",
                ["enum"] = new JsonArray { "markdown", "json" }
            }
        }
    };

    private static JsonObject ToolText(string text, bool isError = false) => new()
    {
        ["content"] = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "text",
                ["text"] = text
            }
        },
        ["isError"] = isError
    };

    private static JsonObject Success(JsonElement idElement, JsonNode? result) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = CloneElement(idElement),
        ["result"] = result
    };

    private static JsonObject Error(JsonElement message, int code, string messageText)
    {
        TryGetProperty(message, "id", out var idElement);
        return new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = idElement.ValueKind == JsonValueKind.Undefined ? null : CloneElement(idElement),
            ["error"] = new JsonObject
            {
                ["code"] = code,
                ["message"] = messageText
            }
        };
    }

    private static JsonNode? CloneElement(JsonElement element) =>
        element.ValueKind == JsonValueKind.Undefined ? null : JsonNode.Parse(element.GetRawText());

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private static string? GetString(JsonElement element, string name)
    {
        if (!TryGetProperty(element, name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };
    }

    private static int GetInt(JsonElement element, string name, int defaultValue)
    {
        if (!TryGetProperty(element, name, out var value))
        {
            return defaultValue;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
        {
            return number;
        }

        return value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out var parsed)
            ? parsed
            : defaultValue;
    }

    private static int Clamp(int value, int min, int max, int defaultValue)
    {
        if (value < min)
        {
            return Math.Min(defaultValue, max);
        }

        return Math.Min(value, max);
    }

    private static bool GetBool(JsonElement element, string name, bool defaultValue)
    {
        if (!TryGetProperty(element, name, out var value))
        {
            return defaultValue;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(value.GetString(), out var parsed) => parsed,
            _ => defaultValue
        };
    }

    private static MemoryStatus? GetStatus(JsonElement element)
    {
        var value = GetString(element, "status");
        return Enum.TryParse<MemoryStatus>(value, ignoreCase: true, out var status) ? status : null;
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength].TrimEnd() + "...";

    private static string FormatLinks(IReadOnlyList<string> links) =>
        links.Count == 0 ? "none" : string.Join(", ", links);
}