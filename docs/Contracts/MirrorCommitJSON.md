# Mirror commit JSON (`--validate-mirror`, `--commit-mirror`)

Two standalone commands check the AudioMirror health gate and, for `--commit-mirror`, make a local commit. They are the
commands the GUI runs instead of raw git, so an AudioMirror XML with an invalid Year is never committed (BL-317 / GUI-140).

- `AudioManager.exe --validate-mirror` checks the changed XML files and commits nothing.
- `AudioManager.exe --commit-mirror --message "<text>"` runs the same check and, only when it finds zero problems, runs
  `git add -A` and `git commit -m "<text>"` in the AudioMirror repo. It never pushes.

Both commands return before any mode parsing and never touch the library or NewMusic. The gate reads only the AudioMirror
repo (`git status --porcelain` and the XML files). The auto-commit path in the integrate and analysis flows runs the same
gate before it commits.

## What the gate checks

The gate lists changed and untracked `.xml` files with `git status --porcelain -uall`. Deleted files are skipped. Each
file that still exists is checked with these rules:

| Check | Refused when | Shared rule |
|---|---|---|
| well-formed XML | the file does not parse as a track XML | `TrackXML.Read` |
| Title | missing or blank | gate |
| Artists | missing or blank | gate |
| Album | missing or blank | gate |
| Year | present and not `YearRule.IsValid` (4 digits, 1900 to next year) | `YearRule` |
| album cover count | not exactly one | `LibChecker.CoverCountProblem` |
| Compilation | not set | `LibChecker.CompilationProblem` |

Not run by the gate: the library-wide LibChecker rules (filename match, unwanted text, duplicates, folder rules, album
subfolder rules, the Misc and Compilations folder rules). They need the whole library, so they stay in `analysis`.
Low-resolution covers are report-only in LibChecker and are not checked here.

## The JSON line

Both commands print their normal console lines first, then exactly one JSON object as the **last** stdout line. Nothing
is printed after it. Parse the last line only.

```jsonc
{
  "ok": true,                 // true only when no git error and zero problems
  "committed": false,         // true only when --commit-mirror made a commit
  "checked": 3,               // changed or untracked XML files that were checked
  "problems": [               // [] when clean
    {"file": "AUDIO_MIRROR/Artists/Alpha/Album1/a.xml", "reason": "Year '201' is not a valid year (1900 to next year)"}
  ],
  "error": ""                 // empty string when none
}
```

- `file` is repo-relative with forward slashes, relative to the AudioMirror repo root.
- `reason` is the human message for the first failed rule in the order of the table above.
- `error` is non-empty for a git failure, a missing repo, a missing or empty `--message`, or a flag mix that is not allowed.
- Strings use the same escaping as the other contract files (`JsonText.Quote`).

## Exit codes

| Code | Meaning |
|---|---|
| `0` | `ok` is true. `--commit-mirror` exits 0 whether or not it committed (a clean set with no changes has nothing to commit). |
| `3` | refused: `problems` is not empty. Nothing was committed. |
| `1` | any other error (`error` is set). For `--commit-mirror` that includes a failed `git add` or `git commit`. |

## Flags

- `--validate-mirror` runs the check only.
- `--commit-mirror` requires `--message`. The text must not be empty or whitespace.
- Using both flags together is an error (exit 1, JSON `error` set).

## Auto-commit path

`AudioMirrorCommitter.TryCommitAt` (used by the analysis and integrate flows) runs the gate after its clean-tree check. When
the gate reports problems it prints `[WARN]` lines, prints a `[WARN] AudioMirror commit refused` line, commits nothing and
returns false. The same return value covers a failed git step, so the post-integration mirror step reports
`mirror_commit_failed` (see [MirrorResultJSONFormat.md](MirrorResultJSONFormat.md)). Fix the XML, then run `--mirror-retry`.

## Versioning

The JSON has no `schemaVersion` yet. Keys are stable. Readers must ignore unknown keys. A removed or renamed key is a breaking
change, so update the GUI with it.
