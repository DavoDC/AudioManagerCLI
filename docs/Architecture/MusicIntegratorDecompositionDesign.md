# MusicIntegrator Decomposition: Design

Status: APPROVED by the owner 2026-11-12, design chosen by Claude. No code has moved yet; section 7 is the build order.

Date: first proposal 2026-10-12, rewritten as the approved plan 2026-11-12. Source: `src/AudioManager/Code/Doer/MusicIntegrator.cs`, 3,704 lines as of 2026-11-12. Line numbers below are as of 2026-11-12 and will drift; member names are the durable reference. The ranked engine assessment in `IntegrationAndParseCacheArchitecture.md` lists the same class as its top size item.

## 1. Why

`MusicIntegrator` is the one class that performs the irreversible workflow (it moves files from NewMusic into the library), and it is the largest and least decomposed type in the program. Batch scanning, scan-ahead promotion, duplicate detection and resolution, routing, the MAX_PATH gates, artist-folder case renames, Misc auto-migration and its revert, NewMusic cleanup, routing-JSON and execution-record emission, the confidence report and the projected LibChecker all live in it, sharing about 25 mutable fields. Five of its six constructors exist only so tests can reach internal logic (one of them, `MusicIntegrator(string)`, is also used by `--misc-revert` in `Program.cs`).

The aim is smaller units that can each be tested alone, with the file-moving core thin and easy to review. The hard constraint is unchanged behaviour: the same files go to the same places, with the same console lines, the same JSON and the same exit codes. Cleaner code, easier tests and the ability to point a run at another folder tree are the gains; none of them may cost a behaviour change.

## 2. Ground rules

1. Characterisation first. Before a member moves, a test that passes on the current code pins its behaviour. An existing test is the pin where one exists (named in section 7); a missing case is added in an earlier commit than the move.
2. Verbatim moves. A move changes where code lives and how it is reached, never what it does. A moved member body is byte-identical apart from the seam edits listed in section 5, and the move commit contains nothing else. Behaviour-neutral refactors (new types, renamed fields, access-modifier changes) are their own commits.
3. One class or one seam per commit, each commit green on `scripts\dev\verify.bat --no-pause` (build, unit tests, routing manifest).
4. Every commit is also gated by the dry-run parity harness (section 6): the same `integrate --dry-run --no-input` run, with and without `--json-output`, against generated fixture roots, by the exe built before the change and the exe built after it. Console output, routing JSON and exit code must be identical after normalising the timestamp and the sandbox root. The harness never touches the real library or NewMusic, and nothing in this plan runs a real (non dry-run) integrate; only David runs real integration.
5. The guarded path stays guarded: nothing here changes the exe arguments external consumers build, the `[AUTO]`, `[SKIP]` and execution-record line formats (pinned by `MusicIntegratorConsoleFormatTests`), the goldens in `tests/golden/` (pinned by `GoldenContractTests`), or the JSON contract versions.
6. New non-test source files need a `<Compile Include>` in `src/AudioManager/AudioManager.csproj`; new test files need no registration. New classes are `internal`; `InternalsVisibleTo("AudioManager.Tests")` in `src/AudioManager/Properties/AssemblyInfo.cs` already lets tests reach them.
7. Stop condition: if a behaviour cannot be preserved verbatim across a seam, stop, record it as an open item in section 8, and leave the code where it is. Never fix a suspected bug inside a move.

## 3. Target structure

`MusicIntegrator` stays the facade and orchestrator: the public constructor still runs the whole pipeline in the same order, `Blocked` keeps its meaning, it still derives from `Doer`, and `Program.cs` does not change. It keeps the constructor sequence, `PreflightMirrorGate`, `RunMirrorPathGate`, `PrintRoutingResultsAndFinish`, `WriteJsonOutput`, `WriteExecutionRecord`, `BuildCurrentSummary`, `QuietConsole` and the run outcome fields (log entries, counts, `Blocked`, fingerprints, `_declinedCount`). Everything else moves into classes that each own one cluster of state or one job, chosen from the field usage of the current code rather than from its section headings.

New per-run state types (no behaviour of their own beyond what moves into them):

| Type | Holds | Why it exists |
|---|---|---|
| `IntegrationRoots` | NewMusic, Library, Mirror and Logs folders; `FromConstants()` | The injectable per-run path roots (section 4). |
| `ScanAheadResult` | batch, Misc and Sources counts, batch album counts, spellings, spelling candidates, compilation albums, batch tag cache | The one hand-off from scan-ahead to its several readers. Today `RunScanAhead` assigns eight fields (as of 2026-11-12) that `GetDestDir`, `PreScanFiles`, the gates, the JSON writer and the LibChecker projection read later. |
| `ReplacementLedger` | `_lDeletedLibraryPaths`, `_lStaged`, `_lStagedBySource`, `_dryReplacedLibPaths`, `_lSlotOwners` plus `CommitIfStaged`, `RollbackIfStaged`, `RollbackAllStaged`, `RegisterSlotOwner`, `IsReservedForOtherReplacement` | The L (replace library copy) state is written by the duplicate executor and read by the file mover and the Misc migration; it needs one owner that both sides receive. |

Behaviour classes, in the order they are extracted:

| Class | Moves out of MusicIntegrator (lines as of 2026-11-12) | Reads |
|---|---|---|
| `RoutingJsonWriter` (static) | `BuildJson` 1842 and the `Append*Block` helpers 1770 to 1977, `JStr` 2169, `ComputeLeftovers` 1645, `RelToNewMusicRoot` 1673, `NewMusicFingerprint` 2028, `ReadLatestPreviewFingerprint` 2051, `WriteExecutionRecordWithRetry` 2134 | its parameters only |
| `MirrorIndex` | `LoadArtistAliases` 2322, `GetAliasExpandedKeys` 2353, `NormaliseTitleForDedup` 2380, `AddIndexKey` 2396, `BuildMirrorIndex` 2403, `FindDuplicateInMirror` 2453, `DeriveLibraryPathFromMirrorPath` 2469, `ReadMirrorTrackInfo` 2527, `IsAlbumFolderCompilation` 2576, `GetMirrorRelPath` 3695 | roots |
| `LibraryCounter` | `FindAlbumFolder` 3322, `CountAlbumSongs` 3345, `CountLibrarySinglesOfAlbum` 3377, `CountAkiraTheDonPersonSongs` 3412, `CountAkiraTheDonPersonAlbumSongs` 3495, `CountLibraryAlbumTracks` 2550, `IsDeluxeVersionOf` 2566, and the two caches `_albumLibraryCountCache`, `_librarySinglesAlbumCache` | roots, library path, scan-ahead batch album counts |
| `DestinationRouter` | `GetDestDir` 3048, `SanitiseFolderName` 3034, `GetRouteCategory` 2495, `GetRouteSummary` 2510, `BuildDestFilename` 497, `IsDestBlocked` 826 | roots, library path, `ScanAheadResult`, `LibraryCounter` |
| `IntegrationReport` | `PrintRouteDistributionPreview` 1222, `PrintConfidenceReport` 2205 | router, dry-run flag, roots |
| `ProjectedValidator` | `BuildProjection` 3575, `RunProjectedLibChecker` 3593 | router, mirror index, Misc candidates, roots |
| `DuplicateAdvisor` | `LengthsDiffer` 2778, `RecommendDuplicate` 2785, `ReadLengthSeconds` 2835, `ReadMirrorFacts` 2846, `BuildDupData` 2866, `DupRecommendationText` 2934, `PresentDuplicateAndDecide` 2949, `ReadMenuKey` 3545, `ReadMenuKeyFrom` 3548, `RunDuplicateReview` 471, `MarkInBatchDuplicates` 2624 | mirror index, library counter, manifest, no-input flag |
| `BatchScanner` | `RunScanAhead` 1017, `FolderSpellingFor` 3226, `ChooseCanonicalSpelling` 3234, `PreScanFiles` 2643 | roots, spelling choices, mirror index, advisor, preview TagFixer, dry-run flag |
| `ReplacementLedger` | the five ledger fields and five methods listed above (774 to 825) | itself |
| `DuplicateExecutor` | `ExecuteDuplicateDecisions` 603 | router, ledger, roots, dry-run flag |
| `FileMover` | `RouteAllFiles` 839, `CreateDirectoryTracked` 987, `RemoveEmptyCreatedDirs` 997 | router, ledger, dry-run flag |
| `ArtistFolderCaseRenamer` | `PlanArtistFolderCaseRenames` 3246, `ApplyArtistFolderCaseRenames` 3264, `RevertArtistFolderCaseRenames` 3282, `CaseOnlyFolderMove` 3293, `FindDirNameIgnoreCase` 3304 | library path, journal folder |
| `MiscMigration` | `RunMiscMigration` 1327, `WriteMiscMigrationJournal` 1303, `ReadCaseOnlyArtistEntry` 1458, `RetaggedFileName` 1476, `RetagRenameTarget` 1498, `CaseOnlyArtistEntry` 1511, `ReplaceArtistTagEntry` 1524, `RevertMiscMigration` 1544, `RestoreMigratedTagBestEffort` 1612 | Misc candidates, ledger, mirror index, library path, journal folder, dry-run flag |
| `NewMusicCleanup` | both `CleanupNewMusicFolder` overloads 1636 and 1682 | roots, dry-run flag |

The nested data types (`LogEntry` 98, `ConfidenceRecord` 144, `BatchSummary` 165, `LeftoversSnapshot` 178, `DupData` 186, `MirrorPathBlock` 207, `ScannedFile` 219, `MiscMigrationMove` 1294, `MiscRevertResult` 1625, `DupFacts` 2766) stay nested in `MusicIntegrator` so the several hundred test references to `MusicIntegrator.LogEntry`, `MusicIntegrator.ScannedFile` and the rest keep compiling; they move to a second file of a `partial class MusicIntegrator` that holds data types only.

Expected result: the orchestrator file holds the run sequence, the two gates, the finish and the forwarders, a few hundred lines that read as the workflow David reviews for safety, and after the optional section 7 phase E the forwarders go too.

### Why this beats the first proposal's verbatim 8-class extraction

The first proposal (2026-10-12) had the right order (output-only first, anything that deletes last) and that order is kept. Its seams were drawn by topic rather than by state, and three of them cut a state cluster in half: the replacement ledger was split between `DuplicateResolver` (the writer) and the `RouteAllFiles` remainder (the reader), with the projected validator also needing it; `RunScanAhead` (the producer of eight fields) was bundled into `DestinationRouter` although `PreScanFiles`, the JSON writer and the Misc migration also consume its output; and the per-run caches had no owner at all. A verbatim move across a cut cluster forces either shared mutable fields reached through the facade or an unplanned refactor inside a move commit, which ground rule 2 forbids. It also had no answer for path roots, moved the data types to top level (churning several hundred test references for no behaviour gain), and gated each step on a dry-run against the real NewMusic, which this plan replaces with generated fixture roots.

### Rejected alternatives

1. State machine. The pipeline is linear with four early exits (pre-flight block, duplicate review quit, mirror path block, nothing to integrate); there are no cycles or re-entrant states. A state machine adds a dispatch layer to a sequence that already reads top to bottom, which is the complexity this work is meant to remove.
2. One context object passed to every phase (an `IntegrationRun` bag of all state). It renames the 25 shared fields instead of dividing them, so every phase can still read and write everything; the coupling stays, only less visible.
3. A generic phase interface (`IPhase.Execute(context)`). The phases take different inputs and return different things; a common interface needs the context bag above and buys nothing a plain method call does not.
4. Splitting the class across partial files as the decomposition. Cosmetic: shared fields stay shared, nothing becomes testable alone, and each member would move twice. Partial is used only to house the data types.
5. Injecting a `TextWriter` for console output now. `QuietConsole` and the console-format tests rely on `Console.Out`; changing every writer is a large behaviour-neutral refactor with its own risk. Deferred (section 8).
6. Separate options and outcome classes. Considered and cut while designing: the four flags and the outcome fields are few, each extracted class receives only the flags it reads, and the orchestrator keeps the outcome.
7. A full-pipeline constructor taking `IntegrationRoots` for in-process end-to-end tests. Cut: `TagFixer` still reads `Constants` for the quarantine folder and artist overrides, so an in-process run on temp roots would be only partly isolated. The out-of-process harness with `AUDIOMANAGER_TEST_ROOT` redirects every path and is the end-to-end gate instead.

## 4. Injectable per-run path roots

`IntegrationRoots` holds the four folders MusicIntegrator reads: NewMusic, Library, Mirror and Logs. `IntegrationRoots.FromConstants()` returns `Constants.NewMusicPath`, `Constants.AudioFolderPath`, `Constants.MirrorFolderPath` and `Constants.LogsPath`. `Constants` already honours `AUDIOMANAGER_TEST_ROOT` (`src/AudioManager/Code/Constants.cs`, `ResolveRoots`, pinned by `TestRootIsolationTests`), so a run started under that variable gets sandbox roots with no new mechanism.

Every constructor sets `_roots = IntegrationRoots.FromConstants()`, and every instance read of those four constants inside MusicIntegrator becomes a read of `_roots`. As of 2026-11-12 the reads are in the public constructor, `ExecuteDuplicateDecisions`, `RouteAllFiles`, `RunScanAhead`, `PrintConfidenceReport`, `DeriveLibraryPathFromMirrorPath`, `BuildDupData`, `CountAkiraTheDonPersonSongs`, `CountAkiraTheDonPersonAlbumSongs`, `RunProjectedLibChecker`, `BuildMirrorIndex`, `CleanupNewMusicFolder`, `WriteExecutionRecord`, `PreScanFiles`, `RunMiscMigration`, `WriteJsonOutput` and `ApplyArtistFolderCaseRenames`. Static members that read a constant (`GetRouteCategory`, `GetMirrorRelPath`, `RelToNewMusic`) gain an overload that takes the root, and the existing signature delegates to it with the constant, the pattern `RelToNewMusic(string)` at line 84 already uses. The real-pass `TagFixer` at line 289 switches to the existing root overload with `_roots.NewMusic`, which the default constructor already chains to (`TagFixer.cs` line 97).

Because every run builds its roots from the same constants, this is behaviour-neutral by construction. The extracted classes receive `IntegrationRoots` in their constructors, so each can be tested on a temp folder tree without the environment variable. One test-only constructor `MusicIntegrator(string testLibraryPath, IntegrationRoots roots)` (no pipeline) lets the section 7 step B4 pins drive scan-ahead against a temp mirror.

Known gap, kept on purpose: the test constructors set `_libraryPath` to a temp library while several members still read the library constant (through `_roots.Library` after step B2). Unifying the two changes test-mode output (for example a relative destination becomes absolute), so it is a separate late step (E4), never part of the roots step.

## 5. Seam mechanics

These are the only edits a move commit may make to a moved body; the verbatim checker (step A2) prints every other difference.

1. Same-name accessor. When state moves to an owner object, the old private field name stays as a private get-only property that returns the owner's member, so bodies left behind compile unchanged. Example: `private HashSet<string> _lDeletedLibraryPaths => _ledger._lDeletedLibraryPaths;`. Only collections that are never reassigned may use this; a reassigned field stops the step.
2. Same-name fields in the receiving class. A moved body keeps reading `_libraryPath`, `dryRun` and so on because the receiving class declares fields of the same names, set from its constructor.
3. Qualifier for a member left behind. A moved body calling a static member still on MusicIntegrator gains a `MusicIntegrator.` qualifier; a call to a member of another extracted class goes through the field holding that class.
4. Read of state the class does not own becomes a parameter, computed by the caller with the same expression. Example: `MigrationJournalDir ?? _roots.Logs` is computed by the forwarder and passed to the moved method.
5. Forwarders. Every moved member that a test, `Program.cs` or another class calls keeps a one-line forwarder on MusicIntegrator with the old name and signature, so existing tests stay unchanged and keep acting as pins. External callers as of 2026-11-12: `Program.cs` (`NewMusicFingerprint`, `RevertMiscMigration`, the `Blocked` property), `ArtistSpellingCandidates.cs` (`ChooseCanonicalSpelling`), `LibraryFixer.cs` (`SanitiseFolderName`), `TagFixer.cs` (`RelToNewMusic`).

## 6. Safety gates

Every step runs these, in this order, and stops on the first failure:

1. `scripts\dev\verify.bat --no-pause` exits 0 (exit 3 is a missing prerequisite, not a pass).
2. Move steps only: `python scripts\dev\verbatim_moves.py --base HEAD~1 <member names>` shows no difference other than the seam edits in section 5.
3. `python scripts\dev\golden\integrate_parity.py --baseline-exe <copy of the previous green build> --exe src\AudioManager\bin\Release\AudioManager.exe` prints IDENTICAL. The baseline is a copy of `src\AudioManager\bin\Release` taken from the last green commit into a scratch folder outside the repo.
4. A reviewer one tier above the implementer reads the diff, not the report, and checks the moved bodies against the old file.

What the gates cannot see: the real-run paths (moves, deletes, Misc migration, cleanup, execution record) are covered only by the in-process unit tests on temp folders, because no agent runs a real integrate. `make_goldens.py` and `check_goldens.py` do run a real integrate inside a sandbox (`make_goldens.py` line 310 as of 2026-11-12), so agents do not run them under this plan; whether they may is open item 3. David can run `check_goldens.py` and his own rehearsal after phase D as the real-path end check.

## 7. Steps

Each step is one commit, under about 300 changed lines (a pure move counts once), with the tests named first. "Verify" is always the section 6 gates; a step adds its own checks. Stop rule for every step: a gate fails twice after a diagnosed fix, a body needs an edit outside section 5, or a field turns out to be reassigned where an accessor was planned: stop, write the finding into section 8, leave the code unmoved. Tier is the implementer; the reviewer is one tier up.

### Phase A: harness and pins (no production code moves)

A1. Dry-run parity harness. Tier: Sonnet. Reason: the fixture must provably reach each branch, and a harness that passes vacuously is the main way this plan can fail silently. Files: new `scripts/dev/golden/integrate_parity.py`. It imports `make_goldens` (as `check_goldens.py` does) for `song`, `init_mirror_repo`, `commit_mirror`, `Runner`, `normalise`, `make_sandbox`, `remove_sandbox`, `data_snapshot` and `check_path_text`. It seeds a sandbox covering: an artist promoted by scan-ahead who also has Misc mirror entries, a compilation album, an album that routes to an album subfolder, a library duplicate found through the mirror, an in-batch duplicate, an Akira The Don sampled-person track, an artist folder whose case differs from the tag, and a second sandbox where a destination projects past MAX_PATH. The command lists are hard-coded and every one contains `--dry-run`; the script refuses to start otherwise. It runs both exes with `integrate --dry-run --no-input` and `integrate --dry-run --no-input --json-output`, normalises stdout and the newest `routing-*.json` (timestamps, sandbox root), and compares stdout, JSON and exit code. It asserts each seeded case left its marker in the baseline output (a missing marker fails the run as FIXTURE NOT EXERCISED). Tests first: run it with the same exe as baseline and candidate (must print IDENTICAL), then with a deliberately different JSON file fed through a `--self-test` flag (must print DIFFERENT). Stop rule addition: a case that cannot be reached in dry-run is listed, not faked.

A2. Verbatim move checker. Tier: Haiku. Files: new `scripts/dev/verbatim_moves.py`. Given `--base <rev>` and member names, it extracts each member body from `git show <rev>:src/AudioManager/Code/Doer/MusicIntegrator.cs` and from the current source tree, strips leading indentation, and prints a unified diff per member plus a one-line verdict per member (SAME or CHANGED). Tests first: a small test file beside it with two fixture strings (an identical body, a body with one changed line). Verify: run it on HEAD against HEAD, all SAME.

A3. De-reflect the tests. Tier: Haiku. Behaviour-neutral. Files: `MusicIntegrator.cs` (access modifiers only) and the test files that reach private members by name (as of 2026-11-12: `MiscMigrationJournalTests`, `MiscMigrationRetagTests`, `MusicIntegratorDryRunParity2Tests`, `DuplicateDetectionTests`, `MusicIntegratorDuplicateStagingTests`, `DryFlagAndCatchTests`). Find them with a grep for `GetMethod(`, `GetField(` and `BindingFlags.NonPublic` in `src/AudioManager.Tests`. Each reflected member (for example `_miscMigrationCandidates`, `RunMiscMigration`, `BuildDupData`, `RunDuplicateReview`, `RollbackAllStaged`) goes from private to internal and the test calls it directly. Reflection by string compiles after a move and fails only at run time, so this must land before anything moves. Verify addition: a grep shows no reflection on MusicIntegrator members left.

A4. Missing pins. Tier: Haiku. Tests only. For each case, grep the named test files first and add the case only if absent: the printed confidence block for a clean run and a run with failures (`PrintConfidenceReport`); `DeriveLibraryPathFromMirrorPath` for a path outside the mirror root; one dry-run and one real-run record through `BuildJson` compared value by value; `ExecuteDuplicateDecisions` with K, L and D decisions against a temp library (look in `MusicIntegratorDuplicateStagingTests` and `MusicIntegratorDeleteGuardTests` first); `CleanupNewMusicFolder(string)` on a temp folder tree (look in `MusicIntegratorRouteAllFilesTests` first). New tests must pass on the unchanged code. If the step would exceed 300 lines, split it by member.

### Phase B: seams that move no logic

B1. Data types to a partial file. Tier: Haiku. Verbatim. Add `partial` to the class declaration (line 19) and move the ten nested data types listed in section 3 into new `src/AudioManager/Code/Doer/MusicIntegrator.Models.cs` as `internal partial class MusicIntegrator`, plus its `<Compile Include>`. No test changes. Verify addition: verbatim checker on the ten type names.

B2. Path roots. Tier: Haiku. Behaviour-neutral. New `src/AudioManager/Code/Doer/IntegrationRoots.cs`; in MusicIntegrator, `_roots` set by every constructor from `FromConstants()`, every instance read replaced as listed in section 4, the static overloads, and the `TagFixer` root overload at line 289. `Path.Combine(AppDomain.CurrentDomain.BaseDirectory, Constants.MirrorFolderPath, Constants.MiscDir)` at line 1079 keeps its shape with `_roots.Mirror` in the middle. Tests first: `IntegrationRootsTests` pinning that `FromConstants()` equals the four constants. Verify addition: a grep shows no read of the four constants left in MusicIntegrator outside `FromConstants` callers and the delegating static overloads.

B3. Test constructor with roots. Tier: Haiku. Adds `MusicIntegrator(string testLibraryPath, IntegrationRoots roots)` which runs the existing `MusicIntegrator(string)` body and then sets `_roots`; makes `RunScanAhead` and `CountAlbumSongs` internal. No other change.

B4. Pins for the cross-pass behaviours. Tier: Sonnet. Reason: these pins describe behaviour that spans the pre-flight and the real pass, which only shows up by reasoning about field lifetimes, and each one must pin what the code does today even where it looks wrong. Tests only, in new `src/AudioManager.Tests/MusicIntegratorCrossPassTests.cs`, using the B3 constructor on temp roots: (a) a Misc migration candidate added by one `RunScanAhead` call is still present after a second call whose batch no longer promotes that artist (open item 1); (b) `PreflightMirrorGate` on a real-mode instance projects the raw tags, not the dry TagFixer's final tags: a fixture whose fix changes the destination length blocks or passes according to the raw tags, named with the `_KnownDivergence` suffix as `Rules_LibChecker_AlbumGroupingUsesRawAlbumTag_KnownDivergence` does (open item 2); (c) `CountAlbumSongs` returns its cached count for a second call in the same instance after the library folder changed. All pass on the code as it is.

B5. Scan-ahead hand-off. Tier: Sonnet. Reason: it changes how eight fields are published and read, with ordering rules inside `RunScanAhead` (for example `_batchTagCache` is assigned at line 1073, before the method ends at 1215, and `_scanAheadSpellings` is filled in a loop at 1205 then read at 1208). Behaviour-neutral. New `src/AudioManager/Code/Doer/ScanAheadResult.cs`. `RunScanAhead` builds its values in locals as today and publishes one `ScanAheadResult` into `_scan` where it now assigns the fields; the old field names become same-name accessors over `_scan`; the two test constructors that set counts (lines 419 and 457) build a `ScanAheadResult` with the same values (null spellings stay null). `_miscMigrationCandidates` is not part of the result: it keeps accumulating across calls (open item 1). Stop rule addition: if any reader runs between two of today's assignments and sees a half-published state, stop.

### Phase C: read-only and output classes (verbatim moves, forwarders kept)

C1. `RoutingJsonWriter`, part 1. Tier: Haiku. Moves `BuildJson`, `AppendBlockedBlock`, `AppendArtistSpellingCandidatesBlock`, `AppendReplacedOriginalsBlock`, `AppendSummaryBlock`, `AppendLeftoversBlock`, `AppendConfidenceBlock`, `JStr` into new static `RoutingJsonWriter.cs`; `AppendSummaryBlock` calls `MusicIntegrator.GetRouteCategory` (seam edit 3); forwarder for `BuildJson`. Pins: `MusicIntegratorDuplicateJsonTests`, `MusicIntegratorExecutionRecordTests`, `MusicIntegratorBatchSummaryTests`, `RoutingJsonCountsTests`, `GoldenContractTests`, A4 `BuildJson` pin.

C2. `RoutingJsonWriter`, part 2. Tier: Haiku. Moves `ComputeLeftovers`, `RelToNewMusicRoot`, `NewMusicFingerprint`, `ReadLatestPreviewFingerprint`, `WriteExecutionRecordWithRetry`, with forwarders (`Program.cs` calls `NewMusicFingerprint`). Same pins.

C3. `MirrorIndex`. Tier: Haiku. Moves the section 3 list into new `MirrorIndex.cs`, constructed with roots; static members stay static. Pins: `DuplicateDetectionTests`, A4 `DeriveLibraryPathFromMirrorPath` pin.

C4. `LibraryCounter`. Tier: Haiku. Moves the counting helpers and the two caches into new `LibraryCounter.cs`; the orchestrator creates one instance per MusicIntegrator, so pre-flight and real pass share the caches exactly as the fields do today (open item 4). Pins: `RoutingTests`, routing manifest, B4 (c).

C5. `DestinationRouter`. Tier: Haiku, reviewer Sonnet. Moves the section 3 list into new `DestinationRouter.cs`, constructed with roots, library path, `LibraryCounter` and a settable `Scan` (`ScanAheadResult`) the orchestrator sets after each scan-ahead; same-name accessors over `Scan` keep `GetDestDir` byte-identical; forwarders for `GetDestDir`, `SanitiseFolderName`, `BuildDestFilename`, `IsDestBlocked`. Pins: `RoutingTests`, `RoutingFixtures`, `RulesCharacterisationTests`, the routing manifest (the strongest pin in the program).

C6. `IntegrationReport`. Tier: Haiku. Moves `PrintRouteDistributionPreview` and `PrintConfidenceReport`. Pins: `MusicIntegratorConsoleFormatTests`, A4 confidence pin, parity harness.

C7. `ProjectedValidator`. Tier: Haiku. Moves `BuildProjection` (forwarder kept, tests call it) and `RunProjectedLibChecker`; it receives the Misc candidates dictionary itself, not a copy. Pins: `ProjectedLibCheckerTests`.

C8. `DuplicateAdvisor`, part 1 (decision facts). Tier: Haiku. Moves `LengthsDiffer`, `RecommendDuplicate`, `ReadLengthSeconds`, `ReadMirrorFacts`, `BuildDupData`, `DupRecommendationText`. Pins: `DuplicateDetectionTests`, `DuplicateRecommendationTests`.

C9. `DuplicateAdvisor`, part 2 (review). Tier: Haiku. Moves `PresentDuplicateAndDecide`, `ReadMenuKey`, `ReadMenuKeyFrom`, `RunDuplicateReview`, `MarkInBatchDuplicates`. Pins: `MusicIntegratorDuplicateStagingTests`, `MusicIntegratorNoInputSafetyTests`.

C10. `BatchScanner`, part 1. Tier: Haiku, reviewer Sonnet. Moves `RunScanAhead`, `FolderSpellingFor`, `ChooseCanonicalSpelling` (forwarder kept for `ArtistSpellingCandidates.cs`). The scanner receives the orchestrator's Misc candidates dictionary, so preflight entries still carry into the real pass. Pins: `RoutingTests`, B4 (a), parity harness.

C11. `BatchScanner`, part 2. Tier: Haiku. Moves `PreScanFiles`; the preview TagFixer and dry-run flag are passed in, and the `if (dryRun && ...)` guard at line 2689 moves unchanged (open item 2). Pins: `MusicIntegratorDryRunParityTests`, `MusicIntegratorDryRunParity2Tests`, `PreflightGateTests`, `PreflightQuietTests`, B4 (b).

### Phase D: classes that write, delete or move files

All phase D steps: Tier Sonnet, reviewed by Opus against the old file. Reason: these are the paths that can lose library or NewMusic data; a wrong ledger instance or a wrong dry-run flag still compiles and can pass a test that does not cover that branch.

D1. `ReplacementLedger`. Moves the five fields and five methods (774 to 825) into new `ReplacementLedger.cs`; one instance per MusicIntegrator; same-name accessors on the orchestrator. Stop if any of the five fields is reassigned anywhere. Pins: `MusicIntegratorDuplicateStagingTests`, `MusicIntegratorDeleteGuardTests`.

D2. `DuplicateExecutor`. Moves `ExecuteDuplicateDecisions`; forwarder kept (tests call it directly). Pins: A4 K/L/D pin, `MusicIntegratorDuplicateStagingTests`, `MusicIntegratorDeleteGuardTests`.

D3. `FileMover`. Moves `RouteAllFiles`, `CreateDirectoryTracked`, `RemoveEmptyCreatedDirs`; the `try/finally RollbackAllStaged` in the constructor (lines 385 to 392) stays in the orchestrator. Pins: `MusicIntegratorRouteAllFilesTests`, `RoutingTests`, `MusicIntegratorConsoleFormatTests`.

D4. `ArtistFolderCaseRenamer`. Moves the section 3 list; the journal folder is passed in (seam edit 4). Pins: the case-rename tests found by grepping `PlanArtistFolderCaseRenames`.

D5. `MiscMigration`, part 1. Moves `RunMiscMigration`, `WriteMiscMigrationJournal` and the retag helpers; receives the shared Misc candidates dictionary and the ledger. Pins: `MiscMigrationJournalTests`, `MiscMigrationRetagTests`.

D6. `MiscMigration`, part 2. Moves `RevertMiscMigration` and `RestoreMigratedTagBestEffort`; forwarder kept for `Program.cs` (`--misc-revert`). Same pins.

D7. `NewMusicCleanup`. Moves both `CleanupNewMusicFolder` overloads. Pins: A4 cleanup pin, `MusicIntegratorRouteAllFilesTests`.

After D7, ask David to run `check_goldens.py` and his batch rehearsal once before phase E.

### Phase E: optional cleanup (each behaviour-neutral, one per commit)

E1. `JStr` to `JsonText.Quote`. Tier: Haiku. Only if a new test shows both give identical output for every char 0 to 0xFFFF and for surrogate pairs; otherwise record the difference and keep `JStr`.

E2. Retire forwarders, one class per commit. Tier: Haiku. Test call sites move to the extracted class; the test-only constructors go when nothing uses them (`MusicIntegrator(string)` stays while `Program.cs` uses it).

E3. Replace same-name accessors with direct member names. Tier: Haiku. Mechanical rename, compiler-checked.

E4. Unify `_libraryPath` with `_roots.Library`. Tier: Sonnet. Reason: it changes what test-mode runs print (section 4 known gap), so the affected test expectations change in the same commit and each change must be shown to be test-mode only; production is unchanged because the public constructor already sets both to the same constant (line 252).

## 8. Open items for David

1. Misc candidates leak across passes. `_miscMigrationCandidates` is only ever added to (line 1197) and never cleared, so on a real run an artist promoted by the pre-flight scan-ahead (raw tags) is Misc-migrated even if the real pass (fixed tags) no longer promotes them. The plan preserves this and pins it (B4 a). Decide whether it is intended.
2. The pre-flight gate checks raw tags. `PreflightMirrorGate` runs on the real-run instance, and `PreScanFiles` applies the dry TagFixer's changes only when `dryRun` is true (line 2689), so the pre-flight MAX_PATH gate projects the raw tags. `docs/DevContext.md` (Phase Order And Pre-flight) and the `PreflightGateTests` summary say final tag values; those tests pad to 270 characters and do not tell the two apart. Likely an existing defect; the plan preserves and pins it (B4 b). A fix is a behaviour change outside this work.
3. Real integrate on a sandbox. `make_goldens.py` and `check_goldens.py` run a real integrate inside an `AUDIOMANAGER_TEST_ROOT` sandbox. Under this plan no agent runs them. Allowing agents to run them would add an end-to-end gate on the real-run paths.
4. Per-instance caches persist from pre-flight into the real pass (`_albumLibraryCountCache`, `_librarySinglesAlbumCache`), so real-pass counts can reflect the library as the pre-flight saw it. Preserved by sharing one `LibraryCounter` (C4).
5. Test constructors mix a temp library with library constants (section 4); unified only in E4.
6. Console output through an injected `TextWriter` instead of `Console.Out`: deferred, needs its own plan.
7. The LibChecker versus `GetDestDir` album-grouping divergence (raw tag versus normalised tag, pinned by `Rules_LibChecker_AlbumGroupingUsesRawAlbumTag_KnownDivergence`) stays out of scope.
