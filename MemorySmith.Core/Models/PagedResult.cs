namespace MemorySmith.Core.Models;

/// <summary>Paginated API response wrapper.</summary>
public class PagedResult<T>
{
    public int TotalCount { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
    public List<T> Data { get; set; } = new();
}
