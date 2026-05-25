# Agent and Model Configuration

This guide explains how MemorySmith routes chat models, maintenance-agent models, and approval-gated Agent behavior.

Use it when an operator or an assisting agent needs to answer questions such as:

- Which model does `/chat` use by default?
- Why is the maintenance agent still using the legacy provider/model path?
- Why can Chat answer questions but not write pages?
- Which settings belong in `/admin` Models versus the Configuration tab?

## Mental Model

MemorySmith has two related but distinct model configuration paths:

| Path | Purpose | Managed from |
| --- | --- | --- |
| Generic chat settings | Provider transport defaults, limits, prompt path, attachment bounds, tool loop bounds | `/admin` Configuration tab or app settings file |
| Model profiles | Human-friendly named profiles with default/assignment/role semantics | `/admin` Models tab |

If explicit model profiles exist, they are the preferred routing layer. If they do not, MemorySmith exposes an implicit legacy default derived from `Chat:Provider`, `Chat:OllamaModel`, `Chat:GitHubModel`, and `Chat:OllamaContextWindowTokens`.

## What The Models Tab Owns

The Models tab manages:

- `MemorySmith:Chat:ModelProfiles`
- `MemorySmith:Chat:DefaultModelProfileId`
- `MemorySmith:MaintenanceAgent:ModelProfileId`
- `MemorySmith:MaintenanceAgent:ProposalReviewModelProfileId`
- `MemorySmith:MaintenanceAgent:AdminChatModelProfileId`

Each profile can store:

- stable `Id`
- display `Name`
- `Provider`
- provider model id
- optional context-window metadata
- enabled state
- role allowlist
- description
- assignment flags for default chat, maintenance runs, proposal review, and admin maintenance chat

## What The Configuration Tab Owns

The Configuration tab owns the surrounding behavior:

- `MemorySmith:Chat:*` transport defaults, prompt path, timeout, context limits, tool limits, attachment limits, and `AgentWritesEnabled`
- `MemorySmith:MaintenanceAgent:*` read/write roots, `DirectWrite`, legacy provider/model settings, task toggles, schedule, resource probe, and storage paths

Rule of thumb: if the value is a bounded scalar or a one-per-line list, it probably belongs in Configuration. If the value is a structured named model profile or assignment, it belongs in Models.

## Default Routing

### Chat surface

1. The app lists enabled profiles allowed for the current user roles.
2. Admin users can use every enabled profile.
3. Other users are filtered by `AllowedRoles` unless the profile is unrestricted.
4. The default chat profile comes from `DefaultModelProfileId`.
5. If no explicit profile configuration exists, the app exposes an implicit legacy default profile.
6. If there is no enabled default profile, chat send is disabled.

### Maintenance runs

Maintenance-agent model selection resolves in this order:

1. `MaintenanceAgent:ModelProfileId`
2. legacy `MaintenanceAgent:Provider` + `MaintenanceAgent:Model`

### Proposal review

Proposal review resolves in this order:

1. `MaintenanceAgent:ProposalReviewModelProfileId`
2. `MaintenanceAgent:ModelProfileId`
3. legacy maintenance-agent provider/model

### Admin maintenance chat

Admin maintenance chat resolves in this order:

1. `MaintenanceAgent:AdminChatModelProfileId`
2. `MaintenanceAgent:ModelProfileId`
3. legacy maintenance-agent provider/model

## Write And Approval Boundaries

| Setting | Effect | Important boundary |
| --- | --- | --- |
| `MemorySmith:Chat:AgentWritesEnabled` | Allows Agent mode to submit write proposals | Does not bypass approval or RBAC. |
| `MemorySmith:MaintenanceAgent:DirectWrite` | Allows maintenance agent direct writes in configured write roots | Should remain false for normal proposal-first governance. |
| `MemorySmith:MaintenanceAgent:Write` | Limits where maintenance-agent writes may land | Affects proposal/direct-write validation. |
| User role | Controls who can approve or apply writes | Chat mode alone remains read-only. |

Agent write rule: enabling `AgentWritesEnabled` allows structured write proposals to be created, but durable file changes still require the existing approval flow and sufficient role.

## Provider Defaults And Discovery

| Provider path | Purpose |
| --- | --- |
| `Chat:OllamaEndpoint` | Base URL for Ollama model discovery and chat calls |
| `Chat:OllamaModel` | Legacy default Ollama model when profiles are absent |
| `Chat:GitHubModel` | Legacy default GitHub model when profiles are absent |
| `Chat:GitHubTokenEnvironmentVariable` | Environment variable name for GitHub provider token lookup |
| `Chat:GitHubCliPath` / `GitHubCliUrl` | Optional fallback auth guidance and CLI discovery |

The Models tab resolves provider-specific model discovery through the selected provider and preserves a configured model id if discovery is temporarily unavailable.

## Maintenance-Agent Task Controls

These settings are often mistaken for model settings, but they govern execution behavior instead:

- `MaintenanceAgent:Tasks:*`
- `MaintenanceAgent:Schedule:*`
- `MaintenanceAgent:ResourceProbe:*`
- `MaintenanceAgent:Storage:*`
- `MaintenanceAgent:MaxFindingsPerTask`
- `MaintenanceAgent:AgentVersion`

Transcript controls are under `MaintenanceAgent:Storage:*` and include:

- transcript log path
- transcript retention entries
- transcript redaction enabled

## Troubleshooting Patterns

### Chat cannot send

Check in order:

1. Is there an enabled default profile?
2. If profiles exist, is the current user allowed by role?
3. If profiles do not exist, are the legacy provider/model defaults valid?
4. Does the provider list models successfully?

### Maintenance uses the wrong model

Check whether one of the assignment IDs is blank, then see whether the service is falling back to the legacy maintenance provider/model path.

### Agent can search but not write

That is expected unless all of these are true:

1. Agent mode is active.
2. `Chat:AgentWritesEnabled` is true.
3. The current user has sufficient role.
4. The proposed write still goes through approval.

### Proposal review uses a different model from chat

That is also expected if `ProposalReviewModelProfileId` is assigned to a profile different from the default chat profile.

## Verification Surfaces

| Surface | What to check |
| --- | --- |
| `/admin` Models | Profile list, defaults, assignments, enabled state |
| `/chat` | Available profiles, default selection, provider errors, capability messaging |
| `/maintenance` | Maintenance run behavior and task configuration |
| `/proposals` | Review flow and maintenance-agent output |
| `/api/chat/config` | Runtime provider/model/config response |
| override settings file | Persisted `ModelProfiles` and assignment IDs |

## Agent Assistance Notes

- Ask first whether the operator means generic chat transport settings or model profile routing. Those are different surfaces.
- Do not suggest editing `ModelProfiles` directly in JSON when `/admin` Models is available.
- When an operator reports a mismatch, compare the relevant assignment ID and the fallback legacy provider/model settings before assuming the wrong page is stale.
- Treat transcript retention/redaction as operational governance settings, not chat UX settings.

## Related Pages

- [Configuration Reference](configuration-reference.md)
- [Search and Chat](search-and-chat.md)
- [Operations](../ops/operations.md)