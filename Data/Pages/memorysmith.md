# MemorySmith Pages

MemorySmith pages are markdown files stored under `Data/Pages`. They are separate from structured memories, but both can be used as local context for chat and agent workflows.

Pages can link to page assets with markdown image paths such as `assets/example.png`; the app serves those files under `/page-assets`.

## Current Surfaces

- `/pages` edits and previews markdown-backed pages.
- `/api/pages` exposes page list, search, save, delete, and rendered HTML.
- `/chat` uses configured chat provider interfaces for chat and agent mode.