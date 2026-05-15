using System.Net;
using System.Net.Http.Json;
using MemorySmith.App.Services;
using MemorySmith.Core.Models;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace MemorySmith.Tests;

[TestFixture]
public class PublisherAndStatsTests
{
    [Test]
    public async Task MemoryChangePublisher_AwaitsEverySubscriber()
    {
        var publisher = new MemoryChangePublisher();
        var calls = new List<string>();

        publisher.MemoryChanged += async _ =>
        {
            await Task.Yield();
            calls.Add("first");
        };
        publisher.MemoryChanged += async _ =>
        {
            await Task.Yield();
            calls.Add("second");
        };

        await publisher.PublishMemoryChangedAsync(new MemoryUpdateEvent { Id = "id", Action = "Updated" });

        Assert.That(calls, Is.EqualTo(new[] { "first", "second" }));
    }

    [Test]
    public async Task StatsActivityEndpoint_ClampsInvalidDaysAndReturnsBuckets()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"memorysmith-stats-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, config) =>
                {
                    config.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["MemorySmith:DataPath"] = Path.Combine(tempRoot, "Memories"),
                        ["MemorySmith:EventLogPath"] = Path.Combine(tempRoot, "Events", "audit.log"),
                        ["MemorySmith:Maintenance:Enabled"] = "false"
                    });
                });
            });
            using var client = factory.CreateClient();

            var response = await client.GetAsync("/api/stats/activity?days=-1");
            var buckets = await response.Content.ReadFromJsonAsync<List<ActivityBucket>>();

            Assert.Multiple(() =>
            {
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                Assert.That(buckets, Is.Not.Null);
                Assert.That(buckets, Has.Count.EqualTo(30));
            });
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }
}