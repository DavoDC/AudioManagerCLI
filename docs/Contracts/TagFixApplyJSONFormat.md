# Tag Fix apply, result and journal JSON

Commands (both mutate NewMusic; the plan comes from `tagfix --dry-run --json-output`, see `TagFixJSONFormat.md`):

- `AudioManager.exe tagfix --apply-plan` applies the reviewed plan. It refuses (exit 1, `stale_plan`) when the NewMusic fingerprint no longer matches `data/logs/tagfix-plan.json`, and refuses (`insufficient_disk`) when the backup drive cannot hold the planned files plus 10 percent, and refuses (`files_in_use`, failures name each file) when a planned file is open in another program. A refusal changes nothing.
- `AudioManager.exe tagfix --revert <journal path>` undoes an apply: tags and names come back and quarantined files move back to NewMusic. A file whose tags no longer equal the journal's `new` tags is left alone and counted as `SkippedModified`. A `failed` entry (the apply threw part-way) is never written to: if its file still equals the byte backup it is a quiet no-op, otherwise it is counted as failed with a note naming the backup, so the revert exits 1.

Always written by `--apply-plan`: `data/logs/tagfix-result.json` (schemaVersion 1):

- `status`: `applied`, `partial` (some files failed), `nothing_to_do` (plan had no pending changes), `stale_plan`, `insufficient_disk`, `files_in_use` (a planned file is locked by another program; nothing changed, close it and re-run).
- `apply_locked` is the one exception: another apply holds `data/logs/tagfix-apply.lock` (or a stale lock could not be removed). It exits 1, changes nothing, and writes no `tagfix-result.json`, so an in-progress result is never overwritten. The lock is held for the whole apply and removed afterwards; a lock left by a crashed run is detected (nobody holds it) and removed by the next apply.
- `oldFingerprint` (the reviewed plan) and `newFingerprint` (NewMusic now): on `stale_plan` they differ and a client offers a one-click re-plan.
- `journal`: path of the journal, or null when the apply was refused. `bytesNeeded` and `bytesFree` for the disk check.
- `counts`: `applied`, `alreadyDone`, `failed`, `quarantined`, `needsManual`, `pendingAfter` (a fresh dry-run scan after the apply, so a second apply reports 0).
- `failures`: one string per failed file.

Journal `data/logs/tagfix-journal-<yyyyMMdd-HHmmss>.json`: written before the first change, rewritten atomically after every file. One entry per planned file with `file`, `oldName`, `newName`, the old and new tag fields, `backup`, `quarantinedTo`, `detail` and `status` (`pending`, `done`, `failed`, `quarantined`, `already_done`, `reverted`). After a crash, `pending` entries were never touched.

Byte backups: `data/backups/<stamp>/<relative path>` (gitignored). Backups older than 7 days are pruned at the start of the next apply, never on a clean run and never by a background job. A run whose journal still has `pending` or `failed` entries keeps its backups past 7 days.

Test-only folder override: env `AUDIOMANAGER_TEST_ROOT=<dir>` redirects NewMusic (`<dir>\NewMusic`), its quarantine (`<dir>\NewMusic-Quarantine`), logs (`<dir>\logs`) and backups (`<dir>\backups`). Unset means the real paths, no behaviour change. A file the plan scan could not read (locked, corrupt) is listed in `failures` and makes the status `partial`.
