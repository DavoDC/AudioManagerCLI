# Analysis JSON Format (`analysis --json-output`)

The data contract for external consumers. `AudioManager.exe analysis --json-output` writes **two** files to `data/logs/`
(fixed filenames, latest run wins):

| File | Source | Contains | Typical consumer view |
|---|---|---|---|
| `analysis-stats.json` | `Code/Doer/Analyser/StatsJson.cs` | aggregate library statistics | a statistics view |
| `tracks.json` | `Code/Doer/Analyser/TracksJson.cs` | the full per-track array | a per-track browser |

**Why both exist / the hard rule they enforce:** external consumers must stay **100% decoupled from the raw
AudioMirror XML**. C# owns all data ingestion - it is the only XML parser. `StatsJson` reuses the exact
`StatList` primitives the text report uses (so stats can't drift from `AudioReport.md`), and `TracksJson`
emits every per-track row a browser needs (so consumers never glob or parse XML to list tracks).
**Do NOT reimplement distributions in consumers. Do NOT parse `AudioReport.md`. Do NOT parse AudioMirror XML
in consumers.** Consume these two files.

---

## `analysis-stats.json` (aggregate statistics)

**Versioning:** `schemaVersion` starts at 1. It is bumped only when a field is removed or its meaning
changes (additive fields do not bump it). A consumer should read `schemaVersion` and refuse a mismatch it
was not built against, rather than silently mis-reading.

## Shape

```jsonc
{
  "schemaVersion": 1,
  "generatedAt": "2026-07-03T01:00:56",     // ISO-8601 local time, no offset

  "summary": {
    "trackCount": 5694,
    "artistCount": 1944,                     // distinct artists (multi-artist tracks split on ; and ,)
    "genreCount": 53,                        // distinct genres (same splitting)
    "totalLibraryBytes": 30558669644,        // real on-disk sum of *.mp3 (0 if the Audio folder is unreadable)
    "avgFileBytes": 5366819,                 // totalLibraryBytes / trackCount
    "totalPlaybackSeconds": 1299353.903,
    "totalPlaybackHours": 360.9,             // rounded to 1 dp
    "avgSongLengthSeconds": 228,
    "medianSongLengthSeconds": 211
  },

  // Each distribution is an array of {label, count}, sorted DESCENDING by count.
  // A track with multiple genres/artists is counted once per genre/artist (matches the report).
  "genreDistribution":  [ {"label": "Hip Hop", "count": 2669}, ... ],   // all distinct genres
  "decadeDistribution": [ {"label": "2020s",   "count": 2188}, ... ],   // all decades
  "yearDistribution":   [ {"label": "2022",    "count": 515},  ... ],   // all years present

  "topArtists": {
    "exclMusivation": [ {"label": "Eminem",        "count": 155}, ... ],  // every artist, Musivation-genre tracks removed
    "all":            [ {"label": "Akira The Don", "count": 510}, ... ]   // every artist
  },

  "ageStats": {                              // years; null if no track has a parseable Year
    "averageYears": 11.6,
    "medianYears": 9,
    "newestYears": 0,                        // age of the newest track (currentYear - maxYear)
    "oldestYears": 92
  },
  "ageDistribution": [                        // fixed 5 buckets, always present in this order
    {"label": "0-2y",   "count": 580},        // age <= 2
    {"label": "2-5y",   "count": 1194},       // 2 < age <= 5
    {"label": "5-10y",  "count": 1364},       // 5 < age <= 10
    {"label": "10-20y", "count": 1777},       // 10 < age <= 20
    {"label": "20y+",   "count": 779}         // age > 20
  ],

  "coverArt": {
    "total": 5694,
    "hasKnownDims": 5694,                     // cover present and dimensions parsed
    "noCover": 0,                            // CoverWidth == "0"
    "missingData": 0,                        // CoverWidth empty (pre-force-regen XMLs)
    "unknownFormat": 0,                      // CoverWidth == "Unknown" (art present, format unreadable)
    "subMin800": 247,                        // known dims with min(w,h) < 800
    "nonSquare": 97,                         // known dims with w != h
    "dimensionHistogram": [ {"label": "800x800", "count": 3080}, ... ]  // top 15 "WxH" buckets, desc
  },

  "tagCompleteness": {                        // % of tracks with Title+Artist+Album+Genre+Year all present
    "complete": 5334,                        // "present" = non-blank and not the literal "Missing"
    "total": 5694,
    "percent": 93.7
  },
  "coverCoverage800": {                        // % of ALL tracks whose cover is >= 800px on the short side
    "covered": 5447,                         // known dims with min(w,h) >= 800
    "total": 5694,
    "percent": 95.7
  },
  "lyricsCoverage": {                          // added (additive, schemaVersion stays 1); numbers here are illustrative
    "total": 5694,
    "hasLyrics": 4100,                       // HasLyrics == "True" in the mirror XML (lyrics tag present)
    "noneFound": 120,                        // not hasLyrics, and in the kept list (config/lyrics-status.json) as none_found
    "instrumental": 45,                      // not hasLyrics, and in the kept list as instrumental
    "missing": 1429,                         // everything else, incl. HasLyrics unknown (old XML) with no kept row
    "percentHasLyrics": 72                   // hasLyrics / total, one decimal
  }
}
```

## Consumption notes for consumers

- **Read this file; do not shell the exe per panel.** A consumer runs `analysis --json-output` once at
  startup / on manual refresh, then reads `data/logs/analysis-stats.json`. All statistics panels and stat
  tiles map 1:1 to a field above.
- **Per-track rows** (a library table) are NOT in this file - it is aggregate stats only.
  Per-track data comes from globbing the AudioMirror XML directly (see `Contracts/AudioMirrorFormat.md`).
- **`totalLibraryBytes` is real**, computed from a single disk walk inside the exe. This removes the
  earlier plan to fake file size from bitrate in Python - the accurate number is now free.
- **Numbers are culture-invariant** (always `.` decimal), safe to `JSON.parse` / `json.loads` regardless
  of the machine locale. There is a regression test guarding this (`Build_UsesInvariantDecimalUnderCommaCulture`).
- **Missing/degenerate data:** on an empty library, distributions are `[]`, `ageStats` fields are `null`,
  and percents are `0`. A consumer should render empty states, not crash.

---

## As-of statistics (`analysis --as-of`)

`AudioManager.exe analysis --as-of <YYYY-MM-DD|commit-sha> --json-output` writes the same `analysis-stats.json` shape for the library as it was at an AudioMirror commit. A date means the last commit on or before that day at 23:59:59; a sha is used as given. `analysis --as-of-list` prints the commits (date, sha8, subject).

The file goes to `data/history/stats-<commit date>-<sha8>.json`. It never touches `data/logs/`, never regenerates the mirror and never commits. The XMLs are read from `git archive` of the commit into a temp folder, so the AudioMirror working tree is not touched.

As-of documents add these fields, all absent from live documents. `schemaVersion` stays 1 because they are additive.

- `asOf`: `{"date": "YYYY-MM-DD", "commit": "<full sha>"}`.
- `sizeKnown`: `true` only when every XML at that commit carries `FileBytes`; `totalLibraryBytes` is then the real sum and `avgFileBytes` follows from it. Otherwise `false`, both are `0` and must not be read as real values (commits from before `FileBytes` existed).
- `coverKnown`: `false` when any XML at that commit lacks the `AlbumCover` block. Older XMLs carry only `AlbumCoverCount`, so their width and height are empty and count as `missingData`.
- `lyricsKnown`: `false` when any XML lacks `HasLyrics`. Those tracks count as lyrics missing unless the kept list says otherwise.

## Report import (`analysis --import-report`)

`AudioManager.exe analysis --import-report <AudioReport.txt>` turns one old text report into `data/history/stats-<date>-report.json`. The date is the report's filename prefix (`YYYY-MM-DD`), and a rerun overwrites the same file. It never reads the library or the mirror and never writes `data/logs/`.

An old report prints only tables, so an imported snapshot is partial. Real values: `summary.trackCount` (from the `Tags parsed` line), the genre, year and decade distributions, `topArtists.all` (the Artists table), and `topArtists.exclMusivation` (the Excluding Musivation table, present only in the newest layout; otherwise an empty array). A layout without a decade table gives an empty `decadeDistribution`.

`summary.artistCount` and `summary.genreCount` are the row counts of the printed tables. The old reports cut those tables short, so both are lower bounds, not the library's distinct counts.

Everything else is zero, null or an empty array and must not be read as a measurement: the size, length and playback fields, `ageStats` (null), `ageDistribution` (zero buckets), `coverArt`, `tagCompleteness`, `coverCoverage800` and `lyricsCoverage`. The flags `sizeKnown`, `coverKnown`, `lyricsKnown` and `lengthKnown` are all `false`; `lengthKnown` appears only in report snapshots.

`asOf` is `{"date": "YYYY-MM-DD", "commit": "report"}`. `schemaVersion` stays 1 because the change is additive.

## `tracks.json` (per-track array for browsing)

One object per track, every track. ~4 MB for 5,694 tracks - loaded once at startup (and on refresh),
sliced/filtered in memory by the consumer. This file is why consumers never need to touch the XML.

```jsonc
{
  "schemaVersion": 1,
  "generatedAt": "2026-07-03T01:12:16",
  "trackCount": 5694,
  "tracks": [
    {
      "id": "\\Sources\\YouTube\\PlentaKill\\PlentaKill - Don't You Worry Bot.xml",  // mirror-relative path, unique + stable
      "title": "Don't You Worry Bot",
      "artists": "PlentaKill",              // full field, ; or , separated if multiple
      "primaryArtist": "PlentaKill",        // first artist
      "album": "...",
      "year": "2010",                       // string; "Missing" if absent
      "decade": "2010s",                    // derived; null if year unparseable
      "genres": "Games",                    // full field, "; " separated
      "primaryGenre": "Games",              // first genre
      "trackNumber": "1",
      "lengthSeconds": 213.5,               // null if unparseable
      "length": "3:33",                     // m:ss, "" if unknown
      "compilation": false,                 // boolean (from the "True"/"False" XML string)
      "coverWidth": 1200,                   // int, or null if no readable art
      "coverHeight": 1200,
      "hasArt": true,                       // cover present with parseable dimensions
      "hiResArt": true,                     // hasArt AND min(w,h) >= 800
      "addedDate": "2026-07-01",            // earlier of the XML DateAdded and the MP3 creation date (ISO date), or null
      "modifiedDate": "2026-11-27",         // MP3 last-write date (ISO date), or null
      "filePath": "<home>\\Audio\\Sources\\YouTube\\PlentaKill\\PlentaKill - Don't You Worry Bot.mp3"
    }
    // ... one per track
  ]
}
```

### Consumption notes

- **`filePath` is the real MP3 path**, reconstructed from the mirror path (the AudioMirror mirrors the
Audio folder 1:1). Use it with `mutagen` to extract embedded album art for display. Extract lazily (current page only) and cache thumbnails - do not read 5,694 covers at startup.
- **`filePath` is best-effort:** for the rare track whose filename was sanitised into a different XML
  name, the reconstructed path won't exist on disk. Treat a missing file as "no extractable art" and
  show a placeholder - never crash. (Verified 2026-07-03: a random sample of 20 + the first track all
  resolved to real MP3s; the exception is genuinely rare.)
- **`hasArt`/`hiResArt`** are the album-art *status* (a consumer doesn't need to open the file to know it) -
  use them to decide whether to attempt extraction and to badge low-res covers.
- **`id`** is the stable per-track key for selection/paging state; it is the mirror-relative `.xml` path
  (the source of truth), NOT the `.mp3` path.
- Numbers are culture-invariant, same as the stats file.

## Versioning both files

`StatsJson.SchemaVersion` and `TracksJson.SchemaVersion` are independent. A consumer should
check each file's `schemaVersion` against what it was built for and fail loudly on a mismatch rather than
silently mis-reading. Additive fields do not bump the version; removing a field or changing its meaning does.
