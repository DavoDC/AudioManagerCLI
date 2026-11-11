# Batch readiness and rehearsal scripts

Two scripts in `scripts/dev/` answer "is this NewMusic batch safe to integrate?" without moving a file in the real folders. This is the single source for how to run them.

## Tier 1: `batch-readiness.ps1` (read-only)

Runs `integrate --dry-run --json-output --no-input`, then writes `readiness.json` and `readiness.md` to `-OutDir`.

`powershell -NoProfile -File scripts/dev/batch-readiness.ps1 -Exe <AudioManager.exe> -Root <root> [-TestRoot] [-OutDir <dir>]`

`-Root` is mandatory (nothing is implicit): the folder holding `Audio`, `AudioMirror` and `NewMusic` as the exe sees it. Without `-TestRoot` the exe uses its own real paths, so run tier 1 on real folders only when David has said so. Before and after the run the script snapshots NewMusic, the library, the mirror and the mirror HEAD, and fails with `REAL FOLDER TOUCHED` if anything changed.

Verdict in `readiness.json`: `ready` (no routing errors, no projected LibChecker hits), `review` (errors, hits, or hits not determined), `blocked` (the pre-integration gate refused, for example the mirror is out of sync; the gate status and action are recorded and no routing record exists). Fields: `counts`, `duplicates`, `artistSpellingCandidates`, `newMusicFingerprint`, `projectedLibCheckerHits`, `verdict`.

## Tier 2: `batch-rehearsal.ps1` (full-size, on a copy)

`powershell -NoProfile -File scripts/dev/batch-rehearsal.ps1 -Exe <AudioManager.exe> -WorkDir <new folder under TEMP> [-Integrate] [-SpellingFromSuggested]`

Copies the real NewMusic, library and mirror (a real copy, never a hardlink) into `<WorkDir>\root`, runs tier 1 there, and with `-Integrate` builds a manifest from the routing record (object form, `-SpellingFromSuggested` adds one `artistSpellings` choice per candidate using the suggested spelling), integrates the copy through a subst drive, and saves `execution-*.json`, `mirror-result.json` and `integrate-gate.json` under `tier2\`. The real sources are snapshotted before and after; any change fails the run. `rehearsal.json` records the tier 1 verdict, the mirror status and the execution counts. The work folder gets a README whose first line is a `DELETE-AFTER:` date. If NewMusic is edited while the script runs, the guard fires: rerun.

## Reading the result

The rehearsal's `mirror-result.json` `hits[]` is the list of hits a real integrate would leave (`kind`, `path`, `detail`, see [MirrorResultJSONFormat.md](Contracts/MirrorResultJSONFormat.md)). Hits that need David's decision (version or original text in titles, album tracks in Singles, duplicate choices) are expected and are not engine faults.

## Tests

`powershell -NoProfile -File scripts/dev/snapshot-guard.tests.ps1`, `batch-readiness.tests.ps1` and `batch-rehearsal.tests.ps1` cover the pure helpers and the guard with fake folders under TEMP.
