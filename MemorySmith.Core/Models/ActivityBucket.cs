namespace MemorySmith.Core.Models;

/// <summary>Daily event summary bucket used for activity charts and API responses.</summary>
public sealed record ActivityBucket(DateOnly Date, int Queries, int Changes);