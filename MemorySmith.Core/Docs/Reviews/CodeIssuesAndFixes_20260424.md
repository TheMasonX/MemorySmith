# MemorySmith — Code Issues & Fix Guide

**Date:** 2026-04-24  
**Target Audience:** Development team implementing P0 action items

---

## Issue #1: ConsolidationService is Stubbed

### Current Code
**File:** `MemorySmith.Worker/Services/ConsolidationService.cs`
```csharp
private void RunConsolidation()
{
    _logger.LogInformation("Running consolidation...");
    // TODO: Implement actual consolidation logic
}
```

### Problem
- Service runs every 24 hours but does nothing
- Lifecycle is incomplete: no dedup, no promotion, no memory retirement
- User memories accumulate without consolidation
- Contradicts plan promise of automated lifecycle management

### Suggested Implementation

```csharp
private void RunConsolidation()
{
    _logger.LogInformation("Starting consolidation cycle...");
    var records = _store.LoadAll().ToList();

    // 1. Dedup: merge identical or near-identical memories
    var merged = DeduplicateRecords(records);

    // 2. Promote: Working → Core if stable
    PromoteStableRecords(merged);

    // 3. Deprecate: low-score records or contradicted ones
    DeprecateObsoleteRecords(merged);

    _logger.LogInformation("Consolidation complete. Processed {count} memories.", records.Count);
}

private List<MemoryRecord> DeduplicateRecords(List<MemoryRecord> records)
{
    // Group by title (case-insensitive) and content similarity
    var groups = records
        .GroupBy(r => r.Title.ToLowerInvariant())
        .ToList();

    foreach (var group in groups)
    {
        if (group.Count() <= 1) continue;

        // For now: simple exact-match dedup by title
        // In future: add semantic similarity (embeddings)
        var primary = group.First();
        foreach (var duplicate in group.Skip(1))
        {
            // Merge usage and metadata
            primary.UsageCount += duplicate.UsageCount;
            primary.References.AddRange(duplicate.References);
            primary.Conflicts.AddRange(duplicate.Conflicts);
            primary.Tags.AddRange(duplicate.Tags);

            primary.References = primary.References.Distinct().ToList();
            primary.Conflicts = primary.Conflicts.Distinct().ToList();
            primary.Tags = primary.Tags.Distinct().ToList();

            _store.Delete(duplicate.Id);
            _logger.LogInformation("Merged duplicate memory {id} into {primaryId}", 
                duplicate.Id, primary.Id);
        }

        _store.Save(primary);
    }

    return records.Except(records.Where(r => groups
        .SelectMany(g => g.Skip(1))
        .Any(dup => dup.Id == r.Id))).ToList();
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
            _logger.LogInformation("Promoted {id} to Core", record.Id);
        }
    }
}

private void DeprecateObsoleteRecords(List<MemoryRecord> records)
{
    var scorer = new MemoryScorer();

    foreach (var record in records.Where(r => r.Status != MemoryStatus.Deprecated))
    {
        var score = scorer.Score(record);

        // Deprecate if score too low or explicitly marked
        if (score < 0.2)
        {
            record.Status = MemoryStatus.Deprecated;
            _store.Save(record);
            _logger.LogInformation("Deprecated {id} (score: {score:F2})", record.Id, score);
        }
    }
}
```

### Testing
Add tests in `MemorySmith.Tests/ConsolidationServiceTests.cs`:
```csharp
[TestFixture]
public class ConsolidationServiceTests
{
    [Test]
    public void WhenDuplicateMemoriesExist_ThenMergesUsageCount()
    {
        // Arrange
        var store = new InMemoryMemoryStore();
        var record1 = new MemoryRecord { Title = "Test", Content = "Same", UsageCount = 5 };
        var record2 = new MemoryRecord { Title = "Test", Content = "Same", UsageCount = 3 };
        store.Save(record1);
        store.Save(record2);

        var service = new ConsolidationService(store, /* ... */);

        // Act
        service.RunConsolidation();

        // Assert
        var merged = store.LoadAll().First(r => r.Id == record1.Id);
        Assert.That(merged.UsageCount, Is.EqualTo(8));
    }

    [Test]
    public void WhenWorkingMemoryIsOldAndReferenced_ThenPromotesToCore()
    {
        // Arrange
        var store = new InMemoryMemoryStore();
        var oldWorking = new MemoryRecord 
        { 
            Status = MemoryStatus.Working,
            LastUpdated = DateTime.UtcNow.AddDays(-31),
            References = new() { "ref1", "ref2" },
            Confidence = 0.8
        };
        store.Save(oldWorking);

        var service = new ConsolidationService(store, /* ... */);

        // Act
        service.RunConsolidation();

        // Assert
        var promoted = store.Load(oldWorking.Id);
        Assert.That(promoted.Status, Is.EqualTo(MemoryStatus.Core));
    }
}
```

### Effort
- Implementation: 4–6 hours
- Testing: 1–2 hours
- Total: **5–8 hours**

---

## Issue #2: FileMemoryStore Lacks Filename Sanitization

### Current Code
**File:** `MemorySmith.Storage/FileMemoryStore.cs`
```csharp
var path = Path.Combine(dir, $"{record.Id}.json");
File.WriteAllText(path, JsonSerializer.Serialize(record, JsonOptions));
```

### Problem
- IDs with path separators (`/`, `\`) could escape directory
- IDs with `:` could break on Windows (reserved char)
- IDs with `?` or `*` could interfere with wildcards
- Example attack: ID = `"../../evil.json"` writes outside `Data/Memories/`

### Fix

**Option A: Sanitize IDs**
```csharp
private string SanitizeId(string id)
{
    // Replace unsafe chars with underscore
    return Regex.Replace(id, @"[/\\:?*]", "_");
}

public void Save(MemoryRecord record)
{
    record.Id = SanitizeId(record.Id);

    // ... rest of save logic
    var path = Path.Combine(dir, $"{record.Id}.json");
    // ...
}
```

**Option B: Enforce GUID IDs (better)**
```csharp
[Fact]
public void Load(string id)
{
    // Validate that id is UUID format before loading
    if (!Guid.TryParse(id, out _))
        throw new ArgumentException($"Invalid memory ID format: {id}", nameof(id));

    var path = FindFile(id);
    // ...
}
```

### Recommended Approach
- Use **Option A** (defensive sanitization) + **validation in Controller**
- Add validation in `MemoriesController.Create()`:
  ```csharp
  [HttpPost]
  public async Task<IActionResult> Create([FromBody] MemoryRecord record)
  {
      if (string.IsNullOrWhiteSpace(record.Id))
          record.Id = Guid.NewGuid().ToString();

      // Validate before save
      if (!IsValidId(record.Id))
          return BadRequest(new { error = "Invalid ID format" });

      _store.Save(record);
      // ...
  }

  private static bool IsValidId(string id)
      => Guid.TryParse(id, out _) || 
         Regex.IsMatch(id, @"^[a-zA-Z0-9_-]+$");
  ```

### Testing
```csharp
[Test]
public void WhenIdContainsPathSeparator_ThenSanitizes()
{
    var store = new FileMemoryStore(_tempDir);
    var record = new MemoryRecord 
    { 
        Id = "../../evil.json",
        Content = "malicious" 
    };

    store.Save(record);

    // Verify file is in the correct subdirectory
    var files = Directory.GetFiles(Path.Combine(_tempDir, "Unconsolidated"), "*.json");
    Assert.That(files.Length, Is.EqualTo(1));
    Assert.That(Path.GetFileName(files[0]), Does.Not.Contain(".."));
}
```

### Effort
- Implementation: **1 hour**
- Testing: **0.5 hours**
- Total: **1.5 hours**

---

## Issue #3: File Writes Are Not Atomic

### Current Code
```csharp
var path = Path.Combine(dir, $"{record.Id}.json");
File.WriteAllText(path, JsonSerializer.Serialize(record, JsonOptions));
```

### Problem
- If process crashes mid-write, file is corrupted/incomplete
- No way to recover; JSON is invalid
- Concurrent writers could stomp on each other

### Fix: Temp File + Move Pattern

```csharp
public void Save(MemoryRecord record)
{
    // Remove any stale copy in another status folder
    var existing = FindFile(record.Id);
    if (existing is not null)
    {
        var existingStatus = Path.GetFileName(Path.GetDirectoryName(existing));
        if (!string.Equals(existingStatus, record.Status.ToString(), StringComparison.OrdinalIgnoreCase))
            File.Delete(existing);
    }

    var dir = Path.Combine(_basePath, record.Status.ToString());
    Directory.CreateDirectory(dir);
    var path = Path.Combine(dir, $"{record.Id}.json");

    // ATOMIC: write to temp file, then move (rename is atomic on most filesystems)
    var tempPath = Path.Combine(dir, $".{record.Id}.tmp");
    try
    {
        var json = JsonSerializer.Serialize(record, JsonOptions);
        File.WriteAllText(tempPath, json);

        // Move is atomic on NTFS/ext4/etc.
        File.Move(tempPath, path, overwrite: true);
    }
    finally
    {
        // Cleanup temp file if move failed
        if (File.Exists(tempPath))
            try { File.Delete(tempPath); } catch { /* ignore */ }
    }
}
```

### Testing
```csharp
[Test]
public void WhenProcessCrashesDuringWrite_ThenOldFileIsUncorrupted()
{
    var store = new FileMemoryStore(_tempDir);
    var record1 = new MemoryRecord { Id = "test", Content = "original" };
    store.Save(record1);

    var path = Path.Combine(_tempDir, "Unconsolidated", "test.json");
    var original = File.ReadAllText(path);

    // Simulate crash: write temp file but don't move it
    var tempPath = Path.Combine(_tempDir, "Unconsolidated", ".test.tmp");
    File.WriteAllText(tempPath, "corrupted garbage");

    // Original should still be readable
    var recovered = File.ReadAllText(path);
    Assert.That(recovered, Is.EqualTo(original));

    var loaded = store.Load("test");
    Assert.That(loaded.Content, Is.EqualTo("original"));
}
```

### Effort
- Implementation: **1–2 hours**
- Testing: **1 hour**
- Total: **2–3 hours**

---

## Issue #4: Memory Events Are Generated But Not Persisted

### Current Code
**File:** `MemorySmith.Core/StateMachine/MemoryStateMachine.cs`
```csharp
public MemoryEvent Evaluate(MemoryRecord record)
{
    var event = new MemoryEvent 
    { 
        Timestamp = DateTime.UtcNow,
        MemoryId = record.Id,
        Action = "Promoted",
        Details = "Score threshold met"
    };
    return event; // Returned but not persisted
}
```

**File:** `MemorySmith.Worker/Services/TriageService.cs`
```csharp
private void RunTriage()
{
    var records = _store.LoadAll();
    foreach (var record in records)
    {
        // ... evaluate and update record ...
        // But ignore the returned event!
    }
}
```

### Problem
- Events are created but discarded
- No audit trail for lifecycle transitions
- Can't debug why memories were promoted/deprecated
- Plan promises audit logging; not delivered

### Fix: Persist Events to JSONL File

**Step 1: Create Event Store**
```csharp
// New file: MemorySmith.Storage/EventStore.cs
public interface IEventStore
{
    void AppendEvent(MemoryEvent @event);
    IEnumerable<MemoryEvent> GetEvents(string? memoryId = null, DateTime? since = null);
}

public class FileEventStore : IEventStore
{
    private readonly string _eventFilePath;

    public FileEventStore(string basePath)
    {
        Directory.CreateDirectory(basePath);
        _eventFilePath = Path.Combine(basePath, "events.jsonl");
    }

    public void AppendEvent(MemoryEvent @event)
    {
        var json = JsonSerializer.Serialize(@event);
        // JSONL format: one JSON object per line, append-only
        File.AppendAllText(_eventFilePath, json + Environment.NewLine);
    }

    public IEnumerable<MemoryEvent> GetEvents(string? memoryId = null, DateTime? since = null)
    {
        if (!File.Exists(_eventFilePath)) yield break;

        foreach (var line in File.ReadLines(_eventFilePath))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            var @event = JsonSerializer.Deserialize<MemoryEvent>(line);
            if (@event == null) continue;

            if (memoryId != null && @event.MemoryId != memoryId) continue;
            if (since != null && @event.Timestamp < since) continue;

            yield return @event;
        }
    }
}
```

**Step 2: Register in Worker**
```csharp
// MemorySmith.Worker/Program.cs
var dataPath = builder.Configuration["DataPath"]
    ?? Path.Combine(Directory.GetCurrentDirectory(), "..", "Data");

builder.Services.AddSingleton<IMemoryStore>(_ => 
    new FileMemoryStore(Path.Combine(dataPath, "Memories")));

builder.Services.AddSingleton<IEventStore>(_ => 
    new FileEventStore(Path.Combine(dataPath, "Audit")));
```

**Step 3: Wire TriageService to Persist Events**
```csharp
public class TriageService : BackgroundService
{
    private readonly IMemoryStore _store;
    private readonly IEventStore _eventStore;

    public TriageService(
        IMemoryStore store,
        IEventStore eventStore,
        ILogger<TriageService> logger,
        BackgroundServiceTelemetryTracker telemetryTracker)
    {
        _store = store;
        _eventStore = eventStore;
        _logger = logger;
        _telemetryTracker = telemetryTracker;
    }

    private void RunTriage()
    {
        var records = _store.LoadAll().ToList();
        var scorer = new MemoryScorer();
        var stateMachine = new MemoryStateMachine();

        foreach (var record in records)
        {
            var oldStatus = record.Status;
            var score = scorer.Score(record);
            var @event = stateMachine.Evaluate(record);

            // Check if status changed
            if (record.Status != oldStatus)
            {
                // Persist the state transition event
                _eventStore.AppendEvent(@event);
                _store.Save(record);
                _logger.LogInformation(
                    "Triaged {id}: {oldStatus} → {newStatus} (score: {score:F2})",
                    record.Id, oldStatus, record.Status, score);
            }
        }
    }
}
```

### Testing
```csharp
[TestFixture]
public class FileEventStoreTests
{
    private string _testDir;
    private FileEventStore _store;

    [SetUp]
    public void Setup()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"events_{Guid.NewGuid()}");
        _store = new FileEventStore(_testDir);
    }

    [TearDown]
    public void Cleanup() => Directory.Delete(_testDir, recursive: true);

    [Test]
    public void WhenAppendEvent_ThenPersistsToFile()
    {
        var @event = new MemoryEvent 
        { 
            Timestamp = DateTime.UtcNow,
            MemoryId = "test-id",
            Action = "Promoted",
            Details = "Score threshold"
        };

        _store.AppendEvent(@event);

        var events = _store.GetEvents().ToList();
        Assert.That(events.Count, Is.EqualTo(1));
        Assert.That(events[0].MemoryId, Is.EqualTo("test-id"));
    }

    [Test]
    public void WhenGetEventsBySince_ThenFiltersCorrectly()
    {
        var before = new MemoryEvent { Timestamp = DateTime.UtcNow.AddDays(-1), 
            MemoryId = "old", Action = "Test", Details = "" };
        var after = new MemoryEvent { Timestamp = DateTime.UtcNow.AddHours(-1), 
            MemoryId = "new", Action = "Test", Details = "" };

        _store.AppendEvent(before);
        _store.AppendEvent(after);

        var recent = _store.GetEvents(since: DateTime.UtcNow.AddHours(-2)).ToList();
        Assert.That(recent.Count, Is.EqualTo(1));
        Assert.That(recent[0].MemoryId, Is.EqualTo("new"));
    }
}
```

### Effort
- IEventStore implementation: 2 hours
- Integration with TriageService: 1 hour
- Testing: 1.5 hours
- Total: **4.5 hours**

---

## Issue #5: Missing MemoryIndex Unit Tests

### Current State
- `MemoryIndex.cs` exists and is used by `IndexingService`
- Only tested indirectly through integration tests
- No isolated unit tests

### Suggested Tests

```csharp
// New file: MemorySmith.Tests/MemoryIndexTests.cs
[TestFixture]
public class MemoryIndexTests
{
    private MemoryIndex _index;

    [SetUp]
    public void Setup() => _index = new MemoryIndex();

    [Test]
    public void Add_AddsRecordToIndex()
    {
        var record = new MemoryRecord { Id = "test", Tags = new() { "important" } };
        _index.Add(record);

        Assert.That(_index.ById, Does.ContainKey("test"));
        Assert.That(_index.ByTag["important"], Does.Contain("test"));
    }

    [Test]
    public void Remove_RemovesRecordFromIndex()
    {
        var record = new MemoryRecord { Id = "test", Tags = new() { "tag1" }, 
            References = new() { "ref1" } };
        _index.Add(record);
        _index.Remove("test");

        Assert.That(_index.ById, Does.Not.ContainKey("test"));
        Assert.That(_index.ByTag.ContainsKey("tag1") && _index.ByTag["tag1"].Count, Is.EqualTo(0));
    }

    [Test]
    public void Rebuild_ClearsAndRepopulatesIndex()
    {
        var records = new List<MemoryRecord>
        {
            new() { Id = "1", Tags = new() { "a" } },
            new() { Id = "2", Tags = new() { "b" } }
        };
        _index.Rebuild(records);

        Assert.That(_index.ById.Count, Is.EqualTo(2));
        Assert.That(_index.ByTag.Count, Is.EqualTo(2));
    }

    [Test]
    public void MultipleReferences_AreIndexedCorrectly()
    {
        var record = new MemoryRecord 
        { 
            Id = "test",
            References = new() { "ref1", "ref2", "ref3" }
        };
        _index.Add(record);

        Assert.That(_index.ByReference["ref1"], Does.Contain("test"));
        Assert.That(_index.ByReference["ref2"], Does.Contain("test"));
        Assert.That(_index.ByReference["ref3"], Does.Contain("test"));
    }
}
```

### Effort
- Implementation: **1.5–2 hours**

---

## Summary of P0 Fixes

| Issue | File(s) | Effort | Priority |
|-------|---------|--------|----------|
| ConsolidationService stubbed | `TriageService.cs` | 5–8h | P0 |
| Filename sanitization missing | `FileMemoryStore.cs` | 1.5h | P0 |
| Non-atomic file writes | `FileMemoryStore.cs` | 2–3h | P0 |
| Event audit not persisted | `TriageService.cs`, new `EventStore.cs` | 4.5h | P0 |
| InitialPlan.md outdated | `InitialPlan.md` | 1h | P0 |
| MemoryIndex unit tests | `MemoryIndexTests.cs` (new) | 2h | P1 |

**Total P0 effort: ~15–17 hours**  
**Recommended sprint: 2–3 developers for 1 week**

---

**Next Step:** Review these issues with the team and prioritize implementation order. Start with ConsolidationService (highest impact on lifecycle integrity).
