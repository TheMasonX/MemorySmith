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