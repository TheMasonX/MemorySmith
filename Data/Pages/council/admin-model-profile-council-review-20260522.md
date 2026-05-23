# Council Review: Admin Model Profiles

## Decision

Implement admin-defined chat model profiles as a small configuration-backed registry first, require one enabled default profile before chat can send, and keep deeper per-user/role assignment and maintenance-agent migration behind follow-up gates.

## Evidence Reviewed

- `MemorySmith.App/Services/MemorySmithOptions.cs`: current `ChatOptions` stores provider defaults as scalar provider/model settings and `GitHubModels` as configured fallback model metadata.
- `MemorySmith.App/Controllers/ChatController.cs`: `/api/chat/config` currently resolves provider/model from `MemorySmith:Chat:Provider`, `OllamaModel`, and `GitHubModel`.
- `MemorySmith.App/Components/Pages/Chat.razor`: the toolbar currently exposes manual Provider and Model controls and persists those browser-local preferences.
- `MemorySmith.App/Services/ChatServices.cs`: `IChatProvider` implementations already isolate model API calls, streaming, and model discovery.
- `MemorySmith.App/Services/AdminSettingsService.cs`: the admin settings editor is allowlisted and scalar/list oriented; complex profile collections need a dedicated admin service/surface rather than being forced through generic row editing.
- Current wiki memories: `project-wiki-chat-configuration-current`, `project-wiki-chat-agent-provider`, `project-wiki-chat-local-storage-persistence`, `project-wiki-admin-configuration-surface`, and `project-wiki-configuration-settings-current`.

## Findings

| Seat | Recommendation | Confidence | Blocking concern |
|---|---|---:|---|
| Source-Grounded Archivist | Keep current-state memories untouched until profile behavior exists; record this decision as a page because it is future-facing. | 92% | Updating memories before implementation would violate the current wiki governance rule. |
| Data Model Architect | Add a `ChatModelProfile` shape under `MemorySmith:Chat` with stable ids, display names, provider, model id, optional context window, enabled flag, and default flag. | 84% | Role-based profile access needs a separate permission model; do not fake it with display-only fields. |
| Retrieval Specialist | Avoid changing provider transport or context retrieval in this slice; profile selection should only resolve provider/model/context metadata. | 87% | Context-window fields must not accidentally alter retrieval budgets before measured behavior exists. |
| Human Learning Advocate | Replace manual provider/model editing in chat with a profile picker using friendly names such as `Athena - Ollama / gemma4:e4b`. | 86% | If no default profile exists, the disabled state must tell users what admin setup is missing. |
| Skeptical Reviewer | Implement the registry in the smallest reversible layer and reject sends without an enabled default; defer maintenance-agent profile assignment and per-role access until tests exist. | 78% | This still changes chat startup behavior and could surprise existing local setups unless legacy scalar settings seed an implicit default. |

## Synthesis

Change now:

- Add a profile registry service backed by the same local settings override file used by admin configuration.
- Seed an implicit default profile from existing `Chat:Provider`, `OllamaModel`, `GitHubModel`, and `OllamaContextWindowTokens` when no explicit profiles exist, so current installs keep working.
- Add an Admin Models surface where admins can add/edit/delete profiles, enable/disable them, and mark one profile as the chat default.
- Change chat UI selection to use enabled profiles and disable sending when no enabled default profile is configured.
- Keep the provider transport contracts intact by resolving a selected profile into the existing provider/model request fields.

Defer:

- Per-role model visibility and assignment rules beyond Admin-only management.
- Maintenance-agent model-profile assignment.
- Proposal-agent review models.
- Chat compaction and proposal-gated chat writes.

## Dissent

The main dissent is whether to store model profiles in SQLite because role-based access is eventually needed. The near-term recommendation remains configuration-backed storage because the existing admin settings workflow already edits local operational configuration, model profiles are deployment settings rather than user-generated content, and no per-role model assignment contract exists yet. Promote to SQLite only when the role-assignment requirements are implemented.

## Acceptance Criteria

- `/admin` exposes a Models section or page protected by Admin policy.
- Admins can create/update/delete enabled model profiles with name, provider, model id, and optional context window.
- Exactly one enabled default chat profile can be selected; chat send is disabled with a setup message when no enabled default exists.
- `/chat` offers profile selection instead of manual provider/model editing for normal users.
- Existing scalar chat provider/model settings continue to seed a usable implicit profile when explicit profiles are absent.
- Focused tests cover profile registry behavior, admin/chat markup, and chat config/default resolution.

## Open Questions

- Should future role-based model access be per role, per user, or both?
- Should maintenance-agent profile assignment reuse the chat default when unset, or require its own explicit default?
- Should profiles include budget/cost metadata for provider quotas before the first role-based release?