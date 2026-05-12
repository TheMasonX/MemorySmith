namespace MemorySmith.Core.Models;

public class MemoryRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Content { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public MemoryStatus Status { get; set; } = MemoryStatus.Unconsolidated;
    public double Confidence { get; set; }
    public List<string> Tags { get; set; } = new();
    public List<string> References { get; set; } = new();
    public List<string> Conflicts { get; set; } = new();
    public List<SourceLink> SourceLinks { get; set; } = new();
    public int UsageCount { get; set; }
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
}
