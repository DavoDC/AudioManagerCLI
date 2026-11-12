# AudioManagerCLI

[![ko-fi](https://ko-fi.com/img/githubbutton_sm.svg)](https://ko-fi.com/G2G31WKOCN)



A C# console application for managing, analysing, and auditing a personal music library - generates metadata statistics, validates organisation rules, and automatically integrates new music.

## What it does

- **Mirror** - creates a lightweight XML snapshot of the library metadata (see [AudioMirror](https://github.com/DavoDC/AudioMirror))
- **Analyse** - generates timestamped statistical reports, runs full library validation (LibChecker), and auto-commits AudioMirror if the library is clean
- **Integrate** - scans a staging folder for new MP3s, pre-processes tags, routes files automatically, and produces a confidence report

## Build and test

Windows, .NET Framework 4.8, MSBuild (Visual Studio Build Tools). From the repo root:

```
scripts\dev\build.bat
scripts\dev\verify.bat
```

`scripts/dev/verify.bat` builds, then runs the unit tests and the routing manifest. The exe lands in `src/AudioManager/bin/Release/AudioManager.exe`.

## CLI Usage

Run `AudioManager.exe <verb> [flags]`. With no arguments it opens the interactive menu.

| Verb | Aliases | What it does |
|------|---------|--------------|
| `analysis` | `analyse`, `analyze` | Rebuilds or updates the AudioMirror, runs the LibChecker validations, and auto-commits the mirror when clean. |
| `integrate` | `integration` | Runs the pre-integration gate, then moves files from NewMusic into the library. |
| `tagfix` | `fix-tags` | Cleans tags on files in NewMusic, and applies or reverts a reviewed tag-fix plan. |

| Flag | Takes a value | Works with | Effect |
|------|---------------|------------|--------|
| `--dry-run` | no | `integrate`, `tagfix` (rejected with `analysis`) | Prints every planned action and modifies no files. |
| `--force-regen` | no | `analysis` | Deletes and rebuilds the AudioMirror instead of updating it incrementally. |
| `--no-input` | no | `integrate` | Skips interactive prompts and accepts the recommended decisions. |
| `--json-output` | no | `analysis`, `integrate`, `tagfix` | Writes machine-readable JSON (see [docs/Contracts](docs/Contracts/)). Tagfix writes its plan JSON only with `--dry-run`. |
| `--no-auto-commit` | no | `analysis`, `integrate`, `--mirror-retry` | Skips the AudioMirror auto-commit. |
| `--manifest <path>` | yes, a manifest file | `integrate` | Integrates only the NewMusic files the manifest selects. |
| `--apply-plan` | no | `tagfix` (rejected with `--dry-run`) | Applies the reviewed tag-fix plan, with guards. |
| `--revert <journal>` | yes, a tagfix journal | `tagfix` (rejected with `--dry-run`) | Reverts a tag-fix apply from its journal. |
| `--mirror-retry` | no | standalone | Re-runs only the post-integration mirror step after a failed one. |
| `--fingerprint` | no | standalone | Prints one JSON line with the current NewMusic fingerprint. Read-only. |
| `--libfix-plan` | no | standalone | Writes the library fix plan for album_in_singles hits. Moves nothing. |
| `--libfix-apply <plan>` | yes, a plan file | standalone | Applies a library fix plan, journaling each move. |
| `--libfix-revert <journal>` | yes, a journal file | standalone | Reverts a library fix from its journal. |
| `--misc-revert <journal>` | yes, a Misc migration journal | standalone | Undoes a Misc migration from its journal. |

Standalone flags run and exit before any verb is read.

Always run with `--dry-run` first. It prints every planned action without modifying files.

Unknown or bare tokens are rejected and nothing runs. A standalone flag runs first, so it ignores other tokens on the same line.

## LibChecker Validations

Ensures the library conforms to organization rules:
- **Filename matching** - all artists from tag appear in filename
- **Tag completeness** - Title, Artists, Album, Year present on all tracks
- **Unwanted strings** - flags leftover credit and version text in tags
- **Album cover** - exactly 1 cover per track (APIC frame)
- **Compilation flag** - set consistently on all tracks
- **Duplicates** - no artist+title duplicates (detects re-integrated songs)
- **Artist folder structure** - artists with enough songs have dedicated folders
- **Album subfolders** - album and Singles layout is checked
- **Misc folder review** - warns if Misc folder has grown too large
- **Compilations validation** - tracks in Compilations/ whose artist has an Artists/ folder are flagged as misrouted, unless the album is a genuine various-artist compilation (3+ distinct primary artists in that album)
- **Genre consistency** - Musivation/Motivation folders have matching genre tag
- **Sources validation** - soundtrack tracks properly named and organized

Run: `AudioManager.exe analysis` (includes LibChecker in the pipeline)

## Statistics Provided

- Total playback hours, library size on disk, average file size
- Average and median song length and track age
- Percentage and count breakdown of: artists, genres, release years, decades
- Cover art quality: coverage count, non-square cover detection, dimension histogram (top 10 dimension pairs)

For listening statistics, visit [LastFM](https://www.last.fm/user/david369music).

## Integration Pipeline

**Automated end-to-end workflow:** drop new MP3s in `Downloads/NewMusic`, run the three-step sequence below:

### Step 1: Tag Fixing (TagFixer)
Cleans raw MP3s (titles, artist tags, file names) before integration. The exact rules are in [MusicLibraryRules.md](docs/Rules/MusicLibraryRules.md).

Run: `AudioManager.exe tagfix [--dry-run]`

### Step 2: Integration (MusicIntegrator)
Routes cleaned files into the library by metadata, with an optional folder picker for media tracks. Routing rules: [MusicLibraryRules.md](docs/Rules/MusicLibraryRules.md).

Run: `AudioManager.exe integrate [--dry-run]`

### Step 3: Analysis & Validation (Analyser + LibChecker)
- Regenerates AudioMirror XML with newly integrated tracks
- Runs full library validation (LibChecker), generates timestamped report
- Auto-commits AudioMirror if library is clean

Auto-runs after step 2, or manually: `AudioManager.exe analysis`

**No manual MP3tag editing needed.** Always use `--dry-run` first to preview changes.

## Project Structure

The C# console app lives in `src/`, and launchers live in `scripts/`. The full annotated tree is in [DevContext.md, Project Structure](docs/DevContext.md#project-structure).

## Performance

Analysis includes an incremental parse cache. On repeated runs where the library has not changed, the Parser reads a single flat cache file instead of thousands of XML files, cutting total analysis time from ~110s to ~4s on a 5600-track library.

Cache is stored in `data/logs/` (gitignored). It is invalidated automatically whenever the AudioMirror gains new or modified XML files (e.g. after integration or force regen).

Incremental Reflector also refreshes individual XMLs when their underlying MP3 has been modified since last analysis (e.g. after a tag edit in Mp3tag), so fresh tags appear without needing a full force regen.

## Tech

- **Language:** C# (.NET Framework 4.8)
- **Metadata:** [TagLib#](https://github.com/mono/taglib-sharp) for ID3 tag reading and writing
- **Architecture:** `Doer` base class - every operation is timed automatically
- **Output:** `TeeWriter` captures console output to both screen and file simultaneously
- **Parse cache:** `ParseCache` - mtime-based flat-file cache for incremental analysis

## Development

**Developed:** November 2023 · **Status:** Actively maintained

First build restores NuGet packages (taglib-sharp) and needs network access.

## Dependencies

- [TagLib#](https://github.com/mono/taglib-sharp)

## Related

- **[AudioMirror](https://github.com/DavoDC/AudioMirror)** - XML mirror of the music library, auto-generated and auto-committed by this tool
