# Integrate gate result JSON

`integrate --json-output` writes `data/logs/integrate-gate.json` at the pre-integration gate, pass or fail, so a client never has to parse console text when integrate stops before routing (no routing JSON is written in that case).

Fields (schemaVersion 1):

- `status`: `passed`, `mirror_stale`, `libchecker_dirty`, `mirror_repo_missing`, `git_error` (git status itself failed), `tagfix_pending` (NewMusic has pending tag fixes or manual-review files; action `run_tagfix`; nothing moved, exit 1) or `gate_error` (the gate threw before it could decide).
- `action`: machine hint for the client: `none`, `commit_mirror`, `fix_library_issues`, `restore_mirror_repo`, `fix_git`, `fix_gate_error`.
- `error`: only on `gate_error`, the exception message. The file from an earlier run is deleted when the gate starts, so a crashed gate never leaves a stale `passed`.
- `mirrorDirty`, `mirrorDirtyCount`, `mirrorDirtyFiles`: every `git status --porcelain` line of AudioMirror (no filename is exempt); only filled when `status` is `mirror_stale`.
- `libCheckerIssues`: total LibChecker hits (0 when the gate stopped earlier or the library is clean). On `passed` it can be above 0: fix-later kinds (`duplicate`, `artist_folder_mismatch`) stay counted and in the report but do not stop the gate, and low-res album art is report-only. Any other hit kind gives `libchecker_dirty`.
- `libWarnings`, `libWarningHits`: additive warn-only LibChecker hits (GUI-91); `libWarnings` is the count and `libWarningHits` lists `{kind, path, detail}` with the warn rule ids `padded_text` (Title, Artists or Album has leading, trailing or double spaces) and `unknown_cover_dims` (art present but its size is Unknown). They never change `libCheckerIssues`, `status` or `action`; unwanted text stays an `unwanted_text` hit inside `libCheckerIssues` until David's warn-only decision for it is built.
- `generatedAt`: local ISO time.

The mirror check runs before LibChecker, so `mirror_stale` is reported up front. Bump `schemaVersion` on any breaking change; consumers are the consumer program's integration screen.
