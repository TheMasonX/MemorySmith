using MemorySmith.Core.Models;

namespace MemorySmith.App.Services;

internal static class MemoryRecordLookup
{
    public static IReadOnlyDictionary<string, MemoryRecord> ToRecordMap(IEnumerable<MemoryRecord> records)
    {
        var map = new Dictionary<string, MemoryRecord>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in records
            .Where(record => !string.IsNullOrWhiteSpace(record.Id))
            .OrderByDescending(record => record.LastUpdated)
            .ThenBy(record => record.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(record => record.Id, StringComparer.Ordinal))
        {
            map.TryAdd(record.Id, record);
        }

        return map;
    }

    public static List<MemoryRecord> ToRecordList(IEnumerable<MemoryRecord> records) =>
        ToRecordMap(records).Values.ToList();
}