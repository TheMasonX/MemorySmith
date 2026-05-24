# Workbench Layout Hardening

This note captures a UI hardening pass focused on cramped viewport behavior in core workbenches.

## Scope

- `/memories`: added collapsible results sidebar behavior and resilient two-pane collapse behavior.
- `/proposals`: added collapsible proposal-list sidebar behavior and resilient detail-first mode.
- `/tags`: improved responsive fit for policy and insights panes to prevent overflow at narrower widths.
- `/tags` suggestions tab: added usage count visibility for each suggestion row.

## Why This Change Exists

At narrower workspace widths, fixed pane assumptions caused cramped layouts and reduced navigability.
This pass prioritizes readability, explicit show/hide controls for sidebars, and better table/editor fit.

## Observable Outcomes

- Users can collapse and re-open list sidebars in Memories and Proposals using explicit controls.
- Tags policy editor and insights area now reflow more predictably on smaller widths.
- Tag suggestion triage now includes usage count context to support better allow/block decisions.

## Follow-up Guidance

- Keep new sidebar toggles on non-trivial panes where list/detail density is high.
- Prefer flexible `minmax(0, 1fr)` detail columns over fixed wide minimums for admin workbenches.
- Include frequency/usage context in governance suggestion surfaces when available.