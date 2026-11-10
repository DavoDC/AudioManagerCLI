# Integrate gate result JSON

`integrate --json-output` writes `data/logs/integrate-gate.json` at the pre-integration gate, pass or fail, so a client never has to parse console text when integrate stops before routing (no routing JSON is written in that case).

Fields (schemaVersion 1):

- `status`: `passed`, `mirror_stale`, `libchecker_dirty` or `mirror_repo_missing`.
- `action`: machine hint for the client: `none`, `commit_mirror`, `fix_library_issues`, `restore_mirror_repo`.
- `mirrorDirty`, `mirrorDirtyCount`, `mirrorDirtyFiles`: the `git status --porcelain` lines of AudioMirror (LastRunInfo.txt ignored); only filled when `status` is `mirror_stale`.
- `libCheckerIssues`: total LibChecker hits (0 when the gate stopped earlier or the library is clean).
- `generatedAt`: local ISO time.

The mirror check runs before LibChecker, so `mirror_stale` is reported up front. Bump `schemaVersion` on any breaking change; consumers are the AudioManagerGUI Integration tab.
