# MemorySmith delta audit — additional findings

Scope: this note only records findings that were not covered in the previous audit pass. It also maps each finding to existing task work so duplication is avoided.

## 1) Task file recovery still misses unreadable-file failures
**Severity:** High  
**Confidence:** 93%

`TaskDomainService.LoadAll()` reads each task file with `File.ReadAllText(file)` before entering the `try`/`catch` that only wraps JSON deserialization. That means malformed JSON falls back to a synthetic record, but unreadable files, permission errors, sharing violations, or transient I/O failures can still throw and abort the whole task load. The fallback contract is therefore narrower than the code shape implies.  
Evidence: `LoadAll()` read occurs before the catch, while the catch only handles parse failures. `CreateMalformedTaskFallback()` then assumes the file was readable.  
Relevant existing tasks: `TSK-0052` and `TSK-0264` cover malformed / broken task recovery, but not the unreadable-file path.  
Recommendation: split file read from parse handling, and surface unreadable files as explicit load errors rather than letting one bad file crash the workbench.

## 2) Malformed task records cannot be deleted through the app
**Severity:** Medium-High  
**Confidence:** 89%

All mutating task operations call `EnsureTaskIsEditable(item)`, which throws when `HasLoadError` is true. `DeleteAsync()` calls that guard before either soft-delete or hard-delete. Combined with the fallback loader, this means a task file that is malformed enough to trigger the recovery path becomes read-only and effectively undeletable from the app. If the file is too broken to repair in UI, the only escape hatch is manual filesystem editing.  
Evidence: the loader synthesizes `HasLoadError=true`, and `DeleteAsync()` gates on editability before deleting.  
Relevant existing tasks: `TSK-0264` is the right umbrella, but it should explicitly include a “quarantine/delete malformed record” escape hatch.  
Recommendation: add a non-editing delete/quarantine path for malformed records, or make the UI show an explicit “remove broken file” action that bypasses the edit gate.

## 3) Attachment filename uniqueness is race-prone
**Severity:** Medium  
**Confidence:** 81%

`TaskAttachmentFiles.SaveAsync()` uses `GetUniqueFileName()` to probe for an unused name, then creates the file. There is no lock or transactional reservation around the probe/create sequence. Two concurrent uploads with the same sanitized filename can race between `File.Exists()` and `File.Create()`, producing a collision or transient failure even though the helper is trying to make names unique.  
Evidence: the uniqueness check is a plain existence probe; the write happens afterward with no synchronization.  
Relevant existing tasks: `TSK-0148` and `TSK-0194` cover attachment support, but not this concurrency edge.  
Recommendation: reserve filenames atomically or include a stable unique suffix up front. The simplest fix is to generate the storage filename from a GUID and keep the human-friendly display name separate.

## 4) Settings override handling is inconsistent between load and update paths
**Severity:** Medium  
**Confidence:** 84%

`MemorySmithLocalDevelopmentPostConfigure.LoadOverrideKeys()` treats malformed JSON, I/O, and unauthorized-access errors as “no overrides” and silently falls back to defaults. By contrast, `AdminSettingsService.UpdateAsync()` only catches `JsonException` when loading the current settings root, so I/O or permission failures can still bubble out as unhandled errors during a settings edit. The same settings surface is therefore forgiving on read and brittle on write.  
Evidence: `LoadOverrideKeys()` catches `JsonException`, `IOException`, and `UnauthorizedAccessException`; `UpdateAsync()` only wraps `LoadSettingsRootAsync()` in a `catch (JsonException)`.  
Relevant existing tasks: `TSK-0181` already points at malformed settings-override fallback in LocalDevelopment, but this is a broader symmetry gap and should be folded into that work.  
Recommendation: make both paths use the same error policy. Either surface explicit configuration health errors everywhere, or keep the file-based override surface but fail consistently on unreadable/invalid config.

## 5) The task service still mixes loader, validator, search, mutation, and quarantine logic in one lock
**Severity:** Low-Medium  
**Confidence:** 73%

`FileTaskService` remains a large single-class orchestration layer that owns parsing, normalization, search scoring, mutation, activity logging, attachment URI policy, and malformed-record fallback. The current shape makes it hard to independently test the loader, the mutation rules, and the quarantine path from the same file, and it encourages hidden coupling between what can be loaded and what can be edited.  
Relevant existing tasks: `TSK-0045` already tracks splitting `TaskDomainService` into layers; this file still justifies that split.  
Recommendation: extract a loader/quarantine component, a mutation component, and an attachment policy helper so broken-record handling cannot accidentally regress the healthy-record path.

## Corrections / task updates suggested

- `TSK-0264` should be expanded from “broken tasks” to “broken and unreadable tasks,” plus a delete/quarantine escape hatch.
- `TSK-0148` or `TSK-0194` should add attachment filename race coverage.
- `TSK-0181` should explicitly include IO/permission symmetry between load-time and update-time settings handling.
- `TSK-0045` should now target a loader/quarantine split, not only a general code-size reduction.
