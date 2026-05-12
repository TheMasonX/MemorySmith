using MemorySmith.Core.Models;
using MemorySmith.Core.StateMachine;
using MemorySmith.Storage;

namespace MemorySmith.Worker.Services;

public class ConsolidationService : BackgroundService
{
    private const string ServiceName = "ConsolidationService";
    private const string ServiceInterval = "24h";

    private readonly IMemoryStore _store;
    private readonly ILogger<ConsolidationService> _logger;
    private readonly BackgroundServiceTelemetryTracker _telemetryTracker;
    private readonly TimeSpan _interval = TimeSpan.FromHours(24);

    public ConsolidationService(
        IMemoryStore store,
        ILogger<ConsolidationService> logger,
        BackgroundServiceTelemetryTracker telemetryTracker)
    {
        _store = store;
        _logger = logger;
        _telemetryTracker = telemetryTracker;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var started = DateTime.UtcNow;
            _telemetryTracker.RecordRunStart(ServiceName, ServiceInterval);

            try
            {
                RunConsolidation();
                var durationMs = (DateTime.UtcNow - started).TotalMilliseconds;
                _telemetryTracker.RecordRunSuccess(ServiceName, durationMs);
            }
            catch (Exception ex)
            {
                var durationMs = (DateTime.UtcNow - started).TotalMilliseconds;
                _telemetryTracker.RecordRunFailure(ServiceName, durationMs);
                _logger.LogError(ex, "Consolidation error");
            }

            await Task.Delay(_interval, stoppingToken);
        }
    }

    private void RunConsolidation()
    {
        _logger.LogInformation("Starting consolidation cycle...");
        var records = _store.LoadAll().ToList();

        // 1. Dedup: merge identical or near-identical memories
        DeduplicateRecords(records);
        
        // 2. Promote: Working → Core if stable
        PromoteStableRecords(records);
        
        // 3. Deprecate: low-score records
        DeprecateObsoleteRecords(records);
        
        _logger.LogInformation("Consolidation complete. Processed {Count} memories.", records.Count);
    }

    private void DeduplicateRecords(List<MemoryRecord> records)
    {
        // Group by title (case-insensitive) to find potential duplicates
        var groups = records
            .GroupBy(r => r.Title.ToLowerInvariant())
            .Where(g => g.Count() > 1)
            .ToList();

        foreach (var group in groups)
        {
            // Simple exact-match dedup by title
            // In future: add semantic similarity (embeddings)
            var primary = group.First();
            var duplicates = group.Skip(1).ToList();

            foreach (var duplicate in duplicates)
            {
                // Merge usage and metadata
                primary.UsageCount += duplicate.UsageCount;
                primary.References.AddRange(duplicate.References);
                primary.Conflicts.AddRange(duplicate.Conflicts);
                primary.Tags.AddRange(duplicate.Tags);
                
                // Remove duplicates from original list
                records.Remove(duplicate);
                
                // Delete from store
                _store.Delete(duplicate.Id);
                _logger.LogInformation("Merged duplicate memory {DuplicateId} into {PrimaryId}", 
                    duplicate.Id, primary.Id);
            }

            // Deduplicate merged collections
            primary.References = primary.References.Distinct().ToList();
            primary.Conflicts = primary.Conflicts.Distinct().ToList();
            primary.Tags = primary.Tags.Distinct().ToList();

            _store.Save(primary);
        }
    }

    private void PromoteStableRecords(List<MemoryRecord> records)
    {
        var cutoffDate = DateTime.UtcNow.AddDays(-30); // 30 days old = stable

        foreach (var record in records.Where(r => r.Status == MemoryStatus.Working))
        {
            bool isOldEnough = record.LastUpdated < cutoffDate;
            bool isReferenced = record.References.Count >= 2;
            bool hasConfidence = record.Confidence >= 0.7;

            if (isOldEnough && isReferenced && hasConfidence)
            {
                record.Status = MemoryStatus.Core;
                _store.Save(record);
                _logger.LogInformation("Promoted {Id} to Core (age: 30d+, refs: {RefCount}, confidence: {Confidence:F2})", 
                    record.Id, record.References.Count, record.Confidence);
            }
        }
    }

    private void DeprecateObsoleteRecords(List<MemoryRecord> records)
    {
        foreach (var record in records.Where(r => r.Status != MemoryStatus.Deprecated))
        {
            var score = MemoryScorer.Score(record);
            
            // Deprecate if score too low
            if (score < 0.2)
            {
                record.Status = MemoryStatus.Deprecated;
                _store.Save(record);
                _logger.LogInformation("Deprecated {Id} (score: {Score:F2})", record.Id, score);
            }
        }
    }
}
