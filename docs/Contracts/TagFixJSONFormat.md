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
      "wouldQuarantine": false,
      "changes": ["tags", "compilation", "rename"]   // any of: tags, compilation, rename, quarantine
    }
  ],

  "needsManual": [         // sorted by "file"; empty array when nothing needs a person
    {"file": "Folder\\Broken.mp3", "rule": "error", "reason": "File corrupt"}
  ]
}
```

## Field rules

- **`files` rows** exist only for a planned change: a tag fix (status would-fix) or a would-quarantine instrumental.
  A file with nothing to fix is not listed. A tag fix that only sets the compilation flag is still a row, so
  `pending` counts it.
- **`oldTags` and `newTags`** hold `title`, `album`, `artists` (the `; `-joined artist field), `genre` and
  `compilation` (boolean), and `artCount` (integer, the number of embedded cover pictures). TagFix never changes
  art, so `artCount` is the same in `oldTags` and `newTags`. For a would-quarantine row `newTags` equals `oldTags`,
  because tags are not rewritten before a move.
- **`oldName` and `newName`** are file names only. `newName` is the name a real run would give the file.
  When the rename is blocked it equals `oldName`.
- **`needsManual`** lists two kinds of row. An errored file gives its error text as `reason`. A planned change
  whose rename is blocked gives one of: `empty title after sanitisation`, `empty artists after sanitisation` or `empty artists and title after sanitisation` (the cleaned title or artists
  is empty, so the file keeps its name) or `rename target already exists` (a different file already holds the
  planned name). The tag fix for a blocked row is still in `files`, so a client can show both.
  Every row also carries a stable `rule` id: `error`, `empty_title`, `empty_artists`, `empty_artists_and_title`, `rename_target_exists`, `unwanted_text` or `needs_manual`. The old id `empty_artist_or_title` (reason `empty artist or title after sanitisation`) is no longer written, but a stored plan with that reason still reads as `empty_artist_or_title`.
  `unwanted_text` is raised on a dry run when a tag still holds unwanted text after the fix (reason `found 'x' in album`); it is skipped when the file has an `<Accept rule="unwanted_text">` row or a `keep=` row. `missing_tag` is raised on a dry run for an empty or implausible Year (BL-316; a year that is not four digits from 1900 to next year counts as empty) when no unwanted text is found (reason `Missing tag: Year`; skipped on an `<Accept rule="missing_tag">` row); a missing title or artist is `empty_title`, `empty_artists` or `empty_artists_and_title`, and a missing album is reported only in the routing `projectedHits`, see RoutingJSONFormat.md.
  `config/tagfix-exceptions.xml` accepts `<Accept file="x.mp3" [sourceRel="Folder\x.mp3"] rule="unwanted_text|missing_tag|apply_title|bad_cover" />`; an accepted `unwanted_text` or `missing_tag` rule is dropped from `projectedHits`.
  `bad_cover` is raised on a dry run when the front cover is not JPEG or PNG (webp does not show in Windows or Apple Music) or is solid black, after the unwanted_text and missing_tag checks (reasons `Cover is image/webp (needs JPEG or PNG for Windows and Apple Music)`, `Cover is solid black`, `Cover cannot be decoded`). Detection only: the engine never rewrites art. A missing cover is not flagged. Skipped on an `<Accept rule="bad_cover">` row.
  Every `needsManual` row also carries `suggestedTitle` (string, empty when there is none) and `oldTitle` (string, the current title tag before any fix), plus `suggestedAlbum` and `oldAlbum` (same rules, for the album tag). `suggestedTitle` is set only for an `unwanted_text` row on the title, and is the title with its last trailing `(...)` or `[...]` group removed. `suggestedAlbum` is set only for an `unwanted_text` row on the album, with the same trailing-group rule, and `oldAlbum` is the album tag before any fix; both are empty otherwise. It is empty when the suggestion would duplicate another title: the dry-run reason then ends with ` (would duplicate)`, so a client can disable ACCEPT. Accepting it is the `apply_title` rule below.
  `apply_title` (Accept rule): on a real apply, the title is set to the stripped suggestion and the file is renamed to match, with the usual backup and revert journal. A collision (another file this run, the library mirror, or a sibling file already named `Artist - Title.mp3`) refuses the file: `rule` `unwanted_text`, `reason` `would duplicate: <title>`, and the file is untouched.
- **`counts` are derived from the rows** above, so they can never disagree with the lists.

## Fingerprint

`fingerprint.value` is the SHA-256 (lower-case hex) of the UTF-8 text made by one line per scanned mp3, sorted
ordinal, joined with `\n`. Each line is `<path relative to NewMusic>|<size in bytes>|<last-write time in UTC ticks>`.
Any file added, removed, renamed, resized or touched changes the value. A future apply must refuse to run when the
current NewMusic content no longer matches the plan's fingerprint. `fingerprint.fileCount` is the number of lines.

## Versioning

`schemaVersion` starts at 1. It is bumped only when a field is removed or its meaning changes (additive fields do
not bump it). A consumer must read `schemaVersion` and refuse a mismatch it was not built against.

The tagFix additions are additive and do not bump `schemaVersion`: `artCount` inside `oldTags` and `newTags` here,
and the routing JSON `tagFix` object below.

## Routing JSON: per-track `tagFix`

The routing JSON (`recordType` routing or execution) carries one `tagFix` key on every `files[]` entry, placed just
before `tagChanges`. It is `null` when the track has no tag fix. Otherwise it is an object:

`{"oldName": "<original file name>", "newName": "<name after the rename>", "oldTags": {...}, "newTags": {...}}`

`oldTags` and `newTags` use the same shape as above, including `artCount`. `tagChanges` stays as the short
display strings, so existing consumers keep working. No routing contract doc under `docs/Contracts/` mentions
`tagChanges`, so this section is the only place the `tagFix` shape is written down.

## Per-file exceptions: `config/tagfix-exceptions.xml`

A hand-edited config read by TagFixer and by the integrate library-duplicate check. It is not written by the engine and not part of the plan JSON. Root element `<TagFixExceptions>`, two row kinds, both keyed by the ORIGINAL NewMusic filename (case-insensitive, no path).

- `<Exception file="..." keep="title|artist|album|casing" />` keeps the named tag as read. `title` skips built-in cleanup and custom rules on the title. `artist` keeps the artists tag as read, with no feat. split. `album` skips built-in album cleanup and custom rules on the album. `casing` skips the artist name casing overrides. Several rows for one file combine. The filename rename follows the kept tags.
- `<NotDuplicate file="..." />` makes the integrate library-duplicate check skip that file, so its routing entry has `libraryDuplicate` false.

A missing file means no exceptions, silently. A malformed file prints one `[WARN]` line and means no exceptions. An unusable row is skipped with a `[WARN]` and the other rows still apply. Code: `src/AudioManager/Code/Doer/TagFixExceptions.cs` (`Load`, `Keeps`, `IsNotDuplicate`, `Accepts`). Tests: `src/AudioManager.Tests/TagFixExceptionsTests.cs`.

### Entry shape for a GUI writer (`<Accept>`, rule `apply_title`)

One row per file. The entry is additive: `<Accept>` rows with an unknown `rule` are skipped with a `[WARN]` by the engine, so the rule id must match exactly.

```json
{"element": "Accept", "file": "Band - Girl on LSD (Alternate Version).mp3", "rule": "apply_title"}
```

As XML: `<Accept file="Band - Girl on LSD (Alternate Version).mp3" rule="apply_title" />`. Add `sourceRel="Folder\x.mp3"` to scope it to one exact path, as the other rules do. The title written is never taken from the row: the engine recomputes the stripped title from the file's current tags. The same accept also strips a residual album group (no duplicate check, since an album is not a file name), and the change is listed as an `Album:` line in the row's changes. A GUI writer must keep `<Accept>` rows whose `rule` it does not know, since the engine reads them.

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
