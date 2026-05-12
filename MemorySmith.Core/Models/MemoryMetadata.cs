namespace MemorySmith.Core.Models;

/// <summary>Lightweight summary of a memory for list views.</summary>
public class MemoryMetadata
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public MemoryStatus Status { get; set; }
    public double Confidence { get; set; }
    public List<string> Tags { get; set; } = new();
    public int UsageCount { get; set; }
    public DateTime LastUpdated { get; set; }
}
