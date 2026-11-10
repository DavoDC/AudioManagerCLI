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
