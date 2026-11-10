# Routing JSON Format (`integrate --json-output`)

The routing record written to `logs/routing-<timestamp>.json` by an integrate run (dry run or real). `recordType` is `routing`, `schemaVersion` 1. Row fields are produced in `MusicIntegrator.BuildJson`.

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
