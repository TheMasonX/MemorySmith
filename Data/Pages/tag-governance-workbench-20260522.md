# Tag Governance Workbench

Status: Working guidance for Phase 2 governance UI.

## Authoring Tags

- Prefer lowercase kebab-case for plain tags, for example `semantic-search`.
- Prefer namespaced tags when the value has structure or lifecycle meaning, for example `kind:decision`, `review-after:2026-06`, or `superseded-by:memory-id`.
- Use the memory editor tag chips to add, remove, and autocomplete tags. Suggestions are completions only; they do not rewrite existing records.
- Keep broad labels out of routine authoring. Tags such as `general`, `misc`, `notes`, or `todo` are low-signal and should become blocklist or alias candidates.
- Treat alias suggestions as approval-required cleanup work. Add the alias to policy or edit affected records intentionally; the system does not apply tag rewrites automatically.

## Interpreting Warnings

- `Info` diagnostics are observed policy feedback. They can be saved unless another validation rule blocks the record.
- `Warning` diagnostics identify policy drift, missing relationships, source-link problems, or stale lifecycle tags. They should be reviewed before relying on the memory in retrieval or chat context.
- `Error` diagnostics are blocking. In `blockUnknown` plain-tag mode, unknown or blocklisted plain tags must be changed or added to policy before the record can be saved.
- The Tag Manager Policy Feedback panel reports invalid editable policy lines, duplicate namespace definitions, and suggestions derived from observed tag usage.

## Policy Modes

- `allowWithSuggestions`: unknown plain tags are allowed and surfaced as informational suggestions.
- `observe`: unknown plain tags are recorded as informational diagnostics.
- `warn`: unknown plain tags remain saveable but show warnings.
- `blockUnknown`: unknown or blocklisted plain tags emit blocking errors during memory create/update.

## Approval Model

The suggestions table is intentionally read-only. Casing variants, near duplicates, low-value tags, broad tags, namespace mistakes, and allowlist candidates are evidence for a human policy edit or memory cleanup pass. The application should not automatically merge, delete, or rename tags without explicit approval.