# P0 Action Items Checklist — Ready to Execute

**Status:** Ready for immediate implementation  
**Total Effort:** 11–17 developer-hours (1–2 weeks, 1 developer)  
**Owner:** Backend team  
**Review Date:** 2026-04-24

---

## Overview

This checklist breaks down the 5 P0 items into atomic tasks with:
- ✅ Acceptance criteria
- 📋 Implementation notes
- 🧪 Test requirements
- ⏱️ Effort estimates

Use this to track progress in sprint tools (Azure DevOps, Jira, GitHub Projects).

---

## P0-1: Implement ConsolidationService

**Epic Title:** Enable Memory Consolidation & Lifecycle Management  
**Why:** ConsolidationService currently does nothing; lifecycle is incomplete  
**Impact:** 🔴 Critical — blocks production deployment

### P0-1.1: Design Consolidation Rules

**Task:** Document dedup/merge/promotion rules  
**Effort:** 1 hour  
**Acceptance Criteria:**
- [ ] Written specification of what "consolidation" means
- [ ] Example: Two records with same title should merge how?
- [ ] Rule: Working → Core transition criteria defined
- [ ] Rule: Deprecation criteria defined

**Notes:**
- Recommend: Title + first 100 chars of content as dedup key
- Recommend: Core promotion = age > 30 days AND refs > 2 AND confidence > 0.7
- Recommend: Deprecate if score < 0.2

**Owner:** [Product/Architect]

---

### P0-1.2: Implement Deduplication

**Task:** Add dedup logic to ConsolidationService  
**Effort:** 2 hours  
**Acceptance Criteria:**
- [ ] Code compiles and builds
- [ ] Given 2 records with same title, they merge into 1
- [ ] Usage counts are summed (e.g., 5 + 3 = 8)
- [ ] Tags, references, conflicts are unioned (no duplicates)
- [ ] Duplicate record is deleted from store
- [ ] Logs record: "Merged duplicate X into Y"

**Implementation Notes:**
```csharp
private List<MemoryRecord> DeduplicateRecords(List<MemoryRecord> records)
{
    var groups = records
        .GroupBy(r => r.Title.ToLowerInvariant())
        .ToList();

    foreach (var group in groups)
    {
        if (group.Count() <= 1) continue;

        var primary = group.First();
        foreach (var duplicate in group.Skip(1))
        {
            primary.UsageCount += duplicate.UsageCount;
            primary.References.AddRange(duplicate.References);
            primary.Conflicts.AddRange(duplicate.Conflicts);
            primary.Tags.AddRange(duplicate.Tags);

            // Remove duplicates from collections
            primary.References = primary.References.Distinct().ToList();
            primary.Conflicts = primary.Conflicts.Distinct().ToList();
            primary.Tags = primary.Tags.Distinct().ToList();

            _store.Delete(duplicate.Id);
            _logger.LogInformation("Merged duplicate {id} into {primaryId}", 
                duplicate.Id, primary.Id);
        }
        _store.Save(primary);
    }

    return records.Except(records.Where(r => 
        groups.SelectMany(g => g.Skip(1)).Any(dup => dup.Id == r.Id)))
        .ToList();
}
```

**Tests Required:**
- [ ] `ConsolidationService_WhenDuplicatesExist_MergesUsageCount`
- [ ] `ConsolidationService_WhenDuplicatesExist_UnionsTags`
- [ ] `ConsolidationService_DeletesSecondaryRecords`

**Owner:** [Backend Dev]

---

### P0-1.3: Implement Promotion Logic

**Task:** Add Working → Core promotion  
**Effort:** 1.5 hours  
**Acceptance Criteria:**
- [ ] Given Working record with age > 30 days, refs > 2, confidence > 0.7
- [ ] Record transitions to Core status
- [ ] New status is persisted
- [ ] Log: "Promoted {id} to Core"

**Implementation Notes:**
```csharp
private void PromoteStableRecords(List<MemoryRecord> records)
{
    var cutoffDate = DateTime.UtcNow.AddDays(-30);

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
```

**Tests Required:**
- [ ] `ConsolidationService_PromotesStableRecords`
- [ ] `ConsolidationService_DoesNotPromote_IfTooYoung`
- [ ] `ConsolidationService_DoesNotPromote_IfUnderReferenced`

**Owner:** [Backend Dev]

---

### P0-1.4: Implement Deprecation Logic

**Task:** Add low-score deprecation  
**Effort:** 0.5 hours  
**Acceptance Criteria:**
- [ ] Record with score < 0.2 transitions to Deprecated
- [ ] Status is persisted
- [ ] Log: "Deprecated {id} (score: {score})"

**Implementation Notes:**
```csharp
private void DeprecateObsoleteRecords(List<MemoryRecord> records)
{
    var scorer = new MemoryScorer();
    foreach (var record in records.Where(r => r.Status != MemoryStatus.Deprecated))
    {
        var score = MemoryScorer.Score(record);
        if (score < 0.2)
        {
            record.Status = MemoryStatus.Deprecated;
            _store.Save(record);
            _logger.LogInformation("Deprecated {id} (score: {score:F2})", 
                record.Id, score);
        }
    }
}
```

**Owner:** [Backend Dev]

---

### P0-1.5: Test ConsolidationService End-to-End

**Task:** Add integration test  
**Effort:** 1 hour  
**Acceptance Criteria:**
- [ ] Create 3 records: Working (old, 3 refs), Working (young), Unconsolidated
- [ ] RunConsolidation()
- [ ] Assert: Old Working promoted to Core
- [ ] Assert: Young Working stays Working
- [ ] Assert: Unconsolidated unchanged

**Test Structure:**
```csharp
[Test]
public void ConsolidationService_FlowsRecordsCorrectly()
{
    var store = new InMemoryMemoryStore(); // Or temp FileMemoryStore

    // Setup: Old working memory (should promote)
    var oldWorking = new MemoryRecord
    {
        Id = "old-working",
        Status = MemoryStatus.Working,
        LastUpdated = DateTime.UtcNow.AddDays(-31),
        References = new() { "ref1", "ref2" },
        Confidence = 0.8,
        UsageCount = 5
    };
    store.Save(oldWorking);

    // Setup: Young working memory (should not promote)
    var youngWorking = new MemoryRecord
    {
        Id = "young-working",
        Status = MemoryStatus.Working,
        LastUpdated = DateTime.UtcNow.AddDays(-5),
        References = new() { "ref1" },
        Confidence = 0.8
    };
    store.Save(youngWorking);

    // Execute
    var service = new ConsolidationService(store, logger, telemetry);
    service.RunConsolidation(); // Should run without exception

    // Assert
    var promotedRecord = store.Load("old-working");
    Assert.That(promotedRecord.Status, Is.EqualTo(MemoryStatus.Core));

    var nonpromoted = store.Load("young-working");
    Assert.That(nonpromoted.Status, Is.EqualTo(MemoryStatus.Working));
}
```

**Owner:** [QA/Backend Dev]

---

**P0-1 Summary:**
- **Total effort:** 5.5 hours (design 1h + dedup 2h + promote 1.5h + deprecate 0.5h + test 1h)
- **Definition of done:** All 5 subtasks complete + tests passing + code reviewed
- **Suggested completion:** Day 1–2 of sprint

---

## P0-2: Persist Event Audit Trail

**Epic Title:** Add Memory Event Audit Logging  
**Why:** Events created but discarded; no audit trail for debugging  
**Impact:** 🟡 Medium — nice-to-have but improves debuggability

### P0-2.1: Create IEventStore Interface

**Task:** Design event store abstraction  
**Effort:** 0.5 hours  
**Acceptance Criteria:**
- [ ] Interface file created: `MemorySmith.Storage/IEventStore.cs`
- [ ] Methods: AppendEvent(event), GetEvents(memoryId, since)
- [ ] No implementation (interface only)

**Code:**
```csharp
namespace MemorySmith.Storage;

public interface IEventStore
{
    void AppendEvent(MemoryEvent @event);
    IEnumerable<MemoryEvent> GetEvents(string? memoryId = null, DateTime? since = null);
}
```

**Owner:** [Backend Dev]

---

### P0-2.2: Implement FileEventStore

**Task:** Create JSONL-based event persistence  
**Effort:** 1.5 hours  
**Acceptance Criteria:**
- [ ] File: `MemorySmith.Storage/FileEventStore.cs`
- [ ] AppendEvent() writes to `Data/Audit/events.jsonl`
- [ ] One JSON object per line (JSONL format)
- [ ] GetEvents() filters by memoryId and since date
- [ ] Handles missing file gracefully

**Code Structure:**
```csharp
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

**Owner:** [Backend Dev]

---

### P0-2.3: Register in DI Container

**Task:** Wire IEventStore in Program.cs  
**Effort:** 0.5 hours  
**Acceptance Criteria:**
- [ ] IEventStore registered in DI
- [ ] FileEventStore created with audit path
- [ ] No build errors

**Code Location:** Program.cs (after IMemoryStore registration)
```csharp
builder.Services.AddSingleton<IEventStore>(_ => 
    new FileEventStore(Path.Combine(dataPath, "Audit")));
```

**Owner:** [Backend Dev]

---

### P0-2.4: Wire TriageService to Persist Events

**Task:** Hook event persistence on state transitions  
**Effort:** 1 hour  
**Acceptance Criteria:**
- [ ] TriageService injects IEventStore
- [ ] When status changes, event is appended
- [ ] Event includes: timestamp, memoryId, action, details
- [ ] Logs: "Triage: {id} {old}→{new} (score: {score})"

**Code Changes (TriageService.cs):**
```csharp
public class TriageService : BackgroundService
{
    private readonly IMemoryStore _store;
    private readonly IEventStore _eventStore; // Add
    // ...

    public TriageService(
        IMemoryStore store,
        IEventStore eventStore, // Add parameter
        ILogger<TriageService> logger,
        BackgroundServiceTelemetryTracker telemetryTracker)
    {
        _store = store;
        _eventStore = eventStore; // Store it
        // ...
    }

    private void RunTriage()
    {
        var stateMachine = new MemoryStateMachine();
        foreach (var record in _store.LoadAll())
        {
            var (newStatus, @event) = stateMachine.Evaluate(record);
            if (newStatus != record.Status)
            {
                // Persist event BEFORE changing status
                if (@event != null)
                    _eventStore.AppendEvent(@event);

                _logger.LogInformation("Triage: {Id} {Old}→{New}", 
                    record.Id, record.Status, newStatus);
                record.Status = newStatus;
                record.LastUpdated = DateTime.UtcNow;
                _store.Save(record);
            }
        }
    }
}
```

**Owner:** [Backend Dev]

---

### P0-2.5: Test Event Persistence

**Task:** Add unit tests for IEventStore  
**Effort:** 1 hour  
**Acceptance Criteria:**
- [ ] FileEventStore_AppendEvent_WritesToFile
- [ ] FileEventStore_GetEvents_FiltersByMemoryId
- [ ] FileEventStore_GetEvents_FiltersBySince
- [ ] FileEventStore_GetEvents_ReturnsEmptyIfFileDoesNotExist
- [ ] All tests pass

**Test Template:**
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
    public void AppendEvent_WritesToFile()
    {
        var @event = new MemoryEvent
        {
            MemoryId = "test-1",
            Action = "Promoted",
            Details = "Score > 2.0"
        };

        _store.AppendEvent(@event);

        var events = _store.GetEvents().ToList();
        Assert.That(events.Count, Is.EqualTo(1));
        Assert.That(events[0].MemoryId, Is.EqualTo("test-1"));
    }

    // ... more tests
}
```

**Owner:** [QA/Backend Dev]

---

**P0-2 Summary:**
- **Total effort:** 4.5 hours (interface 0.5h + impl 1.5h + DI 0.5h + wiring 1h + tests 1h)
- **Suggested completion:** Day 2–3 of sprint
- **Success metric:** `events.jsonl` file grows on each triage run

---

## P0-3: Implement Atomic File Writes

**Epic Title:** Ensure Data Integrity Under Crash/Concurrent Writes  
**Why:** Direct file writes risk corruption if process crashes mid-write  
**Impact:** 🟡 High — prevents silent data loss

### P0-3.1: Update FileMemoryStore.Save() Method

**Task:** Implement temp file + atomic move pattern  
**Effort:** 1.5 hours  
**Acceptance Criteria:**
- [ ] Code compiles
- [ ] Save() writes to temp file first: `{id}.json.tmp`
- [ ] Move temp to final location (atomic rename)
- [ ] Cleanup temp file on error
- [ ] Original file unchanged if crash before move
- [ ] Tests pass

**Implementation:**
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
            try { File.Delete(tempPath); }
            catch { /* ignore cleanup errors */ }
    }
}
```

**Owner:** [Backend Dev]

---

### P0-3.2: Add Tests for Atomicity

**Task:** Test crash scenario simulation  
**Effort:** 1 hour  
**Acceptance Criteria:**
- [ ] Test: If crash occurs during write, temp file left behind
- [ ] Test: On restart, original file is intact
- [ ] Test: Corrupt temp files are ignored by LoadAll()

**Test Template:**
```csharp
[Test]
public void Save_CrashDuringWrite_PreservesOriginalFile()
{
    var store = new FileMemoryStore(_tempDir);

    // Create and save original record
    var record = new MemoryRecord { Id = "test", Content = "original" };
    store.Save(record);

    var path = Path.Combine(_tempDir, "Unconsolidated", "test.json");
    var originalContent = File.ReadAllText(path);

    // Simulate incomplete write: leave temp file, don't complete move
    var tempPath = Path.Combine(_tempDir, "Unconsolidated", ".test.tmp");
    File.WriteAllText(tempPath, "CORRUPTED");

    // Original should still be readable
    var recovered = File.ReadAllText(path);
    Assert.That(recovered, Is.EqualTo(originalContent));

    var loaded = store.Load("test");
    Assert.That(loaded.Content, Is.EqualTo("original"));
}
```

**Owner:** [QA/Backend Dev]

---

**P0-3 Summary:**
- **Total effort:** 2.5 hours (implementation 1.5h + tests 1h)
- **Suggested completion:** Day 1–2 of sprint
- **Success metric:** Temp files cleaned up after Save(); corrupted files don't appear in LoadAll()

---

## P0-4: Add Filename Sanitization & GUID Validation

**Epic Title:** Secure ID Validation (Prevent Path Traversal)  
**Why:** Arbitrary IDs could escape directory or break on Windows  
**Impact:** 🟡 Medium (internal MVP → 🔴 High for public API)

### P0-4.1: Add GUID Validation in MemoriesController

**Task:** Validate record ID format at API boundary  
**Effort:** 0.5 hours  
**Acceptance Criteria:**
- [ ] Create endpoint validates ID is valid GUID or assigns new one
- [ ] Invalid IDs return 400 Bad Request
- [ ] Update endpoint validates ID format
- [ ] Tests pass

**Code (MemoriesController.cs):**
```csharp
[HttpPost]
public async Task<IActionResult> Create([FromBody] MemoryRecord record)
{
    // Auto-generate if missing
    if (string.IsNullOrWhiteSpace(record.Id))
        record.Id = Guid.NewGuid().ToString();

    // Validate format
    if (!Guid.TryParse(record.Id, out _))
        return BadRequest(new { error = "Invalid ID format; must be valid GUID" });

    record.LastUpdated = DateTime.UtcNow;
    _store.Save(record);
    await _hub.Clients.All.ReceiveMemoryUpdate(new MemoryUpdateEvent { Id = record.Id, Action = "Created" });
    await BroadcastStatsAsync();
    return CreatedAtAction(nameof(Get), new { id = record.Id }, record);
}

[HttpPut("{id}")]
public async Task<IActionResult> Update(string id, [FromBody] MemoryRecord record)
{
    // Validate ID format
    if (!Guid.TryParse(id, out _))
        return BadRequest(new { error = "Invalid ID format; must be valid GUID" });

    if (_store.Load(id) is null) return NotFound();
    record.Id = id;
    record.LastUpdated = DateTime.UtcNow;
    _store.Save(record);
    await _hub.Clients.All.ReceiveMemoryUpdate(new MemoryUpdateEvent { Id = id, Action = "Updated" });
    await BroadcastStatsAsync();
    return Ok(record);
}
```

**Owner:** [Backend Dev]

---

### P0-4.2: Test GUID Validation

**Task:** Add validation tests  
**Effort:** 0.25 hours  
**Acceptance Criteria:**
- [ ] Test: Valid GUID is accepted
- [ ] Test: Invalid ID returns 400
- [ ] Test: Missing ID is auto-generated

**Owner:** [QA/Backend Dev]

---

**P0-4 Summary:**
- **Total effort:** 0.75 hours (validation 0.5h + tests 0.25h)
- **Suggested completion:** Day 1 of sprint
- **Success metric:** Only GUID-format IDs are accepted; others return 400

---

## P0-5: Update InitialPlan.md

**Epic Title:** Clarify Architecture Roadmap & Status  
**Why:** Plan advertises features not in MVP; need honest baseline  
**Impact:** 🟡 Medium — manages stakeholder expectations

### P0-5.1: Add Status Table to Sections 5–7

**Task:** Update Search, Lifecycle, and Services sections  
**Effort:** 1 hour  
**Acceptance Criteria:**
- [ ] Section 5 (Search): Clarify "lexical now, vector in Phase 2A"
- [ ] Section 6 (Lifecycle): Note "consolidation stubbed, deferred to Phase 2A"
- [ ] Section 7 (Services): Mark "ConsolidationService is stub"
- [ ] File updated and reviewed

**Additions to InitialPlan.md:**

```markdown
## 5. Indexing & Search

### Phase 1 (MVP — Current Implementation)
- ✅ Lexical full-text search (substring matching on content/title/tags)
- ✅ Filtering by status and tags
- ✅ Pagination support
- ⏱️ Performance: O(n) full table scan per search (acceptable for < 5K records)
- ❌ Semantic/vector search (deferred to Phase 2A)

### Phase 2A (Vector Search — Planned)
- Vector embeddings (model TBD: OpenAI or Ollama)
- Cosine similarity matching
- New endpoint: `POST /api/memories/search-semantic`
- Expected performance: < 1s for 10K records

**Note:** Do not market as "semantic search" until Phase 2A is complete.

---

## 6. Memory Lifecycle & Consolidation

### Phase 1 (MVP — Current Implementation)
- ✅ Scoring formula (40% usage + 30% confidence + 20% refs + 10% recency)
- ✅ State transitions (Unconsolidated → Working → Core / Deprecated)
- ✅ Automatic triage via TriageService (5-min interval)
- ❌ Consolidation service (currently a stub — see note below)

### ConsolidationService — MVP Status
The ConsolidationService runs every 24 hours but performs **no actual consolidation**. It logs the number of records processed and exits.

**Phase 2A implementation will add:**
1. Deduplication (merge identical memories)
2. Promotion rules (Working → Core if stable)
3. Deprecation (retire low-scoring memories)

**Rationale for deferral:** Consolidation requires careful domain specification (e.g., what defines "stable"?). 
Recommend Phase 2A spike to validate merge semantics before implementation.

### Phase 2A (Consolidation — Planned)
- Dedup logic (merge by title + content preview)
- Promotion rules (age > 30 days AND refs > 2 AND confidence > 0.7)
- Deprecation rules (score < 0.2)
- Event tracking for audit trail

---

## 7. Background Services

| Service | Interval | Status | Notes |
|---------|----------|--------|-------|
| TriageService | 5 min | ✅ Working | Scores all records, transitions states |
| ConsolidationService | 24h | 🔴 Stubbed | Logs only; implementation deferred to Phase 2A |
| IndexingService | 1h | ✅ Working | Rebuilds in-memory index |
| StatsBroadcastService | 10s | ✅ Working | Sends stats to dashboard via SignalR |
```

### P0-5.2: Add Completion Status Matrix

**Task:** Create section summarizing what's done vs. deferred  
**Effort:** 0.5 hours  
**Acceptance Criteria:**
- [ ] Table showing each section: Phase 1 ✅ / Partial ⚠️ / Deferred ❌
- [ ] Brief explanation for each gap
- [ ] Clear Phase 2 roadmap

**Addition to InitialPlan.md:**

```markdown
## Status Summary (Rev6, 2026-04-24)

| Feature | Phase 1 MVP | Phase 2A | Phase 2B | Phase 2C |
|---------|-------------|---------|---------|---------|
| **Domain Model** | ✅ | — | — | — |
| **File Storage** | ✅ | — | — | Postgres |
| **Scoring & Transitions** | ✅ | — | — | — |
| **Triage Service** | ✅ | — | — | — |
| **Consolidation** | ❌ Stub | ✅ Implement | — | — |
| **Lexical Search** | ✅ | Keep | — | Optimize |
| **Vector/Semantic Search** | ❌ | ✅ Add | — | Optimize |
| **Graph Edges** | ❌ | — | ✅ Add | Normalize |
| **Audit Trail** | ⚠️ Event model | ✅ Persist | — | — |
| **REST API** | ✅ | Extend | — | — |
| **gRPC API** | ❌ | — | ✅ | — |
| **PostgreSQL** | ❌ Design | — | — | ✅ Implement |

### Completion Baseline
- **Phase 1 (MVP): 62% complete**
- Blockers for production: 5 critical P0 fixes
- ETA to Phase 1 stable: 1–2 weeks (P0 + P1 testing)
- ETA to Phase 2A complete: 6–8 weeks
- ETA to full architecture: 3–4 months
```

### P0-5.3: Update "Open Questions" Section

**Task:** Document decisions made during MVP  
**Effort:** 0.5 hours
**Acceptance Criteria:**
- [ ] Document search strategy (lexical now, vector later)
- [ ] Document consolidation deferral + rationale
- [ ] Document scoring formula validation status

---

**P0-5 Summary:**
- **Total effort:** 2 hours (sections 1h + matrix 0.5h + questions 0.5h)
- **Suggested completion:** Day 3 of sprint (after fixes validated)
- **Success metric:** Plan accurately reflects MVP + clear Phase 2 roadmap

---

## Overall P0 Summary Table

| Item | Effort | Status | Owner |
|------|--------|--------|-------|
| P0-1 Consolidation | 5.5h | 🟡 Ready to start | Backend Dev |
| P0-2 Event Audit | 4.5h | 🟡 Ready to start | Backend Dev + QA |
| P0-3 Atomic Writes | 2.5h | 🟡 Ready to start | Backend Dev + QA |
| P0-4 ID Validation | 0.75h | 🟡 Ready to start | Backend Dev + QA |
| P0-5 Plan Update | 2h | 🟡 Ready to start | Architect |
| **Total** | **15.25h** | Ready | Team |

**Recommended Schedule:**
```
Day 1-2: P0-1 (Consolidation) + P0-3 (Atomicity)
Day 2-3: P0-2 (Event Audit) + P0-4 (Validation)
Day 3: P0-5 (Plan Update) + Review & Testing
```

**Success Criteria:**
- ✅ All tests passing (22 existing + new P0 tests)
- ✅ Build successful
- ✅ ConsolidationService does real work
- ✅ Event audit trail in `Data/Audit/events.jsonl`
- ✅ File writes atomic (temp files cleaned up)
- ✅ IDs validated as GUIDs
- ✅ InitialPlan.md updated with honest status

---

## Next Steps

1. **Assign Tasks:** Pick owner for each P0 item
2. **Create Sprint:** Add 15-hour estimate to sprint planning
3. **Start Day 1:** Begin with P0-1.1 (design) + P0-3.1 (atomicity)
4. **Daily Standup:** Report progress on each item
5. **Code Review:** Ensure master review code samples are followed
6. **Testing:** Use provided test templates
7. **Sign-Off:** Architect reviews InitialPlan.md changes

---

**Prepared by:** GitHub Copilot  
**Date:** 2026-04-24  
**Source:** Master Review + Independent Review  
**Confidence:** 90/100
