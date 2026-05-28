using System.Text.Json;
using MemorySmith.App.Services;
using Microsoft.Extensions.Options;

namespace MemorySmith.Tests;

[TestFixture]
public class LoggingObservabilityServiceTests
{
    private string _tempRoot = null!;

    [SetUp]
    public void SetUp()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"memorysmith-logging-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    [Test]
    public async Task SearchAsync_StreamsStructuredLogsAndKeepsNewestMatchesWithinLimit()
    {
        var logPath = Path.Combine(_tempRoot, "memorysmith.log");
        var now = DateTime.UtcNow;
        await File.WriteAllLinesAsync(logPath,
        [
            CreateStructuredLine(now.AddHours(-3), "Information", "too old"),
            CreateStructuredLine(now.AddMinutes(-25), "Information", "keep first"),
            CreateStructuredLine(now.AddMinutes(-15), "Warning", "keep second"),
            "{not-json}",
            CreateStructuredLine(now.AddMinutes(-5), "Error", "keep third")
        ]);

        var options = new MemorySmithOptions
        {
            Logging = new LoggingOptions
            {
                StructuredFilePath = logPath,
                MaxDiagnosticsLogResults = 10,
                WindowsEventLogEnabled = false
            }
        };

        var service = new LoggingObservabilityService(new StaticOptionsMonitor<MemorySmithOptions>(options));
        var results = await service.SearchAsync(new LogSearchQuery(
            Text: "keep",
            Level: null,
            Hours: 1,
            Limit: 2,
            IncludeWindowsEventLog: false,
            IncludeStructuredLogs: true), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(results, Has.Count.EqualTo(2));
            Assert.That(results.Select(entry => entry.Message), Is.EqualTo(new[] { "keep third", "keep second" }));
            Assert.That(results.All(entry => entry.TimestampUtc >= now.AddHours(-1)), Is.True);
        });
    }

    [Test]
    public async Task SearchAsync_AddsWarningWhenStructuredLogFileCannotBeRead()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Ignore("Exclusive file-lock read failures are only deterministic on Windows in this regression test.");
        }

        var logPath = Path.Combine(_tempRoot, "locked-memorysmith.log");
        await File.WriteAllTextAsync(logPath, CreateStructuredLine(DateTime.UtcNow, "Information", "locked"));

        var options = new MemorySmithOptions
        {
            Logging = new LoggingOptions
            {
                StructuredFilePath = logPath,
                MaxDiagnosticsLogResults = 10,
                WindowsEventLogEnabled = false
            }
        };

        using var lockHandle = new FileStream(logPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var service = new LoggingObservabilityService(new StaticOptionsMonitor<MemorySmithOptions>(options));

        var results = await service.SearchAsync(new LogSearchQuery(
            Text: null,
            Level: null,
            Hours: 1,
            Limit: 5,
            IncludeWindowsEventLog: false,
            IncludeStructuredLogs: true), CancellationToken.None);

        Assert.That(results, Has.Count.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(results[0].Level, Is.EqualTo("Warning"));
            Assert.That(results[0].Message, Does.Contain("could not be read"));
            Assert.That(results[0].Message, Does.Contain("locked-memorysmith.log"));
            Assert.That(results[0].Properties.TryGetValue("ExceptionType", out var exceptionType) ? exceptionType : null, Is.EqualTo("IOException"));
        });
    }

    private static string CreateStructuredLine(DateTime timestampUtc, string level, string message)
    {
        return JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["@t"] = timestampUtc.ToString("O"),
            ["@l"] = level,
            ["@m"] = message,
            ["SourceContext"] = "Tests"
        });
    }

    private sealed class StaticOptionsMonitor<T> : IOptionsMonitor<T>
    {
        public StaticOptionsMonitor(T value)
        {
            CurrentValue = value;
        }

        public T CurrentValue { get; }

        public T Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}