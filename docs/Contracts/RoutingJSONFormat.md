# Routing JSON Format (`integrate --json-output`)

The routing record written to `logs/routing-<timestamp>.json` by an integrate run (dry run or real). `recordType` is `routing`, `schemaVersion` 1. Row fields are produced in `MusicIntegrator.BuildJson`.

## Row key: `sourceRel`, and the `--manifest` file

Each `files` row carries `filename` and, when the source is under NewMusic, `sourceRel`: the file's path relative to NewMusic (backslashes). Two files can share a filename in different folders, so `sourceRel` is the unique row key.

`integrate --manifest <accepted.json>` takes an array of accepted rows: `filename`, `sourceRel` (optional), `artist`, `title`, `dupResolution` (optional, `D`/`L`/`K`). A row with `sourceRel` matches only that exact file (or its Tag Fix rename in the same folder), so a same-named file in another folder stays declined. A row without `sourceRel` matches by filename in any folder (older manifests keep working). Clients should send `sourceRel` for every row. Every row of an execution record's `files` carries `sourceRel`, and so does each entry of `confidence.errors` (beside `filename`), so a failed row can be keyed too; it is omitted only when the source was not under NewMusic.

## summary.counts

Stage counts so a client does not have to scan the rows. All integers, derived from the same rows written to `files`.

| Field | Meaning |
|-------|---------|
| `files` | total rows |
| `routed` | rows with status `moved` or `would-move` |
| `skipped` | rows with status starting `skip`, or `would-skip` |
| `errors` | rows with status `error` |
| `libraryDuplicates` | rows with `libraryDuplicate` true |
| `inBatchDuplicates` | rows with `inBatchDuplicate` true |
| `unrouted` | `files` minus `routed` |

`unreviewed` (rows the user has not decided) is a client-session concept and is not an engine field.

Other `summary` fields (`routes`, `miscAutoMigrations`, `miscAutoMigrationTotal`, `compilationAlbums`) are unchanged.

## blocked (MAX_PATH gate)

Top-level object, present only when the pre-move mirror path gate stopped the batch. It is written by `MusicIntegrator.AppendBlockedBlock` and carries no rows for the blocked batch: nothing moved, so `files` holds only rows decided before the gate ran. The gate runs before any duplicate decision or file move, in both dry and real runs, and the record is written even without `--json-output`. Absent on every other run.

| Field | Meaning |
|-------|---------|
| `status` | `path_too_long` |
| `action` | `rename_or_shorten` (the user renames or shortens the files named in `files`, then runs again) |
| `files` | one entry per offending planned destination |
| `files[].file` | the planned library destination path |
| `files[].projectedLength` | length of the staged XML temp path `<mirror>.stg\<rel>.xml.tmp` (see `Reflector.ProjectedStagingTempPath`) |
| `files[].limit` | the limit the length was compared against (`Reflector.OverlongPathLimit`, 259; a length at or over it blocks) |

The gate projects one path per planned destination that will route. A file decided as a duplicate delete (`D`) leaves NewMusic and is not planned. The process exits with code 1 after a blocked run.

## replacedOriginals

Top-level array, present only on an execution record (`recordType` `execution`) in which at least one L (replace library copy) decision retired a library original. It is written by `MusicIntegrator.AppendReplacedOriginalsBlock` from `ReplaceGuard.Retired`, and is absent on every other run, including dry runs and real runs with no replacement.

| Field | Meaning |
|-------|---------|
| `original` | the library path that was replaced |
| `held` | where the original now lives, under `NewMusic-Quarantine\Replaced\<yyyyMMdd-HHmmss>\` (the `Constants.ReplacedHoldingPath` folder) |

Held originals are never auto-pruned. To restore one, move the `held` file back to its `original` path. A replacement that fails and is rolled back restores the original in place and records nothing here.

## artistSpellingCandidates

Top-level array, present on every routing and execution record and empty (`[]`) when no artist has two or more spellings. It is written by `MusicIntegrator.AppendArtistSpellingCandidatesBlock` from `ArtistSpellingCandidates.BuildSpellingCandidates`, over the batch tags and the Misc XML primaries that scan-ahead reads. It is a suggestion only: no tag is changed by it.

| Field | Meaning |
|-------|---------|
| `key` | the folder-sanitised dedupe key for the artist (ordinal-first spelling) |
| `suggested` | the spelling the folder would use by default (most frequent, then ordinal) |
| `spellings[]` | one entry per exact spelling: `spelling`, `count`, and `tracks` |
| `spellings[].tracks` | batch files as paths relative to NewMusic; Misc songs as their Misc XML file name |

The target library's own tracks are not scanned for this block.

### Choosing a spelling: `--manifest` object form

The manifest may be the bare array (unchanged, no spelling choices) or an object: `{"tracks": [ ...same entries as the bare array... ], "artistSpellings": [{"key": "<candidate key>", "spelling": "Charli XCX"}]}`. `key` is a candidate's `key`; `spelling` is the exact text David chose, which need not be one of the observed spellings but must reduce to the same key (case and stripped symbols), otherwise the choice is ignored with a warning and nothing changes. A duplicate key keeps the first choice.

A chosen spelling is the only way a tag is changed for spelling reasons. It is written to batch files only, through the normal tag fix path (backup, journal, revert), overrides `keep=artist` for that artist, and makes the artist folder follow it. Misc songs that migrate and library tracks are not retagged, so any that carry another spelling stay LibChecker hits. With no object form, or no choice for an artist, no tag is changed. David's words: "i choose name for example for charli i want the captilised form!!! XCX".

## Preview stamp: `newMusicFingerprint`, `previewStale`

A dry-run routing record carries top-level `newMusicFingerprint`: SHA-256 over every mp3 under NewMusic (relative path, size, last-write ticks), taken before Tag Fix runs. A real run's execution record carries `previewFingerprint` (the stamp of the newest `routing-*.json`, or null when there is none) and `previewStale`: `false` when NewMusic still matches that preview, `true` when it changed since (the run also prints a `[WARN] preview_stale` line), `null` when no stamped preview exists. A client that shows a reviewed preview should treat `previewStale: true` as "the decisions were made on a different folder".

Read-only probe: `AudioManager.exe --fingerprint` prints one JSON line `{"newMusicFingerprint": "<hex>"}` for the current NewMusic folder (same algorithm as the dry-run stamp), writes nothing and moves nothing. Tag Fix changes mtimes, so a stamp taken before `tagfix --apply-plan` no longer matches after it; take a new dry run (or re-run `--fingerprint`) after applying.
