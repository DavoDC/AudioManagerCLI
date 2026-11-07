# AudioManagerCLI - Claude Context

For implementation invariants, architecture detail, and code patterns: read `docs/References/DevContext.md`.

## What it does

C# console app (CLI) for managing a personal music library. Two modes:
- **Analysis** - full pipeline: regenerate AudioMirror XML (Reflector), parse metadata (Parser), generate stats report (Analyser), validate library (LibChecker), save report (ReportWriter), auto-commit AudioMirror if clean (AudioMirrorCommitter)
- **Integrate** - scans `Downloads/NewMusic/`, fixes tags (TCMP, genres, parentheticals, featured artists), routes files into the library, logs routing decisions to XML for audit trail

This repo is the C# engine only. It contains no Python, no UI code, and no streaming-service code. Its JSON outputs (`data/logs/tracks.json`, `data/logs/analysis-stats.json`, `data/logs/routing-<timestamp>.json`) and `config/tagfix-custom-rules.xml` are a shared contract with external consumers, so changing their shape needs those consumers updated too.

## Tech Stack

- C# (.NET Framework 4.8)
- TagLib# for ID3 tag reading/writing
- MSBuild for compilation (no Visual Studio needed)

## Project Structure

See "Project Structure" in `docs/References/DevContext.md` for the full tree.

## Build and Run

### Claude: Building the Program

**Always use PowerShell (never Bash) for .bat files, full absolute path, `--no-pause` flag** (no cd needed - build.bat uses `%~dp0` internally):
```powershell
& "<home>\GitHubRepos\AudioManagerCLI\scripts\dev\build.bat" --no-pause
```
Completes in ~2-3 seconds without blocking. Success output example: "Build success/failure" in `docs/References/DevContext.md`.

### Claude: Running Tests (MANDATORY after any C# code change)

**After every C# code change, run tests before committing** - fast (< 1 second), catches tag logic regressions. Files that trigger this: anything in `src/engine/AudioManager/Code/`, especially TagFixer.cs, Track/Track.cs, Constants.cs, src/engine/AudioManager.Tests/.

```powershell
& "<home>\GitHubRepos\AudioManagerCLI\scripts\dev\verify.bat" --no-pause
```

Runs build + unit tests + routing manifest tests. All must pass before any C# commit. All bats support `--no-pause` (always pass it from Claude). If a test fails: fix the code, not the test (unless the test is wrong - state why).

Legacy csproj format, csproj file registration, the `Any CPU`-only platform constraint, and which MSBuild to invoke directly: see "Build troubleshooting" in `docs/References/DevContext.md`.

### Development priority


## Key Paths (from Constants.cs)

- Audio library: `<home>\Audio\`
- New music staging: `<home>\Downloads\NewMusic\`
- Reports output: `<repo-root>\data\reports\` (written by ReportWriter)
- Mirror repo: sits next to this repo at `..\AudioMirror\`

## Data Safety - HIGHEST PRIORITY

**The music library and NewMusic inbox must NEVER experience data loss.**

- The library at `<home>\Audio\` is the primary copy and is not frequently backed up
- The NewMusic inbox at `<home>\Downloads\NewMusic\` is also not backed up
- Before ANY file operation (move, rename, delete, overwrite): verify the operation is safe and reversible
- Prefer dry-run mode first - always test against a small sample before running on the real library
- Never delete source files without confirming the destination write succeeded
- When in doubt, do nothing and ask

## Display Conventions

Label "In AudioMirror" for XML paths and "In library" for MP3 paths, never mixed: "Display Conventions" in `docs/References/DevContext.md`.

## Library Routing Rules

**Before touching GetDestDir or any routing code**, read `docs/References/rules/MusicLibraryRules.md` and see "Library Routing Rules" in `docs/References/DevContext.md` - subfolder-before-song and scan-ahead invariants. Violating them causes files to land in wrong locations.

## Workflow Note

Integration is one big batch every 2-4 weeks: design reports for batch-shaped data, not daily drips ("Integration Cadence" in `docs/References/DevContext.md`).

For LibChecker-warning triage, post-integration validation mechanics, AudioMirror commit/regen policy, and audit-metadata rationale, see `docs/References/DevContext.md`.

## Critical Safety Rule

**Only the user (David) runs real integration.** Real integration moves files from NewMusic into the library - the user must trigger it themselves (CLI `integrate`, or an external consumer's integration step) for data safety and auditability.

**Claude CAN run (read-only, no file moves):**
- `analysis` (and `analysis --force-regen`) - reads library, generates report, no writes except AudioMirror XML regen
- `integrate --dry-run` - previews routing decisions without touching any files

**Always add `--no-auto-commit` when Claude runs force-regen.** AudioMirrorCommitter fires automatically and commits before the diff can be reviewed. Use:
```
& "...\AudioManager.exe" analysis --force-regen --no-input --no-auto-commit
```
After the run: check `git -C AudioMirror diff HEAD`, confirm the diff looks correct, then commit AudioMirror manually.

**Claude CANNOT run:**
- `integrate` (real) - moves files, irreversible

**Claude dev workflow for verifying fixes:** build, then run `integrate --dry-run --no-input` (skips interactive prompts) to see real output without blocking - see "Claude dev verify workflow" in `docs/References/DevContext.md` for the exact commands.

## Tag Fixer Constraint

**TagFixer MUST ONLY operate on NewMusic folder.** Never on the Audio library.

TagFixer modifies ID3 tags and renames files to match library convention. Tag changes on library files are high-risk: changes propagate to many files, are hard to audit, and difficult to reverse.

**Rule:** Never refactor this to accept a `folderPath` parameter or add a library mode. If you need to fix tags in the library, implement it as a separate, read-only analysis tool first, then ask David before touching any files.

**Library Operations Constraint:** what the program can/cannot do to files already in the Audio library - see "Library Operations Constraint" in `docs/References/DevContext.md`.

## Language decision

Decided 2026-09-06, do not relitigate: the engine stays C#, and no external consumer (in any language) ever writes to the library, reimplements C# logic, or parses AudioMirror XML. Reasoning: `docs/References/architecture/ArchitectureLanguageDecision.md`.

## What the Test Suite Can and Cannot Prove

See "What the test suite can and cannot prove" in `docs/References/DevContext.md` - a green `scripts/dev/verify.bat` proves the logic, never that a real integration batch is safe to fire.

## Repo boundary - keep the split clean

This repo is C# only and complete on its own through the CLI; it is a public showcase.

- Never add here: Python, UI code, UI design docs, launchers, or config for other apps. Docs here describe the engine alone.
- The only seam is data: this exe writes `data/logs/tracks.json`, `data/logs/analysis-stats.json` and `data/logs/routing-<timestamp>.json` and reads `config/tagfix-custom-rules.xml`. Changing the shape of any of them means updating every external consumer in the same working session.
- If an external consumer needs a new engine capability, add a CLI flag or JSON field here and document it as seam. Never add consumer-specific code paths.
- Engine content (docs, tests, fixtures, build scripts) stays here. On every cleanup commit review `git diff --stat` for deletions and for moves out of the engine.
- Public repo: no private machine paths, no other people's names, no references to other repos or local folders.
