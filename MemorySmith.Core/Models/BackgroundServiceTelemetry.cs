namespace MemorySmith.Core.Models;

public class BackgroundServiceTelemetry
{
    public string ServiceName { get; set; } = string.Empty;
    public string Interval { get; set; } = string.Empty;
    public DateTime? LastRunUtc { get; set; }
    public DateTime? LastSuccessUtc { get; set; }
    public DateTime? LastFailureUtc { get; set; }
    public double? LastDurationMs { get; set; }
}
