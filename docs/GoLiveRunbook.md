# Go-live runbook: integrating a NewMusic batch

One page for the first real integration through the GUI. The library and NewMusic are NOT backed up: stop on anything unexpected and do not retry blindly.

## Before you start
- To check a batch without touching real folders, run ONE command, `batch-rehearsal.ps1 -Integrate`, and read its `summary.md` (verdict, counts, hits by kind, changes since the last run, items needing David): [BatchReadiness.md](BatchReadiness.md).
- Close anything that holds music files open (players, taggers, sync clients).
- Titles: the Tag Fix plan shows before/after; the title and artist rules are in [MusicLibraryRules.md](Rules/MusicLibraryRules.md).
- Make sure the AudioMirror repo has no uncommitted changes (the gate checks this and blocks with `mirror_stale`).
- Note the baseline: music files in NewMusic (desktop.ini files are ignored by the engine, so 189 files on disk is 187 music files plus 2 desktop.ini).

## Steps (what the GUI runs, and what to check)
Order (David, 2026-11-11): tag fixes first, then review, then the MAX_PATH gate, then integrate, so every review screen shows the FINAL values, never pre-fix ones (rule: [MusicLibraryRules.md](Rules/MusicLibraryRules.md)). A direct real `integrate` also runs a read-only pre-flight (dry tag fix plus the MAX_PATH gate) before it writes any tag, so a blocked run leaves NewMusic untouched; the routing record then carries the top-level `blocked` object, no new JSON fields.
1. **Tag Fix plan** (`tagfix --dry-run --json-output`): writes `logs/tagfix-plan.json`. Check the counts: pending, needsManual (must be 0 or reviewed), renames, quarantines. Nothing on disk changes.
2. **Apply** (`tagfix --apply-plan`): changes tags and filenames, writes byte backups under `data/backups/<stamp>/`, a journal and `logs/tagfix-result.json`. Check `status` is `applied` and `failed` is 0. A `stale_plan` means NewMusic changed since the plan: nothing was touched, re-run step 1.
3. **Dry-run integrate again** (`integrate --dry-run --json-output --no-input`). The preview made BEFORE the apply is stale after it (it predicts the old names), so always re-run it now. Check `summary.counts` (files, routed, skipped, errors, libraryDuplicates) and the gate JSON status.
4. **Gate** (part of integrate): must be `passed`. Statuses: `mirror_stale` (commit the mirror), `libchecker_dirty` (fix library issues), `git_error`, `mirror_repo_missing`. Also a top-level routing `blocked` with `path_too_long` means a planned destination would exceed the Windows path limit in the mirror staging area: nothing moved; rename or shorten the listed files, then re-plan.
5. **Integrate** (`integrate --json-output --no-input`, not dry). Under `--no-input` a library duplicate takes the decision the review step put in the manifest; with none, it keeps both (K). Only an explicit review decision can choose D (the NEW file goes to the Recycle Bin, so it stays recoverable) or L (replace the library copy); the engine's own recommendation never does, it downgrades to K. Resolve every duplicate in review so nothing is left to the default. Check the execution JSON: `moved` equals `routed`, `errors` 0.
6. **Mirror** (runs automatically after integrate): regenerates the mirror, runs LibChecker and commits AudioMirror. Result is in `logs/mirror-result.json` (`ok`, `libchecker_issues`, `mirror_commit_failed`, `skipped`). Nothing is pushed.

## If the mirror step is not `ok`
The files have ALREADY MOVED. Do not re-run integrate. Fix the cause (shorten a long filename, fix a LibChecker issue, fix git) and run only the mirror step: `AudioManager.exe --mirror-retry` (add `--no-auto-commit` to skip the commit). Exit code 0 only when status is `ok`.

## Reverting
- **Tag changes can be reverted only BEFORE integrate** (`tagfix --revert <journal>`). After integrate the files have left NewMusic and revert reports `current file missing` for each (safe, by design).
- Byte backups of the pre-apply files stay in `data/backups/<stamp>/` for 7 days (pruned at the next apply).
- Integrate itself has no undo: moved files are in the library.

<!-- md-block: libfix-pointer -->
- **Fixing a library track the gate flagged** (for example `album_in_singles`): `--libfix-plan`, review `libfix-plan.json`, then `--libfix-apply <plan>`; undo with `--libfix-revert <journal>`. Run the mirror step afterwards. Details: [LibFixJSONFormat.md](Contracts/LibFixJSONFormat.md).
<!-- /md-block: libfix-pointer -->
- **A library file replaced by decision L is kept, not deleted.** Once the new file lands, the old one moves (a rename) to `NewMusic-Quarantine\Replaced\<yyyyMMdd-HHmmss>\`, next to the NewMusic folder. It is never pruned automatically. The execution JSON lists each one under `replacedOriginals` (fields: [RoutingJSONFormat.md](Contracts/RoutingJSONFormat.md)). **Restore:** move the `held` file back to its `original` path (the replacement is at that path now, so move or rename the replacement first, then re-run the mirror step). If the holding folder is on a different volume than the library, integrate refuses to start and nothing moves.

## Known limits
- Two NewMusic files can share one filename (for example `Lukis Mac - Intro.mp3`, different albums): rows are keyed by `sourceRel` (path under NewMusic), so each is accepted or declined on its own. A manifest row without `sourceRel` still matches by filename in any folder; clients should send `sourceRel` ([RoutingJSONFormat.md](Contracts/RoutingJSONFormat.md)).
- A second `tagfix --apply-plan` while one is running is refused (`apply_locked`); details in [TagFixApplyJSONFormat.md](Contracts/TagFixApplyJSONFormat.md).

## Stop and ask when
- The plan `fileCount` differs from the music files you expect, `needsManual` is above 0, or the gate is anything but `passed`.
- Any step exits non-zero or `failed`/`errors` is above 0.
- Real NewMusic or library counts change when no step should have changed them.
