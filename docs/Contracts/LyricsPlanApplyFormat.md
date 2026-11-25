# Lyrics plan apply, result and journal JSON

Commands (standalone, return before verb parsing, never touch NewMusic; they write USLT lyrics into library files):

- `AudioManager.exe --apply-lyrics-plan <plan.json>` is a DRY RUN: it reads the plan, prints the counts and writes nothing (no journal, no backup, no result file, no lock).
- `AudioManager.exe --apply-lyrics-plan <plan.json> --apply` performs the writes, with guards. Exit 0 for `applied` or `nothing_to_do`, else 1.
- `AudioManager.exe --lyrics-revert <journal path>` undoes an apply. A file whose USLT no longer equals the journal's `newLyrics` is left alone and counted as left alone (exit 1). A `pending` entry (crash mid-apply) and a `failed` entry whose file differs from its byte backup count as failed with a note naming the backup (exit 1).

Plan file (version 1), JSON: `{"version":1,"created":"...","tracks":[{"path":"library-relative/with/forward/slashes.mp3","size":123,"mtime":"2026-01-02T03:04:05","verdict":"single","source":"lrclib","action":"keep|write|skip","lyrics":"text or null","evidence":{}}]}`. Keys other than `path`, `size`, `mtime`, `action` and `lyrics` are ignored. A plan whose `version` is not 1 is refused (`bad_plan`), nothing changes.

Per row:

- Only `action: write` rows are acted on. `keep` and `skip` rows (and any other action, a write row with empty lyrics, a path that is empty, rooted or escapes the library, or a missing file) are never touched and count as `skipped_other`.
- A row whose file size or mtime (compared to the second, local time) differs from the plan is stale: counted in `skipped_stale`, never a failure.
- A file whose current USLT is not empty (a frame with non-blank text) is never overwritten: counted in `has_lyrics`.
- Otherwise the lyrics are written as the ID3v2 USLT frame (language `eng`, empty description), replacing any blank USLT frame.

Always written by a real `--apply`: `data/logs/lyrics-result.json` (schemaVersion 1) plus a timestamped copy beside it:

- `status`: `applied`, `partial` (some files failed), `nothing_to_do`, `bad_plan`. `apply_locked` is the exception: another apply holds `data/logs/lyrics-apply.lock` (or a stale lock could not be removed); it exits 1, changes nothing and writes no result file. A lock left by a crashed run is detected and removed by the next apply. A dry run reports `dry_run` on the console only.
- `plan`: the plan path. `journal`: the journal path, or null when nothing was written.
- `counts`: `written`, `skipped_stale`, `has_lyrics`, `skipped_other`, `failed`.
- `failures`: one string per failed file.

Journal `data/logs/lyrics-journal-<yyyyMMdd-HHmmss>.json`: written before the first change, rewritten atomically after every file. One entry per file about to be written: `file` (library-relative), `oldLyrics` (the USLT text before, null when the file had none), `newLyrics`, `backup`, `detail`, `status` (`pending`, `done`, `failed`, `reverted`). Revert restores `oldLyrics` (null removes the frame) only while the current USLT still equals `newLyrics`.

Byte backups: `data/backups-lyrics/<stamp>/<relative path>` (gitignored), copied before each write and never overwritten. Backups older than 7 days are pruned at the start of the next real apply; a run whose journal still has `pending` or `failed` entries keeps its backups.
