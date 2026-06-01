using System.Diagnostics;
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
    private string[] ToolNames => EnabledMcpTools
        .Select(tool => tool.Name)
        .ToArray();
    private IReadOnlyList<ChatToolDescriptor> EnabledMcpTools => _toolCatalog.McpTools
        .Where(IsMcpToolEnabled)
        .ToList();

    private readonly MemoryApplicationService _memories;
    private readonly VarResolver _vars;
    private readonly IOptionsMonitor<MemorySmithOptions> _options;
    private readonly IAuthorizationService _authorization;
    private readonly ChatToolCatalog _toolCatalog;
    private readonly IPageService _pages;
    private readonly ITaskService _tasks;
    private readonly CodeSearchService _codeSearch;

    public McpController(
        MemoryApplicationService memories,
        VarResolver vars,
        IOptionsMonitor<MemorySmithOptions> options,
        IAuthorizationService authorization,
        ChatToolCatalog toolCatalog,
        IPageService pages,
        ITaskService tasks,
        CodeSearchService codeSearch)
    {
        _memories = memories;
        _vars = vars;
        _options = options;
        _authorization = authorization;
        _toolCatalog = toolCatalog;
        _pages = pages;
        _tasks = tasks;
        _codeSearch = codeSearch;
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

        return await DelegateToCatalogAsync(toolName ?? string.Empty, argumentsElement, cancellationToken);
    }

    private async Task<JsonObject> DelegateToCatalogAsync(string toolName, JsonElement argumentsElement, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        if (!_toolCatalog.TryGet(toolName, out var tool) || !tool.AvailableInMcp)
        {
            RecordToolExecutionTelemetry(toolName, Stopwatch.GetElapsedTime(started).TotalMilliseconds, success: false);
            return ToolText($"Unknown MemorySmith tool '{toolName}'.", isError: true);
        }
        if (!IsMcpToolEnabled(tool))
        {
            RecordToolExecutionTelemetry(toolName, Stopwatch.GetElapsedTime(started).TotalMilliseconds, success: false);
            return ToolText($"MemorySmith tool '{toolName}' is disabled by MCP tool configuration.", isError: true);
        }
        if (tool.Risk == ChatToolRisk.SensitiveRead && !await CanReadSourceBundleAsync())
        {
            RecordToolExecutionTelemetry(toolName, Stopwatch.GetElapsedTime(started).TotalMilliseconds, success: false);
            return ToolText("The caller is not authorized to read source bundles.", isError: true);
        }
        if (tool.Risk == ChatToolRisk.Write && !await CanEditMemorySmithAsync())
        {
            RecordToolExecutionTelemetry(toolName, Stopwatch.GetElapsedTime(started).TotalMilliseconds, success: false);
            return ToolText("The caller is not authorized to perform write operations.", isError: true);
        }
        var args = argumentsElement.ValueKind == JsonValueKind.Object
            ? JsonNode.Parse(argumentsElement.GetRawText()) as JsonObject ?? new JsonObject()
            : new JsonObject();
        var options = _options.CurrentValue;
        var ctx = new ChatToolExecutionContext(
            _memories,
            _pages,
            Transport: "mcp",
            User: User,
            Auth: options.Auth,
            DefaultPageMinimumRole: options.Pages.DefaultMinimumRole,
            Vars: _vars,
            Tasks: _tasks,
            CodeSearch: _codeSearch,
            AgentWritesEnabled: options.Chat.AgentWritesEnabled,
            AgentWriteAutoAccept: AgentWriteApprovalModes.IsAutoAccept(options.Chat.AgentWriteApprovalMode));
        var result = await tool.Execute(args, ctx, cancellationToken);
        RecordToolExecutionTelemetry(toolName, Stopwatch.GetElapsedTime(started).TotalMilliseconds, !result.IsError);

        var maxToolResponseCharacters = Clamp(options.Mcp.MaxToolResponseCharacters, 256, 200000, 12000);
        var originalCharacters = result.Text.Length;
        var truncatedText = Truncate(result.Text, maxToolResponseCharacters);

        return ToolText(
            truncatedText,
            isError: result.IsError,
            originalCharacters: originalCharacters,
            maxCharacters: maxToolResponseCharacters);
    }

    private void RecordToolExecutionTelemetry(string toolName, double elapsedMs, bool success)
    {
        var telemetry = _options.CurrentValue.Telemetry;
        if (!telemetry.Enabled || !telemetry.MetricsEnabled || !telemetry.InstrumentMemoryOperations)
        {
            return;
        }

        MemorySmithTelemetry.RecordToolExecution("mcp", toolName, elapsedMs, success);
    }

    private bool IsMcpToolEnabled(ChatToolDescriptor tool)
    {
        var mcp = _options.CurrentValue.Mcp;
        if (ContainsTool(mcp.DisabledTools, tool.Name))
        {
            return false;
        }

        if (ContainsTool(mcp.EnabledTools, tool.Name))
        {
            return true;
        }

        return tool.EnabledByDefaultInMcp;
    }

    private static bool ContainsTool(IEnumerable<string> configuredTools, string toolName) =>
        configuredTools.Any(configured =>
            string.Equals(configured, "*", StringComparison.OrdinalIgnoreCase)
            || string.Equals(configured, toolName, StringComparison.OrdinalIgnoreCase));

    private async Task<bool> CanReadSourceBundleAsync() =>
        (await _authorization.AuthorizeAsync(User, null, MemorySmithPolicies.CanReadSourceBundle)).Succeeded;

    private async Task<bool> CanEditMemorySmithAsync() =>
        (await _authorization.AuthorizeAsync(User, null, MemorySmithPolicies.CanEditMemorySmith)).Succeeded;

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
        foreach (var tool in EnabledMcpTools)
        {
            var clonedSchema = JsonNode.Parse(tool.InputSchema.ToJsonString()) as JsonObject ?? new JsonObject();
            array.Add(BuildTool(tool.Name, tool.Description, clonedSchema));
        }

        return new JsonObject { ["tools"] = array };
    }

    private static JsonObject BuildTool(string name, string description, JsonObject inputSchema) => new()
    {
        ["name"] = name,
        ["description"] = description,
        ["inputSchema"] = inputSchema
    };

    private static JsonObject ToolText(string text, bool isError = false, int? originalCharacters = null, int? maxCharacters = null)
    {
        var response = new JsonObject
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

        if (originalCharacters.HasValue && maxCharacters.HasValue)
        {
            response["meta"] = new JsonObject
            {
                ["memorysmith"] = new JsonObject
                {
                    ["isTruncated"] = originalCharacters.Value > text.Length,
                    ["originalCharacters"] = originalCharacters.Value,
                    ["returnedCharacters"] = text.Length,
                    ["maxCharacters"] = maxCharacters.Value
                }
            };
        }

        return response;
    }

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

    private static string Truncate(string value, int maxLength)
    {
        if (value.Length <= maxLength)
        {
            return value;
        }

        if (maxLength <= 3)
        {
            return value[..maxLength];
        }

        return value[..(maxLength - 3)].TrimEnd() + "...";
    }

    private static string FormatLinks(IReadOnlyList<string> links) =>
        links.Count == 0 ? "none" : string.Join(", ", links);
}
