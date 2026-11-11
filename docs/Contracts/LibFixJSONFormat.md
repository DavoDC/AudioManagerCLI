# Library fix JSON (libfix)

Commands that fix tracks already in the library. First kind: `album_in_singles` (a LibChecker hit: two or more songs of one album, counting the album folder and `Singles/`, but one is still in `Singles/`). Never touches NewMusic and never writes outside `<library>\Artists`.

## Commands
- `AudioManager.exe --libfix-plan`: writes `libfix-plan.json` to the logs folder. Moves nothing. Exit 0 when the plan is written.
- `AudioManager.exe --libfix-apply <planPath>`: moves the planned files, writes `libfix-journal-<yyyyMMdd-HHmmss>.json` and `libfix-result.json`. Exit 0 only for `applied`.
- `AudioManager.exe --libfix-revert <journalPath>`: moves the journaled files back, writes `libfix-result.json`. Exit 0 only for `reverted`.
Only one of the three flags per run. Apply runs only when called with a plan: a plan alone never changes the library.

## Plan (`schema` "libfix-plan-1")
Fields: `kind` ("album_in_singles"), `libraryRoot`, `generated`, `moves[]` (`from`, `to`, `artist`, `album`, `createsFolder`; paths relative to `libraryRoot`), `skipped[]` (`from`, `to`, `reason`, for example the destination already exists), `counts` (`moves`, `skipped`).

## Journal (`schema` "libfix-journal-1")
Fields: `libraryRoot`, `generated`, `moves[]` (`from`, `to`, `createdFolder`: the relative folder that move created, or null). Written before the first move and after each move, so a failed run still has a record to revert.

## Result (`schema` "libfix-result-1")
Fields: `operation` (apply or revert), `generated`, `status`, `message`, `journal`, `counts` (`moved`, `reverted`, `skipped`, `failed`, `foldersRemoved`), `notes[]`.
- apply `status`: `applied`, `stale_plan` (a source is missing or a destination exists; checked for every move before the first, nothing moved), `refused` (plan for another library root or a path outside Artists; nothing moved), `apply_locked` (another libfix apply holds `libfix-apply.lock` beside the journal, or a stale lock could not be removed; nothing moved, exit 1), `failed`.
- revert `status`: `reverted`, `partial` (some file skipped: not present, or its original path is occupied; nothing is overwritten), `refused`, `failed`. Folders the apply created are removed only when empty.

After an apply or revert, re-run the mirror step (`--mirror-retry`) so AudioMirror matches the library.
