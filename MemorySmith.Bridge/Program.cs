using System.CommandLine;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

new BridgeApp(args).Run();

internal sealed class BridgeApp
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private const string DefaultMcpUrl = "https://memorysmith.home.arpa:7090/mcp";
    private const string DefaultProtocolVersion = "2025-06-18";
    private const string DefaultTaskType = "Task";
    private const string DefaultTaskStatus = "Backlog";
    private const string DefaultTaskPriority = "Medium";
    private const string DefaultAssigneeMode = "Custom";
    private const string DefaultAssigneeCustomText = "Agent";
    private const string DefaultReporter = "AgentSmith";
    private const string DefaultAttachmentKind = "file";

    private readonly string[] _args;

    public BridgeApp(string[] args)
    {
        _args = args;
    }

    public void Run()
    {
        var urlOption = new Option<string?>("--url", "-u")
        {
            Description = "Override the MemorySmith MCP endpoint URL. Defaults to .vscode/mcp.json, MEMORYSMITH_MCP_URL, then the documented LAN endpoint."
        };
        var protocolVersionOption = new Option<string?>("--protocol-version")
        {
            Description = "Override the MCP initialize protocol version."
        };
        var apiKeyOption = new Option<string?>("--api-key")
        {
            Description = "Override the X-Api-Key header value used for protected MCP requests."
        };

        var root = new RootCommand("MemorySmith.Bridge - CLI wrapper around the local MemorySmith MCP endpoint.");
        root.Options.Add(urlOption);
        root.Options.Add(protocolVersionOption);
        root.Options.Add(apiKeyOption);

        var toolsCommand = new Command("tools", "Generic MCP tool operations.");

        var toolsListCommand = new Command("list", "List the enabled MemorySmith MCP tools.");
        toolsListCommand.SetAction(async (parseResult, cancellationToken) =>
        {
            await using var client = CreateClient(parseResult, urlOption, protocolVersionOption, apiKeyOption);
            var result = await client.ListToolsAsync(cancellationToken);
            await WriteJsonAsync(result, cancellationToken);
            return 0;
        });

        var toolsCallCommand = new Command("call", "Call any MemorySmith MCP tool by name.");
        var toolNameArgument = new Argument<string>("tool-name")
        {
            Description = "The MemorySmith MCP tool name."
        };
        var argumentsJsonOption = new Option<string?>("--json")
        {
            Description = "Raw JSON object to pass as tool arguments."
        };
        toolsCallCommand.Arguments.Add(toolNameArgument);
        toolsCallCommand.Options.Add(argumentsJsonOption);
        toolsCallCommand.SetAction(async (parseResult, cancellationToken) =>
        {
            await using var client = CreateClient(parseResult, urlOption, protocolVersionOption, apiKeyOption);
            var toolName = parseResult.GetValue(toolNameArgument) ?? throw new InvalidOperationException("Tool name is required.");
            var arguments = ParseArguments(parseResult.GetValue(argumentsJsonOption));
            var result = await client.CallToolAsync(toolName, arguments, cancellationToken);
            await WriteJsonAsync(result, cancellationToken);
            return 0;
        });

        toolsCommand.Subcommands.Add(toolsListCommand);
        toolsCommand.Subcommands.Add(toolsCallCommand);
        root.Subcommands.Add(toolsCommand);

        var tasksCommand = new Command("tasks", "Convenience task authoring and read commands.");
        tasksCommand.Subcommands.Add(BuildTaskListCommand(urlOption, protocolVersionOption, apiKeyOption));
        tasksCommand.Subcommands.Add(BuildTaskGetCommand(urlOption, protocolVersionOption, apiKeyOption));
        tasksCommand.Subcommands.Add(BuildTaskCreateCommand(urlOption, protocolVersionOption, apiKeyOption));
        tasksCommand.Subcommands.Add(BuildTaskUpdateCommand(urlOption, protocolVersionOption, apiKeyOption));
        tasksCommand.Subcommands.Add(BuildTaskSetStatusCommand(urlOption, protocolVersionOption, apiKeyOption));
        tasksCommand.Subcommands.Add(BuildTaskCommentCommand(urlOption, protocolVersionOption, apiKeyOption));
        tasksCommand.Subcommands.Add(BuildTaskAttachmentCommand(urlOption, protocolVersionOption, apiKeyOption));
        root.Subcommands.Add(tasksCommand);

        var pagesCommand = new Command("pages", "Convenience wiki page authoring and read commands.");
        pagesCommand.Subcommands.Add(BuildPageSearchCommand(urlOption, protocolVersionOption, apiKeyOption));
        pagesCommand.Subcommands.Add(BuildPageGetCommand(urlOption, protocolVersionOption, apiKeyOption));
        pagesCommand.Subcommands.Add(BuildPageSaveCommand(urlOption, protocolVersionOption, apiKeyOption));
        pagesCommand.Subcommands.Add(BuildPageDeleteCommand(urlOption, protocolVersionOption, apiKeyOption));
        root.Subcommands.Add(pagesCommand);

        root.Parse(_args).Invoke();
    }

    private static Command BuildTaskListCommand(Option<string?> urlOption, Option<string?> protocolVersionOption, Option<string?> apiKeyOption)
    {
        var command = new Command("list", "List tasks with optional filters.");
        var queryOption = new Option<string?>("--query") { Description = "Search text for title, id, key, or labels." };
        var statusOption = new Option<string?>("--status") { Description = "Task status filter." };
        var assigneeOption = new Option<string?>("--assignee") { Description = "Task assignee filter." };
        var limitOption = new Option<int?>("--limit") { Description = "Maximum number of results." };
        command.Options.Add(queryOption);
        command.Options.Add(statusOption);
        command.Options.Add(assigneeOption);
        command.Options.Add(limitOption);
        command.SetAction(async (parseResult, cancellationToken) =>
        {
            await using var client = CreateClient(parseResult, urlOption, protocolVersionOption, apiKeyOption);
            var arguments = new JsonObject();
            AddIfNotBlank(arguments, "query", parseResult.GetValue(queryOption));
            AddIfNotBlank(arguments, "status", parseResult.GetValue(statusOption));
            AddIfNotBlank(arguments, "assignee", parseResult.GetValue(assigneeOption));
            AddIfNotNull(arguments, "limit", parseResult.GetValue(limitOption));
            var result = await client.CallToolAsync("memorysmith_task_list", arguments, cancellationToken);
            await WriteJsonAsync(result, cancellationToken);
            return 0;
        });
        return command;
    }

    private static Command BuildTaskGetCommand(Option<string?> urlOption, Option<string?> protocolVersionOption, Option<string?> apiKeyOption)
    {
        var command = new Command("get", "Fetch one task by id or key.");
        var idOrKeyArgument = new Argument<string>("id-or-key") { Description = "Task id or key." };
        command.Arguments.Add(idOrKeyArgument);
        command.SetAction(async (parseResult, cancellationToken) =>
        {
            await using var client = CreateClient(parseResult, urlOption, protocolVersionOption, apiKeyOption);
            var arguments = new JsonObject
            {
                ["idOrKey"] = parseResult.GetValue(idOrKeyArgument)
            };
            var result = await client.CallToolAsync("memorysmith_task_get", arguments, cancellationToken);
            await WriteJsonAsync(result, cancellationToken);
            return 0;
        });
        return command;
    }

    private static Command BuildTaskCreateCommand(Option<string?> urlOption, Option<string?> protocolVersionOption, Option<string?> apiKeyOption)
    {
        var command = new Command("create", "Create a MemorySmith task.");
        var titleOption = new Option<string>("--title") { Description = "Task title." };
        var descriptionOption = new Option<string?>("--description") { Description = "Task description." };
        var typeOption = new Option<string?>("--type") { Description = "Task type." };
        var statusOption = new Option<string?>("--status") { Description = "Task status." };
        var priorityOption = new Option<string?>("--priority") { Description = "Task priority." };
        var assigneeModeOption = new Option<string?>("--assignee-mode") { Description = "Assignee mode." };
        var assigneeDirectoryIdOption = new Option<string?>("--assignee-directory-id") { Description = "Directory-backed assignee id." };
        var assigneeCustomTextOption = new Option<string?>("--assignee-custom-text") { Description = "Custom assignee text." };
        var reporterOption = new Option<string?>("--reporter") { Description = "Task reporter." };
        var labelsOption = new Option<string?>("--labels") { Description = "Comma-separated task labels." };
        var dueDateOption = new Option<DateTimeOffset?>("--due-date-utc") { Description = "Optional due date in UTC." };
        var epicIdOption = new Option<string?>("--epic-id") { Description = "Optional epic id." };
        var parentIdOption = new Option<string?>("--parent-id") { Description = "Optional parent id." };
        var slugOption = new Option<string?>("--slug") { Description = "Optional task slug." };

        command.Options.Add(titleOption);
        command.Options.Add(descriptionOption);
        command.Options.Add(typeOption);
        command.Options.Add(statusOption);
        command.Options.Add(priorityOption);
        command.Options.Add(assigneeModeOption);
        command.Options.Add(assigneeDirectoryIdOption);
        command.Options.Add(assigneeCustomTextOption);
        command.Options.Add(reporterOption);
        command.Options.Add(labelsOption);
        command.Options.Add(dueDateOption);
        command.Options.Add(epicIdOption);
        command.Options.Add(parentIdOption);
        command.Options.Add(slugOption);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            await using var client = CreateClient(parseResult, urlOption, protocolVersionOption, apiKeyOption);
            var arguments = new JsonObject
            {
                ["title"] = parseResult.GetValue(titleOption)
            };

            AddIfNotBlank(arguments, "description", parseResult.GetValue(descriptionOption));
            AddIfNotBlank(arguments, "type", parseResult.GetValue(typeOption) ?? DefaultTaskType, DefaultTaskType);
            AddIfNotBlank(arguments, "status", parseResult.GetValue(statusOption) ?? DefaultTaskStatus, DefaultTaskStatus);
            AddIfNotBlank(arguments, "priority", parseResult.GetValue(priorityOption) ?? DefaultTaskPriority, DefaultTaskPriority);
            AddIfNotBlank(arguments, "assigneeMode", parseResult.GetValue(assigneeModeOption) ?? DefaultAssigneeMode, DefaultAssigneeMode);
            AddIfNotBlank(arguments, "assigneeDirectoryId", parseResult.GetValue(assigneeDirectoryIdOption));
            AddIfNotBlank(arguments, "assigneeCustomText", parseResult.GetValue(assigneeCustomTextOption) ?? DefaultAssigneeCustomText, DefaultAssigneeCustomText);
            AddIfNotBlank(arguments, "reporter", parseResult.GetValue(reporterOption) ?? DefaultReporter, DefaultReporter);
            AddLabels(arguments, parseResult.GetValue(labelsOption));
            AddIfNotNull(arguments, "dueDateUtc", parseResult.GetValue(dueDateOption));
            AddIfNotBlank(arguments, "epicId", parseResult.GetValue(epicIdOption));
            AddIfNotBlank(arguments, "parentId", parseResult.GetValue(parentIdOption));
            AddIfNotBlank(arguments, "slug", parseResult.GetValue(slugOption));

            var result = await client.CallToolAsync("memorysmith_task_create", arguments, cancellationToken);
            await WriteJsonAsync(result, cancellationToken);
            return 0;
        });

        return command;
    }

    private static Command BuildTaskUpdateCommand(Option<string?> urlOption, Option<string?> protocolVersionOption, Option<string?> apiKeyOption)
    {
        var command = new Command("update", "Update editable task fields.");
        var idOrKeyArgument = new Argument<string>("id-or-key") { Description = "Task id or key." };
        var titleOption = new Option<string?>("--title") { Description = "Updated title." };
        var descriptionOption = new Option<string?>("--description") { Description = "Updated description." };
        var typeOption = new Option<string?>("--type") { Description = "Updated type." };
        var priorityOption = new Option<string?>("--priority") { Description = "Updated priority." };
        var assigneeModeOption = new Option<string?>("--assignee-mode") { Description = "Updated assignee mode." };
        var assigneeDirectoryIdOption = new Option<string?>("--assignee-directory-id") { Description = "Updated directory assignee id." };
        var assigneeCustomTextOption = new Option<string?>("--assignee-custom-text") { Description = "Updated custom assignee text." };
        var reporterOption = new Option<string?>("--reporter") { Description = "Updated reporter." };
        var labelsOption = new Option<string?>("--labels") { Description = "Comma-separated replacement labels." };
        var dueDateOption = new Option<DateTimeOffset?>("--due-date-utc") { Description = "Updated due date." };
        var epicIdOption = new Option<string?>("--epic-id") { Description = "Updated epic id." };
        var parentIdOption = new Option<string?>("--parent-id") { Description = "Updated parent id." };

        command.Arguments.Add(idOrKeyArgument);
        command.Options.Add(titleOption);
        command.Options.Add(descriptionOption);
        command.Options.Add(typeOption);
        command.Options.Add(priorityOption);
        command.Options.Add(assigneeModeOption);
        command.Options.Add(assigneeDirectoryIdOption);
        command.Options.Add(assigneeCustomTextOption);
        command.Options.Add(reporterOption);
        command.Options.Add(labelsOption);
        command.Options.Add(dueDateOption);
        command.Options.Add(epicIdOption);
        command.Options.Add(parentIdOption);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            await using var client = CreateClient(parseResult, urlOption, protocolVersionOption, apiKeyOption);
            var arguments = new JsonObject
            {
                ["idOrKey"] = parseResult.GetValue(idOrKeyArgument)
            };

            AddIfNotBlank(arguments, "title", parseResult.GetValue(titleOption));
            AddIfNotBlank(arguments, "description", parseResult.GetValue(descriptionOption));
            AddIfNotBlank(arguments, "type", parseResult.GetValue(typeOption));
            AddIfNotBlank(arguments, "priority", parseResult.GetValue(priorityOption));
            AddIfNotBlank(arguments, "assigneeMode", parseResult.GetValue(assigneeModeOption));
            AddIfNotBlank(arguments, "assigneeDirectoryId", parseResult.GetValue(assigneeDirectoryIdOption));
            AddIfNotBlank(arguments, "assigneeCustomText", parseResult.GetValue(assigneeCustomTextOption));
            AddIfNotBlank(arguments, "reporter", parseResult.GetValue(reporterOption));
            AddLabels(arguments, parseResult.GetValue(labelsOption));
            AddIfNotNull(arguments, "dueDateUtc", parseResult.GetValue(dueDateOption));
            AddIfNotBlank(arguments, "epicId", parseResult.GetValue(epicIdOption));
            AddIfNotBlank(arguments, "parentId", parseResult.GetValue(parentIdOption));

            var result = await client.CallToolAsync("memorysmith_task_update", arguments, cancellationToken);
            await WriteJsonAsync(result, cancellationToken);
            return 0;
        });

        return command;
    }

    private static Command BuildTaskSetStatusCommand(Option<string?> urlOption, Option<string?> protocolVersionOption, Option<string?> apiKeyOption)
    {
        var command = new Command("status", "Change a task status.");
        var idOrKeyArgument = new Argument<string>("id-or-key") { Description = "Task id or key." };
        var statusArgument = new Argument<string>("status") { Description = "New task status." };
        var noteOption = new Option<string?>("--note") { Description = "Optional note." };
        command.Arguments.Add(idOrKeyArgument);
        command.Arguments.Add(statusArgument);
        command.Options.Add(noteOption);
        command.SetAction(async (parseResult, cancellationToken) =>
        {
            await using var client = CreateClient(parseResult, urlOption, protocolVersionOption, apiKeyOption);
            var arguments = new JsonObject
            {
                ["idOrKey"] = parseResult.GetValue(idOrKeyArgument),
                ["status"] = parseResult.GetValue(statusArgument)
            };

            AddIfNotBlank(arguments, "note", parseResult.GetValue(noteOption));
            var result = await client.CallToolAsync("memorysmith_task_set_status", arguments, cancellationToken);
            await WriteJsonAsync(result, cancellationToken);
            return 0;
        });

        return command;
    }

    private static Command BuildTaskCommentCommand(Option<string?> urlOption, Option<string?> protocolVersionOption, Option<string?> apiKeyOption)
    {
        var command = new Command("comment", "Add a comment to a task.");
        var idOrKeyArgument = new Argument<string>("id-or-key") { Description = "Task id or key." };
        var bodyArgument = new Argument<string>("body") { Description = "Comment body." };
        command.Arguments.Add(idOrKeyArgument);
        command.Arguments.Add(bodyArgument);
        command.SetAction(async (parseResult, cancellationToken) =>
        {
            await using var client = CreateClient(parseResult, urlOption, protocolVersionOption, apiKeyOption);
            var arguments = new JsonObject
            {
                ["idOrKey"] = parseResult.GetValue(idOrKeyArgument),
                ["body"] = parseResult.GetValue(bodyArgument)
            };

            var result = await client.CallToolAsync("memorysmith_task_add_comment", arguments, cancellationToken);
            await WriteJsonAsync(result, cancellationToken);
            return 0;
        });

        return command;
    }

    private static Command BuildTaskAttachmentCommand(Option<string?> urlOption, Option<string?> protocolVersionOption, Option<string?> apiKeyOption)
    {
        var command = new Command("attachment", "Add a task attachment.");
        var idOrKeyArgument = new Argument<string>("id-or-key") { Description = "Task id or key." };
        var nameArgument = new Argument<string>("name") { Description = "Attachment name." };
        var uriArgument = new Argument<string>("uri") { Description = "Attachment URI." };
        var kindOption = new Option<string?>("--kind") { Description = "Attachment kind." };
        command.Arguments.Add(idOrKeyArgument);
        command.Arguments.Add(nameArgument);
        command.Arguments.Add(uriArgument);
        command.Options.Add(kindOption);
        command.SetAction(async (parseResult, cancellationToken) =>
        {
            await using var client = CreateClient(parseResult, urlOption, protocolVersionOption, apiKeyOption);
            var arguments = new JsonObject
            {
                ["idOrKey"] = parseResult.GetValue(idOrKeyArgument),
                ["name"] = parseResult.GetValue(nameArgument),
                ["uri"] = parseResult.GetValue(uriArgument)
            };

            AddIfNotBlank(arguments, "kind", parseResult.GetValue(kindOption) ?? DefaultAttachmentKind, DefaultAttachmentKind);
            var result = await client.CallToolAsync("memorysmith_task_add_attachment", arguments, cancellationToken);
            await WriteJsonAsync(result, cancellationToken);
            return 0;
        });

        return command;
    }

    private static Command BuildPageSearchCommand(Option<string?> urlOption, Option<string?> protocolVersionOption, Option<string?> apiKeyOption)
    {
        var command = new Command("search", "Search markdown pages.");
        var queryOption = new Option<string?>("--query") { Description = "Search text." };
        var limitOption = new Option<int?>("--limit") { Description = "Maximum result count." };
        command.Options.Add(queryOption);
        command.Options.Add(limitOption);
        command.SetAction(async (parseResult, cancellationToken) =>
        {
            await using var client = CreateClient(parseResult, urlOption, protocolVersionOption, apiKeyOption);
            var arguments = new JsonObject();
            AddIfNotBlank(arguments, "query", parseResult.GetValue(queryOption));
            AddIfNotNull(arguments, "limit", parseResult.GetValue(limitOption));
            var result = await client.CallToolAsync("memorysmith_page_search", arguments, cancellationToken);
            await WriteJsonAsync(result, cancellationToken);
            return 0;
        });

        return command;
    }

    private static Command BuildPageGetCommand(Option<string?> urlOption, Option<string?> protocolVersionOption, Option<string?> apiKeyOption)
    {
        var command = new Command("get", "Read one markdown page by slug.");
        var slugArgument = new Argument<string>("slug") { Description = "Page slug." };
        var maxCharactersOption = new Option<int?>("--max-characters") { Description = "Maximum number of markdown characters to return." };
        command.Arguments.Add(slugArgument);
        command.Options.Add(maxCharactersOption);
        command.SetAction(async (parseResult, cancellationToken) =>
        {
            await using var client = CreateClient(parseResult, urlOption, protocolVersionOption, apiKeyOption);
            var arguments = new JsonObject
            {
                ["slug"] = parseResult.GetValue(slugArgument)
            };

            AddIfNotNull(arguments, "maxCharacters", parseResult.GetValue(maxCharactersOption));
            var result = await client.CallToolAsync("memorysmith_page_get", arguments, cancellationToken);
            await WriteJsonAsync(result, cancellationToken);
            return 0;
        });

        return command;
    }

    private static Command BuildPageSaveCommand(Option<string?> urlOption, Option<string?> protocolVersionOption, Option<string?> apiKeyOption)
    {
        var command = new Command("save", "Create or update a wiki page.");
        var markdownArgument = new Argument<string>("markdown") { Description = "Full markdown content." };
        var slugOption = new Option<string?>("--slug") { Description = "Optional page slug." };
        var titleOption = new Option<string?>("--title") { Description = "Optional explicit title." };
        var minimumRoleOption = new Option<string?>("--minimum-role") { Description = "Optional page visibility role." };
        command.Arguments.Add(markdownArgument);
        command.Options.Add(slugOption);
        command.Options.Add(titleOption);
        command.Options.Add(minimumRoleOption);
        command.SetAction(async (parseResult, cancellationToken) =>
        {
            await using var client = CreateClient(parseResult, urlOption, protocolVersionOption, apiKeyOption);
            var arguments = new JsonObject
            {
                ["markdown"] = parseResult.GetValue(markdownArgument)
            };

            AddIfNotBlank(arguments, "slug", parseResult.GetValue(slugOption));
            AddIfNotBlank(arguments, "title", parseResult.GetValue(titleOption));
            AddIfNotBlank(arguments, "minimumRole", parseResult.GetValue(minimumRoleOption));
            var result = await client.CallToolAsync("memorysmith_page_save", arguments, cancellationToken);
            await WriteJsonAsync(result, cancellationToken);
            return 0;
        });

        return command;
    }

    private static Command BuildPageDeleteCommand(Option<string?> urlOption, Option<string?> protocolVersionOption, Option<string?> apiKeyOption)
    {
        var command = new Command("delete", "Delete a wiki page by slug.");
        var slugArgument = new Argument<string>("slug") { Description = "Page slug." };
        command.Arguments.Add(slugArgument);
        command.SetAction(async (parseResult, cancellationToken) =>
        {
            await using var client = CreateClient(parseResult, urlOption, protocolVersionOption, apiKeyOption);
            var arguments = new JsonObject
            {
                ["slug"] = parseResult.GetValue(slugArgument)
            };

            var result = await client.CallToolAsync("memorysmith_page_delete", arguments, cancellationToken);
            await WriteJsonAsync(result, cancellationToken);
            return 0;
        });

        return command;
    }

    private static McpBridgeClient CreateClient(dynamic parseResult, Option<string?> urlOption, Option<string?> protocolVersionOption, Option<string?> apiKeyOption)
    {
        var url = ResolveMcpUrl(parseResult.GetValue(urlOption));
        var protocolVersion = parseResult.GetValue(protocolVersionOption) ?? DefaultProtocolVersion;
        var apiKey = ResolveApiKey(parseResult.GetValue(apiKeyOption));
        return new McpBridgeClient(url, protocolVersion, apiKey);
    }

    private static Uri ResolveMcpUrl(string? overrideUrl)
    {
        if (!string.IsNullOrWhiteSpace(overrideUrl) && Uri.TryCreate(overrideUrl, UriKind.Absolute, out var overrideUri))
        {
            return overrideUri;
        }

        var envUrl = Environment.GetEnvironmentVariable("MEMORYSMITH_MCP_URL");
        if (!string.IsNullOrWhiteSpace(envUrl) && Uri.TryCreate(envUrl, UriKind.Absolute, out var envUri))
        {
            return envUri;
        }

        var repoRoot = FindRepoRoot();
        if (repoRoot is not null)
        {
            var vscodeMcp = Path.Combine(repoRoot, ".vscode", "mcp.json");
            if (File.Exists(vscodeMcp))
            {
                try
                {
                    using var stream = File.OpenRead(vscodeMcp);
                    using var document = JsonDocument.Parse(stream);
                    if (document.RootElement.TryGetProperty("servers", out var serversElement))
                    {
                        foreach (var server in serversElement.EnumerateObject())
                        {
                            if (server.Value.TryGetProperty("url", out var urlElement) &&
                                urlElement.ValueKind == JsonValueKind.String &&
                                Uri.TryCreate(urlElement.GetString(), UriKind.Absolute, out var fileUri))
                            {
                                return fileUri;
                            }
                        }
                    }
                }
                catch
                {
                    // Fall through to the documented default.
                }
            }
        }

        return new Uri(DefaultMcpUrl);
    }

    private static string? ResolveApiKey(string? overrideApiKey)
    {
        if (!string.IsNullOrWhiteSpace(overrideApiKey))
        {
            return overrideApiKey;
        }

        var envApiKey = Environment.GetEnvironmentVariable("MEMORYSMITH_API_KEY");
        if (!string.IsNullOrWhiteSpace(envApiKey))
        {
            return envApiKey;
        }

        envApiKey = Environment.GetEnvironmentVariable("MemorySmith__ApiKey");
        if (!string.IsNullOrWhiteSpace(envApiKey))
        {
            return envApiKey;
        }

        return null;
    }

    private static string? FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "MemorySmith.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }

    private static JsonObject ParseArguments(string? rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            return new JsonObject();
        }

        var node = JsonNode.Parse(rawJson);
        return node as JsonObject ?? new JsonObject();
    }

    private static void AddIfNotBlank(JsonObject target, string name, string? value, string? defaultValue = null)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        if (defaultValue is not null && string.Equals(value, defaultValue, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        target[name] = value;
    }

    private static void AddIfNotNull(JsonObject target, string name, int? value)
    {
        if (value.HasValue)
        {
            target[name] = value.Value;
        }
    }

    private static void AddIfNotNull(JsonObject target, string name, DateTimeOffset? value)
    {
        if (value.HasValue)
        {
            target[name] = value.Value;
        }
    }

    private static void AddLabels(JsonObject target, string? labels)
    {
        if (string.IsNullOrWhiteSpace(labels))
        {
            return;
        }

        var items = labels.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (items.Length > 0)
        {
            target["labels"] = new JsonArray(items.Select(label => (JsonNode?)label).ToArray());
        }
    }

    private static async Task WriteJsonAsync(JsonNode? node, CancellationToken cancellationToken)
    {
        if (node is null)
        {
            await Console.Out.WriteLineAsync("null");
            return;
        }

        var json = node.ToJsonString(JsonOptions);
        await Console.Out.WriteLineAsync(json);
    }

    private sealed class McpBridgeClient : IAsyncDisposable
    {
        private readonly HttpClient _httpClient;
        private readonly string _protocolVersion;
        private bool _initialized;

        public McpBridgeClient(Uri endpoint, string protocolVersion, string? apiKey)
        {
            _protocolVersion = protocolVersion;
            _httpClient = new HttpClient
            {
                BaseAddress = endpoint,
                Timeout = TimeSpan.FromSeconds(60)
            };
            _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("MemorySmith.Bridge/1.0");
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("X-Api-Key", apiKey);
            }
        }

        public async Task<JsonNode?> ListToolsAsync(CancellationToken cancellationToken)
        {
            await EnsureInitializedAsync(cancellationToken);
            return await SendAsync("tools/list", null, cancellationToken);
        }

        public async Task<JsonNode?> CallToolAsync(string toolName, JsonObject arguments, CancellationToken cancellationToken)
        {
            await EnsureInitializedAsync(cancellationToken);
            var payload = new JsonObject
            {
                ["name"] = toolName,
                ["arguments"] = arguments
            };

            return await SendAsync("tools/call", payload, cancellationToken);
        }

        private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
        {
            if (_initialized)
            {
                return;
            }

            var initParams = new JsonObject
            {
                ["protocolVersion"] = _protocolVersion,
                ["capabilities"] = new JsonObject
                {
                    ["tools"] = new JsonObject()
                },
                ["clientInfo"] = new JsonObject
                {
                    ["name"] = "MemorySmith.Bridge",
                    ["version"] = "1.0.0"
                }
            };

            await SendAsync("initialize", initParams, cancellationToken);
            _initialized = true;
        }

        private async Task<JsonNode?> SendAsync(string method, JsonObject? parameters, CancellationToken cancellationToken)
        {
            var request = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = Guid.NewGuid().ToString("N"),
                ["method"] = method
            };

            if (parameters is not null)
            {
                request["params"] = parameters;
            }

            using var response = await _httpClient.PostAsJsonAsync(string.Empty, request, JsonOptions, cancellationToken);
            var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"MCP request '{method}' failed with {(int)response.StatusCode} {response.ReasonPhrase}: {responseJson}");
            }

            var node = JsonNode.Parse(responseJson);
            if (node is not JsonObject responseObject)
            {
                throw new InvalidOperationException($"MCP request '{method}' returned a non-object response.");
            }

            if (responseObject.TryGetPropertyValue("error", out var errorNode) && errorNode is not null)
            {
                throw new InvalidOperationException($"MCP request '{method}' failed: {errorNode.ToJsonString(JsonOptions)}");
            }

            responseObject.TryGetPropertyValue("result", out var resultNode);
            return resultNode;
        }

        public ValueTask DisposeAsync()
        {
            _httpClient.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}