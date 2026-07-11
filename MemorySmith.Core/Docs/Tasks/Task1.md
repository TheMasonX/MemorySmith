# **MemorySmith — Initial Task Set (Copilot‑Ready)**  
*Phase 1: Core, Storage, Worker, Tests, Docs*

---

## **1. Create Solution + Core Project**
**Goal:** Establish the foundation of the entire system.

**Tasks**
- Add the following initial files:
  - `MemoryRecord.cs`
  - `MemoryStatus.cs`
  - `IMemoryScorer.cs`
  - `MemoryScorer.cs` (basic scoring implementation)
  - `IMemoryConsolidator.cs`
  - `MemoryConsolidationPipeline.cs`
  - `MemoryIndex.cs`
  - `IMemoryGraph.cs` (interface for edges + embeddings)
  - `MemoryEngine.cs` (facade service)
- Add JSON schema under `Schemas/memory.schema.json`
- Add XML documentation comments to all public types

---

## **2. Implement MemoryRecord + Metadata Rules**
**Goal:** Define the canonical memory format.

**Tasks**
- Implement `MemoryRecord` with:
  - Id, Title, Content
  - Status (enum)
  - Confidence
  - Tags
  - References
  - Conflicts
  - UsageCount
  - LastUpdated
- Add validation helpers:
  - `EnsureValidId()`
  - `EnsureValidStatus()`
  - `NormalizeTags()`
- Add static factory method:
  - `MemoryRecord.CreateNew(string content, string title, IEnumerable<string> tags)`

---

## **3. Implement Scoring + State Machine**
**Goal:** Provide deterministic promotion/demotion logic.

**Tasks**
- Implement `MemoryScorer` with the weighted scoring formula
- Add `MemoryStateMachine`:
  - `EvaluatePromotion()`
  - `EvaluateDemotion()`
  - `ShouldPromoteToWorking()`
  - `ShouldPromoteToCore()`
  - `ShouldDeprecate()`
- Add unit tests (NUnit) for each transition

---

## **4. Implement In‑Memory Index**
**Goal:** Enable fast lookups before storage is added.

**Tasks**
- Implement `MemoryIndex` with:
  - `ById`
  - `ByTag`
  - `ByReference`
- Add methods:
  - `Add(MemoryRecord record)`
  - `Remove(string id)`
  - `Rebuild(IEnumerable<MemoryRecord> records)`
- Add tests for indexing behavior

---

## **5. Create Storage Project + FileMemoryStore**
**Goal:** Provide the default file‑based persistence layer.

**Tasks**
- Create project `MemorySmith.Storage`
- Add interface `IMemoryStore`
- Implement `FileMemoryStore`:
  - Directory layout: `Data/Memories/{status}/`
  - JSON serialization/deserialization
  - Atomic writes (temp file → move)
  - LoadAll() scanning all status folders
- Add helper:
  - `MemoryPathResolver`
- Add tests using a temporary directory

---

## **6. Add Embedding + Graph Abstractions**
**Goal:** Prepare for semantic search and Postgres migration.

**Tasks**
- Add interface `IEmbeddingProvider`
- Add interface `IVectorIndex`
- Add interface `IGraphStore`
- Add file‑based implementations:
  - `FileEmbeddingStore`
  - `FileGraphStore` (edges.json)
- Add placeholder cosine similarity implementation

---

## **7. Create Worker Project**
**Goal:** Implement background triage, consolidation, indexing.

**Tasks**
- Create project `MemorySmith.Worker`
- Add hosted services:
  - `TriageService`
  - `ConsolidationService`
  - `IndexingService`
- Add DI setup in `Program.cs`
- Add configuration model:
  - `WorkerSettings.json`
- Add logging for each pipeline step

---

## **8. Create NUnit Test Project**
**Goal:** Establish testing baseline.

**Tasks**
- Create project `MemorySmith.Tests` using NUnit
- Add packages:
  - `NUnit`
  - `NUnit3TestAdapter`
  - `Microsoft.NET.Test.Sdk`
- Add test categories:
  - `CoreTests`
  - `StorageTests`
  - `WorkerTests`
- Add initial tests:
  - MemoryRecord validation
  - Scoring logic
  - State transitions
  - FileMemoryStore round‑trip
  - Index rebuild

---

## **9. Add REST API (Optional Early, Required Later)**
**Goal:** Provide agent‑friendly access.

**Tasks**
- Create controllers:
  - `MemoryController`
  - `SearchController`
- Add endpoints:
  - List, Read, Write, Delete
  - Search
  - RecordUsage
- Add DTOs for API responses
- Add minimal integration tests using WebApplicationFactory

---

## **10. Prepare PostgreSQL Migration Layer (Future)**
**Goal:** Add normalized relational schema + provider.

**Tasks**
- Add project `MemorySmith.Storage.Postgres` (empty for now)
- Add interfaces:
  - `IPostgresConnectionFactory`
  - `IPostgresMemoryStore` (inherits IMemoryStore)
- Add SQL schema files:
  - `schema_memory_records.sql`
  - `schema_memory_tags.sql`
  - `schema_memory_references.sql`
  - `schema_memory_conflicts.sql`
  - `schema_memory_embeddings.sql`
- Add README explaining migration path

---

## **11. Add Developer CLI (Optional)**
**Goal:** Provide operator tools.

**Tasks**
- Create project `MemorySmith.Tools`
- Add commands:
  - `memory list`
  - `memory triage`
  - `memory dream`
  - `memory search`
- Use `System.CommandLine`

---

## **12. Add CI/CD Pipeline**
**Goal:** Ensure build + test stability.

**Tasks**
- Add `.github/workflows/dotnet.yml`
- Steps:
  - checkout
  - setup-dotnet
  - restore
  - build
  - test
- Add optional:
  - dotnet format
  - security scanning