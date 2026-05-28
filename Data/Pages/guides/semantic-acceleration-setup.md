# Semantic Acceleration Setup Guide

This guide covers the current ONNX hardware-acceleration options in MemorySmith, how to deploy them safely, how CPU fallback behaves, and how to warm the repo code vector index into `Data/Graph/code-search/code-search.db`.

## 1. What MemorySmith Supports

MemorySmith supports three ONNX Runtime deployment flavors for semantic embeddings:

- `Cpu`: portable default and the current repo-safe baseline.
- `Cuda`: NVIDIA-backed acceleration on supported Windows/Linux hosts.
- `OpenVino`: Intel OpenVINO acceleration on supported Windows hosts.

Runtime provider selection is controlled separately from the published binary:

- `MemorySmithOnnxRuntimeFlavor` controls which native ONNX package is published with the app.
- `MemorySmith:SemanticSearch:ExecutionProvider` controls which provider the app requests at runtime.

The recommended rule is simple: keep the runtime provider aligned with the published flavor unless you are intentionally testing failure-and-fallback behavior.

## 1.1 CUDA Host Prerequisites

For the current `Microsoft.ML.OnnxRuntime.Gpu` 1.24.1 package in this repo, the safest Windows expectation is:

- NVIDIA driver installed
- CUDA runtime/toolkit available on `PATH`
- cuDNN 9 runtime available on `PATH`

On the current maintainer machine, the CUDA toolkit added these paths automatically:

- `C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA\v13.3\bin\x64`
- `C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA\v13.3\bin`

The cuDNN local installer did **not** add its runtime folder automatically. The installed DLLs landed under:

- `C:\Program Files\NVIDIA\CUDNN\v9.22\bin\12.9\x64`
- `C:\Program Files\NVIDIA\CUDNN\v9.22\bin\13.2\x64`

For MemorySmith's current ONNX package, prefer adding the `12.9\x64` cuDNN folder to the machine `PATH` first. That is the most conservative match for ONNX Runtime's documented CUDA 12 + cuDNN 9 baseline.

Verify that Windows can resolve the DLL before you expect CUDA to activate:

```powershell
where.exe cudnn64_9.dll
```

If that command returns nothing, ONNX Runtime will usually fail with a message similar to:

- `onnxruntime_providers_cuda.dll ... depends on cudnn64_9.dll which is missing`

## 2. CPU Fallback Contract

MemorySmith now falls back to CPU embeddings when all of these are true:

- the requested provider is `Cuda` or `OpenVino`
- `MemorySmith:SemanticSearch:CpuFallbackEnabled=true`
- the requested hardware provider fails to initialize

When fallback happens:

- the app still starts
- semantic embeddings remain available through CPU ONNX
- `/health` reports the requested provider, the active provider, and the fallback reason
- persisted document embeddings are invalidated when the provider settings or ONNX runtime version change

That means a CUDA-flavored server can be configured to prefer `Cuda` without turning semantic search off on machines that are missing the right runtime pieces.

## 3. Runtime Settings

These keys live under `MemorySmith:SemanticSearch`:

```json
{
  "EmbeddingsEnabled": true,
  "PrewarmOnStartupEnabled": true,
  "ExecutionProvider": "Cpu",
  "CpuFallbackEnabled": true,
  "CudaDeviceId": 0,
  "OpenVinoDeviceId": ""
}
```

Notes:

- `ExecutionProvider` accepts `Cpu`, `Cuda`, or `OpenVino`.
- `PrewarmOnStartupEnabled` defaults to `true` and kicks off a background query/document probe after app startup so the first real semantic/code-search request does not pay the full lazy ONNX initialization cost.
- `CudaDeviceId` selects the GPU when using CUDA.
- `OpenVinoDeviceId` is optional; blank lets ONNX Runtime pick the default OpenVINO target.
- Leave `CpuFallbackEnabled=true` unless you explicitly want startup failure when the requested hardware provider is unavailable.

## 4. Source-Run Setup

For a local source run, keep using the normal app profiles and add the ONNX flavor as an MSBuild property.

CPU default:

```powershell
dotnet run --project MemorySmith.App --launch-profile https
```

CUDA build:

```powershell
dotnet run --project MemorySmith.App --launch-profile https -p:MemorySmithOnnxRuntimeFlavor=Cuda
```

If CUDA still falls back after installing cuDNN, prepend the cuDNN runtime directory before starting the app:

```powershell
$env:PATH = 'C:\Program Files\NVIDIA\CUDNN\v9.22\bin\12.9\x64;' + $env:PATH
dotnet run --project MemorySmith.App --launch-profile https -p:MemorySmithOnnxRuntimeFlavor=Cuda
```

For a persistent machine-level fix on Windows, add the same directory to the machine `PATH`, then restart the terminal, the app, and any installed MemorySmith service.

OpenVINO build:

```powershell
dotnet run --project MemorySmith.App --launch-profile https -p:MemorySmithOnnxRuntimeFlavor=OpenVino
```

For source-run overrides, place `appsettings.LocalOverrides.json` beside the running app output or set `MemorySmith:SettingsOverridePath` to an explicit file. A typical local override looks like this:

```json
{
  "MemorySmith": {
    "SemanticSearch": {
      "EmbeddingsEnabled": true,
      "PrewarmOnStartupEnabled": true,
      "ExecutionProvider": "Cuda",
      "CpuFallbackEnabled": true,
      "CudaDeviceId": 0,
      "OpenVinoDeviceId": ""
    }
  }
}
```

## 5. Windows Service Setup

Use `Scripts/Redeploy-MemorySmithService.ps1`. It now owns all of these in one place:

- publish flavor selection through `-OnnxRuntimeFlavor`
- semantic runtime settings through `-SemanticExecutionProvider`, `-CpuFallbackEnabled`, `-CudaDeviceId`, and `-OpenVinoDeviceId`
- writing the effective `appsettings.LocalOverrides.json` file for the published service
- passing `MemorySmith:SettingsOverridePath` to the installed service runtime

Example CUDA-preferred deploy with HTTPS and CPU fallback:

```powershell
./Scripts/Redeploy-MemorySmithService.ps1 \
  -UseHttps \
  -OnnxRuntimeFlavor Cuda \
  -SemanticExecutionProvider Cuda \
  -CpuFallbackEnabled $true \
  -CudaDeviceId 0
```

Before redeploying a Windows service, make sure the service host can also resolve `cudnn64_9.dll`. On this repo's current package set, the practical fix is to add:

- `C:\Program Files\NVIDIA\CUDNN\v9.22\bin\12.9\x64`

to the machine `PATH`, then restart the service so the new environment is inherited.

Example OpenVINO-preferred deploy with CPU fallback:

```powershell
./Scripts/Redeploy-MemorySmithService.ps1 \
  -UseHttps \
  -OnnxRuntimeFlavor OpenVino \
  -SemanticExecutionProvider OpenVino \
  -CpuFallbackEnabled $true
```

If you want to force a hard failure instead of fallback when the provider is unavailable, set `-CpuFallbackEnabled $false`.

## 6. Verifying The Active Provider

After startup, open `/health`.

The semantic-search card now tells you:

- whether embeddings are available
- the active execution provider
- the requested provider when fallback occurred
- the failure reason if the requested provider could not initialize

Typical healthy fallback example:

- `Embeddings available / active Cpu / Requested Cuda (0) was unavailable; fell back to CPU ...`

Typical healthy CUDA example:

- `Embeddings available / active Cuda (0) / ONNX embedding provider is available via Cuda (0).`

## 7. Build The Repo Code Vector Database

The code-search index is stored under:

- `Data/Graph/code-search/code-search.db`

To warm it through the running server and capture timing, use:

```powershell
./Scripts/Warm-CodeSearchIndex.ps1 \
  -BaseUrl https://localhost:7090 \
  -SkipCertificateCheck \
  -ForceRebuild \
  -SummaryPath artifacts/browser-validation/code-search-index-summary.json
```

The script automatically reads `MemorySmith.ApiKey` from `artifacts/MemorySmith.App/appsettings.LocalOverrides.json` when that file exists. For a different protected deployment, pass `-ApiKey` directly or point `-SettingsOverridePath` at the matching override file.

What the script does:

- calls `memorysmith_code_search` through the MCP endpoint to force indexing
- waits for the synchronous indexing/search call to finish
- reads `memorysmith_code_search_status`
- reports elapsed time, indexed file count, indexed chunk count, provider mode, provider status, SQLite path, SQLite size, rough throughput, and the raw build timing breakdown

Operational note:

- Startup prewarm is now enabled by default. On a normal service or app boot, MemorySmith starts a background query/document probe immediately so the first operator-driven semantic request usually sees the warmed session instead of the full lazy initialization hit.
- If you disable `PrewarmOnStartupEnabled`, the first CUDA-backed request becomes a true warm-up again. That first `Warm-CodeSearchIndex.ps1` run after a fresh process start can spend tens of seconds loading the model and preparing the GPU session even when the index itself is fully reused.
- After deploying or restarting a CUDA-preferred app or service with prewarm disabled, run one no-rebuild warm pass first if you want to hide that one-time startup cost from later operators or benchmark runs.

If you are using the HTTP source-run profile, swap `https://localhost:7090` for `http://localhost:5089`.

## 8. Profiling Guidance

The warm-index script reports two useful timing views:

- `ElapsedMilliseconds`: total wall-clock time for the rebuild-triggering MCP search call
- `BuildDurationMilliseconds`: the build progress timestamps reported by `CodeSearchService`

The script summary also now includes `TimingBreakdown`, which mirrors `memorysmith_code_search_status.build.timings`:

- `providerInitializationMilliseconds`
- `fileReadMilliseconds`
- `contentHashMilliseconds`
- `chunkingMilliseconds`
- `embeddingMilliseconds`
- `databaseWriteMilliseconds`
- `removedDocumentCleanupMilliseconds`
- `embeddingCallCount`
- `embeddedChunkCount`
- `averageEmbeddingMilliseconds`

Use `-ForceRebuild` when you want cold-build numbers. Omit it when you want warm incremental behavior.

When comparing CUDA against CPU fallback, treat the very first CUDA no-rebuild pass as a combined warm-up plus measurement. It includes one-time ONNX provider initialization that does not recur until the process restarts.

Current bottleneck guidance:

- `databaseWriteMilliseconds` is usually not the dominant cost on this repo because code-search already batches SQLite document rewrites.
- The main cold-build bottleneck is the synchronous per-chunk embedding loop in `CodeSearchService`, which makes thousands of small embedding calls instead of handing the GPU larger batches.
- A healthy CUDA provider can therefore be slower than CPU fallback for the current repo-sized workload even when `/health` and `memorysmith_code_search_status` both report active `Cuda (0)`.

For reproducible comparisons:

1. Run once with `-ForceRebuild`.
2. Run again without `-ForceRebuild`.
3. Compare provider mode/status plus elapsed/build duration.

## 9. Expected Data Outputs

After a successful rebuild you should see:

- `Data/Graph/code-search/code-search.db`
- `Data/Graph/embeddings/*.json` for memory-record semantic caches

The code-search SQLite database holds indexed code chunks and their embedding payloads. The memory embedding cache remains file-backed JSON because it is keyed by memory id and invalidation hash rather than code chunk rows.

## 10. Troubleshooting

If CUDA or OpenVINO does not activate:

1. Confirm you published the matching `MemorySmithOnnxRuntimeFlavor`.
2. Confirm `ExecutionProvider` matches the intended hardware provider.
3. Run `where.exe cudnn64_9.dll` and confirm Windows can resolve the cuDNN runtime.
4. If cuDNN is installed under `C:\Program Files\NVIDIA\CUDNN\...`, add the matching `bin\...\x64` folder to the machine `PATH` and restart the app/service.
5. Keep `CpuFallbackEnabled=true` so the app stays available while you diagnose the hardware path.
6. Check `/health` or `memorysmith_code_search_status` for the provider-specific initialization error.
7. Re-run `./Scripts/Warm-CodeSearchIndex.ps1` and inspect `ProviderMode`, `ProviderStatus`, and `TimingBreakdown` in the summary.

If the repo code index does not appear:

1. Confirm the app can reach the repository root from `MemorySmith:CodeSearch:RepositoryRootPath`.
2. Confirm `MemorySmith:CodeSearch:Enabled=true`.
3. Run the warm-index script with `-ForceRebuild`.
4. Check the returned `IndexPath` and `Build.LastError` values.