using MemorySmith.Core.Indexing;
using MemorySmith.Core.Models;
using MemorySmith.Core.StateMachine;
using MemorySmith.Storage;
using Microsoft.Extensions.Options;

namespace MemorySmith.App.Services;

public class MemoryMaintenanceTasks
{
    private readonly IMemoryStore _store;
    private readonly IEventStore _eventStore;
    private readonly MemoryIndex _index;
    private readonly MemorySmithOptions _options;

    public MemoryMaintenanceTasks(
        IMemoryStore store,
        IEventStore eventStore,
        MemoryIndex index,
        IOptions<MemorySmithOptions>? options = null)
    {
        _store = store;
        _eventStore = eventStore;
        _index = index;
        _options = options?.Value ?? new MemorySmithOptions();
    }

    public Task RunTriageAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var stateMachine = new MemoryStateMachine();

        foreach (var record in _store.LoadAll().ToList())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (newStatus, evt) = stateMachine.Evaluate(record, _options.Maintenance.AutomaticDeprecationEnabled);
            if (newStatus == record.Status)
            {
                continue;
            }

            record.Status = newStatus;
            record.LastUpdated = DateTime.UtcNow;
            _store.Save(record);
            if (evt is not null)
            {
                _eventStore.AppendEvent(evt);
            }
        }

        return Task.CompletedTask;
    }

    public Task RunIndexRebuildAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _index.Rebuild(_store.LoadAll().ToList());
        return Task.CompletedTask;
    }

    public Task RunConsolidationAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var records = _store.LoadAll().ToList();
        DeduplicateRecords(records);
        PromoteStableRecords(records);
        if (_options.Maintenance.AutomaticDeprecationEnabled)
        {
            DeprecateObsoleteRecords(records);
        }
        else
        {
            RecommendObsoleteRecords(records);
        }
        return Task.CompletedTask;
    }

    private void DeduplicateRecords(List<MemoryRecord> records)
    {
        var groups = records
            .Where(record => !string.IsNullOrWhiteSpace(record.Title))
            .GroupBy(record => record.Title.Trim().ToLowerInvariant())
            .Where(group => group.Count() > 1)
            .ToList();

        foreach (var group in groups)
        {
            var primary = group.First();
            foreach (var duplicate in group.Skip(1).ToList())
            {
                primary.UsageCount += duplicate.UsageCount;
                primary.References.AddRange(duplicate.References);
                primary.Conflicts.AddRange(duplicate.Conflicts);
                primary.Tags.AddRange(duplicate.Tags);
                records.Remove(duplicate);
                _store.Delete(duplicate.Id);
            }

            primary.References = DistinctNormalized(primary.References);
            primary.Conflicts = DistinctNormalized(primary.Conflicts);
            primary.Tags = DistinctNormalized(primary.Tags);
            _store.Save(primary);
        }
    }

    private void PromoteStableRecords(IEnumerable<MemoryRecord> records)
    {
        var cutoffDate = DateTime.UtcNow.AddDays(-30);
        foreach (var record in records.Where(r => r.Status == MemoryStatus.Working))
        {
            if (record.LastUpdated < cutoffDate && record.References.Count >= 2 && record.Confidence >= 0.7)
            {
                record.Status = MemoryStatus.Core;
                _store.Save(record);
            }
        }
    }

    private void DeprecateObsoleteRecords(IEnumerable<MemoryRecord> records)
    {
        foreach (var record in records.Where(r => r.Status != MemoryStatus.Deprecated))
        {
            if (MemoryScorer.Score(record) < MemoryStateMachine.DeprecationThreshold)
            {
                record.Status = MemoryStatus.Deprecated;
                _store.Save(record);
            }
        }
    }

    private void RecommendObsoleteRecords(IEnumerable<MemoryRecord> records)
    {
        var existingRecommendations = _eventStore.GetEvents()
            .Where(memoryEvent => string.Equals(memoryEvent.Action, "DeprecationRecommended", StringComparison.OrdinalIgnoreCase))
            .Select(memoryEvent => memoryEvent.MemoryId)
            .Where(memoryId => !string.IsNullOrWhiteSpace(memoryId))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var record in records.Where(r => r.Status != MemoryStatus.Deprecated))
        {
            var score = MemoryScorer.Score(record);
            if (score >= MemoryStateMachine.DeprecationThreshold ||
                existingRecommendations.Contains(record.Id))
            {
                continue;
            }

            _eventStore.AppendEvent(new MemoryEvent
            {
                MemoryId = record.Id,
                Action = "DeprecationRecommended",
                Details = $"Low score {score:F3}; automatic deprecation is disabled.",
                Timestamp = DateTime.UtcNow
            });
            existingRecommendations.Add(record.Id);
        }
    }

    private static List<string> DistinctNormalized(IEnumerable<string> values) =>
        values
            .Select(value => value.Trim().ToLowerInvariant())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}