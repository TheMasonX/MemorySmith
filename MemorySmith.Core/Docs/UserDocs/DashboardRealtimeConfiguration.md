# Single-Host Dashboard and Service Configuration

MemorySmith now runs as `MemorySmith.App`, a single host for the Blazor dashboard, REST API, MCP endpoint, file storage, and background maintenance. The older Worker and Dashboard projects remain on disk as migration history only.

## Dashboard Surface

| Route | Purpose |
|---|---|
| `/memories` | Primary memory dashboard/workbench for browsing, search, editing, source links, and agent context copying. |
| `/pages` | Markdown-backed page editor, search, and HTML preview for `Data/Pages`. |
| `/chat` | Memory-enhanced chat and agent mode through the configured chat provider. |
| `/api/search` | Combined memory and page search for broader local discovery. |
| `/health` | Operator dashboard with counts, readiness, activity charts, maintenance telemetry, runtime configuration, storage paths, endpoints, and storage diagnostics. |
| `/variables` | `%VarName%` source-link path variable management. |

## Service Install CLI

Publish `MemorySmith.App`, then run install and uninstall from an elevated PowerShell session:

```powershell
.\MemorySmith.App.exe install --memory-directory C:\MemorySmith\Memories --port 5089
.\MemorySmith.App.exe uninstall --service-name MemorySmith
.\MemorySmith.App.exe --help
```

Important install flags:

| Flag | Purpose |
|---|---|
| `install`, `--install-service` | Create the Windows Service. |
| `uninstall`, `--uninstall-service` | Stop and delete the Windows Service. |
| `--memory-directory` | Sets `MemorySmith:DataPath`; `Pages`, `Events/audit.log`, and `vars.json` are derived from its parent folder. |
| `--port` | Sets the local HTTP port, defaulting to `5089` for service installs. |
| `--service-name` | Windows Service name, default `MemorySmith`. |
| `--service-start-type` | `auto`, `demand`, or `disabled`. |

Arguments after `--` are passed through to ASP.NET Core for advanced hosting configuration. Use either `--port` or a runtime `--urls` value.

## Current Configuration Keys

All runtime settings are under the `MemorySmith` configuration section:

| Key | Purpose |
|---|---|
| `DataPath` | Memory JSON directory containing `Unconsolidated`, `Working`, `Core`, and `Deprecated`. |
| `PagesPath` | Markdown page directory; `assets` under this directory is served at `/page-assets`. |
| `EventLogPath` | JSONL audit log used for activity charts. |
| `VarsPath` | Source-link variable store used by `%VarName%` expansion. |
| `ApiKey` | Optional API/MCP key required through `X-Api-Key` when configured. |
| `AllowRemoteApi` | Allows non-loopback API/MCP callers when set to `true`. |
| `Maintenance:*` | Background maintenance intervals and startup grace. |
| `SourceLinks:*` | Source bundle max bytes and trusted local source roots. |
| `Chat:*` | Chat provider, Ollama endpoint/model, context limits, and agent write setting. |

The effective redacted configuration is visible at `/health` and `GET /api/diagnostics`.