# User-Defined Requirements

Last updated: 2026-05-31
Source: Direct user instructions in active development sessions.

## Purpose
This page captures explicit user-defined requirements that must be treated as durable constraints until the user changes them.

## Active Requirements

### 1) Auth Work Constraint During Active Test Runs
- Do not force app restarts or admin-run relaunches if an existing app instance is needed for other active tests.
- Defer auth redirect runtime restart steps when they would disrupt ongoing test work.

### 2) Sidebar Visibility And Responsive Behavior
- Make sidebar regions visibly wider where they are too compressed.
- On mobile or narrow layouts, prefer stacked panel layouts over overly narrow side-by-side panes.

### 3) Draggable Splitter Direction
- The site should move toward GridSplitter-style draggable dividers across more multi-pane surfaces.
- This is a product direction requirement, not a one-off page tweak.

### 4) Code Search Status Panel Behavior
- The status section should be collapsible.
- Default state should be collapsed.
- The section should automatically open when build state transitions occur.

### 5) Parser Strategy Preference For Code Search
- Tree-sitter only is acceptable if it adequately covers .NET scenarios.
- Using both Roslyn and tree-sitter is acceptable when Roslyn provides worthwhile .NET performance and quality improvements.
- Practical preference: prioritize correctness and retrieval quality for .NET code paths.

## Interpretation Rules
- Treat these as user-defined requirements, not optional suggestions.
- If a requirement conflicts with an implementation plan, raise the conflict explicitly and preserve the requirement by default.
- If a requirement changes in a future session, update this page with the new directive and date.
