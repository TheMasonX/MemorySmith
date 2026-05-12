using MemorySmith.Core.Models;

namespace MemorySmith.App.Services;

public sealed record MemoryListQuery(int Page = 1, int PageSize = 20, MemoryStatus? Status = null, string? Tags = null);

public sealed record MemorySearchQuery(string? Query = null, MemoryStatus? Status = null, string? Tags = null, int Limit = 20);

public sealed record SemanticMemorySearchQuery(string? Query = null, MemoryStatus? Status = null, string? Tags = null, int Limit = 20);

public sealed record HybridMemorySearchQuery(string? Query = null, MemoryStatus? Status = null, string? Tags = null, int Limit = 20);

public sealed record MemorySearchResult(
	string Id,
	string Title,
	MemoryStatus Status,
	double Confidence,
	double Score,
	IReadOnlyList<string> Tags,
	int UsageCount,
	string Snippet,
	string MatchReason,
	DateTime LastUpdated);