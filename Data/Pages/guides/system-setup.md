# System Setup Guide

This guide walks a new user through setting up MemorySmith on a local machine. It focuses on a first successful run with clear checkpoints in the UI.

## 1. Prerequisites

Install these dependencies before running the app:

- .NET 9 SDK
- Git
- PowerShell 7+

Optional but useful:

- Python 3.x for static wiki site generation (`docs/build_pages_site.py`)
- Local Ollama runtime if you want chat provider testing beyond the default setup

## 2. Clone And Open The Repository

```powershell
git clone https://github.com/TheMasonX/MemorySmith.git
cd MemorySmith
```

Open the folder in VS Code and confirm the expected top-level folders exist (`MemorySmith.App`, `MemorySmith.Core`, `MemorySmith.Storage`, `MemorySmith.Tests`, `Data`).

> [!NOTE]
> Screenshot placeholder [SYS-SETUP-01]: VS Code workspace root showing the main project folders.

## 3. Validate The Toolchain

From the repository root, run:

```powershell
dotnet --info
dotnet restore MemorySmith.slnx
dotnet build MemorySmith.slnx --configuration Release
```

If build succeeds, your base environment is ready.

> [!NOTE]
> Screenshot placeholder [SYS-SETUP-02]: terminal output showing successful restore and build.

## 4. Run MemorySmith Locally

Start the app:

```powershell
dotnet run --project MemorySmith.App --launch-profile http
```

Default URL:

- `http://localhost:5089`

Open the app in a browser and verify the home routes load.

> [!NOTE]
> Screenshot placeholder [SYS-SETUP-03]: browser showing the running app at `http://localhost:5089`.

## 5. First-Time Admin Setup

Navigate to:

- `/admin/setup`

Create the first Admin account. After creation, sign in at:

- `/login`

Then open:

- `/admin`

Confirm the admin surface loads and the current user role is Admin.

> [!NOTE]
> Screenshot placeholder [SYS-SETUP-04]: `/admin/setup` first-admin form.
> [!NOTE]
> Screenshot placeholder [SYS-SETUP-05]: `/admin` page after successful sign-in.

## 6. Verify Core Product Surfaces

Use this quick smoke checklist:

1. `/memories`: list renders and search returns results.
2. `/pages`: markdown pages load and preview works.
3. `/chat`: provider/model selector appears and prompt can be submitted.
4. `/tasks`: board/list loads without errors.
5. `/tags`: governance page loads and current policy can be viewed.
6. `/health`: readiness and path cards display expected values.

> [!NOTE]
> Screenshot placeholder [SYS-SETUP-06]: `/memories` with a successful search.
> [!NOTE]
> Screenshot placeholder [SYS-SETUP-07]: `/pages` editor + preview panel.
> [!NOTE]
> Screenshot placeholder [SYS-SETUP-08]: `/chat` with a completed response.
> [!NOTE]
> Screenshot placeholder [SYS-SETUP-09]: `/health` status cards.

## 7. Run Validation Tests

Run the standard validation command:

```powershell
dotnet test MemorySmith.slnx --configuration Release
```

For focused work, run only the relevant test project or test filter.

> [!NOTE]
> Screenshot placeholder [SYS-SETUP-10]: passing NUnit test summary.

## 8. Data Folder Expectations

Verify the deployment data structure exists:

```text
Data/
  Events/
  Graph/
  Memories/
  Models/
  Pages/
  vars.json
```

If behavior is unexpected, check configured paths first:

1. `MemorySmith:DataPath`
2. `MemorySmith:PagesPath`
3. `MemorySmith:EventLogPath`
4. `MemorySmith:VarsPath`
5. `MemorySmith:SemanticSearch:*`

> [!NOTE]
> Screenshot placeholder [SYS-SETUP-11]: `Data/` directory tree in Explorer or terminal.

## 9. Optional: Enable Semantic Embeddings

Place model assets under `Data/Models`:

- `e5-base-v2.onnx`
- `vocab.txt`

Use relative configuration values:

```json
{
  "EmbeddingsEnabled": true,
  "ModelPath": "Models/e5-base-v2.onnx",
  "VocabularyPath": "Models/vocab.txt",
  "TokenizerKind": "WordPiece",
  "PoolingMode": "Mean"
}
```

`TokenizerKind` and `PoolingMode` are part of the semantic-provider contract and also affect persisted embedding reuse under `Data/Graph/embeddings`. The current built-in provider supports `WordPiece` tokenization and `Mean` or `Cls` pooling; if you point the app at a model family that needs a different tokenizer, `/health` should report that mismatch clearly instead of silently serving stale vectors.

Restart the app and confirm semantic provider status in `/health`.

If you want hardware acceleration, CPU fallback behavior, Windows service deployment, or repo code-index warming/profiling, continue with [Semantic Acceleration Setup Guide](semantic-acceleration-setup.md).

On Windows CUDA hosts, also verify `where.exe cudnn64_9.dll` before expecting GPU activation. The cuDNN local installer can leave the DLLs in `C:\Program Files\NVIDIA\CUDNN\...` without adding that folder to `PATH`, which makes MemorySmith fall back to CPU even though cuDNN is installed.

> [!NOTE]
> Screenshot placeholder [SYS-SETUP-12]: `/health` semantic provider active state.

## 10. Troubleshooting Quick Path

If setup fails, check in this order:

1. SDK/tooling (`dotnet --info`, restore/build output)
2. App startup logs from `dotnet run`
3. `/health` path and readiness diagnostics
4. Effective appsettings values for `MemorySmith` paths and auth settings
5. File permissions for `Data/Events`, `Data/Memories`, and `Data/Pages`

Most setup issues come from path mismatches to an unintended `Data` folder.

For a full setting-by-setting map after first run succeeds, use [Configuration Reference](configuration-reference.md).

> [!NOTE]
> Screenshot placeholder [SYS-SETUP-13]: example `/health` view highlighting a misconfigured path.

## HTTPS Option

Use the dedicated [HTTPS Setup Guide](https-setup.md) if you want local development with trusted TLS and an explicit HTTPS launch profile.

## Screenshot Backlog Template

Use this checklist while collecting images later:

- [ ] Workspace root in VS Code
- [ ] Successful restore/build terminal output
- [ ] Running app on localhost
- [ ] First admin setup page
- [ ] Admin dashboard after sign-in
- [ ] Memories search success
- [ ] Pages editor and preview
- [ ] Chat response example
- [ ] Health dashboard status cards
- [ ] Passing test summary
- [ ] Data folder tree
- [ ] Semantic provider enabled status
- [ ] Health path-mismatch troubleshooting example
- [ ] SYS-SETUP-01 workspace root in VS Code
- [ ] SYS-SETUP-02 successful restore/build terminal output
- [ ] SYS-SETUP-03 running app on localhost
- [ ] SYS-SETUP-04 first admin setup page
- [ ] SYS-SETUP-05 admin dashboard after sign-in
- [ ] SYS-SETUP-06 memories search success
- [ ] SYS-SETUP-07 pages editor and preview
- [ ] SYS-SETUP-08 chat response example
- [ ] SYS-SETUP-09 health dashboard status cards
- [ ] SYS-SETUP-10 passing test summary
- [ ] SYS-SETUP-11 data folder tree
- [ ] SYS-SETUP-12 semantic provider enabled status
- [ ] SYS-SETUP-13 health path-mismatch troubleshooting example

