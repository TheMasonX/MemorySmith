using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace MemorySmith.App.Services;

public sealed record LogSearchQuery(
    string? Text,
    string? Level,
    int Hours,
    int Limit,
    bool IncludeWindowsEventLog,
    bool IncludeStructuredLogs);

public sealed record LogEntryDto(
    DateTime TimestampUtc,
    string Level,
    string Message,
    string Source,
    string? TraceId,
    string? CorrelationId,
    double? ElapsedMs,
    IReadOnlyDictionary<string, string> Properties);

public sealed record LogDayBucket(DateOnly Date, int Errors, int Warnings, int Requests, double? P95LatencyMs);

public sealed record LogMetricsSnapshot(
    DateTime ObservedAtUtc,
    int WindowDays,
    int TotalEntries,
    int ErrorCount,
    int WarningCount,
    int RequestCount,
    double? P50LatencyMs,
    double? P95LatencyMs,
    double? P99LatencyMs,
    IReadOnlyList<LogDayBucket> Buckets);

public sealed class LoggingObservabilityService
{
    private readonly IOptionsMonitor<MemorySmithOptions> _options;

    public LoggingObservabilityService(IOptionsMonitor<MemorySmithOptions> options)
    {
        _options = options;
    }

    public Task<IReadOnlyList<LogEntryDto>> SearchAsync(LogSearchQuery query, CancellationToken cancellationToken)
    {
        var settings = _options.CurrentValue.Logging;
        var cappedLimit = Math.Clamp(query.Limit <= 0 ? settings.MaxDiagnosticsLogResults : query.Limit, 1, settings.MaxDiagnosticsLogResults);
        return Task.FromResult<IReadOnlyList<LogEntryDto>>(LoadEntries(query, settings, cappedLimit, cancellationToken));
    }

    public Task<LogMetricsSnapshot> GetMetricsAsync(int? days, CancellationToken cancellationToken)
    {
        var settings = _options.CurrentValue.Logging;
        var windowDays = Math.Clamp(days ?? settings.MetricsWindowDays, 1, 365);
        var metricSampleLimit = Math.Clamp(settings.MetricsSampleLimit, 100, 50000);
        var entries = LoadEntries(
            new LogSearchQuery(
                Text: null,
                Level: null,
                Hours: windowDays * 24,
                Limit: metricSampleLimit,
                IncludeWindowsEventLog: settings.WindowsEventLogEnabled,
                IncludeStructuredLogs: true),
            settings,
            metricSampleLimit,
            cancellationToken);

        var latencies = entries
            .Where(entry => entry.ElapsedMs.HasValue)
            .Select(entry => entry.ElapsedMs!.Value)
            .OrderBy(value => value)
            .ToList();

        var buckets = entries
            .GroupBy(entry => DateOnly.FromDateTime(entry.TimestampUtc))
            .OrderBy(group => group.Key)
            .Select(group =>
            {
                var dayLatencies = group.Where(entry => entry.ElapsedMs.HasValue).Select(entry => entry.ElapsedMs!.Value).OrderBy(value => value).ToList();
                return new LogDayBucket(
                    group.Key,
                    group.Count(entry => IsError(entry.Level)),
                    group.Count(entry => IsWarning(entry.Level)),
                    group.Count(entry => IsRequest(entry)),
                    Percentile(dayLatencies, 95));
            })
            .ToList();

        return Task.FromResult(new LogMetricsSnapshot(
            DateTime.UtcNow,
            windowDays,
            entries.Count,
            entries.Count(entry => IsError(entry.Level)),
            entries.Count(entry => IsWarning(entry.Level)),
            entries.Count(IsRequest),
            Percentile(latencies, 50),
            Percentile(latencies, 95),
            Percentile(latencies, 99),
            buckets));
    }

    private List<LogEntryDto> LoadEntries(LogSearchQuery query, LoggingOptions settings, int limit, CancellationToken cancellationToken)
    {
        var sinceUtc = DateTime.UtcNow.AddHours(-Math.Clamp(query.Hours <= 0 ? 24 : query.Hours, 1, 24 * 30));
        var entries = new List<LogEntryDto>();

        if (query.IncludeStructuredLogs)
        {
            entries.AddRange(ReadStructuredEntries(query, settings, limit, sinceUtc, cancellationToken));
        }

        if (OperatingSystem.IsWindows() && settings.WindowsEventLogEnabled && query.IncludeWindowsEventLog)
        {
            entries.AddRange(ReadWindowsEventLogs(settings, sinceUtc, query.Text, query.Level, cancellationToken));
        }

        var ordered = entries
            .OrderByDescending(entry => entry.TimestampUtc)
            .Take(limit)
            .ToList();
        return ordered;
    }

    private static List<LogEntryDto> ReadStructuredEntries(LogSearchQuery query, LoggingOptions settings, int limit, DateTime sinceUtc, CancellationToken cancellationToken)
    {
        var results = new List<LogEntryDto>();

        foreach (var file in ResolveStructuredLogFiles(settings.StructuredFilePath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(file))
            {
                continue;
            }

            IEnumerable<string> lines;
            try
            {
                lines = File.ReadLines(file);
            }
            catch
            {
                continue;
            }

            foreach (var line in lines)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!TryParseStructuredLog(line, out var entry) || !Matches(entry, sinceUtc, query.Text, query.Level))
                {
                    continue;
                }

                InsertByTimestamp(results, entry, limit);
            }
        }

        return results;
    }

    private static void InsertByTimestamp(List<LogEntryDto> results, LogEntryDto entry, int limit)
    {
        if (limit <= 0)
        {
            return;
        }

        var insertIndex = 0;
        while (insertIndex < results.Count && results[insertIndex].TimestampUtc >= entry.TimestampUtc)
        {
            insertIndex++;
        }

        if (insertIndex >= limit)
        {
            return;
        }

        results.Insert(insertIndex, entry);
        if (results.Count > limit)
        {
            results.RemoveAt(results.Count - 1);
        }
    }

    private static bool IsError(string level) => string.Equals(level, "Error", StringComparison.OrdinalIgnoreCase)
        || string.Equals(level, "Fatal", StringComparison.OrdinalIgnoreCase);

    private static bool IsWarning(string level) => string.Equals(level, "Warning", StringComparison.OrdinalIgnoreCase);

    private static bool IsRequest(LogEntryDto entry) => entry.ElapsedMs.HasValue
        || entry.Properties.ContainsKey("RequestPath")
        || entry.Properties.ContainsKey("Path")
        || entry.Message.Contains("HTTP", StringComparison.OrdinalIgnoreCase);

    private static double? Percentile(IReadOnlyList<double> sortedValues, int percentile)
    {
        if (sortedValues.Count == 0)
        {
            return null;
        }

        var index = (int)Math.Ceiling((percentile / 100d) * sortedValues.Count) - 1;
        index = Math.Clamp(index, 0, sortedValues.Count - 1);
        return Math.Round(sortedValues[index], 2);
    }

    private static bool Matches(LogEntryDto entry, DateTime sinceUtc, string? text, string? level)
    {
        if (entry.TimestampUtc < sinceUtc)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(level) && !string.Equals(entry.Level, level, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        if (entry.Message.Contains(text, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return entry.Properties.Any(pair =>
            pair.Key.Contains(text, StringComparison.OrdinalIgnoreCase)
            || pair.Value.Contains(text, StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<string> ResolveStructuredLogFiles(string configuredPath)
    {
        var resolvedPath = Path.IsPathRooted(configuredPath)
            ? configuredPath
            : Path.Combine(AppContext.BaseDirectory, configuredPath);

        var directory = Path.GetDirectoryName(resolvedPath);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return [];
        }

        var fileName = Path.GetFileName(resolvedPath);
        if (fileName.Contains("-."))
        {
            var wildcard = fileName.Replace("-.", "*.", StringComparison.Ordinal);
            return Directory.EnumerateFiles(directory, wildcard, SearchOption.TopDirectoryOnly)
                .OrderByDescending(path => File.GetLastWriteTimeUtc(path))
                .ThenByDescending(path => path, StringComparer.OrdinalIgnoreCase);
        }

        return [resolvedPath];
    }

    private static bool TryParseStructuredLog(string line, out LogEntryDto entry)
    {
        entry = default!;
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        try
        {
            var node = JsonNode.Parse(line)?.AsObject();
            if (node is null)
            {
                return false;
            }

            var timestamp = ParseTimestamp(node["@t"]?.GetValue<string>());
            var level = node["@l"]?.GetValue<string>() ?? "Information";
            var message = node["@m"]?.GetValue<string>() ?? node["@mt"]?.GetValue<string>() ?? string.Empty;

            var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in node)
            {
                if (pair.Key.StartsWith("@", StringComparison.Ordinal))
                {
                    continue;
                }

                properties[pair.Key] = pair.Value?.ToJsonString() ?? string.Empty;
            }

            properties.TryGetValue("TraceId", out var traceId);
            properties.TryGetValue("CorrelationId", out var correlationId);
            var elapsedMs = TryReadElapsed(properties);

            entry = new LogEntryDto(timestamp, level, message, "StructuredFile", TrimJson(traceId), TrimJson(correlationId), elapsedMs, properties);
            return true;
        }
        catch
        {
            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    private static IEnumerable<LogEntryDto> ReadWindowsEventLogs(LoggingOptions settings, DateTime sinceUtc, string? text, string? level, CancellationToken cancellationToken)
    {
        var results = new List<LogEntryDto>();

        try
        {
            using var eventLog = new EventLog(string.IsNullOrWhiteSpace(settings.WindowsEventLogName) ? "Application" : settings.WindowsEventLogName);
            for (var index = eventLog.Entries.Count - 1; index >= 0; index--)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var item = eventLog.Entries[index];
                if (item.TimeGenerated.ToUniversalTime() < sinceUtc)
                {
                    break;
                }

                if (!string.IsNullOrWhiteSpace(settings.WindowsEventLogSource)
                    && !string.Equals(item.Source, settings.WindowsEventLogSource, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var mappedLevel = item.EntryType switch
                {
                    EventLogEntryType.Error => "Error",
                    EventLogEntryType.Warning => "Warning",
                    _ => "Information"
                };

                var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["InstanceId"] = item.InstanceId.ToString(),
                    ["Category"] = item.Category,
                    ["MachineName"] = item.MachineName
                };

                var entry = new LogEntryDto(
                    item.TimeGenerated.ToUniversalTime(),
                    mappedLevel,
                    item.Message,
                    "WindowsEventLog",
                    null,
                    null,
                    null,
                    properties);

                if (Matches(entry, sinceUtc, text, level))
                {
                    results.Add(entry);
                }
            }
        }
        catch
        {
            // Intentionally swallow event-log read failures so diagnostics still returns structured logs.
        }

        return results;
    }

    private static DateTime ParseTimestamp(string? value)
    {
        if (DateTime.TryParse(value, out var parsed))
        {
            return parsed.ToUniversalTime();
        }

        return DateTime.UtcNow;
    }

    private static string? TrimJson(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        return value.Trim('"');
    }

    private static double? TryReadElapsed(IReadOnlyDictionary<string, string> properties)
    {
        if (!properties.TryGetValue("Elapsed", out var rawElapsed)
            && !properties.TryGetValue("ElapsedMs", out rawElapsed))
        {
            return null;
        }

        var normalized = TrimJson(rawElapsed);
        if (double.TryParse(normalized, out var value))
        {
            return value;
        }

        return null;
    }
}
