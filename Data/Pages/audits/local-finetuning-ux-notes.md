# MemorySmith Fine-Tuning Harness — UX Supplement

**Status:** Companion note to `finetune_harness_design.md` and the scaffold bundle.
**Date:** 2026-05-28
**Scope:** The UI surface that turns the harness from "a thing you run from PowerShell" into a thing that lives inside the MemorySmith web app.

This note adds three things the main design under-specified:

1. A **context-window dropdown with VRAM hints** for the chat sidebar (and a calibration step that uses real model architecture, not estimates).
2. **VRAM heuristic calculators** for both inference and training — drop-in C# and Python helpers.
3. The **in-app training admin page** (`/admin/training`) with live progress streaming.

Plus a punch list of smaller "chat-friendly" polish items.

---

## 1. Context-window dropdown — the chat sidebar UX

### 1.1 What the user sees

Inside the chat preferences panel (Blazor — same area that selects provider/model today), a context-window control replaces the silent `num_ctx` default:

```
┌─ Context window ────────────────────────────────────┐
│  [▼ 16K — recommended  ⓘ ~6.1 GB VRAM with this model]│
│   2K  — fastest, 1 short turn                       │
│   4K  — short conversations                         │
│   8K  — medium conversations                        │
│ ✓ 16K — recommended (default)                       │
│   24K — long conversations                          │
│   32K — long with retrieval                         │
│   Custom…                                           │
└─────────────────────────────────────────────────────┘
```

Selecting "Custom…" reveals a number input that accepts 256–262,144 (the upstream max for `qwen3.5:4b`). The dropdown disables values that would push past available VRAM by a configurable safety margin (default 90%). Disabled rows still render but with a strikethrough + tooltip explaining the budget.

### 1.2 Blazor component skeleton

Drop into `MemorySmith.App/Components/Shared/ContextWindowPicker.razor`:

```razor
@inject Microsoft.Extensions.Options.IOptionsMonitor<MemorySmithOptions> Settings
@inject IOllamaModelIntrospector Introspector
@inject IJSRuntime JS

<MudSelect T="int"
           @bind-Value="SelectedValue"
           Label="Context window"
           Adornment="Adornment.End"
           AdornmentIcon="@Icons.Material.Outlined.Memory"
           Variant="Variant.Outlined"
           Margin="Margin.Dense"
           HelperText="@HelperText">
    @foreach (var preset in Presets)
    {
        var estimate = _estimates.GetValueOrDefault(preset.Tokens);
        <MudSelectItem T="int"
                       Value="preset.Tokens"
                       Disabled="@(estimate?.ExceedsBudget == true)">
            <div class="ctx-row">
                <span class="ctx-tokens">@preset.Label</span>
                <span class="ctx-note">@preset.Note</span>
                @if (estimate is not null)
                {
                    <span class="ctx-vram @(estimate.ExceedsBudget ? "over-budget" : "")">
                        ~@estimate.GbDisplay GB
                    </span>
                }
            </div>
        </MudSelectItem>
    }
    <MudSelectItem T="int" Value="-1">Custom…</MudSelectItem>
</MudSelect>

@if (SelectedValue == -1)
{
    <MudNumericField T="int"
                     @bind-Value="CustomValue"
                     Min="256" Max="262144" Step="256"
                     Label="Custom tokens"
                     HelperText="@CustomHelperText"
                     Variant="Variant.Outlined"
                     Margin="Margin.Dense" />
}

@code {
    [Parameter] public string ModelTag { get; set; } = "qwen3.5:4b";
    [Parameter] public int Value { get; set; } = 16384;
    [Parameter] public EventCallback<int> ValueChanged { get; set; }
    [Parameter] public double VramBudgetGb { get; set; } = 7.2;   // 90% of 8 GB

    private int SelectedValue
    {
        get => Presets.Any(p => p.Tokens == Value) ? Value : -1;
        set
        {
            if (value == -1) { /* custom mode */ return; }
            Value = value;
            ValueChanged.InvokeAsync(value);
        }
    }
    private int CustomValue
    {
        get => Value;
        set { Value = value; ValueChanged.InvokeAsync(value); }
    }

    private static readonly (int Tokens, string Label, string Note)[] Presets = new[]
    {
        (2048,  "2K",  "fastest, 1 short turn"),
        (4096,  "4K",  "short conversations"),
        (8192,  "8K",  "medium conversations"),
        (16384, "16K", "recommended (default)"),
        (24576, "24K", "long conversations"),
        (32768, "32K", "long with retrieval"),
    };

    private Dictionary<int, VramEstimate> _estimates = new();
    private string HelperText = "Loading model architecture…";
    private string CustomHelperText = "";

    protected override async Task OnParametersSetAsync()
    {
        var arch = await Introspector.GetArchitectureAsync(ModelTag);
        _estimates = Presets.ToDictionary(
            p => p.Tokens,
            p => VramHeuristic.EstimateInference(arch, p.Tokens, kvType: "q8_0", budgetGb: VramBudgetGb));
        HelperText = $"Model: {arch.Name} · {arch.ParametersBillion:0.0}B params · {arch.NumLayers} layers · {arch.NumKvHeads} KV heads";
        CustomHelperText = "256 – 262,144 tokens. Budget is ~7.2 GB on this GPU.";
    }
}
```

A tiny CSS block in the same razor file (or shared theme):

```css
.ctx-row { display: flex; align-items: baseline; gap: 12px; width: 100%; }
.ctx-tokens { font-weight: 600; min-width: 48px; }
.ctx-note   { color: var(--mud-palette-text-secondary); flex: 1; }
.ctx-vram   { color: var(--mud-palette-text-secondary); font-feature-settings: "tnum"; }
.ctx-vram.over-budget { color: var(--mud-palette-error); text-decoration: line-through; }
```

### 1.3 Wiring into the existing chat preferences

The chat page (`Chat.razor`) already has a provider/model panel and stores into `localStorage` via `memorysmith.chat.preferences.v1`. Two changes:

- Replace the silent `OllamaContextWindowTokens` setting with this component.
- Add a `ChatPreferencesState.ContextWindow` field (int) and persist it the same way the model selection is persisted.
- On `ChatRequest` construction, pass the chosen value to the server.

The server uses it to set `options.num_ctx` in the Ollama payload (per the existing `OllamaChatProvider.num_ctx.patch.md` in the scaffold).

---

## 2. VRAM heuristic calculator — `IOllamaModelIntrospector` + helpers

### 2.1 The formula in plain English

**Inference total VRAM** ≈ `weights + kv_cache + activations + overhead`, where:

- `weights` = on-disk model size × 1.1 (loader overhead).
- `kv_cache` = `2 × num_layers × num_kv_heads × head_dim × bytes_per_element × num_ctx`
- `activations` ≈ `0.4 – 0.8 GB` (essentially constant across context lengths for inference).
- `overhead` ≈ `0.3 – 0.5 GB` for the llama.cpp runtime + CUDA workspace.

`bytes_per_element` is the KV cache quantization:

| KV cache type | bytes/element | Effective |
|---|---|---|
| `f16` | 2.0 | baseline |
| `q8_0` | 1.0 | half |
| `q4_0` | ~0.55 | ~quarter (includes scale bytes) |

The single biggest variable is `num_kv_heads`. GQA models like the Qwen3 family typically use 8 KV heads, not 32 — which is why a 4B model can host a 256K context on consumer hardware without OOM. Knowing the real value (not guessing) matters.

### 2.2 The C# introspector — gets the real numbers from Ollama

`MemorySmith.App/Services/Training/OllamaModelIntrospector.cs`:

```csharp
public sealed record ModelArchitecture(
    string Name,
    string Family,           // "qwen35", "llama", "gemma", etc.
    double ParametersBillion,
    int NumLayers,
    int NumAttentionHeads,
    int NumKvHeads,
    int HeadDim,
    int EmbeddingDim,
    int VocabSize,
    string DefaultQuantization,
    long WeightsBytesOnDisk);

public interface IOllamaModelIntrospector
{
    Task<ModelArchitecture> GetArchitectureAsync(string tag, CancellationToken ct = default);
}

public sealed class OllamaModelIntrospector : IOllamaModelIntrospector
{
    private readonly HttpClient _http;
    private readonly IMemoryCache _cache;

    public OllamaModelIntrospector(HttpClient http, IMemoryCache cache)
    {
        _http = http;
        _cache = cache;
    }

    public async Task<ModelArchitecture> GetArchitectureAsync(string tag, CancellationToken ct = default)
    {
        if (_cache.TryGetValue<ModelArchitecture>(tag, out var cached) && cached is not null)
            return cached;

        // Ollama exposes /api/show with full model metadata including
        // model_info.<family>.block_count, attention.head_count, etc.
        var req = new { name = tag, verbose = true };
        using var resp = await _http.PostAsJsonAsync("/api/show", req, ct);
        resp.EnsureSuccessStatusCode();

        using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await System.Text.Json.JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var root = doc.RootElement;

        var modelInfo = root.GetProperty("model_info");
        var details   = root.GetProperty("details");

        // The family prefix differs per model. For qwen3.5:4b it's "qwen35".
        // Walk the keys to find the one with .block_count.
        var familyKey = modelInfo.EnumerateObject()
            .First(p => p.Name.EndsWith(".block_count")).Name.Split('.')[0];

        ModelArchitecture arch = new(
            Name: tag,
            Family: familyKey,
            ParametersBillion: ParseBillions(details.GetProperty("parameter_size").GetString()),
            NumLayers: modelInfo.GetProperty($"{familyKey}.block_count").GetInt32(),
            NumAttentionHeads: modelInfo.GetProperty($"{familyKey}.attention.head_count").GetInt32(),
            NumKvHeads: modelInfo.TryGetProperty($"{familyKey}.attention.head_count_kv", out var kv)
                        ? kv.GetInt32()
                        : modelInfo.GetProperty($"{familyKey}.attention.head_count").GetInt32(),
            HeadDim: modelInfo.TryGetProperty($"{familyKey}.attention.head_dim", out var hd)
                     ? hd.GetInt32()
                     : modelInfo.GetProperty($"{familyKey}.embedding_length").GetInt32()
                       / modelInfo.GetProperty($"{familyKey}.attention.head_count").GetInt32(),
            EmbeddingDim: modelInfo.GetProperty($"{familyKey}.embedding_length").GetInt32(),
            VocabSize: modelInfo.GetProperty($"{familyKey}.vocab_size").GetInt32(),
            DefaultQuantization: details.GetProperty("quantization_level").GetString() ?? "",
            WeightsBytesOnDisk: GetSizeBytes(root));

        _cache.Set(tag, arch, TimeSpan.FromHours(1));
        return arch;
    }

    private static double ParseBillions(string? s)
        => double.TryParse((s ?? "").TrimEnd('B', 'b').Trim(), out var v) ? v : 0;

    private static long GetSizeBytes(System.Text.Json.JsonElement root)
        => root.TryGetProperty("size", out var s) ? s.GetInt64() : 0;
}
```

Register in `Program.cs`:

```csharp
builder.Services.AddHttpClient<IOllamaModelIntrospector, OllamaModelIntrospector>(c =>
    c.BaseAddress = new Uri(builder.Configuration["MemorySmith:Chat:OllamaEndpoint"] ?? "http://localhost:11434"));
builder.Services.AddMemoryCache();
```

### 2.3 The VRAM heuristic — `VramHeuristic`

`MemorySmith.App/Services/Training/VramHeuristic.cs`:

```csharp
public sealed record VramEstimate(
    double WeightsGb,
    double KvCacheGb,
    double ActivationsGb,
    double OverheadGb,
    double TotalGb,
    bool ExceedsBudget,
    double BudgetGb)
{
    public string GbDisplay => TotalGb.ToString("0.0");
}

public static class VramHeuristic
{
    public static VramEstimate EstimateInference(
        ModelArchitecture arch,
        int numCtx,
        string kvType = "q8_0",       // "f16" | "q8_0" | "q4_0"
        double budgetGb = 7.2)
    {
        double weightsGb = (arch.WeightsBytesOnDisk / (1024.0 * 1024.0 * 1024.0)) * 1.10;

        double bytesPerKvElement = kvType switch
        {
            "f16"  => 2.0,
            "q8_0" => 1.0,
            "q4_0" => 0.55,
            _      => 2.0,
        };

        double kvCacheBytes =
            2.0                                  // K + V
            * arch.NumLayers
            * arch.NumKvHeads
            * arch.HeadDim
            * bytesPerKvElement
            * numCtx;
        double kvCacheGb = kvCacheBytes / (1024.0 * 1024.0 * 1024.0);

        // Activations + runtime overhead. Loose constants, calibrated for
        // llama.cpp on Blackwell consumer cards.
        const double activationsGb = 0.6;
        const double overheadGb    = 0.4;

        double total = weightsGb + kvCacheGb + activationsGb + overheadGb;
        return new VramEstimate(
            WeightsGb: Math.Round(weightsGb, 2),
            KvCacheGb: Math.Round(kvCacheGb, 2),
            ActivationsGb: activationsGb,
            OverheadGb: overheadGb,
            TotalGb: Math.Round(total, 2),
            ExceedsBudget: total > budgetGb,
            BudgetGb: budgetGb);
    }

    public static VramEstimate EstimateTraining(
        ModelArchitecture arch,
        int seqLen,
        int loraRank,
        bool qLoraFourBit = true,
        bool gradientCheckpointing = true,
        double budgetGb = 7.5)
    {
        // Order-of-magnitude estimate. Unsloth's published numbers are the
        // calibration target; if the live run disagrees by >20%, log and
        // surface a warning to the admin page so the heuristic can be
        // tightened over time.
        double baseWeightsGb = (arch.WeightsBytesOnDisk / (1024.0 * 1024.0 * 1024.0))
                               * (qLoraFourBit ? 1.0 : 2.4);  // FP16 = ~2.4x Q4_K_M

        // LoRA adapters are tiny but Adam optimizer state and grads scale
        // with adapter parameter count. Rough multiplier of 8x adapter size.
        double adapterParams = loraRank * arch.NumLayers * 7.0 * 2.0 * arch.EmbeddingDim;
        double optimizerStateGb = (adapterParams * 12) / (1024.0 * 1024.0 * 1024.0);

        // Activations dominate at long sequences. Gradient checkpointing
        // cuts this dramatically.
        double activationsGb = (arch.NumLayers * arch.EmbeddingDim * seqLen * 8L)
                               / (1024.0 * 1024.0 * 1024.0);
        if (gradientCheckpointing) activationsGb *= 0.35;

        const double cudaOverheadGb = 0.8;

        double total = baseWeightsGb + optimizerStateGb + activationsGb + cudaOverheadGb;

        return new VramEstimate(
            WeightsGb: Math.Round(baseWeightsGb, 2),
            KvCacheGb: 0,
            ActivationsGb: Math.Round(activationsGb, 2),
            OverheadGb: Math.Round(optimizerStateGb + cudaOverheadGb, 2),
            TotalGb: Math.Round(total, 2),
            ExceedsBudget: total > budgetGb,
            BudgetGb: budgetGb);
    }
}
```

### 2.4 The Python mirror

For the harness's pre-flight check, add `MemorySmith.Training/vram_heuristic.py` (mirrors the C# math):

```python
from dataclasses import dataclass

@dataclass
class ModelArchitecture:
    name: str
    family: str
    parameters_b: float
    num_layers: int
    num_attn_heads: int
    num_kv_heads: int
    head_dim: int
    embedding_dim: int
    vocab_size: int
    weights_bytes_on_disk: int

def estimate_inference(arch, num_ctx, kv_type="q8_0", budget_gb=7.2):
    weights_gb = (arch.weights_bytes_on_disk / (1024**3)) * 1.10
    bytes_per = {"f16": 2.0, "q8_0": 1.0, "q4_0": 0.55}.get(kv_type, 2.0)
    kv_gb = (2 * arch.num_layers * arch.num_kv_heads * arch.head_dim
             * bytes_per * num_ctx) / (1024**3)
    total = weights_gb + kv_gb + 0.6 + 0.4
    return {
        "weights_gb": round(weights_gb, 2),
        "kv_cache_gb": round(kv_gb, 2),
        "total_gb": round(total, 2),
        "exceeds_budget": total > budget_gb,
        "budget_gb": budget_gb,
    }

def estimate_training(arch, seq_len, lora_rank, qlora=True, ckpt=True, budget_gb=7.5):
    base = (arch.weights_bytes_on_disk / (1024**3)) * (1.0 if qlora else 2.4)
    adapter_params = lora_rank * arch.num_layers * 7 * 2 * arch.embedding_dim
    optimizer_gb = (adapter_params * 12) / (1024**3)
    activations_gb = (arch.num_layers * arch.embedding_dim * seq_len * 8) / (1024**3)
    if ckpt:
        activations_gb *= 0.35
    total = base + optimizer_gb + activations_gb + 0.8
    return {
        "weights_gb": round(base, 2),
        "activations_gb": round(activations_gb, 2),
        "total_gb": round(total, 2),
        "exceeds_budget": total > budget_gb,
        "budget_gb": budget_gb,
    }
```

Use it in `harness.py` as a pre-flight: if `estimate_training` says the request will OOM, fail fast with a clear message rather than letting CUDA do it 40 minutes in.

### 2.5 Calibration — close the gap with reality

The heuristic is a heuristic, not truth. The harness writes every actual VRAM peak it sees into `runs/<id>/status.json` (already in the design). A new Blazor admin page surfaces a calibration view: heuristic vs measured, scatter plot. When the ratio drifts past ±15% for three consecutive runs, the constants (`activationsGb`, `overheadGb`, the GC multiplier) get nudged. Easy to do; closes the loop.

---

## 3. The in-app training admin page

A new Blazor route at `/admin/training`. Today the design treats this as a TODO; this section makes it concrete.

### 3.1 Page layout

```
/admin/training
┌──────────────────────────────────────────────────────────────┐
│  Training                                                     │
│  ──────────────────────────────────────────────────────────  │
│                                                               │
│  Active model: memorysmith-athena:v17                         │
│  Last promoted: 2026-05-22 14:03 by tmason                   │
│  [Show history ▾]                                            │
│                                                               │
│  ┌─ New run ─────────────────────────┐ ┌─ Active runs ─────┐ │
│  │ Base model: [qwen3.5:4b      ▾]   │ │ ● v18 train 47%  │ │
│  │ Format:     [Filtered SFT     ▾]  │ │   loss 0.41      │ │
│  │ Epochs:     [3]                   │ │   ETA 24 min     │ │
│  │ Seq len:    [4096]                │ │   [logs] [cancel]│ │
│  │ LoRA rank:  [16]                  │ │                  │ │
│  │ Export:     [sft-may.jsonl    ▾]  │ └──────────────────┘ │
│  │                                   │                       │
│  │ Est. VRAM: 6.1 GB / 7.5 budget   │ ┌─ Pending promo ───┐ │
│  │ Est. time: 1h 47m                 │ │ v18 (eval ✓)     │ │
│  │ Est. cost: $0.07 electricity     │ │   obj1: 0.91     │ │
│  │                                   │ │   obj2: 0.83     │ │
│  │ [Start run]                       │ │   obj3: 0.87     │ │
│  └───────────────────────────────────┘ │  [diff] [promote]│ │
│                                        └──────────────────┘ │
│                                                               │
│  Recent runs                                                  │
│  v17  2026-05-22  done    obj1=0.88 obj2=0.81 obj3=0.84 ★   │
│  v16  2026-05-15  failed  out of memory at seq_len=8192       │
│  v15  2026-05-08  done    obj1=0.86 obj2=0.79 obj3=0.82      │
└──────────────────────────────────────────────────────────────┘
```

### 3.2 Route definition + DI

```razor
@page "/admin/training"
@attribute [Authorize(Policy = "TrainingAdmin")]
@inject ITrainingHarness Harness
@inject ITrainingHistoryStore History
@inject IOllamaModelIntrospector Introspector
@inject NavigationManager Nav

<PageTitle>Training · MemorySmith</PageTitle>

<MudGrid>
    <MudItem xs="12" md="6"><NewRunCard ... /></MudItem>
    <MudItem xs="12" md="6"><ActiveRunsList ... /></MudItem>
    <MudItem xs="12">      <PromotionCandidates ... /></MudItem>
    <MudItem xs="12">      <RecentRunsTable ... /></MudItem>
</MudGrid>
```

The page is composed of small razor components, each isolated to its concern. None of them know anything about Python — they all go through `ITrainingHarness`.

### 3.3 Live progress — SignalR not polling

Polling `status.json` every second from every connected browser is fine on a single-user box but it scales poorly and feels janky. Better: a SignalR hub that the .NET wrapper pushes to whenever it parses a new event line from stdout.

`MemorySmith.App/Hubs/TrainingHub.cs`:

```csharp
public sealed class TrainingHub : Hub
{
    public Task SubscribeToRun(string runId) =>
        Groups.AddToGroupAsync(Context.ConnectionId, $"run:{runId}");
    public Task UnsubscribeFromRun(string runId) =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, $"run:{runId}");
}
```

The `PythonHarnessProcess.PumpStdoutAsync` method gets one extra line — after it appends to `events.jsonl`, it also pushes through the hub:

```csharp
await _hubContext.Clients.Group($"run:{running.RunId}")
    .SendAsync("event", parsedLogLine, cancellationToken);
```

Each Blazor component that wants to watch a run subscribes through a small `TrainingHubClient` helper. Disconnects cleanly on dispose.

### 3.4 Authorization

A new `TrainingAdmin` policy. On `secure-local` and `remote-hardened` profiles, only the principal whose name matches `MemorySmith:Training:AdminPrincipalId` can view the page; on `local-dev` it's open. Wire into the existing auth surface — there's already a security profile system the admin page should hook into rather than re-invent.

### 3.5 What the page does NOT do

- **Does not edit `chat-template.jinja2` from the browser.** That artifact is part of the codebase, version-controlled, deliberate. The admin page just shows its hash.
- **Does not delete Ollama tags.** Pruning is a separate `Scripts/prune-old-tags.ps1` that warns before deleting. We do not want a runaway click to nuke history.
- **Does not modify `appsettings.json` directly for anything but `ActiveModelTag` (via the promote flow).** Other settings are read-only here.

---

## 4. Smaller chat-friendly polish

A punch list of quick wins. Each is an afternoon of work, none requires the harness.

### 4.1 Active-model badge in the chat header

Today the user has no idea which Ollama tag they're talking to. Add a small chip next to the chat title:

```razor
<MudChip Variant="Variant.Outlined" Size="Size.Small" Icon="@Icons.Material.Outlined.Memory">
    @_activeModelTag
</MudChip>
```

Click expands to a popover showing: model name, parameter count, quantization, training run id (if it was produced by the harness), promoted-at timestamp.

### 4.2 "Why did this tool call fail?" surface

When the model emits a tool call that fails (wrong args, missing required field), the user today sees only the surface symptom: an empty or garbled response. Catch the parse failure in `MemoryChatAgent.ReadToolCalls` and surface a small inline error:

```
⚠️ The model's tool call didn't parse cleanly. (Schema: arguments.query is required.)
   [Show raw output ▾]
```

This is gold for the training data — when a user sees this and hits thumbs-down, the note "schema error" auto-fills.

### 4.3 Per-conversation context override

Most conversations don't need 16K. A chat-level override on the settings panel lets a user say "this conversation is short — use 4K to free GPU memory for my IDE." Stored in the same `ChatPreferencesState` as the global default. Surfaces a "context: 4K (override)" pill when active.

### 4.4 "Regenerate" button (sets up DPO v2)

Already mentioned in the design as the v2 enabler. Concrete UI:

```razor
@if (turn.Role == "assistant" && _showFeedback)
{
    <MudIconButton Icon="@Icons.Material.Outlined.Refresh"
                   Size="Size.Small"
                   OnClick="@(() => RegenerateAsync(turn))"
                   aria-label="Regenerate (creates a sibling response)" />
}
```

On click, the C# side re-issues the same request with a fresh seed, stores BOTH responses keyed by the same `requestId`, and lets the user thumbs the better one. Once 500+ paired signals accumulate, flip `TrainingOptions.PreferenceFormat = Dpo`. The export starts producing DPO pairs without further code changes.

### 4.5 First-run nudge

When `Training.FeedbackEnabled = false` and a user has had ~20 chat turns, the admin page surfaces a one-time banner:

> *"Want to help your model learn from your usage? Flip on chat transcripts + thumbs feedback — it stays on your machine."*

With a one-click button that mutates `appsettings.json` (only the two toggles, only when invoked here, with a clear confirmation dialog).

### 4.6 Eval report inline in the chat

When the active model was produced by the harness, the chat header chip's popover includes the eval scores. "Why is the model behaving this way?" is partially answered by "it scored 0.83 on Objective 2 — markdown discipline is its weak spot." Sets accurate expectations.

### 4.7 Memory-type chip on each memory

For the new `MemoryType` enum, every memory render gets a small colored chip:

| Type | Color | Icon |
|---|---|---|
| Episodic | blue | `Event` |
| Semantic | green | `MenuBook` |
| SystemConfig | orange | `Settings` |

Hovering shows the `SubType` if present. Helps both the user and the model — over time, seeing consistent chips reinforces the user's mental model of how to classify their own writes.

---

## 5. Wiring it all up — recommended order

If you ship the scaffold's four commits as planned, layer these in as commits 5–7:

**Commit 5 — UX foundation:**
- `IOllamaModelIntrospector` + `OllamaModelIntrospector`
- `VramHeuristic` + Python mirror
- `ContextWindowPicker.razor` component
- Replace the silent context default in `Chat.razor`

**Commit 6 — Training admin page:**
- `/admin/training` page + sub-components
- `TrainingHub` SignalR hub
- Hook `PythonHarnessProcess` to push events through the hub
- `TrainingAdmin` auth policy

**Commit 7 — Chat-friendly polish:**
- Active-model badge + popover
- Tool-call failure inline error
- Per-conversation context override
- Regenerate button (DPO setup)
- Memory-type chips
- First-run nudge banner

After commit 7, the harness lives **inside** MemorySmith rather than alongside it. A user can go from "I'd like to fine-tune" to a deployed model without touching PowerShell, and they understand at a glance what the GPU is doing and why.

---

## 6. What this supplement does NOT cover

- Replacing the PowerShell scripts entirely with web-only flows. The scripts remain useful for CI-less environments, headless servers, and air-gapped boxes where the Blazor app might not even be running.
- Auto-promotion. Even with a perfect eval pass, model promotion stays human-gated. The page makes promotion one click — that's already a big enough reduction in friction.
- Real-time GPU graph (utilization over time). Nice-to-have; deferred. The status heartbeat already records peak VRAM; a sparkline is easy to add when the calibration loop wants more data.
- A model marketplace. Not in scope. The harness builds *your* model; sharing fine-tunes with other MemorySmith instances is a separate design.

---

## 7. The smallest useful first slice

If you only do one thing from this supplement, do **§ 1 + § 2**: the context-window dropdown with real VRAM estimates. It's the single biggest "oh that's nice" moment for a user who's never seen the inside of a quantization budget. Two new files (`OllamaModelIntrospector.cs`, `VramHeuristic.cs`), one new component (`ContextWindowPicker.razor`), and one edit to the chat preferences panel. Roughly an afternoon. After that, the training admin page is a natural next step because the foundation it builds on — DI-registered introspection, MudBlazor patterns, hub plumbing — is already paid for.
