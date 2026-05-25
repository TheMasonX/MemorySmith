using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MemorySmith.App.Controllers;
using MemorySmith.App.Services;
using MemorySmith.Core.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Data.Sqlite;

namespace MemorySmith.Tests;

[TestFixture]
public class AppApiContractTests
{
    private string _tempDir = null!;
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;

    [SetUp]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"memorysmith-api-{Guid.NewGuid():N}");
        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, config) =>
                {
                    config.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["MemorySmith:DataPath"] = Path.Combine(_tempDir, "Memories"),
                        ["MemorySmith:PagesPath"] = Path.Combine(_tempDir, "Pages"),
                        ["MemorySmith:EventLogPath"] = Path.Combine(_tempDir, "Events", "audit.log"),
                        ["MemorySmith:DataProtectionKeysPath"] = Path.Combine(_tempDir, "Keys"),
                        ["MemorySmith:Database:ConnectionString"] = $"Data Source={Path.Combine(_tempDir, "memorysmith.db")};Pooling=False",
                        ["MemorySmith:Audit:JsonlPath"] = Path.Combine(_tempDir, "Events", "audit-{yyyy}-W{week}.jsonl"),
                        ["MemorySmith:History:RootPath"] = Path.Combine(_tempDir, ".history"),
                        ["MemorySmith:Maintenance:Enabled"] = "false"
                    });
                });
            });
        _client = _factory.CreateClient();
    }

    [TearDown]
    public void TearDown()
    {
        _client.Dispose();
        _factory.Dispose();
        Serilog.Log.CloseAndFlush();
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [Test]
    public async Task TasksPageRoute_ReturnsSuccessAndContainsTasksHeading()
    {
        var response = await _client.GetAsync("/tasks");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(body, Does.Contain("Tasks"));
        });
    }

    [Test]
    public async Task RequestPipeline_EmitsProblemDetailsTraceIdAndStructuredRequestCorrelation()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"memorysmith-request-pipeline-{Guid.NewGuid():N}");
        var factory = CreateRequestPipelineFactory(tempDir);

        try
        {
            using var client = factory.CreateClient();
            var normalResponse = await client.GetAsync("/api/health/live");
            normalResponse.EnsureSuccessStatusCode();
            var normalEntry = await WaitForStructuredLogEntryAsync(tempDir, entry =>
                GetString(entry, "SourceContext") == "Serilog.AspNetCore.RequestLoggingMiddleware" &&
                GetString(entry, "RequestPath") == "/api/health/live" &&
                GetInt32(entry, "StatusCode") == StatusCodes.Status200OK);

            var failureResponse = await client.GetAsync("/api/test-failures/throw");
            var problemJson = await failureResponse.Content.ReadAsStringAsync();
            using var problem = JsonDocument.Parse(problemJson);
            var traceId = GetString(problem.RootElement, "traceId");
            var requestEntry = await WaitForStructuredLogEntryAsync(tempDir, entry =>
                GetString(entry, "SourceContext") == "Serilog.AspNetCore.RequestLoggingMiddleware" &&
                GetString(entry, "RequestPath") == "/api/test-failures/throw" &&
                GetInt32(entry, "StatusCode") == StatusCodes.Status500InternalServerError);
            var exceptionEntry = await WaitForStructuredLogEntryAsync(tempDir, entry =>
                GetString(entry, "@mt") == "Unhandled request failure {Method} {Path} TraceId={TraceId}" &&
                GetString(entry, "TraceId") == traceId);

            Assert.Multiple(() =>
            {
                Assert.That(GetString(normalEntry, "TraceId"), Is.Not.Null.And.Not.Empty);
                Assert.That(GetString(normalEntry, "CorrelationId"), Is.Not.Null.And.Not.Empty);
                Assert.That(GetString(normalEntry, "RequestMethod"), Is.EqualTo("GET"));
                Assert.That(GetDouble(normalEntry, "Elapsed"), Is.GreaterThanOrEqualTo(0));
                Assert.That(GetString(normalEntry, "@l"), Is.EqualTo("Warning"));
                Assert.That(failureResponse.StatusCode, Is.EqualTo(HttpStatusCode.InternalServerError));
                Assert.That(failureResponse.Content.Headers.ContentType?.MediaType, Is.EqualTo("application/problem+json"));
                Assert.That(GetString(problem.RootElement, "title"), Is.EqualTo("An unexpected error occurred."));
                Assert.That(traceId, Is.Not.Null.And.Not.Empty);
                Assert.That(problemJson, Does.Not.Contain("Synthetic TSK-0105 test failure"));
                Assert.That(GetString(requestEntry, "TraceId"), Is.EqualTo(traceId));
                Assert.That(GetString(requestEntry, "CorrelationId"), Is.Not.Null.And.Not.Empty);
                Assert.That(GetDouble(requestEntry, "Elapsed"), Is.GreaterThanOrEqualTo(0));
                Assert.That(GetString(exceptionEntry, "Path"), Is.EqualTo("/api/test-failures/throw"));
            });
        }
        finally
        {
            await DisposeFactoryTempDirAsync(factory, tempDir);
        }
    }

    [Test]
    public async Task TasksApi_FullWorkflow_SupportsCrudHistoryAndRelatedArtifacts()
    {
        var invalidCreate = await _client.PostAsJsonAsync("/api/tasks", new TaskCreateRequest(
            Title: "Invalid task",
            Description: "Invalid assignee payload",
            Type: "Task",
            Status: TaskStatuses.Backlog,
            Priority: TaskPriorities.Medium,
            AssigneeMode: TaskAssigneeModes.Directory,
            AssigneeDirectoryId: null,
            AssigneeCustomText: null,
            Reporter: "copilot",
            Labels: ["invalid"],
            DueDateUtc: null,
            EpicId: null,
            ParentId: null));

        var createResponse = await _client.PostAsJsonAsync("/api/tasks", new TaskCreateRequest(
            Title: "Implement task backend",
            Description: "Track full backend delivery",
            Type: "Feature",
            Status: TaskStatuses.Backlog,
            Priority: TaskPriorities.High,
            AssigneeMode: TaskAssigneeModes.Custom,
            AssigneeDirectoryId: null,
            AssigneeCustomText: "Copilot",
            Reporter: "copilot",
            Labels: ["tasks", "backend"],
            DueDateUtc: null,
            EpicId: null,
            ParentId: null,
            Slug: "backend-delivery"));

        createResponse.EnsureSuccessStatusCode();
        var created = await createResponse.Content.ReadFromJsonAsync<TaskItem>();
        Assert.That(created, Is.Not.Null);

        var statusResponse = await _client.PostAsJsonAsync($"/api/tasks/{created!.Id}/status", new TaskStatusUpdateRequest(TaskStatuses.InProgress, "starting implementation"));
        statusResponse.EnsureSuccessStatusCode();

        var updateResponse = await _client.PutAsJsonAsync($"/api/tasks/{created.Id}", new TaskUpdateRequest(
            Title: "Implement task backend v2",
            Description: "Track full backend delivery with triage updates",
            Type: null,
            Priority: TaskPriorities.Critical,
            AssigneeMode: TaskAssigneeModes.Directory,
            AssigneeDirectoryId: "reviewer-01",
            AssigneeCustomText: string.Empty,
            Reporter: null,
            Labels: ["tasks", "backend", "vetted"],
            DueDateUtc: null,
            EpicId: null,
            ParentId: null));
        updateResponse.EnsureSuccessStatusCode();

        var commentResponse = await _client.PostAsJsonAsync($"/api/tasks/{created.Id}/comments", new TaskCommentRequest("Progress update"));
        commentResponse.EnsureSuccessStatusCode();

        var pageLinkResponse = await _client.PostAsJsonAsync($"/api/tasks/{created.Id}/links/pages", new TaskPageLinkRequest("plans/tasks-page-feature-design-20260523"));
        pageLinkResponse.EnsureSuccessStatusCode();

        var invalidPageLinkResponse = await _client.PostAsJsonAsync($"/api/tasks/{created.Id}/links/pages", new TaskPageLinkRequest("does/not/exist"));
        invalidPageLinkResponse.EnsureSuccessStatusCode();

        var externalLinkResponse = await _client.PostAsJsonAsync($"/api/tasks/{created.Id}/links/external", new TaskExternalLinkRequest("Spec", "https://example.test/spec"));
        externalLinkResponse.EnsureSuccessStatusCode();

        var invalidAttachmentResponse = await _client.PostAsJsonAsync($"/api/tasks/{created.Id}/attachments", new TaskAttachmentRequest("Screenshot", "image", "javascript:alert('xss')"));
        Assert.That(invalidAttachmentResponse.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));

        var attachmentResponse = await _client.PostAsJsonAsync($"/api/tasks/{created.Id}/attachments", new TaskAttachmentRequest("Screenshot", "image", "https://example.test/artifacts/browser-validation/tasks.png"));
        attachmentResponse.EnsureSuccessStatusCode();
        var withAttachment = await attachmentResponse.Content.ReadFromJsonAsync<TaskItem>();

        var history = await _client.GetFromJsonAsync<List<TaskActivityEntry>>($"/api/tasks/{created.Id}/history");
        var list = await _client.GetFromJsonAsync<List<TaskSummary>>("/api/tasks?query=backend");
        var loaded = await _client.GetFromJsonAsync<TaskItem>($"/api/tasks/{created.Id}");
        var removeAttachmentResponse = await _client.DeleteAsync($"/api/tasks/{created.Id}/attachments/{withAttachment!.Attachments[0].Id}");
        var softDeleteResponse = await _client.DeleteAsync($"/api/tasks/{created.Id}");
        var archived = await _client.GetFromJsonAsync<TaskItem>($"/api/tasks/{created.Id}");

        Assert.Multiple(() =>
        {
            var createdSummary = list!.Single(item => item.Id == created.Id);
            Assert.That(invalidCreate.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(createResponse.StatusCode, Is.EqualTo(HttpStatusCode.Created));
            Assert.That(created.Id, Does.Contain("backend-delivery"));
            Assert.That(list, Is.Not.Null);
            Assert.That(list!.Any(item => item.Id == created.Id), Is.True);
            Assert.That(createdSummary.AssigneeMode, Is.EqualTo(TaskAssigneeModes.Directory));
            Assert.That(createdSummary.AssigneeDirectoryId, Is.EqualTo("reviewer-01"));
            Assert.That(createdSummary.AssigneeCustomText, Is.Null);
            Assert.That(loaded, Is.Not.Null);
            Assert.That(loaded!.Title, Is.EqualTo("Implement task backend v2"));
            Assert.That(loaded.Description, Does.Contain("triage"));
            Assert.That(loaded.Priority, Is.EqualTo(TaskPriorities.Critical));
            Assert.That(loaded.AssigneeMode, Is.EqualTo(TaskAssigneeModes.Directory));
            Assert.That(loaded.AssigneeDirectoryId, Is.EqualTo("reviewer-01"));
            Assert.That(loaded.Labels, Does.Contain("vetted"));
            Assert.That(loaded!.Comments.Count, Is.EqualTo(1));
            Assert.That(loaded.ExternalLinks.Count, Is.EqualTo(1));
            Assert.That(loaded.Attachments.Count, Is.EqualTo(1));
            Assert.That(loaded.Attachments[0].Uri, Is.EqualTo("https://example.test/artifacts/browser-validation/tasks.png"));
            Assert.That(loaded.LinkedPages.Count, Is.EqualTo(2));
            Assert.That(loaded.LinkedPages, Does.Contain("plans/tasks-page-feature-design-20260523"));
            Assert.That(loaded.LinkedPages, Does.Contain("does/not/exist"));
            Assert.That(history, Is.Not.Null);
            Assert.That(history!.Any(item => item.Action == "created"), Is.True);
            Assert.That(history!.Any(item => item.Action == "page_link_warning" && item.Note!.Contains("does/not/exist", StringComparison.OrdinalIgnoreCase)), Is.True);
            Assert.That(removeAttachmentResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(softDeleteResponse.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
            Assert.That(archived, Is.Not.Null);
            Assert.That(archived!.Status, Is.EqualTo(TaskStatuses.Archived));
            Assert.That(Directory.EnumerateFiles(Path.Combine(_tempDir, "Tasks"), "*.json", SearchOption.TopDirectoryOnly).Any(), Is.True);
            Assert.That(File.Exists(Path.Combine(_tempDir, "Events", "tasks.activity.jsonl")), Is.True);
        });
    }

        [Test]
        public async Task TasksApi_MalformedTaskFile_DoesNotCrashAndReturnsLoadErrorMetadata()
        {
                var tasksRoot = Path.Combine(_tempDir, "Tasks");
                Directory.CreateDirectory(tasksRoot);
                var malformedPath = Path.Combine(tasksRoot, "tsk-9999-broken-json.json");
                var malformedJson = """
{
    "id": "tsk-9999-broken-json",
    "key": "TSK-9999",
    "title": "Broken task record",
    "description": "Line one
Line two",
    "type": "Task",
    "status": "Backlog",
    "priority": "High",
    "assigneeMode": "Custom",
    "assigneeCustomText": "Copilot"
}
""";
                await File.WriteAllTextAsync(malformedPath, malformedJson);

                var pageResponse = await _client.GetAsync("/tasks");
                var listResponse = await _client.GetAsync("/api/tasks?limit=200");
                var list = await listResponse.Content.ReadFromJsonAsync<List<TaskSummary>>();
                var malformedSummary = list?.FirstOrDefault(item => item.Key == "TSK-9999" || item.Id == "tsk-9999-broken-json");
                var loaded = await _client.GetFromJsonAsync<TaskItem>("/api/tasks/TSK-9999");

                Assert.Multiple(() =>
                {
                        Assert.That(pageResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                        Assert.That(listResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                        Assert.That(list, Is.Not.Null);
                        Assert.That(malformedSummary, Is.Not.Null);
                        Assert.That(malformedSummary!.HasLoadError, Is.True);
                        Assert.That(malformedSummary.SourceFilePath, Is.EqualTo("tsk-9999-broken-json.json"));
                        Assert.That(loaded, Is.Not.Null);
                        Assert.That(loaded!.HasLoadError, Is.True);
                        Assert.That(loaded.LoadError, Is.Not.Null.And.Not.Empty);
                });
        }

    [Test]
    public async Task TasksApi_MalformedTaskFile_MutationEndpointsReturnBadRequest()
    {
        var tasksRoot = Path.Combine(_tempDir, "Tasks");
        Directory.CreateDirectory(tasksRoot);
        var malformedPath = Path.Combine(tasksRoot, "tsk-9999-broken-json.json");
        var malformedJson = """
{
    "id": "tsk-9999-broken-json",
    "key": "TSK-9999",
    "title": "Broken task record",
    "description": "Line one
Line two",
    "type": "Task",
    "status": "Backlog",
    "priority": "High",
    "assigneeMode": "Custom",
    "assigneeCustomText": "Copilot"
}
""";
        await File.WriteAllTextAsync(malformedPath, malformedJson);

        var statusResponse = await _client.PostAsJsonAsync(
            "/api/tasks/TSK-9999/status",
            new TaskStatusUpdateRequest(TaskStatuses.InProgress, "attempt mutation"));
        var deleteResponse = await _client.DeleteAsync("/api/tasks/TSK-9999");

        Assert.Multiple(() =>
        {
            Assert.That(statusResponse.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(deleteResponse.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        });
    }

    [Test]
    public async Task TasksApi_HybridSemanticSearch_DefaultOn_FindsReorderedQuery()
    {
        var createPrimary = await _client.PostAsJsonAsync("/api/tasks", new TaskCreateRequest(
            Title: "Bootstrap hardening for auth setup",
            Description: "Protect first-admin setup and authentication flow from forgery risks",
            Type: "Task",
            Status: TaskStatuses.Backlog,
            Priority: TaskPriorities.High,
            AssigneeMode: TaskAssigneeModes.Custom,
            AssigneeDirectoryId: null,
            AssigneeCustomText: "Copilot",
            Reporter: "copilot",
            Labels: ["security", "auth", "bootstrap"],
            DueDateUtc: null,
            EpicId: null,
            ParentId: null,
            Slug: "bootstrap-auth-hardening"));
        createPrimary.EnsureSuccessStatusCode();

        var createSecondary = await _client.PostAsJsonAsync("/api/tasks", new TaskCreateRequest(
            Title: "Tag manager color polish",
            Description: "Improve visual hierarchy in admin tag manager",
            Type: "Task",
            Status: TaskStatuses.Backlog,
            Priority: TaskPriorities.Medium,
            AssigneeMode: TaskAssigneeModes.Custom,
            AssigneeDirectoryId: null,
            AssigneeCustomText: "Copilot",
            Reporter: "copilot",
            Labels: ["ui", "polish"],
            DueDateUtc: null,
            EpicId: null,
            ParentId: null,
            Slug: "tag-manager-color-polish"));
        createSecondary.EnsureSuccessStatusCode();

        var results = await _client.GetFromJsonAsync<List<TaskSummary>>("/api/tasks?query=setup bootstrap auth hardening&limit=50");

        Assert.That(results, Is.Not.Null);
        Assert.That(results!.Any(item => item.Title.Contains("Bootstrap hardening for auth setup", StringComparison.OrdinalIgnoreCase)), Is.True);
    }

    [Test]
    public async Task TasksApi_ReadEndpoints_AnonymousDisabled_RejectsUnauthenticatedCaller()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"memorysmith-tasks-auth-{Guid.NewGuid():N}");
        var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, config) =>
                {
                    config.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["MemorySmith:DataPath"] = Path.Combine(tempDir, "Memories"),
                        ["MemorySmith:PagesPath"] = Path.Combine(tempDir, "Pages"),
                        ["MemorySmith:EventLogPath"] = Path.Combine(tempDir, "Events", "audit.log"),
                        ["MemorySmith:DataProtectionKeysPath"] = Path.Combine(tempDir, "Keys"),
                        ["MemorySmith:Database:ConnectionString"] = $"Data Source={Path.Combine(tempDir, "memorysmith.db")};Pooling=False",
                        ["MemorySmith:Audit:JsonlPath"] = Path.Combine(tempDir, "Events", "audit-{yyyy}-W{week}.jsonl"),
                        ["MemorySmith:History:RootPath"] = Path.Combine(tempDir, ".history"),
                        ["MemorySmith:Maintenance:Enabled"] = "false",
                        ["MemorySmith:Auth:AnonymousAccess"] = "None",
                        ["MemorySmith:Auth:OpenLocalEditorCompatibility"] = "false"
                    });
                });
            });

        try
        {
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            var listResponse = await client.GetAsync("/api/tasks?limit=1");
            var detailResponse = await client.GetAsync("/api/tasks/TSK-0001");

            Assert.Multiple(() =>
            {
                Assert.That(listResponse.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized).Or.EqualTo(HttpStatusCode.Forbidden).Or.EqualTo(HttpStatusCode.Redirect));
                Assert.That(detailResponse.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized).Or.EqualTo(HttpStatusCode.Forbidden).Or.EqualTo(HttpStatusCode.Redirect));
            });
        }
        finally
        {
            factory.Dispose();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Test]
    public async Task GetMemories_ClampsPageSizeAndKeepsRouteContract()
    {
        var response = await _client.GetFromJsonAsync<PagedResult<MemoryMetadata>>("/api/memories?page=-5&pageSize=500");

        Assert.Multiple(() =>
        {
            Assert.That(response, Is.Not.Null);
            Assert.That(response!.Page, Is.EqualTo(1));
            Assert.That(response.PageSize, Is.EqualTo(100));
        });
    }

    [Test]
    public async Task PostMemory_WithInvalidBody_ReturnsValidationProblem()
    {
        var response = await _client.PostAsJsonAsync("/api/memories", new MemoryRecord
        {
            Id = "invalid-content",
            Title = "Invalid",
            Content = ""
        });

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        var body = await response.Content.ReadAsStringAsync();
        Assert.That(body, Does.Contain("Content"));
    }

    [Test]
    public async Task CreateGetIncrementDelete_FullApiWorkflow_PersistsRealFiles()
    {
        var createResponse = await _client.PostAsJsonAsync("/api/memories", new MemoryRecord
        {
            Id = "workflow",
            Title = "Workflow",
            Content = "Real file-backed workflow",
            Tags = [" api ", "api"]
        });
        createResponse.EnsureSuccessStatusCode();

        var created = await createResponse.Content.ReadFromJsonAsync<MemoryRecord>();
        var loaded = await _client.GetFromJsonAsync<MemoryRecord>($"/api/memories/{created!.Id}");
        var usageResponse = await _client.PostAsync($"/api/memories/{created.Id}/usage", null);
        usageResponse.EnsureSuccessStatusCode();
        var deleteResponse = await _client.DeleteAsync($"/api/memories/{created.Id}");

        Assert.Multiple(() =>
        {
            Assert.That(createResponse.StatusCode, Is.EqualTo(HttpStatusCode.Created));
            Assert.That(loaded!.Tags, Is.EqualTo(new[] { "api" }));
            Assert.That(deleteResponse.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
            Assert.That(Directory.EnumerateFiles(Path.Combine(_tempDir, "Memories"), "*.json", SearchOption.AllDirectories), Is.Empty);
            Assert.That(Directory.EnumerateFiles(Path.Combine(_tempDir, ".history"), "*.snapshot.json", SearchOption.AllDirectories).Count(), Is.GreaterThanOrEqualTo(2));
        });
    }

    [Test]
    public async Task SharedApiKey_CanWriteAfterFirstAdminExists()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"memorysmith-api-key-{Guid.NewGuid():N}");
        const string apiKey = "contract-secret";
        var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, config) =>
                {
                    config.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["MemorySmith:DataPath"] = Path.Combine(tempDir, "Memories"),
                        ["MemorySmith:PagesPath"] = Path.Combine(tempDir, "Pages"),
                        ["MemorySmith:EventLogPath"] = Path.Combine(tempDir, "Events", "audit.log"),
                        ["MemorySmith:DataProtectionKeysPath"] = Path.Combine(tempDir, "Keys"),
                        ["MemorySmith:Database:ConnectionString"] = $"Data Source={Path.Combine(tempDir, "memorysmith.db")};Pooling=False",
                        ["MemorySmith:Audit:JsonlPath"] = Path.Combine(tempDir, "Events", "audit-{yyyy}-W{week}.jsonl"),
                        ["MemorySmith:History:RootPath"] = Path.Combine(tempDir, ".history"),
                        ["MemorySmith:Maintenance:Enabled"] = "false",
                        ["MemorySmith:ApiKey"] = apiKey
                    });
                });
            });

        try
        {
            using var setupClient = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            setupClient.DefaultRequestHeaders.Add(MemorySmithRequestGuardMiddleware.ApiKeyHeaderName, apiKey);
            var setupResponse = await setupClient.PostAsJsonAsync("/api/admin/setup", new SetupAdminRequest("Admin User", "admin@example.test", "ThisIsAValidPassword123!"));
            setupResponse.EnsureSuccessStatusCode();

            using var apiClient = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            apiClient.DefaultRequestHeaders.Add(MemorySmithRequestGuardMiddleware.ApiKeyHeaderName, apiKey);
            var createResponse = await apiClient.PostAsJsonAsync("/api/memories", new MemoryRecord
            {
                Id = "api-key-write",
                Title = "API key write",
                Content = "Compatibility write path"
            });

            Assert.That(createResponse.StatusCode, Is.EqualTo(HttpStatusCode.Created));
        }
        finally
        {
            factory.Dispose();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Test]
    public async Task AdminPage_WithAnonymousAdminConfig_DoesNotRenderAdminWorkbenchForSignedOutUser()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"memorysmith-admin-page-{Guid.NewGuid():N}");
        var factory = CreateIsolatedFactory(tempDir, new Dictionary<string, string?>
        {
            ["MemorySmith:Auth:AnonymousAccess"] = MemorySmithRoles.Admin,
            ["MemorySmith:Auth:AuthenticatedDefaultRole"] = MemorySmithRoles.Admin,
            ["MemorySmith:Auth:AutoEditorForAuthenticatedUsers"] = "true"
        });

        try
        {
            using var setupClient = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            var setupResponse = await setupClient.PostAsJsonAsync("/api/admin/setup", new SetupAdminRequest("Admin User", "admin@example.test", "ThisIsAValidPassword123!"));
            setupResponse.EnsureSuccessStatusCode();

            using var anonymousClient = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            var pageResponse = await anonymousClient.GetAsync("/admin");
            var body = await pageResponse.Content.ReadAsStringAsync();

            Assert.Multiple(() =>
            {
                Assert.That(pageResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK).Or.EqualTo(HttpStatusCode.Redirect).Or.EqualTo(HttpStatusCode.Unauthorized).Or.EqualTo(HttpStatusCode.Forbidden));
                Assert.That(body, Does.Not.Contain("Users, OAuth, models, settings, audit, history"));
                if (pageResponse.StatusCode == HttpStatusCode.OK)
                {
                    Assert.That(body, Does.Contain("Sign In"));
                }
            });
        }
        finally
        {
            await DisposeFactoryTempDirAsync(factory, tempDir);
        }
    }

    [Test]
    public async Task AdminRoleApi_WithAnonymousAdminConfig_RejectsSignedOutRoleChanges()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"memorysmith-admin-api-{Guid.NewGuid():N}");
        var factory = CreateIsolatedFactory(tempDir, new Dictionary<string, string?>
        {
            ["MemorySmith:Auth:AnonymousAccess"] = MemorySmithRoles.Admin,
            ["MemorySmith:Auth:AuthenticatedDefaultRole"] = MemorySmithRoles.Admin,
            ["MemorySmith:Auth:AutoEditorForAuthenticatedUsers"] = "true"
        });

        try
        {
            using var setupClient = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            var setupResponse = await setupClient.PostAsJsonAsync("/api/admin/setup", new SetupAdminRequest("Admin User", "admin@example.test", "ThisIsAValidPassword123!"));
            setupResponse.EnsureSuccessStatusCode();

            using var anonymousClient = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            var roleResponse = await anonymousClient.PostAsync($"/api/admin/users/{Guid.NewGuid():N}/roles/{MemorySmithRoles.Editor}", null);

            Assert.That(IsAuthChallenge(roleResponse.StatusCode), Is.True);
        }
        finally
        {
            await DisposeFactoryTempDirAsync(factory, tempDir);
        }
    }

    [Test]
    public async Task AdminApi_WithAuthDisabled_StillRejectsSignedOutAdminAccess()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"memorysmith-admin-auth-disabled-{Guid.NewGuid():N}");
        var factory = CreateIsolatedFactory(tempDir, new Dictionary<string, string?>
        {
            ["MemorySmith:Auth:Enabled"] = "false"
        });

        try
        {
            using var anonymousClient = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            var usersResponse = await anonymousClient.GetAsync("/api/admin/users");
            var settingsResponse = await anonymousClient.GetAsync("/api/admin/settings");

            Assert.Multiple(() =>
            {
                Assert.That(IsAuthChallenge(usersResponse.StatusCode), Is.True);
                Assert.That(IsAuthChallenge(settingsResponse.StatusCode), Is.True);
            });
        }
        finally
        {
            await DisposeFactoryTempDirAsync(factory, tempDir);
        }
    }

    [Test]
    public async Task AdminSettings_UpdateRequiresAdminAndPersistsAllowedSetting()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"memorysmith-admin-settings-{Guid.NewGuid():N}");
        var settingsPath = Path.Combine(tempDir, "appsettings.LocalDevelopment.json");
        var factory = CreateIsolatedFactory(tempDir, new Dictionary<string, string?>
        {
            ["MemorySmith:SettingsOverridePath"] = settingsPath
        });

        try
        {
            using var anonymousClient = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            var anonymousResponse = await anonymousClient.PutAsJsonAsync("/api/admin/settings", new AdminSettingUpdateRequest("MemorySmith:Chat:MaxToolIterations", "3"));

            using var adminClient = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            var setupResponse = await adminClient.PostAsJsonAsync("/api/admin/setup", new SetupAdminRequest("Admin User", "admin@example.test", "ThisIsAValidPassword123!"));
            setupResponse.EnsureSuccessStatusCode();
            var settings = await adminClient.GetFromJsonAsync<IReadOnlyList<AdminSettingItem>>("/api/admin/settings") ?? [];
            var updateResponse = await adminClient.PutAsJsonAsync("/api/admin/settings", new AdminSettingUpdateRequest("MemorySmith:Chat:MaxToolIterations", "3"));
            var defaultVisibilityResponse = await adminClient.PutAsJsonAsync("/api/admin/settings", new AdminSettingUpdateRequest("MemorySmith:Pages:DefaultMinimumRole", PageAccessLevels.Authenticated));
            var sourceRootsResponse = await adminClient.PutAsJsonAsync("/api/admin/settings", new AdminSettingUpdateRequest("MemorySmith:SourceLinks:AllowedFileRoots", $"{Path.Combine(tempDir, "allowed-one")}\n{Path.Combine(tempDir, "allowed-two")}"));
            var nullableContextResponse = await adminClient.PutAsJsonAsync("/api/admin/settings", new AdminSettingUpdateRequest("MemorySmith:Chat:OllamaContextWindowTokens", string.Empty));

            Assert.Multiple(() =>
            {
                Assert.That(IsAuthChallenge(anonymousResponse.StatusCode), Is.True);
                Assert.That(settings, Is.Not.Empty);
                Assert.That(settings.All(setting => !string.IsNullOrWhiteSpace(setting.HelpText) && setting.HelpText.Length > 40), Is.True);
                Assert.That(settings.All(setting => !setting.HelpText.StartsWith("Controls MemorySmith:", StringComparison.Ordinal)), Is.True);
                Assert.That(settings.Select(setting => setting.Key), Does.Contain("MemorySmith:Database:UseWal"));
                Assert.That(settings.Select(setting => setting.Key), Does.Contain("MemorySmith:SourceLinks:AllowedFileRoots"));
                Assert.That(settings.Select(setting => setting.Key), Does.Contain("MemorySmith:MaintenanceAgent:ResourceProbe:BusyProcessNames"));
                Assert.That(settings.Single(setting => setting.Key == "MemorySmith:ApiKey").IsSensitive, Is.True);
                Assert.That(updateResponse.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
                Assert.That(defaultVisibilityResponse.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
                Assert.That(sourceRootsResponse.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
                Assert.That(nullableContextResponse.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
                Assert.That(File.Exists(settingsPath), Is.True);
            });

            var json = await File.ReadAllTextAsync(settingsPath);
            Assert.That(json, Does.Contain("\"MaxToolIterations\": 3"));
            Assert.That(json, Does.Contain("\"DefaultMinimumRole\": \"Authenticated\""));
            Assert.That(json, Does.Contain("\"AllowedFileRoots\": ["));
            Assert.That(json, Does.Contain("\"OllamaContextWindowTokens\": null"));
        }
        finally
        {
            await DisposeFactoryTempDirAsync(factory, tempDir);
        }
    }

    [Test]
    public async Task HealthLiveAndReady_ReturnSuccessWithoutStartingWorker()
    {
        var live = await _client.GetAsync("/api/health/live");
        var ready = await _client.GetAsync("/api/health/ready");

        Assert.Multiple(() =>
        {
            Assert.That(live.IsSuccessStatusCode, Is.True);
            Assert.That(ready.IsSuccessStatusCode, Is.True);
        });
    }

    [Test]
    public async Task Diagnostics_ReturnsRedactedConfigurationAndPathStatus()
    {
        var setupResponse = await _client.PostAsJsonAsync("/api/admin/setup", new SetupAdminRequest("Admin User", "admin@example.test", "ThisIsAValidPassword123!"));
        setupResponse.EnsureSuccessStatusCode();

        var response = await _client.GetAsync("/api/diagnostics");
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();

        Assert.Multiple(() =>
        {
            Assert.That(body, Does.Contain("configuration"));
            Assert.That(body, Does.Contain("dataPath"));
            Assert.That(body, Does.Contain("pagesPath"));
            Assert.That(body, Does.Contain("apiKeyConfigured"));
            Assert.That(body, Does.Contain("warnings"));
            Assert.That(body, Does.Contain("paths"));
            Assert.That(body, Does.Contain("storageDiagnostics"));
            Assert.That(body, Does.Not.Contain("apiKey\""));
        });
    }

    [Test]
    public async Task DiagnosticsMeasurementBaseline_ReturnsSearchGovernanceAndPageMetrics()
    {
        var setupResponse = await _client.PostAsJsonAsync("/api/admin/setup", new SetupAdminRequest("Admin User", "admin@example.test", "ThisIsAValidPassword123!"));
        setupResponse.EnsureSuccessStatusCode();

        var response = await _client.GetAsync("/api/diagnostics/measurement-baseline");
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();

        Assert.Multiple(() =>
        {
            Assert.That(body, Does.Contain("search"));
            Assert.That(body, Does.Contain("semanticSearchMode"));
            Assert.That(body, Does.Contain("pages"));
            Assert.That(body, Does.Contain("tags"));
            Assert.That(body, Does.Contain("sourceLinks"));
            Assert.That(body, Does.Contain("thresholds"));
        });
    }

    [Test]
    public async Task PagesApi_SavesSearchesRendersAndDeletesMarkdownPages()
    {
        var createResponse = await _client.PostAsJsonAsync("/api/pages", new PageSaveRequest(
            "contract-page",
            "Contract Page",
            "Body with ![image](assets/example.png)"));
        createResponse.EnsureSuccessStatusCode();

        var created = await createResponse.Content.ReadFromJsonAsync<PageDocument>();
        var search = await _client.GetFromJsonAsync<PageSummary[]>("/api/pages?query=contract");
        var searchEnvelope = await _client.GetFromJsonAsync<RetrievalResultEnvelope<PageSummary>>("/api/pages?query=contract&format=envelope");
        var html = await _client.GetStringAsync("/api/pages/contract-page/html");
        var deleteResponse = await _client.DeleteAsync("/api/pages/contract-page");

        Assert.Multiple(() =>
        {
            Assert.That(created, Is.Not.Null);
            Assert.That(created!.Slug, Is.EqualTo("contract-page"));
            Assert.That(search!.Select(page => page.Slug), Does.Contain("contract-page"));
            Assert.That(searchEnvelope!.SchemaVersion, Is.EqualTo("memorysmith.page-results.v1"));
            Assert.That(searchEnvelope.Provider.Kind, Is.EqualTo("page"));
            Assert.That(searchEnvelope.Results.Select(page => page.Slug), Does.Contain("contract-page"));
            Assert.That(html, Does.Contain(">Contract Page</h1>"));
            Assert.That(html, Does.Contain("/page-assets/example.png"));
            Assert.That(deleteResponse.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
        });
    }

    [Test]
    public async Task PagesApi_GetHtmlSupportsNestedSlugs()
    {
        var createResponse = await _client.PostAsJsonAsync("/api/pages", new PageSaveRequest(
            "notes/intro",
            "Intro",
            "Nested body"));
        createResponse.EnsureSuccessStatusCode();

        var htmlResponse = await _client.GetAsync("/api/pages/notes/intro/html");
        var html = await htmlResponse.Content.ReadAsStringAsync();

        Assert.Multiple(() =>
        {
            Assert.That(htmlResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(html, Does.Contain(">Intro</h1>"));
        });
    }

    [Test]
    public async Task PagesApi_FiltersPagesByMinimumRole()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"memorysmith-page-visibility-{Guid.NewGuid():N}");
        var pagesPath = Path.Combine(tempDir, "Pages");
        var factory = CreateIsolatedFactory(tempDir, new Dictionary<string, string?>
        {
            ["MemorySmith:Auth:AnonymousAccess"] = "None"
        });

        try
        {
            var pages = new FilePageService(pagesPath);
            var assetsPath = Path.Combine(pagesPath, "assets");
            Directory.CreateDirectory(assetsPath);
            await File.WriteAllBytesAsync(Path.Combine(assetsPath, "public.png"), [1, 2, 3]);
            await File.WriteAllBytesAsync(Path.Combine(assetsPath, "admin.png"), [4, 5, 6]);
            await pages.SaveAsync(new PageSaveRequest("public-page", "Public Page", "Public body ![public](assets/public.png)", PageAccessLevels.Anonymous), CancellationToken.None);
            await pages.SaveAsync(new PageSaveRequest("signed-in-page", "Signed In Page", "Signed-in body", PageAccessLevels.Authenticated), CancellationToken.None);
            await pages.SaveAsync(new PageSaveRequest("admin-page", "Admin Page", "Admin body ![admin](assets/admin.png)", PageAccessLevels.Admin), CancellationToken.None);

            using var anonymousClient = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            var anonymousPages = await anonymousClient.GetFromJsonAsync<PageSummary[]>("/api/pages");
            var signedInResponse = await anonymousClient.GetAsync("/api/pages/signed-in-page");
            var adminResponse = await anonymousClient.GetAsync("/api/pages/admin-page");
            var publicAssetResponse = await anonymousClient.GetAsync("/page-assets/public.png");
            var adminAssetResponse = await anonymousClient.GetAsync("/page-assets/admin.png");

            using var adminClient = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            var setupResponse = await adminClient.PostAsJsonAsync("/api/admin/setup", new SetupAdminRequest("Admin User", "admin@example.test", "ThisIsAValidPassword123!"));
            setupResponse.EnsureSuccessStatusCode();
            var adminPages = await adminClient.GetFromJsonAsync<PageSummary[]>("/api/pages");
            var adminPageResponse = await adminClient.GetAsync("/api/pages/admin-page");
            var adminPageAssetResponse = await adminClient.GetAsync("/page-assets/admin.png");

            Assert.Multiple(() =>
            {
                Assert.That(anonymousPages!.Select(page => page.Slug), Is.EqualTo(new[] { "public-page" }));
                Assert.That(signedInResponse.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
                Assert.That(adminResponse.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
                Assert.That(publicAssetResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                Assert.That(adminAssetResponse.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
                Assert.That(adminPages!.Select(page => page.Slug), Is.SupersetOf(new[] { "public-page", "signed-in-page", "admin-page" }));
                Assert.That(adminPageResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                Assert.That(adminPageAssetResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            });
        }
        finally
        {
            await DisposeFactoryTempDirAsync(factory, tempDir);
        }
    }

    [Test]
    public async Task PageSearchApis_ReturnVisibleMatchesBeyondFirstTwoHundredHiddenResults()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"memorysmith-page-search-visibility-{Guid.NewGuid():N}");
        var pagesPath = Path.Combine(tempDir, "Pages");
        var factory = CreateIsolatedFactory(tempDir, new Dictionary<string, string?>
        {
            ["MemorySmith:Auth:AnonymousAccess"] = MemorySmithRoles.Viewer
        });

        try
        {
            var pages = new FilePageService(pagesPath);
            const string query = "crowded api visibility token";
            await PageVisibilitySearchFixture.SeedAsync(pages, query, CancellationToken.None);

            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            var pageResults = await client.GetFromJsonAsync<PageSummary[]>($"/api/pages?query={Uri.EscapeDataString(query)}&limit=2");
            var unifiedResults = await client.GetFromJsonAsync<UnifiedSearchResult[]>($"/api/search?query={Uri.EscapeDataString(query)}&limit=4");
            var unifiedPageIds = unifiedResults!
                .Where(result => string.Equals(result.Kind, "page", StringComparison.OrdinalIgnoreCase))
                .Select(result => result.Id)
                .ToArray();

            Assert.Multiple(() =>
            {
                Assert.That(pageResults, Is.Not.Null);
                Assert.That(pageResults!, Has.Length.EqualTo(PageVisibilitySearchFixture.PublicPageSlugs.Length));
                Assert.That(pageResults!.Select(page => page.Slug), Is.EquivalentTo(PageVisibilitySearchFixture.PublicPageSlugs));
                Assert.That(unifiedResults, Is.Not.Null);
                Assert.That(unifiedPageIds, Has.Length.EqualTo(PageVisibilitySearchFixture.PublicPageSlugs.Length));
                Assert.That(unifiedPageIds, Is.EquivalentTo(PageVisibilitySearchFixture.PublicPageSlugs));
            });
        }
        finally
        {
            await DisposeFactoryTempDirAsync(factory, tempDir);
        }
    }

    [Test]
    public async Task PageAssetsApi_RejectsMalformedPercentEncoding()
    {
        var response = await _client.GetAsync("/page-assets/%zz");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public async Task CombinedSearch_ReturnsMemoryAndPageResults()
    {
        await _client.PostAsJsonAsync("/api/memories", new MemoryRecord
        {
            Id = "combined-memory",
            Title = "Combined Search Memory",
            Content = "shared discovery phrase",
            Tags = ["combined"],
            SourceLinks = [new SourceLink { Uri = "%MissingVariable%MemorySmith.App/Program.cs" }]
        });
        await _client.PostAsJsonAsync("/api/pages", new PageSaveRequest(
            "combined-page",
            "Combined Search Page",
            "shared discovery phrase"));

        var results = await _client.GetFromJsonAsync<UnifiedSearchResult[]>("/api/search?query=shared%20discovery&limit=10");
        Assert.That(results, Is.Not.Null);
        var nonNullResults = results!;

        Assert.Multiple(() =>
        {
            Assert.That(nonNullResults.Select(result => result.Kind), Does.Contain("memory"));
            Assert.That(nonNullResults.Select(result => result.Kind), Does.Contain("page"));
            Assert.That(nonNullResults.Single(result => result.Id == "combined-page").Url, Is.EqualTo("/pages/combined-page"));
            Assert.That(nonNullResults.Single(result => result.Id == "combined-memory").Diagnostics.Select(diagnostic => diagnostic.Code), Does.Contain("source.missing_variable"));
            Assert.That(nonNullResults.Single(result => result.Id == "combined-memory").Provider.Kind, Is.EqualTo("semantic"));
            Assert.That(nonNullResults.Single(result => result.Id == "combined-page").Provider.Kind, Is.EqualTo("page"));
        });
    }

    [Test]
    public async Task MemorySearchApi_DefaultListRemainsCompatibleAndEnvelopeIsOptIn()
    {
        var createResponse = await _client.PostAsJsonAsync("/api/memories", new MemoryRecord
        {
            Id = "api-warning-memory",
            Title = "API Warning Memory",
            Content = "api retrieval warning propagation",
            Status = MemoryStatus.Core,
            Tags = ["project-wiki"],
            Confidence = 1,
            SourceLinks = [new SourceLink { Uri = "%MissingVariable%MemorySmith.App/Program.cs" }]
        });
        createResponse.EnsureSuccessStatusCode();

        var defaultResponse = await _client.PostAsJsonAsync("/api/memories/search", new MemorySearchQuery("api retrieval warning", Limit: 5));
        defaultResponse.EnsureSuccessStatusCode();
        var defaultBody = await defaultResponse.Content.ReadAsStringAsync();
        var defaultResults = await defaultResponse.Content.ReadFromJsonAsync<MemoryRecord[]>();
        var envelopeResponse = await _client.PostAsJsonAsync("/api/memories/search?format=envelope", new MemorySearchQuery("api retrieval warning", Limit: 5));
        envelopeResponse.EnsureSuccessStatusCode();
        var envelope = await envelopeResponse.Content.ReadFromJsonAsync<RetrievalResultEnvelope<MemorySearchResult>>();

        Assert.Multiple(() =>
        {
            Assert.That(defaultResults!.Single().Id, Is.EqualTo("api-warning-memory"));
            Assert.That(defaultBody, Does.Not.Contain("schemaVersion"));
            Assert.That(envelope!.SchemaVersion, Is.EqualTo("memorysmith.retrieval-results.v1"));
            Assert.That(envelope.Provider.Kind, Is.EqualTo("lexical"));
            Assert.That(envelope.Results.Single().Diagnostics.Select(diagnostic => diagnostic.Code), Does.Contain("source.missing_variable"));
            Assert.That(envelope.Warnings, Has.Some.Contains("source.missing_variable"));
        });
    }

    [Test]
    public async Task ChatApi_WithEmptyMessage_ReturnsBadRequest()
    {
        var response = await _client.PostAsJsonAsync("/api/chat", new { message = "", mode = "Chat" });

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    private static WebApplicationFactory<Program> CreateIsolatedFactory(string tempDir, Dictionary<string, string?>? overrides = null) =>
        new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, config) =>
                {
                    var values = new Dictionary<string, string?>
                    {
                        ["MemorySmith:DataPath"] = Path.Combine(tempDir, "Memories"),
                        ["MemorySmith:PagesPath"] = Path.Combine(tempDir, "Pages"),
                        ["MemorySmith:EventLogPath"] = Path.Combine(tempDir, "Events", "audit.log"),
                        ["MemorySmith:DataProtectionKeysPath"] = Path.Combine(tempDir, "Keys"),
                        ["MemorySmith:Database:ConnectionString"] = $"Data Source={Path.Combine(tempDir, "memorysmith.db")};Pooling=False",
                        ["MemorySmith:Audit:JsonlPath"] = Path.Combine(tempDir, "Events", "audit-{yyyy}-W{week}.jsonl"),
                        ["MemorySmith:History:RootPath"] = Path.Combine(tempDir, ".history"),
                        ["MemorySmith:Maintenance:Enabled"] = "false"
                    };

                    if (overrides is not null)
                    {
                        foreach (var pair in overrides)
                        {
                            values[pair.Key] = pair.Value;
                        }
                    }

                    config.AddInMemoryCollection(values);
                });
            });

    private static WebApplicationFactory<Program> CreateRequestPipelineFactory(string tempDir) =>
        CreateIsolatedFactory(tempDir, new Dictionary<string, string?>
        {
            ["MemorySmith:Logging:StructuredFilePath"] = Path.Combine(tempDir, "Logs", "structured-.jsonl"),
            ["MemorySmith:Logging:SlowRequestThresholdMs"] = "0"
        }).WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.AddControllers().AddApplicationPart(typeof(TestFailureController).Assembly);
            });
        });

    private static bool IsAuthChallenge(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.Redirect or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;

    private static async Task<JsonElement> WaitForStructuredLogEntryAsync(string tempDir, Func<JsonElement, bool> predicate)
    {
        var logDirectory = Path.Combine(tempDir, "Logs");
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (Directory.Exists(logDirectory))
            {
                foreach (var filePath in Directory.EnumerateFiles(logDirectory, "*.jsonl", SearchOption.TopDirectoryOnly))
                {
                    foreach (var line in await ReadSharedLinesAsync(filePath))
                    {
                        if (string.IsNullOrWhiteSpace(line))
                        {
                            continue;
                        }

                        using var document = JsonDocument.Parse(line);
                        if (predicate(document.RootElement))
                        {
                            return document.RootElement.Clone();
                        }
                    }
                }
            }

            await Task.Delay(100);
        }

        Assert.Fail($"Expected matching structured log entry under {logDirectory}.");
        return default;
    }

    private static async Task<List<string>> ReadSharedLinesAsync(string filePath)
    {
        var lines = new List<string>();
        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        while (await reader.ReadLineAsync() is { } line)
        {
            lines.Add(line);
        }

        return lines;
    }

    private static string? GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static int? GetInt32(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var value)
            ? value
            : null;

    private static double? GetDouble(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.Number && property.TryGetDouble(out var value)
            ? value
            : null;

    private static async Task DisposeFactoryTempDirAsync(WebApplicationFactory<Program> factory, string tempDir)
    {
        await factory.DisposeAsync();
        Serilog.Log.CloseAndFlush();
        SqliteConnection.ClearAllPools();
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, recursive: true);
                }

                return;
            }
            catch (IOException) when (attempt < 4)
            {
                await Task.Delay(100);
            }
        }
    }
}

[ApiController]
public sealed class TestFailureController : ControllerBase
{
    [HttpGet("/api/test-failures/throw")]
    public IActionResult Throw() => throw new InvalidOperationException("Synthetic TSK-0105 test failure");
}