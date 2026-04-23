# **MemorySmith — Design Document (Rev5, 2026‑04‑23)**  
*A modular .NET 10 memory engine with file‑based storage, normalized PostgreSQL migration path, semantic indexing, and agent‑friendly APIs.*

---

## **1. Overview**
MemorySmith is a modular, single‑repo .NET 10 solution for structured memory management, semantic search, and agent‑driven knowledge consolidation. It models memory as small, metadata‑rich JSON records that flow through a lifecycle:

**Unconsolidated → Working → Core → Deprecated**

The system supports:
- File‑based storage (default)
- Optional PostgreSQL backend (future)
- Optional vector search (pgvector / pg_embedding)
- Background triage + consolidation jobs
- REST + gRPC APIs for agents
- NUnit‑based testing
- CI/CD via GitHub Actions

The architecture is intentionally layered so each subsystem can be replaced or scaled independently.

---

## **2. Solution Structure**
```
MemorySmith/
 ├─ MemorySmith.Core/        # Domain model, state machine, scoring, indexing
 ├─ MemorySmith.Storage/     # File store (default), Postgres provider (future)
 ├─ MemorySmith.Worker/      # Background triage, consolidation, indexing
 ├─ MemorySmith.Tools/       # CLI utilities (optional)
 ├─ MemorySmith.Tests/       # NUnit tests
 ├─ Schemas/                 # JSON schemas
 ├─ Data/                    # File-based memory store
 └─ docs/
```

### **Project Responsibilities**
| Project | Responsibility |
|--------|----------------|
| **MemorySmith.Core** | MemoryRecord model, state machine, scoring, consolidation pipelines, indexing, graph, embedding abstractions |
| **MemorySmith.Storage** | IMemoryStore interface, FileMemoryStore, future PostgresMemoryStore |
| **MemorySmith.Worker** | Windows/Linux Worker Service hosting triage, consolidation, indexing |
| **MemorySmith.Tools** | CLI tools for triage, listing, debugging |
| **MemorySmith.Tests** | NUnit unit + integration tests |

---

## **3. Memory Data Model**
### **3.1 C# DTO**
```csharp
public class MemoryRecord
{
    public string Id { get; set; }
    public string Content { get; set; }
    public string Title { get; set; }
    public MemoryStatus Status { get; set; }
    public double Confidence { get; set; }
    public List<string> Tags { get; set; }
    public List<string> References { get; set; }
    public List<string> Conflicts { get; set; }
    public int UsageCount { get; set; }
    public DateTime LastUpdated { get; set; }
}
```

### **3.2 JSON Schema**
Stored under `Schemas/memory.schema.json`.

### **3.3 Chunking**
- 100–300 tokens per memory  
- Sentence/paragraph boundaries  
- Mirrors AWS Bedrock semantic chunking best practices  

---

## **4. Storage Layer**
### **4.1 IMemoryStore**
```csharp
public interface IMemoryStore
{
    MemoryRecord Load(string id);
    void Save(MemoryRecord record);
    void Delete(string id);
    IEnumerable<MemoryRecord> LoadAll();
}
```

### **4.2 Default: FileMemoryStore**
- One JSON file per memory  
- Stored under `Data/Memories/{status}/`  
- Git‑friendly  
- Easy diffs, easy debugging  

### **4.3 Future: PostgreSQL Provider**
A normalized relational schema (see Section 10) will support:
- ACID writes  
- Fast relational queries  
- pgvector/pg_embedding for semantic search  
- JSONB for flexible metadata  

The Postgres provider will implement:
```
PostgresMemoryStore : IMemoryStore
PostgresVectorIndex : IVectorIndex
```

---

## **5. Indexing & Graph**
### **5.1 In‑Memory Index**
```csharp
public class MemoryIndex
{
    public Dictionary<string, MemoryRecord> ById { get; } = new();
    public Dictionary<string, HashSet<string>> ByTag { get; } = new();
    public Dictionary<string, HashSet<string>> ByReference { get; } = new();
}
```

### **5.2 Graph**
- `edges.json` stores explicit relationships  
- Embeddings stored under `Data/Graph/embeddings/{id}.json`  

### **5.3 Vector Search**
- Initially brute‑force cosine similarity  
- Later: pgvector ANN indexes  

---

## **6. Memory Lifecycle**
### **States**
```csharp
public enum MemoryStatus { Unconsolidated, Working, Core, Deprecated }
```

### **Scoring**
```csharp
double score = 0.4 * record.UsageCount
             + 0.3 * record.Confidence
             + 0.2 * record.References.Count
             + 0.1 * (1.0 / (1 + daysSince(record.LastUpdated)));
```

### **Transitions**
- **Unconsolidated → Working** (score ≥ threshold)  
- **Working → Core** (validated, referenced, stable)  
- **Any → Deprecated** (obsolete, contradicted, low score)  

---

## **7. Background Services**
Hosted in **MemorySmith.Worker** using `IHostedService`.

### **Services**
| Service | Trigger | Purpose |
|---------|---------|---------|
| **TriageService** | File change or timer | Deduping, tagging, validation |
| **ConsolidationService** | Daily | Merging, rewriting, promoting |
| **IndexingService** | After triage or nightly | Rebuild index + embeddings |

---

## **8. Agent API (MCP)**
### **REST Endpoints**
| Method | Route | Description |
|--------|--------|-------------|
| GET | `/api/memories` | List metadata |
| GET | `/api/memories/{id}` | Read memory |
| POST | `/api/memories` | Write memory |
| DELETE | `/api/memories/{id}` | Delete memory |
| POST | `/api/memories/search` | Semantic search |
| POST | `/api/memories/{id}/usage` | Increment usage |

### **gRPC**
Mirrors REST functionality.

---

## **9. Testing (NUnit)**
### **9.1 Test Project Setup**
```
dotnet new nunit -n MemorySmith.Tests
dotnet add MemorySmith.Tests package NUnit
dotnet add MemorySmith.Tests package NUnit3TestAdapter
dotnet add MemorySmith.Tests package Microsoft.NET.Test.Sdk
```

### **9.2 Test Coverage**
- MemoryRecord validation  
- Scoring logic  
- State transitions  
- FileMemoryStore integration tests  
- Worker service end‑to‑end tests  

---

## **10. PostgreSQL Migration Path**
### **10.1 Normalized Schema**
#### **memory_records**
```sql
CREATE TABLE memory_records (
    id TEXT PRIMARY KEY,
    title TEXT,
    content TEXT NOT NULL,
    status TEXT NOT NULL CHECK (status IN ('unconsolidated','working','core','deprecated')),
    confidence DOUBLE PRECISION NOT NULL DEFAULT 0,
    usage_count INTEGER NOT NULL DEFAULT 0,
    last_updated TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    metadata JSONB NOT NULL DEFAULT '{}'
);
```

#### **memory_tags**
```sql
CREATE TABLE memory_tags (
    memory_id TEXT REFERENCES memory_records(id) ON DELETE CASCADE,
    tag TEXT NOT NULL,
    PRIMARY KEY (memory_id, tag)
);
```

#### **memory_references**
```sql
CREATE TABLE memory_references (
    source_id TEXT REFERENCES memory_records(id) ON DELETE CASCADE,
    target_id TEXT REFERENCES memory_records(id) ON DELETE CASCADE,
    PRIMARY KEY (source_id, target_id)
);
```

#### **memory_conflicts**
```sql
CREATE TABLE memory_conflicts (
    memory_id TEXT REFERENCES memory_records(id) ON DELETE CASCADE,
    conflict_id TEXT REFERENCES memory_records(id) ON DELETE CASCADE,
    PRIMARY KEY (memory_id, conflict_id)
);
```

#### **memory_embeddings**
Using pgvector:
```sql
CREATE EXTENSION IF NOT EXISTS vector;

CREATE TABLE memory_embeddings (
    memory_id TEXT PRIMARY KEY REFERENCES memory_records(id) ON DELETE CASCADE,
    embedding vector(1536)
);
```

Or using pg_embedding (Postgres 17+):
```sql
CREATE TABLE memory_embeddings (
    memory_id TEXT PRIMARY KEY REFERENCES memory_records(id) ON DELETE CASCADE,
    embedding embedding
);
```

### **10.2 Migration Strategy**
1. Keep file‑based store as default  
2. Add Postgres provider behind IMemoryStore  
3. Add Postgres vector provider behind IVectorIndex  
4. Add config switch in Worker  
5. Migrate edges.json → relational table  
6. Migrate embeddings → pgvector/pg_embedding  

---

## **11. Logging & Audit**
Every state change produces a MemoryEvent:
```csharp
public class MemoryEvent
{
    public DateTime Timestamp { get; set; }
    public string MemoryId { get; set; }
    public string Action { get; set; }
    public string Details { get; set; }
}
```

Logged to file or structured sink.

---

## **12. CI/CD**
GitHub Actions workflow:
- Restore  
- Build  
- Run NUnit tests  
- Optional: dotnet format, security scanning  

---

## **13. Open Questions**
- Should confidence decay over time?  
- Should consolidation be more ML‑driven?  
- Should embeddings be recomputed on every write or batched?  
- Should we support multi‑writer concurrency in Postgres mode? 