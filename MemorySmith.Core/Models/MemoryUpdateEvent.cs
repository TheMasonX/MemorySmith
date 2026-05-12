namespace MemorySmith.Core.Models;

public class MemoryUpdateEvent
{
    public string Id { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}
