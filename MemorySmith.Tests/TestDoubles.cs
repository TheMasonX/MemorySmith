using MemorySmith.App.Services;
using MemorySmith.Core.Models;
using MemorySmith.Storage;

namespace MemorySmith.Tests;

public class InMemoryMemoryStore : IMemoryStore
{
    private readonly Dictionary<string, MemoryRecord> _records = new();

    public MemoryRecord? Load(string id) =>
        _records.TryGetValue(id, out var record) ? record : null;

    public void Save(MemoryRecord record) =>
        _records[record.Id] = record;

    public void Delete(string id) =>
        _records.Remove(id);

    public IEnumerable<MemoryRecord> LoadAll() =>
        _records.Values.ToList();
}

public static class PageVisibilitySearchFixture
{
    public static readonly string[] PublicPageSlugs = ["public-page-1", "public-page-2"];

    public static async Task SeedAsync(FilePageService pages, string query, CancellationToken cancellationToken)
    {
        await pages.SaveAsync(new PageSaveRequest(
            PublicPageSlugs[0],
            "Public Page 1",
            $"{query} visible result one",
            PageAccessLevels.Anonymous), cancellationToken);
        await pages.SaveAsync(new PageSaveRequest(
            PublicPageSlugs[1],
            "Public Page 2",
            $"{query} visible result two",
            PageAccessLevels.Anonymous), cancellationToken);

        for (var index = 1; index <= 200; index++)
        {
            await pages.SaveAsync(new PageSaveRequest(
                $"signed-in-page-{index:D3}",
                $"Signed In Page {index:D3}",
                $"{query} {query} signed-in result {index:D3}",
                PageAccessLevels.Authenticated), cancellationToken);
        }
    }
}