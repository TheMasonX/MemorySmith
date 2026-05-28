using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace MemorySmith.App.Services;

public static class MemorySmithTelemetry
{
    public const string ActivitySourceName = "MemorySmith.App";
    public const string MeterName = "MemorySmith.App";

    private static readonly ActivitySource ActivitySource = new(ActivitySourceName);
    private static readonly Meter Meter = new(MeterName);
    private static readonly Histogram<double> OperationDurationMs =
        Meter.CreateHistogram<double>("memorysmith.operation.duration", "ms", "Duration of bounded MemorySmith domain operations.");
    private static readonly Counter<long> OperationCount =
        Meter.CreateCounter<long>("memorysmith.operation.count", "count", "Count of bounded MemorySmith domain operations.");
    private static readonly Counter<long> OperationFailureCount =
        Meter.CreateCounter<long>("memorysmith.operation.failures", "count", "Count of failed bounded MemorySmith domain operations.");

    public static Activity? StartOperation(string operation, string category)
    {
        var activity = ActivitySource.StartActivity(operation, ActivityKind.Internal);
        if (activity is null)
        {
            return null;
        }

        activity.SetTag("memorysmith.operation", operation);
        activity.SetTag("memorysmith.category", category);
        return activity;
    }

    public static void RecordOperation(string operation, string category, double elapsedMs, bool success, bool isSlow)
    {
        var tags = new TagList
        {
            { "memorysmith.operation", operation },
            { "memorysmith.category", category },
            { "memorysmith.success", success },
            { "memorysmith.slow", isSlow }
        };

        OperationCount.Add(1, tags);
        OperationDurationMs.Record(elapsedMs, tags);
        if (!success)
        {
            OperationFailureCount.Add(1, tags);
        }
    }
}
