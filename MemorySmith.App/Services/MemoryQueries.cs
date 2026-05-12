using MemorySmith.Core.Models;

namespace MemorySmith.App.Services;

public sealed record MemoryListQuery(int Page = 1, int PageSize = 20, MemoryStatus? Status = null, string? Tags = null);

public sealed record MemorySearchQuery(string? Query = null, MemoryStatus? Status = null, string? Tags = null, int Limit = 20);

public sealed record SemanticMemorySearchQuery(string? Query = null, MemoryStatus? Status = null, string? Tags = null, int Limit = 20);

public sealed record HybridMemorySearchQuery(string? Query = null, MemoryStatus? Status = null, string? Tags = null, int Limit = 20);

public sealed record MemoryContextPackQuery(
	string? Query = null,
	MemoryStatus? Status = null,
	string? Tags = null,
	int Limit = 5,
	int ReferenceDepth = 1,
	int MaxContentChars = 1200,
	int MaxRecords = 20,
	string? Ids = null,
	bool IncludeBacklinks = false);

public sealed record MemoryContextPack(string? Query, DateTime GeneratedAt, IReadOnlyList<MemoryContextPackRecord> Records, IReadOnlyList<string> Warnings);

public sealed record MemoryContextPackRecord(
	string Id,
	string Title,
	MemoryStatus Status,
	double Confidence,
	IReadOnlyList<string> Tags,
	IReadOnlyList<string> References,
	IReadOnlyList<string> Conflicts,
	IReadOnlyList<SourceLink> SourceLinks,
	int UsageCount,
	DateTime LastUpdated,
	string Relationship,
	double? Score,
	string? MatchReason,
	string Content);

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