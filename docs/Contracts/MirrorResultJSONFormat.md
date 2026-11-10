# Mirror result JSON

`integrate` (non-dry run) writes `logs\mirror-result.json` (under `Constants.LogsPath`) after the post-integration mirror step: force regen, LibChecker, then the AudioMirror auto-commit. It is written on every integrate run that reaches that step, success or failure.

Why it exists: by then the integration has already moved files. A failure in the mirror step must not look like a failed move, and a client must not have to parse console text to learn that.

Fields (schemaVersion 1):

- `recordType`: always `mirrorResult`.
- `schemaVersion`: `1`. Bump on any breaking change.
- `generatedAt`: local ISO time.
- `status`: `ok` (committed), `mirror_commit_failed` (regen threw, the repo is missing, or git add/commit failed), `libchecker_issues` (LibChecker not clean, nothing committed), `skipped` (`--no-auto-commit`).
- `error`: the exception message or git failure text for `mirror_commit_failed`; `null` otherwise.
- `retry`: the command that re-runs only this step, `AudioManagerCLI.exe --mirror-retry`.

Retry: `AudioManagerCLI.exe --mirror-retry [--no-auto-commit]` runs only the mirror step. It never runs integration and never moves files. It rewrites this file and exits 0 only when `status` is `ok`, otherwise 1.
