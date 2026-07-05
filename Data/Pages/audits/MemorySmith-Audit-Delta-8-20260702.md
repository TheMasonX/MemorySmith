# MemorySmith Code Audit — Delta Report #8 (2026-07-02, continued)

**Scope of this document:** deltas only, on top of reports #1–#7. This pass covered `Chat.razor`'s non-rendering logic — the core send/stream loop, attachment handling, write-approval flow, and session lifecycle (create/select/delete) — roughly 1,000 of its ~2,760 code-behind lines, selected by following the highest-risk paths (file I/O, cancellation, concurrent-state mutation) rather than reading top-to-bottom. One new finding; several things checked and ruled out, including one I initially suspected was a bug and confirmed isn't.

---

## Headline delta

| # | Finding | Type | Confidence |
|---|---|---|---|
| 1 | **Deleting the chat session that's currently streaming a response silently discards that response.** `DeleteSessionAsync` has no check for whether the session being deleted is the one an in-flight `SendAsync` call is actively writing into. The delete removes the session object from `_sessions` immediately; the streaming loop's local `session` variable keeps a reference and keeps mutating it, but since it's no longer in `_sessions`, the next `SaveSessionsAsync()` call (including the one `SendAsync` itself runs in its `finally` block once the response finishes) never persists it. The completed response — potentially a long agent run with real memory/page writes already applied server-side — just vanishes from the UI's perspective. | 🟡 New | **85%** |

---

## 1. Deleting the actively-streaming session orphans and discards its response

**Evidence — `DeleteSessionAsync`:**
```csharp
private async Task DeleteSessionAsync(string id)
{
    if (_sessions.FirstOrDefault(session => session.Id == id) is not { } session) { return; }
    ...
    var wasActive = string.Equals(_activeSessionId, id, StringComparison.Ordinal);
    var pendingAttachments = session.PendingAttachments.ToList();
    _sessions.Remove(session);          // ← removed from the list SaveSessionsAsync serializes
    if (_sessions.Count == 0) { StartNewChat(); }
    else if (wasActive) { /* switch _activeSessionId to another session */ }
    ...
    await SaveSessionsAsync();
}
```
No check anywhere in this method for `_isSending`, `_sendCts`, or whether `id` matches whatever session an in-flight `SendAsync` call is currently targeting.

**Contrast with the two other session-lifecycle actions, which are fine:** `SelectSessionAsync` (switch to a different session) and `StartNewChat`/`StartNewChatAsync` also lack an `_isSending` guard, but that's harmless in those cases — the streaming `SendAsync` call's `session` reference is still present in `_sessions` (just no longer the *active* one), so it keeps completing "in the background" and gets correctly persisted on the next save; switching back to that session later would show the finished response. `DeleteSessionAsync` is qualitatively different because it removes the session object from the list entirely, not just changes which one is active — there's no list membership left for the eventual `SaveSessionsAsync()` to serialize.

**Traced consequence, not assumed:** `SendAsync`'s own `finally` block unconditionally calls `await SaveSessionsAsync();` after the stream completes (success, cancellation, or error) — but by then, if the session was deleted mid-stream, `_sessions` (the list `SaveSessionsAsync` actually writes) no longer contains it. The completed `pendingTurn` — including, in Agent mode, any memory/page writes that were already applied server-side via `ApplyAgentWritesAsync` before the turn finished rendering — has nowhere to land. The user sees whatever session they switched to (per the `wasActive` branch's fallback), with no indication that a response they were waiting on ever completed.

**Why this is plausible in practice, not just a theoretical race:** a user impatient with a slow-generating response (this app explicitly supports long agent runs with multi-step tool loops, per the "Stop after current step" control found in Report earlier passes) clicking "delete this chat" instead of "stop generating" is a completely ordinary UI mistake, not an adversarial edge case requiring precise timing.

**Recommendation:** In `DeleteSessionAsync`, check whether `id` matches a session with an in-flight generation (the simplest signal available in this component is `_isSending && ReferenceEquals(session, ActiveSession)` combined with tracking which session `_sendCts`/`_activeRunControl` belongs to — currently these are single fields, not keyed by session, which would need a small refactor to support "is *this specific* session generating" rather than only "is *the* active session generating"). At minimum, block deletion of the session an in-flight `_sendCts` belongs to with a message like "Stop generation before deleting this chat," mirroring the existing pattern where `SendAsync` itself blocks starting a new send while `_isSending` is true.

**Confidence: 85%** — the code-level mechanism (list removal before the streaming task's own save) is directly verified; the discount reflects that I haven't traced whether `MemoryChatAgent`/session-storage on the server side has some independent persistence path for agent-applied writes (the memory/page writes themselves, as opposed to the chat transcript) that would survive this regardless of the client-side session object's fate — Report #1 and #5 confirmed memory/page writes go through `MemoryApplicationService.CreateAsync`/`UpdateAsync` directly, which doesn't depend on the chat session object at all, so **the underlying memory/page content is NOT lost** — only the chat transcript's record of the turn (its content, trace, and the UI's knowledge that the write happened) is what gets silently dropped. Worth being precise about that distinction: this is a UI/transcript data-loss bug, not a data-integrity bug in the wiki content itself.

---

## 2. Checked and ruled out (including one initial false lead)

- **Individual write-approval buttons racing against "Approve All":** initially suspected that clicking an individual "Approve" button on a proposal while "Approve All" is mid-loop (guarded only by `_isApprovingWrites`, which the individual-approval methods don't check) could cause a double-apply. On reflection, this isn't exploitable: Blazor Server serializes UI event handling per circuit via its synchronization context — a second button click's handler won't begin executing until the first one's `await` chain fully completes — so by the time an individual click could run, "Approve All" would have already removed that proposal from the turn's pending list, making the individual click a no-op via its own `FirstOrDefault` lookup returning null. Flagging that I considered and rejected this, per the audit's evidence-over-hunches standard.
- **Attachment temp-file naming (`ChatAttachmentFiles.SaveTempAsync`)** — initially looked like a path-traversal risk since it's seeded with the browser-supplied `file.Name`. Confirmed only the file *extension* is extracted from the original name (with a length/fallback check), and the actual path is built from a fresh timestamp+GUID — the original name never reaches the filesystem path. No issue.
- **Component disposal on navigation (`DisposeAsync`)** — confirmed `_sendCts?.Cancel()` is called on dispose, so navigating away from the Chat page mid-generation does correctly cancel the in-flight request rather than leaking it. (`ConfirmInternalNavigation`'s confirmation dialog only fires for an unsent *draft*, not for an in-progress *generation* — a minor, low-stakes UX gap since the cleanup itself is safe and the streaming loop's periodic 2-second `SaveSessionsAsync()` calls mean at most a couple of seconds of partial content would be lost, not flagging this as a separate headline finding given how minor it is relative to Finding 1 above.)

---

## 3. Coverage note

This pass covered roughly 1,000 of `Chat.razor`'s ~2,760 code-behind lines, prioritized by risk (file I/O, cancellation, concurrent state) rather than sequential reading. Not yet covered in this file: the interactive question-card flow (`SendQuestionOptionAsync`/`SendQuestionOtherAsync`), the trace-drawer/reasoning-display logic, preference persistence (`SavePreferencesAsync`/`LoadPreferencesAsync`), and the model-profile-selection UI logic (`OnModelProfileChanged`/`ApplySelectedModelProfile`) — any would be a reasonable continuation within this same file. Outside this file, `TaskDomainService.cs`, ~2,000 lines of `CodeSearchService.cs`, and the training/Python harness scripts remain from the original outstanding list.
