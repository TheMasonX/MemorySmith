using System.Net;
using System.Net.Http.Json;
using MemorySmith.Core.Models;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

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
                        ["MemorySmith:EventLogPath"] = Path.Combine(_tempDir, "Events", "audit.log"),
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
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
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
            Assert.That(Directory.EnumerateFiles(_tempDir, "*.json", SearchOption.AllDirectories), Is.Empty);
        });
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
}