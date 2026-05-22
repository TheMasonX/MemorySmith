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
    private string[] ToolNames => _toolCatalog.McpTools
        .Select(tool => tool.Name)
        .Concat(["memorysmith_source_bundle", "memorysmith_find_by_source"])
        .ToArray();

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

        if (toolName is "memorysmith_source_bundle")
        {
            return await CanReadSourceBundleAsync()
                ? ToolText(await FormatSourceBundleAsync(argumentsElement, cancellationToken))
                : ToolText("The caller is not authorized to read source bundles.", isError: true);
        }

        if (toolName is "memorysmith_find_by_source")
        {
            return ToolText(await FormatFindBySourceAsync(argumentsElement, cancellationToken));
        }

        return await DelegateToCatalogAsync(toolName ?? string.Empty, argumentsElement, cancellationToken);
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
        var ctx = new ChatToolExecutionContext(_memories, _pages, Transport: "mcp", User: User, Auth: _options.Auth, DefaultPageMinimumRole: _options.Pages.DefaultMinimumRole);
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
        var array = new JsonArray();
        foreach (var tool in _toolCatalog.McpTools)
        {
            var clonedSchema = JsonNode.Parse(tool.InputSchema.ToJsonString()) as JsonObject ?? new JsonObject();
            array.Add(BuildTool(tool.Name, tool.Description, clonedSchema));
        }

        array.Add(BuildTool(
            "memorysmith_source_bundle",
            "Read the source file content for all source links attached to the specified memory records. Useful for fetching the exact code or document sections that KB entries reference. Returns URL references as-is (unfetchable server-side). Use format=jsonl for streaming-friendly large bundles.",
            BuildSourceBundleSchema()));
        array.Add(BuildTool(
            "memorysmith_find_by_source",
            "Back-map a source path or URL fragment to every KB entry that references it. Matches against both raw and resolved (variable-expanded) URIs.",
            BuildFindBySourceSchema()));

        return new JsonObject { ["tools"] = array };
    }

    private static JsonObject BuildTool(string name, string description, JsonObject inputSchema) => new()
    {
        ["name"] = name,
        ["description"] = description,
        ["inputSchema"] = inputSchema
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
