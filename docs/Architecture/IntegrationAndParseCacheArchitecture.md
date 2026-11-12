# Integration and ParseCache Architecture

Split from `DevContext.md` (doc maintenance pass). Stage 3 integration architecture and ParseCache detail, moved verbatim.

<!-- md-block: engine-assessment -->
## Engine assessment (2026-11-12)

A ranked read of where the structure helps and where it fights safe change. Ranks: high blocks safe integration or future change; medium costs effort or confidence but has a workaround; low is tidiness.

### Big picture

The engine is one exe with three verbs (`analysis`, `integrate`, `tagfix`) plus single-purpose flags that run before verb parsing (`--mirror-retry`, `--fingerprint`, `--libfix-plan|apply|revert`, `--misc-revert`). Modules sit under `Doer/`: the mirror chain (`Reflector` builds into staging and swaps, `Parser` with `ParseCache`, `Analyser`, `LibChecker`, `AudioMirrorCommitter`), the integrate chain (`TagFixer`, `TagFixApplier`, `IntegrationManifest`, `MusicIntegrator`, `ReplaceGuard`, `RecycleBin`), library fixes (`LibraryFixer`), and shared helpers (`RulesEngine`, `JsonText`, `AtomicFile`). The only seam to consumers is data: the JSON files in `docs/Contracts/`, pinned by `tests/golden/` through `GoldenContractTests`, plus the console line formats pinned by `MusicIntegratorConsoleFormatTests`.

### Ranked items

| Rank | Item | Why this rank | State |
|---|---|---|---|
| High | `MusicIntegrator.cs` is about 3,700 lines holding scanning, scan-ahead, duplicate review and deletion, routing, Misc migration and revert, cleanup, JSON emission, the confidence report and the projected LibChecker, sharing state through fields. Longest members: `RunScanAhead` 200 lines, the constructor 177, `GetDestDir` 172, `ExecuteDuplicateDecisions` 169, `RouteAllFiles` 146. | Every change to the irreversible path lands in one file whose fields cross every responsibility, so review cost and regression risk grow with each fix. | Waits: the extraction plan in `MusicIntegratorDecompositionDesign.md` needs the owner's approval before code moves (section 7 there). Figures above refresh that doc's section 3. |
| High | Paths are process-wide statics. `Constants.NewMusicPath` and the test root are read once, and helpers such as `RelToNewMusic` ignore the root their caller already holds (`PreflightMirrorGate` takes `newMusicRoot` but matches the manifest against the constant). | A full integrate run cannot be pointed at a per-test fixture root, so the manifest keep set, scan-ahead spelling collection and other end-to-end paths are verified by reading, not by tests. | Partly fixed: `RelToNewMusic` has a root overload and `PreflightMirrorGate` passes its own root, pinned by a temp-root gate test. A per-run injectable root (so the manifest keep set taken before preflight can be tested end to end) is still open. |
| High | Tag fix revert skipped journal entries marked `failed` without a note, and backup pruning kept only journals with `pending` entries. A `failed` entry can have changed bytes (the tag write ran, a later step threw). | Silent skip plus later backup deletion is the one path where a NewMusic file, which has no other backup, could be left changed with no record. | Fixed: revert reports a `failed` entry whose bytes differ from its backup, and pruning keeps those backups. |
| Medium | Argument handling is spread through `Main`: the pre-verb flags are matched anywhere in `args`, so `integrate --dry-run --mirror-retry` runs the mirror retry (which can auto-commit the mirror) instead of a dry run; unknown-flag checks run only on the verb path. | Surprising, not dangerous in normal use; a single parsed options object would make every combination explicit. | Open. Rejecting such combinations tightens the CLI surface, so it needs the owner's sign-off. |
| Medium | `IntegrationManifest.Parse`, which decides which files may move, is a hand-written tokenizer. | It guards the irreversible step; a real JSON parser removes a class of edge cases. | Open, owner-reviewed change on the guarded path. |
| Medium | README CLI section lists three verbs and two flags; the exe accepts fourteen flags with strict rules (bare tokens rejected, value-taking flags, no `--dry-run` with `--apply-plan` or `--revert`). | The public face of the repo under-describes the product, and the strict rules live only in code. | Fixed: README lists every verb and flag; `DevContext.md` has a CLI Argument Rules section. |
| Medium | Dry run checks a stale `.replaced.bak` with a plain file check, while the real `ReplaceGuard.Stage` first resolves leftovers and may clear it. | The dry run can warn where the real run would succeed; it never reports clean where the real run fails, so it errs safe. | Open. |
| Low | `AudioMirrorCommitter.GetTrackedArtists` ignores the git exit code. | A git failure gives a wrong artist list in the commit text, never a wrong commit. | Open. |
| Low | `MusicIntegrator.JStr` is a private copy of `JsonText.Quote`. | Identical today; two escapers can drift. | Open; folds into the JSON writer extraction. |
| Low | `CLAUDE.md` says the repo holds no Python, but the golden generator and checker under `scripts/dev/golden/` are Python. | Doc says one thing, tree another. | Fixed: `CLAUDE.md` names the dev-only golden scripts. |
| Low | Tag fix apply stamps runs to the second. | A run lock already prevents two applies at once, so a clash needs two runs in the same second in sequence. | Open. |

### Resolved since the 2026-09-06 review

Checked in code on 2026-11-12: dry run and real run share `IsDestBlocked` and count replacements the same way; mirror regen builds into staging and swaps, so the live mirror is never deleted first; Misc migration moves are per-file; the duplicate delete path re-checks the library copy and survives a throw; replaced library copies are retired to a holding folder, not deleted; the pre-integration git check fails closed; the commit result is classified failure-first; a fresh clone restores taglib-sharp through `scripts\dev\build.bat`; JSON escaping is shared through `JsonText` everywhere except the copy noted above.
<!-- /md-block: engine-assessment -->

---

## Stage 3 Integration Architecture

**User workflow:** This section explains how it works for developers.

**The pipeline has two conceptually separate concerns:**

1. **TagFixer (tag cleanup phase):** Reads raw NewMusic files, applies automatic corrections:
   - Tag cleanup rules (titles, featured artists, file names, compilation flag, genre) live in [Rules/MusicLibraryRules.md](../Rules/MusicLibraryRules.md)

2. **Integration (routing phase):** Routes cleaned files to library destinations:
   - Applies rules from Rules/MusicLibraryRules.md (Artists folder, Compilations, Musivation, Motivation, Sources, Miscellaneous)
   - All console output captured to `data/logs/run-YYYYMMDD-HHmmss.log` with `[HH:mm:ss]` timestamps (via TeeWriter in Program.cs)
   - Dry-run mode: previews all fixes + routing without moving files
   - Post-integration: auto-runs LibChecker to validate library clean

**Design rationale:** Separating tag-fixing from routing makes integration testable and reversible. TagFixer produces clean, ready-to-route files. Integration purely routes. Audit trail documents every decision. If routing fails, it's a routing bug, not a tag issue.

---

## ParseCache Architecture

Three-layer cache system. Understanding this prevents confusion when diagnosing stale analysis output.

```
MP3 files (ground truth - <home>\Audio\)
    ↓ Reflector refreshes (force regen = full rebuild; incremental = new files + stale XMLs refreshed)
AudioMirror XMLs (Layer 1 - metadata snapshot, mtime updated on write)
    ↓ Parser reads
ParseCache data/logs/parse-cache.txt (Layer 2 - parsed TrackTag objects, flat pipe-delimited file)
```

**`IsMirrorStale(mirrorPath, cacheTime)` returns true (cache miss) when:**
- No XML files found - mirror is empty (Reflector crashed after `Directory.Delete`, before recreating)
- Any XML `LastWriteTime > cacheTime` - new song added or force regen ran

Any filesystem error in the mtime check also degrades to a miss (never crashes).

**Reflector incremental behavior (Layer 1):**
- **Detects tag edits to existing MP3s (fixed 2026-06-02):** `IsStaleMirrorXml(mp3Path, xmlPath)` compares `MP3.LastWriteTimeUtc` vs `XML.LastWriteTimeUtc`. If MP3 is newer, overwrites XML with real path, triggering a fresh tag read on that analysis run. Counter shown in output only when > 0 (no noise on normal runs).
- **Does NOT remove XMLs for deleted MP3s:** Reflector only iterates real files and creates; never deletes orphaned XMLs. Force regen fully handles this (deletes mirror entirely, rebuilds). Won't-fix for incremental mode - the expected workflow for library deletions is always followed by force regen.

ParseCache inherits the second limitation - a deleted MP3's cached data persists until force regen. Stale XML entries (first limitation) no longer affect ParseCache after the 2026-06-02 fix.

**Force regen removes all staleness:** deletes mirror entirely, rebuilds all XMLs from MP3 reads, then Parser saves a fresh cache.

**Validation-after-deletion hazard (the ghost-XML trap):** the add-only nature of incremental regen is not just an analysis concern - integration itself deletes (`[L]` duplicate decisions) and moves (Misc migration) library files, then post-integration validation regenerates incrementally. The vacated locations keep their XMLs, so LibChecker validates a mirror that describes a library state that no longer exists and flags the ghosts as duplicates / misplaced songs. Fast tell: post-integration regen prints `Tags parsed > MP3 file count` - the difference is the ghost count (2026-06-28: 5702 parsed vs 5693 on disk = 9 ghosts = 9 vacated locations). Before treating any post-integration LibChecker warning as real, force-regen and re-check; warnings at a deleted/moved-from path are almost always ghosts. The durable fix is to make incremental regen prune orphaned XMLs (IDEAS TIER 1). Full analysis: `docs/Rules/PostIntegrationValidation.md`.

**Projection pattern for pre-move validation:** LibChecker consumes `List<TrackTag>`, never MP3 files - so the post-integration library state can be validated in memory during the dry run, before anything moves. Build a projected tag list: current parsed tags, minus the TrackTags the integrator will delete or move away from, plus synthesized destination-path TrackTags carrying the post-TagFixer values (the integrator already holds these). Running LibChecker on the projection predicts real issues with zero ghost false positives, because deletions are modelled by construction. This is the preferred validation seam (IDEAS TIER 1) - any new check that needs "what will the library look like after this batch?" should build a projected `List<TrackTag>` rather than inspecting the live mirror after the fact.
