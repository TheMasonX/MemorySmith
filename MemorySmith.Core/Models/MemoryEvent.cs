namespace MemorySmith.Core.Models;

public class MemoryEvent
{
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string MemoryId { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string Details { get; set; } = string.Empty;
}
