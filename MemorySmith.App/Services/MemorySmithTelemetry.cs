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
    private static readonly Histogram<double> ToolExecutionDurationMs =
        Meter.CreateHistogram<double>("memorysmith.tool.execution.duration", "ms", "Duration of MemorySmith chat and MCP tool executions.");
    private static readonly Counter<long> ToolExecutionCount =
        Meter.CreateCounter<long>("memorysmith.tool.execution.count", "count", "Count of MemorySmith chat and MCP tool executions.");
    private static readonly Counter<long> ToolExecutionFailureCount =
        Meter.CreateCounter<long>("memorysmith.tool.execution.failures", "count", "Count of failed MemorySmith chat and MCP tool executions.");

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

    public static void RecordToolExecution(string transport, string toolName, double elapsedMs, bool success)
    {
        var normalizedTransport = string.IsNullOrWhiteSpace(transport) ? "unknown" : transport.Trim().ToLowerInvariant();
        var normalizedToolName = string.IsNullOrWhiteSpace(toolName) ? "unknown" : toolName.Trim();
        var safeElapsedMs = double.IsNaN(elapsedMs) || double.IsInfinity(elapsedMs) || elapsedMs < 0 ? 0 : elapsedMs;

        var tags = new TagList
        {
            { "memorysmith.transport", normalizedTransport },
            { "memorysmith.tool", normalizedToolName },
            { "memorysmith.success", success },
            { "memorysmith.error", !success }
        };

        ToolExecutionCount.Add(1, tags);
        ToolExecutionDurationMs.Record(safeElapsedMs, tags);
        if (!success)
        {
            ToolExecutionFailureCount.Add(1, tags);
        }
    }
}
