# MemorySmith

MemorySmith is a single-host ASP.NET Core app for local structured memory management. The app hosts the Blazor UI, REST API, file-backed storage, and background maintenance in one process.

## Run

```powershell
dotnet run --project MemorySmith.App --launch-profile http
```

Then open the URL printed by `dotnet run` and use:

- `/` for the dashboard summary.
- `/memories` for browse/search/create/edit/delete workflows.
- `/health` for readiness, stats, maintenance telemetry, and recent events.
- `/api/memories`, `/api/stats`, and `/api/health/*` for automation.
- `/mcp` for the local MemorySmithWiki MCP JSON-RPC endpoint.

## Windows Service

Publish the app, then run the published executable from an elevated PowerShell session to install or uninstall the Windows Service:

```powershell
.\MemorySmith.App.exe --install-service --service-name MemorySmith --service-display-name "MemorySmith" -- --urls http://localhost:5089
.\MemorySmith.App.exe --uninstall-service --service-name MemorySmith
```

Optional install settings are `--service-description` and `--service-start-type` (`auto`, `demand`, or `disabled`). Arguments after `--` are preserved as runtime arguments for the service process.

## Project Memory Wiki

The repository `Data/Memories` folder is the live MemorySmith wiki for this project itself. `MemorySmith.App` defaults `MemorySmith:DataPath` to `../Data/Memories`, so local runs browse and search those project memories directly.

Tests that need realistic project data copy `Data/Memories` into a temp directory first, then exercise the copied fixture through the same file store and API paths. This keeps the source wiki stable while making it the application's testbase.

The wiki currently includes records for architecture, storage rules, validation commands, MCP integration, semantic-search gaps, and generalization friction. Use the app search tools to keep future research grounded in these records.

## Search And MCP

MemorySmith exposes two search paths, and the `/memories` UI can switch between keyword and semantic modes:

- `POST /api/memories/search` for deterministic keyword search over title, content, and tags.
- `POST /api/memories/search/semantic` for a local semantic baseline that ranks token, tag, title, reference, and alias overlap with match explanations.

The semantic path is intentionally not an embedding/vector index yet. The project wiki records track that as a future generalization gap.

VS Code MCP integration is configured in `.vscode/mcp.json` and points at:

```text
http://localhost:5089/mcp
```

The MCP endpoint exposes `memorysmith_search`, `memorysmith_semantic_search`, and `memorysmith_get` tools over the same project wiki data.

## Validate

```powershell
dotnet build MemorySmith.slnx -v minimal
dotnet test MemorySmith.slnx -v minimal
```

The active solution builds `MemorySmith.App` as the only deployable host. Older Worker and Dashboard projects are retained as historical migration material but are no longer part of the active solution.