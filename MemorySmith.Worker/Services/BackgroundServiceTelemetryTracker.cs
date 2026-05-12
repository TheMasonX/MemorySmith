using System.Collections.Concurrent;
using MemorySmith.Core.Models;

namespace MemorySmith.Worker.Services;

public class BackgroundServiceTelemetryTracker
{
    private readonly ConcurrentDictionary<string, BackgroundServiceTelemetry> _telemetry = new(StringComparer.OrdinalIgnoreCase);

    public void RecordRunStart(string serviceName, string interval)
    {
        ArgumentNullException.ThrowIfNull(serviceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(interval);

        _telemetry.AddOrUpdate(
            serviceName,
            _ => new BackgroundServiceTelemetry
            {
                ServiceName = serviceName,
                Interval = interval,
                LastRunUtc = DateTime.UtcNow
            },
            (_, existing) =>
            {
                existing.Interval = interval;
                existing.LastRunUtc = DateTime.UtcNow;
                return existing;
            });
    }

    public void RecordRunSuccess(string serviceName, double durationMs)
    {
        ArgumentNullException.ThrowIfNull(serviceName);

        var now = DateTime.UtcNow;
        _telemetry.AddOrUpdate(
            serviceName,
            _ => new BackgroundServiceTelemetry
            {
                ServiceName = serviceName,
                LastRunUtc = now,
                LastSuccessUtc = now,
                LastDurationMs = durationMs
            },
            (_, existing) =>
            {
                existing.LastRunUtc = now;
                existing.LastSuccessUtc = now;
                existing.LastDurationMs = durationMs;
                return existing;
            });
    }

    public void RecordRunFailure(string serviceName, double durationMs)
    {
        ArgumentNullException.ThrowIfNull(serviceName);

        var now = DateTime.UtcNow;
        _telemetry.AddOrUpdate(
            serviceName,
            _ => new BackgroundServiceTelemetry
            {
                ServiceName = serviceName,
                LastRunUtc = now,
                LastFailureUtc = now,
                LastDurationMs = durationMs
            },
            (_, existing) =>
            {
                existing.LastRunUtc = now;
                existing.LastFailureUtc = now;
                existing.LastDurationMs = durationMs;
                return existing;
            });
    }

    public IReadOnlyList<BackgroundServiceTelemetry> GetSnapshot() =>
        _telemetry.Values
            .OrderBy(x => x.ServiceName, StringComparer.OrdinalIgnoreCase)
            .Select(x => new BackgroundServiceTelemetry
            {
                ServiceName = x.ServiceName,
                Interval = x.Interval,
                LastRunUtc = x.LastRunUtc,
                LastSuccessUtc = x.LastSuccessUtc,
                LastFailureUtc = x.LastFailureUtc,
                LastDurationMs = x.LastDurationMs
            })
            .ToList();
}
