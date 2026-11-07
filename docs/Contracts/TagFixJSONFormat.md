# Tag Fix Plan JSON Format (`tagfix --dry-run --json-output`)

The data contract for a client that previews Tag Fix before any file changes. `AudioManager.exe tagfix --dry-run --json-output`
writes one file to `data/logs/` with a fixed filename (latest dry run wins):

| File | Source | Contains |
|---|---|---|
| `tagfix-plan.json` | `Code/Doer/TagFixPlanJson.cs` | the per-file tag and filename changes a real Tag Fix would make, a fingerprint of the NewMusic content they were planned from, and the counts |

**The hard rule:** the plan is read-only. A dry run never renames, rewrites, moves or deletes a file, and it never
writes this JSON from a real run. `--json-output` without `--dry-run` writes no JSON and prints a warning. The
real apply and its revert journal are not part of this contract yet.

**Only the engine writes this file.** A client reads it and never reimplements tag logic or parses MP3 tags itself.

---

## Shape

```jsonc
{
  "schemaVersion": 1,
  "generatedAt": "2026-11-07T21:08:51",       // ISO-8601 local time, no offset

  "fingerprint": {
    "algorithm": "sha256",
    "fileCount": 230,                           // every *.mp3 under NewMusic that the scan found
    "value": "9232fb54...dd58"                  // 64 lower-case hex characters, see "Fingerprint" below
  },

  "counts": {
    "pending": 230,        // rows in "files": every planned tag fix plus every planned quarantine
    "needsManual": 3,      // rows in "needsManual": errored files plus planned changes whose rename is blocked
    "renames": 158,        // rows in "files" whose newName differs from oldName (case-sensitive)
    "quarantines": 0       // rows in "files" with wouldQuarantine true
  },

  "files": [               // sorted by "file" (case-insensitive); empty array when nothing is planned
    {
      "file": "Album Folder\\01 - Song.mp3",   // path relative to NewMusic, backslash on Windows
      "oldTags": {"title": "Song (Explicit)", "album": "Album", "artists": "Artist", "genre": "Hip Hop", "compilation": false},
      "newTags": {"title": "Song", "album": "Album", "artists": "Artist", "genre": "Hip Hop", "compilation": true},
      "oldName": "01 - Song.mp3",
      "newName": "Artist - Song.mp3",          // equals oldName when no rename is planned or the rename is blocked
      "wouldQuarantine": false
    }
  ],

  "needsManual": [         // sorted by "file"; empty array when nothing needs a person
    {"file": "Folder\\Broken.mp3", "reason": "File corrupt"}
  ]
}
```

## Field rules

- **`files` rows** exist only for a planned change: a tag fix (status would-fix) or a would-quarantine instrumental.
  A file with nothing to fix is not listed. A tag fix that only sets the compilation flag is still a row, so
  `pending` counts it.
- **`oldTags` and `newTags`** hold `title`, `album`, `artists` (the `; `-joined artist field), `genre` and
  `compilation` (boolean). For a would-quarantine row `newTags` equals `oldTags`, because tags are not
  rewritten before a move.
- **`oldName` and `newName`** are file names only. `newName` is the name a real run would give the file.
  When the rename is blocked it equals `oldName`.
- **`needsManual`** lists two kinds of row. An errored file gives its error text as `reason`. A planned change
  whose rename is blocked gives one of: `empty artist or title after sanitisation` (the cleaned artist or title
  is empty, so the file keeps its name) or `rename target already exists` (a different file already holds the
  planned name). The tag fix for a blocked row is still in `files`, so a client can show both.
- **`counts` are derived from the rows** above, so they can never disagree with the lists.

## Fingerprint

`fingerprint.value` is the SHA-256 (lower-case hex) of the UTF-8 text made by one line per scanned mp3, sorted
ordinal, joined with `\n`. Each line is `<path relative to NewMusic>|<size in bytes>|<last-write time in UTC ticks>`.
Any file added, removed, renamed, resized or touched changes the value. A future apply must refuse to run when the
current NewMusic content no longer matches the plan's fingerprint. `fingerprint.fileCount` is the number of lines.

## Versioning

`schemaVersion` starts at 1. It is bumped only when a field is removed or its meaning changes (additive fields do
not bump it). A consumer must read `schemaVersion` and refuse a mismatch it was not built against.

## Encoding and line endings

UTF-8 without a byte-order mark, written atomically (temp file, then replace). Lines end with CRLF on Windows.
A consumer's JSON parser reads both.

## Source and tests

- Builder and fingerprint: `src/AudioManager/Code/Doer/TagFixPlanJson.cs` (`Build`, `Fingerprint`, `Write`).
- Per-file rows: `TagFixer.FixLog` in `src/AudioManager/Code/Doer/TagFixer.cs` (`OldTags`, `NewTags`,
  `WouldQuarantine`, `NeedsManual`, `RenameBlocked`), filled only by the dry-run branch and the shared tag reads.
- Tests: `src/AudioManager.Tests/TagFixPlanJsonTests.cs`. A dry-run test proves the file is unchanged after a plan.
- The filename rule is the one a real run uses (`Reflector.SanitiseFilename`), so a planned `newName` is what a
  real run would write, not a guess.
