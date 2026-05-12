using System.Text.Json;
using System.Text.Json.Nodes;
using MemorySmith.App.Services;
using MemorySmith.Core.Models;
using Microsoft.AspNetCore.Mvc;

namespace MemorySmith.App.Controllers;

[ApiController]
[Route("mcp")]
public class McpController : ControllerBase
{
    private static readonly JsonSerializerOptions ToolJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly MemoryApplicationService _memories;

    public McpController(MemoryApplicationService memories)
    {
        _memories = memories;
    }

    [HttpGet]
    public IActionResult GetInfo()
    {
        return Ok(new
        {
            Name = "MemorySmithWiki",
            Endpoint = "/mcp",
            Transport = "HTTP JSON-RPC",
            Tools = new[] { "memorysmith_search", "memorysmith_semantic_search", "memorysmith_get" }
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
        if (!hasId && method?.StartsWith("notifications/", StringComparison.OrdinalIgnoreCase) == true)
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
            "memorysmith_search" => ToolText(FormatKeywordResults(await _memories.SearchAsync(ReadKeywordQuery(argumentsElement), cancellationToken))),
            "memorysmith_semantic_search" => ToolText(FormatSemanticResults(await _memories.SemanticSearchAsync(ReadSemanticQuery(argumentsElement), cancellationToken))),
            "memorysmith_get" => ToolText(await FormatRecordAsync(argumentsElement, cancellationToken)),
            _ => ToolText($"Unknown MemorySmith tool '{toolName}'.", isError: true)
        };
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

    private static MemorySearchQuery ReadKeywordQuery(JsonElement argumentsElement) => new(
        Query: GetString(argumentsElement, "query"),
        Status: GetStatus(argumentsElement),
        Tags: GetString(argumentsElement, "tags"),
        Limit: GetInt(argumentsElement, "limit", 20));

    private static SemanticMemorySearchQuery ReadSemanticQuery(JsonElement argumentsElement) => new(
        Query: GetString(argumentsElement, "query"),
        Status: GetStatus(argumentsElement),
        Tags: GetString(argumentsElement, "tags"),
        Limit: GetInt(argumentsElement, "limit", 20));

    private static string FormatKeywordResults(IReadOnlyList<MemoryRecord> records)
    {
        if (records.Count == 0)
        {
            return "No keyword search results.";
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

    private static JsonObject BuildToolsListResult() => new()
    {
        ["tools"] = new JsonArray
        {
            BuildTool(
                "memorysmith_search",
                "Search MemorySmith wiki records with exact keyword and tag filtering.",
                BuildSearchSchema()),
            BuildTool(
                "memorysmith_semantic_search",
                "Search MemorySmith wiki records with local semantic token scoring and match explanations.",
                BuildSearchSchema()),
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
                })
        }
    };

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

    private static MemoryStatus? GetStatus(JsonElement element)
    {
        var value = GetString(element, "status");
        return Enum.TryParse<MemoryStatus>(value, ignoreCase: true, out var status) ? status : null;
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength].TrimEnd() + "...";
}