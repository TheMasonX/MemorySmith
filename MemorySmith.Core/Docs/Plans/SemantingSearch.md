# 🧠 Recommended ONNX Embedding Models

These models are already available in ONNX format and work beautifully for semantic search:

### **1. E5‑Small / E5‑Base (recommended)**
- Excellent retrieval performance.
- Trained specifically for semantic search.
- ONNX versions available.

### **2. MiniLM‑L6‑v2 ONNX**
- Lightweight, fast, widely used.
- 384‑dimensional embeddings (same as sentence‑transformers).

### **3. Microsoft’s own ONNX models**
Microsoft maintains several ONNX‑exported transformer models suitable for embedding tasks.

Human Note:
Downloaded E5-Base here. Please make the model file path configurable, but default to this path.
Please add onnx to the .gitignore as well since I don't want to redist
[E5-Base](../../../Data/Models/embedding-model.onnx)

---

# 🏗️ Architecture: ONNX Embedder + FAISS + MemorySmith

```
MemorySmith (.NET)
 ├── Memory Store (C#)
 ├── Page Store (C#)
 ├── Lucene.NET Index (keyword)
 ├── ONNX Embedder (C#)
 ├── FAISS Index (C# via native binding)
 └── Hybrid Search (RRF)
```

No Python.  
No external services.  
Everything local.

---

# 🔧 Step‑by‑Step Implementation Plan

## **1. Add ONNX Runtime to MemorySmith**

```bash
dotnet add package Microsoft.ML.OnnxRuntime
dotnet add package Microsoft.ML.OnnxRuntime.Managed
```

If you want GPU acceleration:

```bash
dotnet add package Microsoft.ML.OnnxRuntime.Gpu
```

---

## **2. Load the ONNX embedding model**

Example: MiniLM‑L6‑v2 ONNX

```csharp
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

public class OnnxEmbedder
{
    private readonly InferenceSession _session;

    public OnnxEmbedder(string modelPath)
    {
        _session = new InferenceSession(modelPath);
    }

    public float[] Embed(string text)
    {
        // Tokenize text → convert to input tensors
        var inputs = Tokenizer.Encode(text);

        // Run inference
        using var results = _session.Run(inputs);

        // Extract embedding (mean pooling)
        var embedding = results.First().AsEnumerable<float>().ToArray();

        // Normalize for cosine similarity
        Normalize(embedding);

        return embedding;
    }

    private void Normalize(float[] v)
    {
        var norm = MathF.Sqrt(v.Sum(x => x * x));
        for (int i = 0; i < v.Length; i++)
            v[i] /= norm;
    }
}
```

You’ll need a tokenizer — easiest is:

- Use a **WordPiece/BERT tokenizer** implemented in C# (several open‑source libs exist)
- Or embed a small Rust tokenizer via WASM (fast and clean)

---

## **3. Integrate FAISS in .NET**

You have two options:

### **Option A — Use FAISS via native bindings**
- Use `Faiss.Net` or your own P/Invoke wrapper.
- Supports `IndexFlatIP`, `IndexHNSW`, `IndexIVF`.

### **Option B — Use a pure C# ANN library**
If you want to avoid native libs:

- **HNSW.NET** (pure C#)
- **AnnLite**
- **NMSLIB via bindings**

But FAISS is still the gold standard.

---

## **4. Build the Semantic Index**

### **Index structure**

- `faiss.IndexFlatIP(dim)` for exact cosine search
- `List<string> IdMap` mapping row → Memory/Page ID

### **Indexing Memories**

```csharp
public void IndexMemory(Memory m)
{
    var text = BuildEmbeddingText(m);
    var vector = _embedder.Embed(text);

    _faissIndex.Add(vector);
    _idMap.Add($"memory:{m.Id}");
}
```

### **Indexing Pages**

```csharp
public void IndexPage(Page p)
{
    var text = MarkdownToPlainText(p.Content);
    var vector = _embedder.Embed(text);

    _faissIndex.Add(vector);
    _idMap.Add($"page:{p.Path}");
}
```

---

## **5. Semantic Search Endpoint**

Replace your current semantic scoring with:

```csharp
public async Task<List<SemanticResult>> SemanticSearch(string query, int k)
{
    var qVec = _embedder.Embed(query);

    var (scores, indices) = _faissIndex.Search(qVec, k);

    var results = new List<SemanticResult>();

    for (int i = 0; i < indices.Length; i++)
    {
        var id = _idMap[indices[i]];
        results.Add(new SemanticResult(id, scores[i]));
    }

    return results;
}
```

This plugs directly into:

- `/api/memories/search/semantic`
- `/api/memories/search/hybrid`

---

## **6. Hybrid Search (Lucene.NET + ONNX/FAISS)**

You already have RRF.  
Just replace your semantic scores with FAISS scores.

```csharp
RRF = 1 / (k + rank)
```

Combine:

- Lucene rank
- FAISS rank

Return the merged list.

---

# 🚀 Performance Notes

- ONNX Runtime is extremely fast on CPU.
- MiniLM‑L6‑v2 ONNX runs ~1–3ms per embedding on a modern CPU.
- FAISS FlatIP can handle millions of vectors easily.
- If you need more speed:
  - Switch to **HNSW** index
  - Use **GPU EP** for ONNX Runtime

---