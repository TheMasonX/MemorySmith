using MemorySmith.Core.Models;

namespace MemorySmith.Storage;

public interface IMemoryStore
{
    MemoryRecord? Load(string id);
    void Save(MemoryRecord record);
    void Delete(string id);
    IEnumerable<MemoryRecord> LoadAll();
}
