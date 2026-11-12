using AudioManager.Code.Modules;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml;
using File = System.IO.File;
using TagLib;

namespace AudioManager
{
    /// <summary>
    /// Scans the NewMusic folder, validates files, and sorts them into the Audio library.
    /// </summary>
    internal partial class MusicIntegrator : Doer
    {
        // Console line formats a client parses (see the external contract tests).
        // Kept as functions so Tests/MusicIntegratorConsoleFormatTests.cs pins the exact text.
        internal static string FormatAutoLine(string artists, string title, string albumSuffix) =>
            $"[AUTO] {artists} - {title}{albumSuffix}";

        internal static string FormatSkipLine(string artists, string title) =>
            $"[SKIP] {artists} - {title}: already exists at destination";

        internal static string FormatExecutionJsonLine(string path) =>
            $"\n  EXECUTION JSON: {path}";

        // Bump on any breaking change to BuildJson's contract shape (docs/).
        internal const int SchemaVersion = 1;
        internal bool dryRun;
        private bool noInput;
        private bool jsonOutput;
        internal Dictionary<string, List<string>> _miscMigrationCandidates = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        // Populated by RunScanAhead; used by GetDestDir to build full routing reason for Misc fallback
        // The scan-ahead values live in one ScanAheadResult that RunScanAhead replaces stage by stage (same order as the old field writes);
        // the old field names stay as read-only accessors so every reader is unchanged.
        private ScanAheadResult _scan = ScanAheadResult.Empty();
        private Dictionary<string, int> _scanAheadBatchCounts => _scan.BatchCounts;
        private Dictionary<string, string> _scanAheadSpellings => _scan.Spellings;
        // Set by RunScanAhead: artists whose batch and Misc tags carry 2+ spellings (suggestion only, written to the routing JSON).
        private List<ArtistSpellingCandidate> _artistSpellingCandidates => _scan.SpellingCandidates;
        /// <summary>The run's TagFixer, so the dry-run preview applies keep= flags and custom rules like the real run.</summary>
        private TagFixer _previewTagFixer;
        private Dictionary<string, int> _scanAheadMiscCounts => _scan.MiscCounts;
        private Dictionary<string, int> _scanAheadSourcesCounts => _scan.SourcesCounts;
        // primaryArtist -> album -> count; avoids O(N*M) TagLib re-reads in CountAlbumSongs batch side
        private Dictionary<string, Dictionary<string, int>> _scanAheadBatchAlbumCounts => _scan.BatchAlbumCounts;
        // Album names with 3+ distinct primary artists in the batch - routed to Compilations/ when no artist folder
        private HashSet<string> _compilationAlbums => _scan.CompilationAlbums;
        private string _libraryPath;
        // Selective-integration manifest, if any - consulted in PresentDuplicateAndDecide's
        // noInput branch so a review-stage duplicate resolution overrides the exe's own
        // recommendation on a real run. Never used to weaken the accept/decline partition.
        private IntegrationManifest _manifest;
        // mp3s the manifest declined in this run (0 without a manifest); reported as leftovers.declinedCount.
        private int _declinedCount;
        // Manifest-chosen artist spellings (object manifest form). Passed to TagFixer for batch files and used to name folders.
        private Dictionary<string, string> _artistSpellingChoices = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // Raw tag data cached from RunScanAhead so PreScanFiles avoids a second TagLib read per file
        private Dictionary<string, (string artists, string title, string album, string genres, uint year)> _batchTagCache => _scan.BatchTagCache;
        private LibraryCounter _counterInstance;
        private LibraryCounter Counter => _counterInstance ?? (_counterInstance = new LibraryCounter(() => _roots, () => _libraryPath, () => _scanAheadBatchAlbumCounts));
        private DestinationRouter _routerInstance;
        private DestinationRouter Router => _routerInstance ?? (_routerInstance = new DestinationRouter(() => _libraryPath, () => _scan, Counter));
        private IntegrationReport _reportInstance;
        private IntegrationReport Report => _reportInstance ?? (_reportInstance = new IntegrationReport(() => _roots, () => dryRun, () => _scan, Router));
        // Library paths deleted by L-decisions during real integration; used in RunMiscMigration
        // to distinguish "deleted by dup resolution" from "genuinely stale mirror".
        private HashSet<string> _lDeletedLibraryPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // L-decision stage-then-commit: library copies moved aside (not deleted) until the replacement
        // lands. _lStaged: libPath -> bakPath. _lStagedBySource: replacement SourcePath -> libPath.
        // Commit deletes the bak on success; rollback restores it on any skip; leftovers roll back at end.
        private Dictionary<string, string> _lStaged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, string> _lStagedBySource = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // Dry run only: library paths an L-decision would replace (not blocked in 3a). Their copy is staged
        // aside in the real run, so 3b treats their destination as free, as the real run does.
        private HashSet<string> _dryReplacedLibPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Both modes: library path of each L-decision slot -> SourcePath of the replacement that owns it (first owner wins).
        // While a slot is reserved, 3b blocks every other file routed to that path, so a K file cannot take the freed slot.
        private Dictionary<string, string> _lSlotOwners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);


        // Per-run path roots. Every constructor sets _roots first thing, before any other statement (see IntegrationRoots).
        internal IntegrationRoots _roots;

        /// <summary>Path of a file relative to NewMusic with backslashes, or null when it is not under NewMusic.</summary>
        internal static string RelToNewMusic(string path) => RelToNewMusic(path, Constants.NewMusicPath);

        /// <summary>Path of a file relative to the given root with backslashes, or null when it is not under that root.</summary>
        internal static string RelToNewMusic(string path, string root)
        {
            if (string.IsNullOrEmpty(path)) return null;
            root = root?.TrimEnd('\\', '/');
            if (string.IsNullOrEmpty(root)) return null;
            string full = path.Replace('/', '\\');
            if (!full.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase)) return null;
            return full.Substring(root.Length + 1);
        }

        /// <summary>Set by the pre-move MAX_PATH gate. Null when the batch was allowed to proceed.</summary>
        internal MirrorPathBlock Blocked { get; private set; }

        /// <summary>
        /// Construct and run the music integrator
        /// </summary>
        /// <param name="dryRun">If true, print planned actions without executing any file moves.</param>
        /// <param name="noInput">If true, skip all interactive prompts and auto-accept recommended decisions.</param>
        /// <param name="jsonOutput">If true, write routing decisions as JSON to data/logs/routing-{timestamp}.json after dry-run.</param>
        /// <param name="manifest">If set, only NewMusic files matching the manifest are integrated;
        /// all other files are left untouched in NewMusic (selective integration).</param>
        public MusicIntegrator(bool dryRun = false, bool noInput = false, bool jsonOutput = false,
            IntegrationManifest manifest = null)
        {
            _roots = IntegrationRoots.FromConstants();
            this.dryRun = dryRun;
            this.noInput = noInput;
            this.jsonOutput = jsonOutput;
            this._manifest = manifest;
            if (manifest != null)
            {
                _artistSpellingChoices = manifest.ArtistSpellings;
                foreach (string warning in manifest.SpellingWarnings) Console.WriteLine($" - [WARN] {warning}");
            }
            _libraryPath = _roots.Library;
            string modeLabel = dryRun ? " [DRY RUN - no files will be moved]" : "";
            Console.WriteLine($"\nIntegrating new music...{modeLabel}");

            int movedCount = 0;
            int skippedCount = 0;
            var logEntries = new List<LogEntry>();
            int totalFiles = 0;

            try
            {
                // Fingerprint NewMusic before Tag Fix can touch anything: this is the state the user's preview was (or was not) made on.
                _startFingerprint = NewMusicFingerprint(_roots.NewMusic);
                if (!dryRun)
                {
                    _previewFingerprint = ReadLatestPreviewFingerprint(_roots.Logs);
                    if (_previewFingerprint != null && _previewFingerprint != _startFingerprint)
                        Console.WriteLine(" - [WARN] preview_stale: NewMusic changed since the last dry run; the decisions you reviewed may not match what is integrated now.");
                }

                // A manifest declines files before Tag Fix too: declined files are never rewritten, renamed or quarantined.
                // Recorded before the pre-flight so its keep set sees the same preexisting files as the real pass.
                if (manifest != null && Directory.Exists(_roots.NewMusic))
                {
                    manifest.RecordPreexisting(Directory.GetFiles(_roots.NewMusic, "*.mp3", SearchOption.AllDirectories)
                        .Select(f => RelToNewMusic(f, _roots.NewMusic)).Where(r => r != null));
                }

                // Pre-flight (real runs only): the dry-run steps plus the MAX_PATH gate, on the tag values the real run
                // will produce. Writes nothing. A block stops the batch before the real TagFixer rewrites NewMusic.
                if (!dryRun && !PreflightMirrorGate(_roots.NewMusic, _roots.Mirror, manifest))
                {
                    WriteJsonOutput(logEntries, Blocked);
                    return;
                }

                // Step 1: Fix tags in NewMusic folder
                var tagFixer = new TagFixer(_roots.NewMusic, dryRun, manifest == null ? (Func<string, bool>)null : (f => manifest.Matches(Path.GetFileName(f), RelToNewMusic(f, _roots.NewMusic))), _artistSpellingChoices);
                var tagChanges = tagFixer.FileChanges;

                var files = Directory.Exists(_roots.NewMusic)
                    ? Directory.GetFiles(_roots.NewMusic, "*.mp3", SearchOption.AllDirectories)
                    : Array.Empty<string>();

                // Selective integration: keep only manifest-accepted files. Applied before
                // scan-ahead and duplicate review so declined files influence nothing
                // (no folder-threshold counts, no dup prompts) and are never touched.
                if (manifest != null && files.Length > 0)
                {
                    int beforeCount = files.Length;
                    files = files.Where(f => manifest.Matches(Path.GetFileName(f), RelToNewMusic(f, _roots.NewMusic))).ToArray();
                    int excluded = beforeCount - files.Length;
                    _declinedCount = excluded;
                    Console.WriteLine($" - Manifest filter: {files.Length} of {beforeCount} NewMusic file(s) selected" +
                        (excluded > 0 ? $"; {excluded} left untouched in NewMusic" : ""));
                    foreach (var miss in manifest.UnmatchedEntries())
                        Console.WriteLine($" - [WARN] Manifest entry matched no NewMusic file: " +
                            $"{miss.Filename ?? $"{miss.Artist} - {miss.Title}"} (stale scan? re-run the dry run)");
                    if (files.Length == 0)
                    {
                        Console.WriteLine(" - ERROR: manifest matched no files in NewMusic - nothing integrated.");
                        return;
                    }
                }

                totalFiles = files.Length;

                if (files.Length == 0)
                {
                    Console.WriteLine(" - Nothing to integrate!");
                    return;
                }

                // Scan-ahead: identify artists that will hit 3+ threshold
                var newArtistFolders = RunScanAhead(files);

                // Pre-scan all files: read + clean tags, find duplicates (no UI)
                _previewTagFixer = tagFixer;
                var scannedFiles = PreScanFiles(files, tagChanges, tagFixer.LogsByFile);

                // Flag in-batch duplicates (same normalised artist+title appearing twice in NewMusic)
                MarkInBatchDuplicates(scannedFiles);

                // Step 2: Batch duplicate review - all duplicates presented together before routing
                if (!RunDuplicateReview(scannedFiles, logEntries))
                {
                    // A real run has already rewritten NewMusic tags by now; say so and point at the revertable plan flow.
                    if (!dryRun) Console.WriteLine(QuitAfterTagFixNote());
                    return;
                }

                // Step 3: Route files (all duplicate decisions already made)
                int routeableCount = scannedFiles.Count(sf => sf.IsReadable && sf.Duplicate?.SkipRouting != true);
                Console.WriteLine("\n===========================================================================");
                Console.WriteLine(dryRun ? "Routing (Dry Run)" : "Routing");
                Console.WriteLine("===========================================================================");
                Console.WriteLine($"Files in batch: {routeableCount}");

                // Dry-run: pre-compute route distribution and show before per-file output
                if (dryRun && routeableCount > 1)
                    PrintRouteDistributionPreview(scannedFiles, newArtistFolders);
                Console.WriteLine();

                // Pre-move MAX_PATH gate: a planned destination whose staged XML temp path would reach the limit
                // stops the whole batch here, before ExecuteDuplicateDecisions or RouteAllFiles moves anything.
                if (!RunMirrorPathGate(scannedFiles, newArtistFolders, _roots.Mirror))
                {
                    WriteJsonOutput(logEntries, Blocked);
                    return;
                }

                // 3a: Execute all duplicate decisions together so their outputs are grouped
                //     before routing output begins. D/L outputs appear consecutively here;
                //     K files produce no output and are routed normally in 3b.
                if (!ExecuteDuplicateDecisions(scannedFiles, newArtistFolders, logEntries, ref skippedCount)) return;

                // 3b: Route all files. D-decided and dry-run L-decided duplicates already handled
                //     above; real-mode L files and K files fall through to routing here.
                // In dry-run: collect outputs and print sorted by destination path after the loop.
                // 3b-pre: tag decides the artist folder name. Existing folders that differ only by case are renamed (journaled, revertable).
                if (_scanAheadSpellings != null)
                {
                    var casePlan = PlanArtistFolderCaseRenames(_scanAheadSpellings.Values);
                    foreach (var r in casePlan)
                        Console.WriteLine($"  Artist folder '{r.Key}' {(dryRun ? "will be" : "is being")} renamed to '{r.Value}' (case only, matches the tag)");
                    if (!dryRun && casePlan.Count > 0)
                    {
                        string renameJournal = ApplyArtistFolderCaseRenames(casePlan);
                        Console.WriteLine($"  Journal: {renameJournal}");
                    }
                }
                var routingStopwatch = System.Diagnostics.Stopwatch.StartNew();
                List<(string destPath, string block)> dryRunRoutingOutputs;
                try
                {
                    dryRunRoutingOutputs = RouteAllFiles(scannedFiles, newArtistFolders, logEntries, ref movedCount, ref skippedCount);
                }
                finally
                {
                    RollbackAllStaged();
                }
                routingStopwatch.Stop();

                // Print dry-run routing outputs sorted by destination path
                if (dryRun && dryRunRoutingOutputs != null)
                {
                    foreach (var (_, block) in dryRunRoutingOutputs.OrderBy(x => x.destPath, StringComparer.OrdinalIgnoreCase))
                        Console.Write(block);
                }

                // Write JSON output if requested (dry-run only)
                if (dryRun && jsonOutput)
                    WriteJsonOutput(logEntries);

                // Projected LibChecker: validate post-integration library state before any files move
                if (dryRun)
                    RunProjectedLibChecker(scannedFiles, newArtistFolders);

                PrintRoutingResultsAndFinish(logEntries, movedCount, skippedCount, totalFiles, routingStopwatch);
            }
            finally
            {
                Finished(); // records time for Doer.PrintTotalTimeTaken(), no separate console output
            }
        }

        /// <summary>Constructor that does not run the integration pipeline. Used by the tests and by --misc-revert (Program.TryRunMiscRevert).</summary>
        internal MusicIntegrator(string testLibraryPath)
        {
            _roots = IntegrationRoots.FromConstants();
            _libraryPath = testLibraryPath;
            _scan = new ScanAheadResult(
                batchCounts: new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
                spellings: null,
                spellingCandidates: new List<ArtistSpellingCandidate>(),
                miscCounts: new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
                sourcesCounts: new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
                batchAlbumCounts: new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase),
                compilationAlbums: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                batchTagCache: null);
        }

        /// <summary>Test constructor with injected roots; runs no pipeline.</summary>
        internal MusicIntegrator(string testLibraryPath, IntegrationRoots roots) : this(testLibraryPath)
        {
            _roots = roots;
        }

        /// <summary>
        /// Test-only constructor for exercising PresentDuplicateAndDecide's noInput/manifest-override
        /// path without running the real pipeline (no NewMusic/library filesystem access).
        /// </summary>
        internal MusicIntegrator(string testLibraryPath, bool noInput, IntegrationManifest manifest) : this(testLibraryPath)
        {
            this.noInput = noInput;
            this._manifest = manifest;
        }

        /// <summary>Test-only constructor for dry-run parity tests. Routes against testLibraryPath, no pipeline.</summary>
        internal MusicIntegrator(string testLibraryPath, bool dryRun) : this(testLibraryPath)
        {
            this.dryRun = dryRun;
        }

        /// <summary>Test-only constructor for duplicate-review tests: dry-run and noInput/manifest together. No pipeline.</summary>
        internal MusicIntegrator(string testLibraryPath, bool dryRun, bool noInput, IntegrationManifest manifest) : this(testLibraryPath)
        {
            this.dryRun = dryRun;
            this.noInput = noInput;
            this._manifest = manifest;
        }

        /// <summary>
        /// Test-only constructor that accepts pre-built batch album counts.
        /// Allows routing tests to verify album subfolder routing without real TagLib MP3 files.
        /// </summary>
        internal MusicIntegrator(string testLibraryPath, Dictionary<string, Dictionary<string, int>> batchAlbumCounts)
        {
            _roots = IntegrationRoots.FromConstants();
            _libraryPath = testLibraryPath;
            _scan = new ScanAheadResult(
                batchCounts: new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
                spellings: null,
                spellingCandidates: new List<ArtistSpellingCandidate>(),
                miscCounts: new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
                sourcesCounts: new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
                batchAlbumCounts: batchAlbumCounts ?? new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase),
                compilationAlbums: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                batchTagCache: null);
        }

        /// <summary>
        /// Step 2: presents all in-batch duplicates for review before routing begins.
        /// Returns false if the user quits (constructor must return immediately without routing).
        /// </summary>
        internal bool RunDuplicateReview(List<ScannedFile> scannedFiles, List<LogEntry> logEntries)
        {
            var duplicateFiles = scannedFiles.Where(sf => sf.Duplicate != null).ToList();
            if (duplicateFiles.Count == 0) return true;

            Console.WriteLine($"\nReviewing {duplicateFiles.Count} duplicate(s)...");
            foreach (var sf in duplicateFiles)
            {
                if (!PresentDuplicateAndDecide(sf))
                {
                    sf.LogEntry.Status = "quit";
                    logEntries.Add(sf.LogEntry);
                    return false;
                }
            }
            return true;
        }

        /// <summary>Warning printed when a real run quits at duplicate review, after the real TagFixer has rewritten NewMusic.</summary>
        internal static string QuitAfterTagFixNote()
        {
            return " - [WARN] Quit after tag fixes: NewMusic tags were already rewritten and nothing was routed. " +
                "Use the tagfix plan flow (tagfix --dry-run --json-output, then --apply-plan) when you need a revertable journal.";
        }

        internal static string BuildDestFilename(Track track) => DestinationRouter.BuildDestFilename(track);

        /// <summary>
        /// Real-run pre-flight: the dry-run steps (dry TagFixer, the same manifest file selection, scan-ahead, pre-scan)
        /// then the pre-move MAX_PATH gate, so the gate sees the final tag values. Moves, renames and writes nothing.
        /// Returns false when the batch is blocked (Blocked is set). The real pass that follows overwrites every field
        /// this sets (_previewTagFixer, _batchTagCache, the scan-ahead fields), so its results match a run without it.
        /// </summary>
        internal bool PreflightMirrorGate(string newMusicRoot, string mirrorPath, IntegrationManifest manifest = null)
        {
            var files = Directory.Exists(newMusicRoot)
                ? Directory.GetFiles(newMusicRoot, "*.mp3", SearchOption.AllDirectories)
                : Array.Empty<string>();
            Func<string, bool> keep = manifest == null ? (Func<string, bool>)null : (f => manifest.Matches(Path.GetFileName(f), RelToNewMusic(f, newMusicRoot)));
            if (keep != null && files.Length > 0)
                files = files.Where(keep).ToArray();
            if (files.Length == 0) return true; // the real pass reports "nothing to integrate" or the manifest error

            // The dry steps print their scan lines once here and again in the real pass, so they run quiet.
            // The TagFixer constructor is included: it prints the dry header and does the dry scan itself.
            TagFixer previewFixer;
            HashSet<string> newArtistFolders;
            List<ScannedFile> scannedFiles;
            using (new QuietConsole())
            {
                previewFixer = new TagFixer(newMusicRoot, true, keep, _artistSpellingChoices);
                newArtistFolders = RunScanAhead(files);
                _previewTagFixer = previewFixer;
                scannedFiles = PreScanFiles(files, previewFixer.FileChanges, previewFixer.LogsByFile);
            }
            bool clear = RunMirrorPathGate(scannedFiles, newArtistFolders, mirrorPath);
            if (clear) Console.WriteLine(" - Pre-flight: mirror path gate passed on final tag values");
            return clear;
        }

        /// <summary>
        /// Swaps Console.Out for TextWriter.Null until disposed. Dispose restores the writer that was current on
        /// construction, also when the wrapped block throws (the using statement runs Dispose in a finally).
        /// </summary>
        private sealed class QuietConsole : IDisposable
        {
            private readonly TextWriter _original;

            public QuietConsole()
            {
                _original = Console.Out;
                Console.SetOut(TextWriter.Null);
            }

            public void Dispose()
            {
                Console.SetOut(_original);
            }
        }

        /// <summary>
        /// Pre-move MAX_PATH gate, shared by dry and real runs. Projects the staged XML temp path of each planned
        /// destination (files that will route; D-decided duplicates leave NewMusic and are not planned) and
        /// records a block when any reaches Reflector.OverlongPathLimit. Moves nothing.
        /// Returns true when the batch may proceed.
        /// </summary>
        internal bool RunMirrorPathGate(List<ScannedFile> scannedFiles, HashSet<string> newArtistFolders, string mirrorPath)
        {
            var planned = new List<string>();
            foreach (var sf in scannedFiles)
            {
                if (!sf.IsReadable || sf.Duplicate?.Decision == 'D') continue;
                Track track = sf.Track;
                if (track.Title.Equals(Constants.MissingTag) || track.Artists.Equals(Constants.MissingTag)
                    || track.PrimaryArtist.Equals(Constants.MissingTag)) continue;
                try
                {
                    string destDir = GetDestDir(track, newArtistFolders, _compilationAlbums, out _, out _);
                    planned.Add(Path.Combine(destDir, BuildDestFilename(track)));
                }
                catch (Exception)
                {
                    // Projection only: the routing pass reports this file's own failure, so skip it here.
                    continue;
                }
            }

            var overlong = Reflector.FindOverlongMirrorPaths(mirrorPath, _libraryPath, planned);
            if (overlong.Count == 0) return true;

            Blocked = new MirrorPathBlock { Files = overlong };
            Console.WriteLine();
            Console.WriteLine("===========================================================================");
            Console.WriteLine($"[BLOCKED] {overlong.Count} planned destination(s) exceed the mirror path limit ({Blocked.Limit}).");
            Console.WriteLine("===========================================================================");
            foreach (var (file, length) in overlong)
                Console.WriteLine($" - {length} chars: {file}");
            Console.WriteLine("Nothing was moved. Rename or shorten the files above, then run again.\n");
            return false;
        }

        /// <summary>
        /// Step 3a: executes all duplicate decisions (D delete-from-NewMusic, L replace-in-library) so
        /// their console output is grouped before per-file routing output begins. K files produce no
        /// output here and fall through to routing in Step 3b (RouteAllFiles).
        /// Returns false if a Q decision slipped through (safety net; Step 2 already exits before this
        /// point in the normal case).
        /// </summary>
        internal bool ExecuteDuplicateDecisions(List<ScannedFile> scannedFiles, HashSet<string> newArtistFolders,
            List<LogEntry> logEntries, ref int skippedCount)
        {
            foreach (var sf in scannedFiles.Where(sf2 => sf2.Duplicate != null))
            {
                var entry = sf.LogEntry;
                var dup = sf.Duplicate;
                Track track = sf.Track;
                char decision = dup.Decision;

                // Safety net: Q in Step 2 already exits the constructor, but guard anyway
                if (decision == 'Q')
                {
                    entry.Status = "quit";
                    logEntries.Add(entry);
                    return false;
                }

                if (decision == 'D')
                {
                    // The library duplicate is the only other copy. If it is gone (removed, or staged aside by an
                    // L decision for another source in this run), sending the NewMusic file would leave no copy: keep it.
                    if (!File.Exists(dup.LibraryFilePath) || _lStaged.ContainsKey(dup.LibraryFilePath)
                        || (dryRun && _dryReplacedLibPaths.Contains(dup.LibraryFilePath)))
                    {
                        if (dryRun)
                        {
                            Console.WriteLine($"[DRY RUN] Library copy missing, would keep NewMusic file: {dup.RelNewPath}");
                            entry.Status = "would-keep";
                            entry.Detail = "duplicate (would keep: library copy missing)";
                        }
                        else
                        {
                            Console.WriteLine($"  [ERROR] Kept NewMusic file {dup.RelNewPath}: library copy missing: {dup.RelLibraryPath}");
                            entry.Status = "error";
                            entry.Detail = "duplicate (kept: library copy missing)";
                        }
                        logEntries.Add(entry); skippedCount++;
                        Console.WriteLine();
                        dup.SkipRouting = true;
                        continue;
                    }
                    if (dryRun)
                    {
                        Console.WriteLine($"[DRY RUN] Would send to Recycle Bin from NewMusic: {dup.RelNewPath}");
                        entry.Status = "would-delete";
                        entry.Detail = "duplicate (would delete)";
                    }
                    else
                    {
                        try
                        {
                            RecycleBin.Send(sf.SourcePath);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"  [ERROR] Could not send to Recycle Bin from NewMusic {dup.RelNewPath}: {ex.Message}");
                            entry.Status = "error";
                            entry.Detail = "duplicate (delete failed: " + ex.GetType().Name + ")";
                            logEntries.Add(entry); skippedCount++;
                            Console.WriteLine();
                            dup.SkipRouting = true;
                            continue;
                        }
                        Console.WriteLine($"  Sent to Recycle Bin from NewMusic: {dup.RelNewPath}");
                        entry.Status = "deleted";
                        entry.Detail = "duplicate (deleted)";
                    }
                    logEntries.Add(entry); skippedCount++;
                    Console.WriteLine();
                    dup.SkipRouting = true;
                }
                else if (decision == 'L')
                {
                    if (dryRun && !File.Exists(dup.LibraryFilePath))
                    {
                        Console.WriteLine($"  [WARN] Library file not found: {dup.RelLibraryPath}");
                        entry.Status = "error";
                        entry.Detail = "duplicate (library file not found)";
                        logEntries.Add(entry); skippedCount++;
                        Console.WriteLine();
                        dup.SkipRouting = true;
                    }
                    else if (dryRun && File.Exists(dup.LibraryFilePath + ".replaced.bak"))
                    {
                        // Real run: ReplaceGuard.Stage throws while a stale bak exists. Plain check here, since a dry run never calls Stage or ResolveOne.
                        Console.WriteLine($"  [ERROR] Could not stage library copy {dup.RelLibraryPath}: Refusing to stage: backup already exists: {dup.LibraryFilePath}.replaced.bak");
                        entry.Status = "error";
                        entry.Detail = "duplicate (library stage failed)";
                        logEntries.Add(entry); skippedCount++;
                        Console.WriteLine();
                        dup.SkipRouting = true;
                    }
                    else if (dryRun)
                    {
                        string lDestDir = GetDestDir(track, newArtistFolders, _compilationAlbums, out string lReason, out _);
                        string lDestFile = Reflector.SanitiseFilename(track.Artists) + " - " + Reflector.SanitiseFilename(track.Title) + ".mp3";
                        string lDestPath = Path.Combine(lDestDir, lDestFile);
                        string lRelDest = lDestPath.StartsWith(_roots.Library, StringComparison.OrdinalIgnoreCase)
                            ? lDestPath.Substring(_roots.Library.Length).TrimStart('\\', '/')
                            : lDestPath;
                        // Real run: the library copy is staged, then 3b routes with no batch set, so only an occupied
                        // destination (other than the copy being replaced) skips the replacement.
                        if (IsDestBlocked(lDestPath, dup.LibraryFilePath, null))
                        {
                            Console.WriteLine(FormatSkipLine(track.Artists, track.Title));
                            Console.WriteLine();
                            // Real run sets Reason and Destination in 3b before the skip; mirror them here.
                            entry.Reason = lReason;
                            entry.Destination = lRelDest;
                            entry.Status = "would-skip"; entry.Detail = "already exists at destination";
                            logEntries.Add(entry); skippedCount++;
                            dup.SkipRouting = true;
                        }
                        else
                        {
                            Console.WriteLine($"[DRY RUN] Would delete from library: {dup.RelLibraryPath}");
                            Console.WriteLine($"[DRY RUN] Would keep new file: {dup.DisplayNewFilename}");
                            Console.WriteLine($"[DRY RUN] Would route to: {lRelDest}");
                            entry.Status = "would-replace";
                            entry.Detail = "duplicate (would replace)";
                            _dryReplacedLibPaths.Add(dup.LibraryFilePath);
                            RegisterSlotOwner(dup.LibraryFilePath, sf.SourcePath);
                            // SkipRouting stays false: 3b routes the new file and logs the would-move, as the real run does
                        }
                    }
                    else
                    {
                        if (File.Exists(dup.LibraryFilePath))
                        {
                            try
                            {
                                // Stage, never delete: the library copy is restored if routing the replacement fails
                                string bakPath = ReplaceGuard.Stage(dup.LibraryFilePath);
                                _lStaged[dup.LibraryFilePath] = bakPath;
                                _lStagedBySource[sf.SourcePath] = dup.LibraryFilePath;
                                RegisterSlotOwner(dup.LibraryFilePath, sf.SourcePath);
                                _lDeletedLibraryPaths.Add(dup.LibraryFilePath);
                                Console.WriteLine($"  Staged library copy for replacement: {dup.RelLibraryPath}");
                                Console.WriteLine($"  Integrating replacement: {dup.RelNewPath}");
                                entry.Detail = "duplicate (library replaced)";
                                Console.WriteLine();
                                // SkipRouting stays false: new file is routed in 3b
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"  [ERROR] Could not stage library copy {dup.RelLibraryPath}: {ex.Message}");
                                entry.Status = "error";
                                entry.Detail = "duplicate (library stage failed)";
                                logEntries.Add(entry); skippedCount++;
                                Console.WriteLine();
                                dup.SkipRouting = true;
                            }
                        }
                        else
                        {
                            Console.WriteLine($"  [WARN] Library file not found: {dup.RelLibraryPath}");
                            entry.Status = "error";
                            entry.Detail = "duplicate (library file not found)";
                            logEntries.Add(entry); skippedCount++;
                            Console.WriteLine();
                            dup.SkipRouting = true;
                        }
                    }
                }
                // K: no output in 3a; routed normally in 3b
            }
            return true;
        }

        /// <summary>Commits the L-staged library copy paired with this replacement once it has routed.</summary>
        internal void CommitIfStaged(ScannedFile sf)
        {
            if (!_lStagedBySource.TryGetValue(sf.SourcePath, out string libPath)) return;
            if (_lStaged.TryGetValue(libPath, out string bakPath)) ReplaceGuard.Commit(bakPath);
            _lStaged.Remove(libPath);
            _lStagedBySource.Remove(sf.SourcePath);
        }

        /// <summary>Restores the L-staged library copy paired with this replacement when its routing is skipped.</summary>
        internal void RollbackIfStaged(ScannedFile sf)
        {
            if (!_lStagedBySource.TryGetValue(sf.SourcePath, out string libPath)) return;
            if (_lStaged.TryGetValue(libPath, out string bakPath)) ReplaceGuard.Rollback(bakPath, libPath);
            _lStaged.Remove(libPath);
            _lStagedBySource.Remove(sf.SourcePath);
            _lDeletedLibraryPaths.Remove(libPath);
        }

        /// <summary>Rolls back every L-staged library copy still pending at the end of routing.</summary>
        internal void RollbackAllStaged()
        {
            foreach (var kv in _lStaged.ToList())
            {
                ReplaceGuard.Rollback(kv.Value, kv.Key);
                _lDeletedLibraryPaths.Remove(kv.Key);
            }
            _lStaged.Clear();
            _lStagedBySource.Clear();
        }

        /// <summary>Records sourcePath as the owner of the L slot at libPath. The first owner wins.</summary>
        private void RegisterSlotOwner(string libPath, string sourcePath)
        {
            if (!_lSlotOwners.ContainsKey(libPath)) _lSlotOwners[libPath] = sourcePath;
        }

        /// <summary>
        /// True when destPath is an L slot owned by a different replacement. Such a slot is reserved for its
        /// owner even while its library copy is staged aside, so no other file may land there in this batch.
        /// </summary>
        private bool IsReservedForOtherReplacement(string destPath, string sourcePath)
        {
            return _lSlotOwners.TryGetValue(destPath, out string owner)
                && !string.Equals(owner, sourcePath, StringComparison.OrdinalIgnoreCase);
        }

        internal static bool IsDestBlocked(string destPath, string libraryPathBeingReplaced, HashSet<string> plannedDests) => DestinationRouter.IsDestBlocked(destPath, libraryPathBeingReplaced, plannedDests);

        /// <summary>
        /// Step 3b: routes all files. D-decided duplicates and blocked L-decided duplicates were already handled
        /// in ExecuteDuplicateDecisions (3a); unblocked L files (real and dry-run) and K files fall through to routing here.
        /// In dry-run mode, routing output blocks are collected and returned for the caller to print
        /// sorted by destination path; otherwise returns null.
        /// </summary>
        internal List<(string destPath, string block)> RouteAllFiles(List<ScannedFile> scannedFiles,
            HashSet<string> newArtistFolders, List<LogEntry> logEntries, ref int movedCount, ref int skippedCount)
        {
            var dryRunRoutingOutputs = dryRun ? new List<(string destPath, string block)>() : null;
            var plannedDests = dryRun ? new HashSet<string>(StringComparer.OrdinalIgnoreCase) : null;
            foreach (var sf in scannedFiles)
            {
                var entry = sf.LogEntry;

                if (!sf.IsReadable)
                {
                    Console.WriteLine();
                    Console.WriteLine("===========================================================================");
                    Console.WriteLine($"[ERROR] Skipping unreadable file: {Path.GetFileName(sf.SourcePath)}");
                    Console.WriteLine("===========================================================================");
                    Console.WriteLine($"Full path: {sf.SourcePath}");
                    Console.WriteLine($"Error details: {sf.ReadError}");
                    Console.WriteLine("The rest of the batch will continue.\n");
                    entry.Status = "error";
                    entry.Detail = $"unreadable: {sf.ReadError}";
                    logEntries.Add(entry); skippedCount++;
                    RollbackIfStaged(sf);
                    continue;
                }

                // Skip files fully resolved during duplicate execution (3a)
                if (sf.Duplicate?.SkipRouting == true) continue;

                List<string> createdDirs = null; // folders this file's move created; removed again if the move fails
                try
                {
                    Track track = sf.Track;
                    string primaryArtist = track.PrimaryArtist;

                    // Skip if un-routable (tags are already clean from TagFixer)
                    if (track.Title.Equals(Constants.MissingTag) || track.Artists.Equals(Constants.MissingTag) || primaryArtist.Equals(Constants.MissingTag))
                    {
                        Console.WriteLine($"- Skipped '{Path.GetFileName(sf.SourcePath)}': missing required tag");
                        entry.Status = "skipped"; entry.Detail = "missing required tag";
                        logEntries.Add(entry); skippedCount++;
                        RollbackIfStaged(sf);
                        continue;
                    }

                    // Build destination filename
                    string destFilename = BuildDestFilename(track);

                    // Determine destination directory and whether it requires creating a new artist folder
                    string destDir = GetDestDir(track, newArtistFolders, _compilationAlbums, out string reason, out bool isNewFolder);
                    entry.Reason = reason;
                    entry.IsNewFolder = isNewFolder;
                    entry.InBatchDuplicate = sf.InBatchDuplicate;
                    entry.CompilationAlbum = _compilationAlbums != null
                        && !string.IsNullOrEmpty(track.Album)
                        && _compilationAlbums.Contains(track.Album);

                    string destPath = Path.Combine(destDir, destFilename);

                    // Compute relative destination path for display
                    string relativeDest = destPath;
                    if (destPath.StartsWith(_roots.Library, StringComparison.OrdinalIgnoreCase))
                    {
                        int startIndex = _roots.Library.Length;
                        if (startIndex <= destPath.Length)
                        {
                            relativeDest = destPath.Substring(startIndex);
                            if (relativeDest.StartsWith("\\", StringComparison.Ordinal) || relativeDest.StartsWith("/", StringComparison.Ordinal))
                                relativeDest = relativeDest.Substring(1);
                        }
                    }

                    entry.Destination = relativeDest;
                    string routeSummary = GetRouteSummary(relativeDest);

                    // Album suffix shown on header line for quick context. New-folder flag shown on Route line.
                    string albumSuffix = (!string.IsNullOrEmpty(track.Album) && !track.Album.Equals(Constants.MissingTag))
                        ? $" [{track.Album}]" : "";
                    string routeNewNote = isNewFolder ? " *" : "";

                    // Dry run: a destination occupied only by the library copy this run replaces counts as free (staged in the real run)
                    // An L slot owned by another replacement is reserved (staged in the real run, planned in dry run): this file is a duplicate here.
                    if (IsReservedForOtherReplacement(destPath, sf.SourcePath)
                        || IsDestBlocked(destPath, dryRun && _dryReplacedLibPaths.Contains(destPath) ? destPath : null, plannedDests))
                    {
                        Console.WriteLine(FormatSkipLine(track.Artists, track.Title));
                        Console.WriteLine();
                        entry.Status = dryRun ? "would-skip" : "skipped"; entry.Detail = "already exists at destination";
                        logEntries.Add(entry); skippedCount++;
                        RollbackIfStaged(sf);
                    }
                    else if (dryRun)
                    {
                        var sb = new StringBuilder();
                        if (sf.InBatchDuplicate)
                            sb.AppendLine($"[WARN: IN-BATCH DUPLICATE] {Path.GetFileName(sf.SourcePath)}");
                        sb.AppendLine(FormatAutoLine(track.Artists, track.Title, albumSuffix));
                        foreach (var change in entry.TagChanges)
                            sb.AppendLine($" > {change}");
                        sb.AppendLine($" Route: {routeSummary}{routeNewNote}");
                        sb.AppendLine($" Reason: {reason}");
                        sb.AppendLine($" Path: {relativeDest.Replace('\\', '/')}");
                        sb.AppendLine();
                        dryRunRoutingOutputs.Add((relativeDest, sb.ToString()));
                        plannedDests.Add(destPath);
                        entry.Status = "would-move";
                        logEntries.Add(entry); movedCount++;
                    }
                    else
                    {
                        if (sf.InBatchDuplicate)
                            Console.WriteLine($"[WARN: IN-BATCH DUPLICATE] {Path.GetFileName(sf.SourcePath)}");
                        createdDirs = CreateDirectoryTracked(destDir);
                        if (_lStagedBySource.TryGetValue(sf.SourcePath, out string landingLibPath))
                            ReplaceGuard.MarkLanding(landingLibPath, destPath);
                        File.Move(sf.SourcePath, destPath);
                        CommitIfStaged(sf);
                        movedCount++;
                        Console.WriteLine(FormatAutoLine(track.Artists, track.Title, albumSuffix));
                        foreach (var change in entry.TagChanges)
                            Console.WriteLine($" > {change}");
                        Console.WriteLine($" Route: {routeSummary}{routeNewNote}");
                        Console.WriteLine($" Reason: {reason}");
                        Console.WriteLine($" Path: {relativeDest.Replace('\\', '/')}");
                        Console.WriteLine();
                        entry.Status = "moved";
                        logEntries.Add(entry);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine();
                    Console.WriteLine("===========================================================================");
                    Console.WriteLine($"[ERROR] Skipping file that failed to route: {Path.GetFileName(sf.SourcePath)}");
                    Console.WriteLine("===========================================================================");
                    Console.WriteLine($"Full path: {sf.SourcePath}");
                    Console.WriteLine($"Error details: {ex.Message}");
                    Console.WriteLine("The rest of the batch will continue.\n");
                    entry.Status = "error";
                    entry.Detail = $"routing failed: {ex.Message}";
                    logEntries.Add(entry); skippedCount++;
                    RollbackIfStaged(sf);
                    RemoveEmptyCreatedDirs(createdDirs);
                }
            }
            return dryRunRoutingOutputs;
        }

        /// <summary>Creates dir and any missing parents. Returns the folders this call created, deepest first.</summary>
        private static List<string> CreateDirectoryTracked(string dir)
        {
            var created = new List<string>();
            for (string d = dir; d != null && !Directory.Exists(d); d = Path.GetDirectoryName(d))
                created.Add(d);
            Directory.CreateDirectory(dir);
            return created;
        }

        /// <summary>Removes folders a failed move left behind: only the ones created by its CreateDirectoryTracked call, and only while empty.</summary>
        private static void RemoveEmptyCreatedDirs(List<string> created)
        {
            if (created == null) return;
            foreach (string d in created)
            {
                try
                {
                    if (Directory.Exists(d) && Directory.GetFileSystemEntries(d).Length == 0)
                        Directory.Delete(d);
                }
                catch (Exception) { /* best effort: a folder we cannot remove is left as it is */ }
            }
        }

        /// <summary>
        /// Pre-scans the batch to find artists that will hit the 3-song threshold.
        /// Checks existing Misc songs in AudioMirror XML + batch count.
        /// Returns a set of primary artist names that need a new Artists/ folder.
        /// Prints a preview of what will happen for those artists.
        /// </summary>
        internal HashSet<string> RunScanAhead(string[] batchFiles)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Console.Write($"\nScan-ahead: reading {batchFiles.Length} file(s)...");

            // Count artists in the incoming batch; also map album -> distinct primary artists for compilation detection.
            // Cache raw tag data here so PreScanFiles avoids a second TagLib read per file.
            var batchCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var spellings = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var batchAlbumCounts = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
            var albumArtists = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            var spellingPairs = new List<(string TrackPath, string ArtistTag)>();
            var tagCache = new Dictionary<string, (string artists, string title, string album, string genres, uint year)>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in batchFiles)
            {
                try
                {
                    using (TagLib.File tf = TagLib.File.Create(f))
                    {
                        string artists = tf.Tag.JoinedPerformers;
                        // Cache all fields PreScanFiles will need, so it can skip its own TagLib.File.Create
                        tagCache[f] = (artists, tf.Tag.Title, tf.Tag.Album, tf.Tag.JoinedGenres, tf.Tag.Year);
                        if (string.IsNullOrEmpty(artists)) continue;
                        // Normalize via TagFixer so artist-name variants (e.g. "JAŸ-Z" via U+0178)
                        // are resolved to their canonical form ("Jay-Z") before being used as dict keys.
                        // Without this, batchAlbumCounts stores "JAŸ-Z" but CountAlbumSongs queries
                        // "Jay-Z" (tag-fixed), and OrdinalIgnoreCase can't bridge the Unicode gap.
                        var fixedArtists = TagFixer.ExtractAndFixArtists(tf.Tag.Title ?? "", artists);
                        string primary = fixedArtists.Count > 0 ? fixedArtists[0].Trim() : Track.ProcessProperty(artists)[0].Trim();
                        if (string.IsNullOrEmpty(primary)) continue;
                        batchCounts[primary] = batchCounts.ContainsKey(primary) ? batchCounts[primary] + 1 : 1;
                        if (!spellings.TryGetValue(primary, out var spellList)) spellings[primary] = spellList = new List<string>();
                        spellList.Add(primary);
                        spellingPairs.Add((RelToNewMusic(f, _roots.NewMusic) ?? Path.GetFileName(f), primary));
                        string album = tf.Tag.Album;
                        if (!string.IsNullOrEmpty(album) && !album.Equals(Constants.MissingTag, StringComparison.OrdinalIgnoreCase))
                        {
                            // Normalize album to match how PreScanFiles cleans tags before routing.
                            // Without this, "The Blueprint (Explicit Version)" and "The Blueprint" are
                            // counted under different keys, causing CountAlbumSongs to return 0 for the batch.
                            album = RulesEngine.NormalizeAlbum(album);
                            if (!string.IsNullOrEmpty(album))
                            {
                                if (!albumArtists.ContainsKey(album))
                                    albumArtists[album] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                                albumArtists[album].Add(primary);
                                if (!batchAlbumCounts.ContainsKey(primary))
                                    batchAlbumCounts[primary] = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                                var byAlbum = batchAlbumCounts[primary];
                                byAlbum[album] = byAlbum.ContainsKey(album) ? byAlbum[album] + 1 : 1;
                            }
                        }
                    }
                }
                catch { /* skip unreadable files - no cache entry, PreScanFiles falls back to TagLib */ }
            }
            _scan = _scan.WithBatchTagCache(tagCache);

            // Count existing Misc songs by artist from AudioMirror XML
            var miscCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var miscFilesByArtist = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            string mirrorMiscPath = Path.GetFullPath(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, _roots.Mirror, Constants.MiscDir));
            if (Directory.Exists(mirrorMiscPath))
            {
                var miscXmlFiles = Directory.GetFiles(mirrorMiscPath, "*.xml");
                Console.Write($" checking Misc ({miscXmlFiles.Length})...");
                foreach (var xmlFile in miscXmlFiles)
                {
                    try
                    {
                        var xmlDoc = new System.Xml.XmlDocument();
                        xmlDoc.Load(xmlFile);
                        var artistsEl = xmlDoc.SelectSingleNode("//Artists");
                        if (artistsEl == null) continue;
                        string primary = Track.ProcessProperty(artistsEl.InnerText)[0].Trim();
                        if (string.IsNullOrEmpty(primary)) continue;
                        miscCounts[primary] = miscCounts.ContainsKey(primary) ? miscCounts[primary] + 1 : 1;
                        // Misc songs migrate into the new folder with their tags untouched, so their spelling votes too.
                        if (!spellings.TryGetValue(primary, out var miscSpell)) spellings[primary] = miscSpell = new List<string>();
                        miscSpell.Add(primary);
                        spellingPairs.Add((Path.GetFileName(xmlFile), primary));
                        if (!miscFilesByArtist.ContainsKey(primary))
                            miscFilesByArtist[primary] = new List<string>();
                        miscFilesByArtist[primary].Add(xmlFile);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"\n  [WARN] Skipped malformed XML '{Path.GetFileName(xmlFile)}': {ex.Message}");
                    }
                }
            }

            // Count existing Sources/ songs by artist (Films, Shows, Anime) from AudioMirror XML.
            // Sources/ tracks count toward the 3-song threshold but are NOT migrated - they stay
            // in Sources/ regardless of whether the artist gets a new Artists/ folder.
            var sourcesCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            string mirrorSourcesPath = Path.Combine(_roots.Mirror, Constants.SourcesDir);
            if (Directory.Exists(mirrorSourcesPath))
            {
                var sourcesXmlFiles = Directory.GetFiles(mirrorSourcesPath, "*.xml", SearchOption.AllDirectories);
                Console.Write($" checking Sources ({sourcesXmlFiles.Length})...");
                foreach (var xmlFile in sourcesXmlFiles)
                {
                    try
                    {
                        var xmlDoc = new System.Xml.XmlDocument();
                        xmlDoc.Load(xmlFile);
                        var artistsEl = xmlDoc.SelectSingleNode("//Artists");
                        if (artistsEl == null) continue;
                        string primary = Track.ProcessProperty(artistsEl.InnerText)[0].Trim();
                        if (string.IsNullOrEmpty(primary)) continue;
                        sourcesCounts[primary] = sourcesCounts.ContainsKey(primary) ? sourcesCounts[primary] + 1 : 1;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"\n  [WARN] Skipped malformed XML '{Path.GetFileName(xmlFile)}': {ex.Message}");
                    }
                }
            }
            Console.WriteLine(" done.");

            // Find artists that will hit 3+ threshold and don't already have an Artists/ folder
            var previewLines = new List<string>();
            foreach (var kvp in batchCounts)
            {
                string artist = kvp.Key;
                int batchCount = kvp.Value;
                int miscCount = miscCounts.ContainsKey(artist) ? miscCounts[artist] : 0;
                int sourcesCount = sourcesCounts.ContainsKey(artist) ? sourcesCounts[artist] : 0;
                int total = batchCount + miscCount + sourcesCount;

                string artistFolder = Path.Combine(_roots.Library, Constants.ArtistsDir, SanitiseFolderName(artist));
                string musivArtistFolder = Path.Combine(_roots.Library, Constants.MusivDir, SanitiseFolderName(artist));
                bool hasExistingFolder = Directory.Exists(artistFolder) || Directory.Exists(musivArtistFolder);
                if (RulesEngine.EarnsArtistFolder(total) && !hasExistingFolder)
                {
                    result.Add(artist);
                    string note;
                    if (miscCount > 0 && sourcesCount > 0)
                        note = $"{batchCount} in batch + {miscCount} in Misc + {sourcesCount} in Sources = {total} total -> new Artists/{artist}/";
                    else if (miscCount > 0)
                        note = $"{batchCount} in batch + {miscCount} in Misc = {total} total -> new Artists/{artist}/";
                    else if (sourcesCount > 0)
                        note = $"{batchCount} in batch + {sourcesCount} in Sources = {total} total -> new Artists/{artist}/";
                    else
                        note = $"{batchCount} in batch = {total} total -> new Artists/{artist}/";
                    if (miscCount > 0)
                        note += $" ({miscCount} existing Misc song(s) will be auto-migrated)";
                    previewLines.Add($"  - {note}");
                }
            }

            if (result.Count > 0)
            {
                Console.WriteLine($"\nScan-ahead: {result.Count} artist(s) will hit 3-song threshold:");
                foreach (var line in previewLines) Console.WriteLine(line);
                Console.WriteLine();
            }

            // Detect compilation albums: 3+ distinct primary artists on same album in this batch
            var compilationAlbums = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in albumArtists)
            {
                if (RulesEngine.IsCompilation(kvp.Value.Count))
                    compilationAlbums.Add(kvp.Key);
            }
            _scan = _scan.WithCompilationAlbums(compilationAlbums);
            if (compilationAlbums.Count > 0)
            {
                Console.WriteLine($"Scan-ahead: {compilationAlbums.Count} compilation album(s) detected:");
                foreach (var album in compilationAlbums)
                    Console.WriteLine($"  - '{album}' ({albumArtists[album].Count} distinct artists) -> Compilations/{album}/");
                Console.WriteLine();
            }

            // Track Misc XMLs for promoted artists so RunMiscMigration can move them post-routing
            foreach (var artist in result)
            {
                if (miscFilesByArtist.TryGetValue(artist, out var xmlPaths) && xmlPaths.Count > 0)
                    _miscMigrationCandidates[artist] = xmlPaths;
            }

            // Store counts so GetDestDir can build a full routing reason for Misc fallback
            var scanSpellings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _scan = _scan.WithBatchCountsAndSpellings(batchCounts, scanSpellings);
            foreach (var kv in spellings)
            {
                scanSpellings[kv.Key] = FolderSpellingFor(kv.Key, _artistSpellingChoices, ChooseCanonicalSpelling(kv.Value));
                var distinct = new HashSet<string>(kv.Value, StringComparer.Ordinal);
                if (distinct.Count > 1)
                    Console.WriteLine($"  [review] '{kv.Key}' is spelled {distinct.Count} ways in the batch and Misc ({string.Join(", ", distinct)}); folder uses '{scanSpellings[kv.Key]}', tracks tagged otherwise are listed in their routing reason. Tags are not edited.");
            }
            var spellingCandidates = ArtistSpellingCandidates.BuildSpellingCandidates(spellingPairs);
            _scan = _scan.WithFinalCounts(spellingCandidates, batchAlbumCounts, miscCounts, sourcesCounts);

            return result;
        }

        private void PrintRouteDistributionPreview(List<ScannedFile> scannedFiles, HashSet<string> newArtistFolders) => Report.PrintRouteDistributionPreview(scannedFiles, newArtistFolders);

        /// <summary>
        /// Prints routing errors, the moved/skipped count, timing, confidence report (real run), then
        /// runs Misc migration, folder cleanup, and the "Finished" banner.
        /// </summary>
        private void PrintRoutingResultsAndFinish(List<LogEntry> logEntries, int movedCount, int skippedCount, int totalFiles, System.Diagnostics.Stopwatch routingStopwatch)
        {
            var routingErrors = logEntries.Where(e => e.Status == "error").ToList();
            if (routingErrors.Count > 0)
            {
                Console.WriteLine($"[ERRORS: {routingErrors.Count}]");
                foreach (var e in routingErrors) Console.WriteLine($" {e.Filename}: {e.Detail}");
                Console.WriteLine();
            }
            Console.WriteLine(dryRun ? $"Routed: {movedCount}  |  Skipped: {skippedCount}" : $"Moved: {movedCount}  |  Skipped: {skippedCount}");
            Console.WriteLine();
            Console.WriteLine($"Routing - time taken: {Doer.ConvertTimeSpanToString(routingStopwatch.Elapsed)}");

            ConfidenceRecord confidenceRecord = null;
            if (!dryRun)
            {
                // Unconditional on a real run - not gated on jsonOutput. This keeps a client's
                // `args` untouched (execution record contract, section 1):
                // the exe emits the execution record on its own, no new CLI flag needed.
                // Known scope limit (accepted, not "fixed" here): RunMiscMigration/CleanupNewMusicFolder
                // below move library files that are not in logEntries, so the record does not cover them.
                confidenceRecord = PrintConfidenceReport(logEntries, totalFiles, movedCount, skippedCount);
            }

            // The execution record is written AFTER cleanup so its "leftovers" block is a snapshot of
            // NewMusic as the run left it. Nothing in RunMiscMigration or CleanupNewMusicFolder reads the
            // record, and BuildCurrentSummary reads only _miscMigrationCandidates, which RunMiscMigration
            // does not change. The finally keeps the record on a throw in either step, as it was before.
            try
            {
                RunMiscMigration();
                CleanupNewMusicFolder();
            }
            finally
            {
                if (!dryRun) WriteExecutionRecord(logEntries, confidenceRecord);
            }

            Console.WriteLine("\n============================================================");
            Console.WriteLine("Finished");
            Console.WriteLine("============================================================");
        }

        /// <summary>
        /// Migrates existing Misc songs to Artists/{artist}/Singles/ for artists promoted this batch.
        /// Runs in both dry-run (shows what would happen) and real mode (moves files).
        /// No-op if no artists were promoted with existing Misc songs.
        /// </summary>
        /// <summary>Folder the Misc migration journal (misc-migration-*.json) is written to; tests point it at a temp folder.</summary>
        internal string MigrationJournalDir = null;

        /// <summary>
        /// Rewrites the migration journal atomically (schemaVersion 1). Each move is {"from", "to"} plus an optional
        /// "retag": {"field": "artist", "from": old tag entry, "to": folder spelling}, present only when the tag was retagged.
        /// </summary>
        private static void WriteMiscMigrationJournal(string dir, string path, List<MiscMigrationMove> moves)
        {
            try
            {
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("{\"recordType\": \"miscMigration\", \"schemaVersion\": 1, \"moves\": [");
                for (int i = 0; i < moves.Count; i++)
                {
                    var m = moves[i];
                    string line = "  {\"from\": " + JsonText.Quote(m.From) + ", \"to\": " + JsonText.Quote(m.To);
                    if (m.RetagFrom != null)
                        line += ", \"retag\": {\"field\": \"artist\", \"from\": " + JsonText.Quote(m.RetagFrom) + ", \"to\": " + JsonText.Quote(m.RetagTo) + "}";
                    sb.AppendLine(line + "}" + (i < moves.Count - 1 ? "," : ""));
                }
                sb.AppendLine("]}");
                AtomicFile.WriteAllText(path, sb.ToString(), new System.Text.UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  [WARN] Could not write the Misc migration journal: {ex.Message}");
            }
        }

        internal int RunMiscMigration()
        {
            if (_miscMigrationCandidates.Count == 0) return 0;
            var journaledMoves = new List<MiscMigrationMove>();
            string journalDir = MigrationJournalDir ?? _roots.Logs;
            string journalPath = Path.Combine(journalDir, $"misc-migration-{DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.json");

            Console.WriteLine("\n===========================================================================");
            Console.WriteLine(dryRun ? "Misc Migration (Dry Run)" : "Misc Migration");
            Console.WriteLine("===========================================================================");

            int totalCount = 0;
            int failedCount = 0;
            foreach (var kvp in _miscMigrationCandidates)
            {
                string artist = kvp.Key;
                var xmlPaths = kvp.Value;
                string destFolder = Path.Combine(_libraryPath, Constants.ArtistsDir, SanitiseFolderName(artist), Constants.SinglesDir);
                string relDest = $"Artists\\{SanitiseFolderName(artist)}\\{Constants.SinglesDir}";

                Console.WriteLine($"\n  {artist}: {xmlPaths.Count} song(s) -> {relDest}");

                foreach (var xmlPath in xmlPaths)
                {
                    string libPath = DeriveLibraryPathFromMirrorPath(xmlPath);
                    string fileName = Path.GetFileName(libPath);

                    if (!File.Exists(libPath))
                    {
                        if (_lDeletedLibraryPaths.Contains(libPath))
                            Console.WriteLine($"  [INFO] Already deleted by duplicate resolution: {fileName}");
                        else
                            Console.WriteLine($"  [WARN] Not found in library (stale mirror?): {fileName}");
                        continue;
                    }

                    if (dryRun)
                    {
                        // Same outcome as the real File.Move below, which throws IOException when the destination exists
                        if (File.Exists(Path.Combine(destFolder, fileName)))
                        {
                            Console.WriteLine($"  [ERROR] Misc migration would fail for {fileName}: destination already exists");
                            failedCount++;
                        }
                        else
                        {
                            Console.WriteLine($"[DRY RUN] Would move: {fileName}");
                            string oldArtist = ReadCaseOnlyArtistEntry(libPath, artist);
                            if (oldArtist != null)
                            {
                                Console.WriteLine($"[DRY RUN] Would retag artist '{oldArtist}' -> '{artist}': {fileName}");
                                string plannedRename = RetagRenameTarget(Path.Combine(destFolder, fileName), oldArtist, artist);
                                if (plannedRename != null)
                                    Console.WriteLine($"[DRY RUN] Would rename '{fileName}' -> '{Path.GetFileName(plannedRename)}'");
                            }
                            totalCount++;
                        }
                    }
                    else
                    {
                        // Per-file guard: one failed move must not abort the rest of the migration
                        List<string> createdDirs = null;
                        try
                        {
                            createdDirs = CreateDirectoryTracked(destFolder);
                            string destPath = Path.Combine(destFolder, fileName);
                            File.Move(libPath, destPath);
                            var move = new MiscMigrationMove { From = libPath, To = destPath };
                            journaledMoves.Add(move);
                            WriteMiscMigrationJournal(journalDir, journalPath, journaledMoves);
                            Console.WriteLine($"  Moved: {fileName}");
                            totalCount++;

                            // BL-149: the move is journaled first, so a failed retag keeps the move and its journal entry
                            string oldArtist = ReadCaseOnlyArtistEntry(destPath, artist);
                            if (oldArtist != null)
                            {
                                try
                                {
                                    ReplaceArtistTagEntry(destPath, oldArtist, artist);
                                    move.RetagFrom = oldArtist;
                                    move.RetagTo = artist;
                                    WriteMiscMigrationJournal(journalDir, journalPath, journaledMoves);
                                    Console.WriteLine($"  Retagged artist '{oldArtist}' -> '{artist}': {fileName}");

                                    // BL-151: the file name takes the folder spelling too; a failed rename keeps the name and the move
                                    string renamedPath = RetagRenameTarget(destPath, oldArtist, artist);
                                    if (renamedPath != null)
                                    {
                                        try
                                        {
                                            File.Move(destPath, renamedPath);
                                            move.To = renamedPath;
                                            WriteMiscMigrationJournal(journalDir, journalPath, journaledMoves);
                                            Console.WriteLine($"  Renamed: {fileName} -> {Path.GetFileName(renamedPath)}");
                                        }
                                        catch (Exception nex)
                                        {
                                            Console.WriteLine($"  [WARN] Rename failed for {fileName} (the name is kept): {nex.Message}");
                                        }
                                    }
                                }
                                catch (Exception rex)
                                {
                                    failedCount++;
                                    Console.WriteLine($"  [ERROR] Artist retag failed for {fileName} (the move is kept): {rex.Message}");
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            failedCount++;
                            RemoveEmptyCreatedDirs(createdDirs);
                            Console.WriteLine($"  [ERROR] Misc migration failed for {fileName}: {ex.Message}");
                        }
                    }
                }
            }

            Console.WriteLine();
            Console.WriteLine(dryRun
                ? $"Would migrate: {totalCount} Misc song(s), {failedCount} would fail"
                : $"Migrated: {totalCount} Misc song(s), {failedCount} failed");
            return failedCount;
        }

        /// <summary>
        /// BL-149: the old ARTIST tag entry that equals artist ignoring case but not exactly (Performers first, then
        /// AlbumArtists), or null when no retag is due. Read-only: the file is opened and never saved. A read failure
        /// prints a WARN and gives null, so it never aborts the migration.
        /// </summary>
        private static string ReadCaseOnlyArtistEntry(string path, string artist)
        {
            try
            {
                using (var tf = TagLib.File.Create(path))
                    return CaseOnlyArtistEntry(tf.Tag.Performers, artist) ?? CaseOnlyArtistEntry(tf.Tag.AlbumArtists, artist);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  [WARN] Could not read tags for the artist retag check: {Path.GetFileName(path)}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// BL-151: the file name with its artist entry equal to oldEntry (ignoring case) replaced by newEntry, or null when
        /// no entry matches. Only the artist part before the first " - " is read; entries there are separated by "; ".
        /// </summary>
        internal static string RetaggedFileName(string fileName, string oldEntry, string newEntry)
        {
            int sep = fileName.IndexOf(" - ", StringComparison.Ordinal);
            if (sep <= 0) return null;
            string[] entries = fileName.Substring(0, sep).Split(new[] { "; " }, StringSplitOptions.None);
            bool changed = false;
            for (int i = 0; i < entries.Length; i++)
            {
                if (string.Equals(entries[i], oldEntry, StringComparison.OrdinalIgnoreCase) && !string.Equals(entries[i], newEntry, StringComparison.Ordinal))
                {
                    entries[i] = newEntry;
                    changed = true;
                }
            }
            return changed ? string.Join("; ", entries) + fileName.Substring(sep) : null;
        }

        /// <summary>
        /// BL-151: the renamed path for a retagged file at currentPath, or null when no rename is due. A name held by
        /// another file gives a WARN and null, so the old name is kept. A name differing from the current one only by case
        /// is the same file on Windows, so it is not a clash.
        /// </summary>
        private static string RetagRenameTarget(string currentPath, string oldEntry, string newEntry)
        {
            string newName = RetaggedFileName(Path.GetFileName(currentPath), oldEntry, newEntry);
            if (newName == null) return null;
            string target = Path.Combine(Path.GetDirectoryName(currentPath), newName);
            if (File.Exists(target) && !string.Equals(target, currentPath, StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"  [WARN] Rename skipped, '{newName}' already exists: {Path.GetFileName(currentPath)}");
                return null;
            }
            return target;
        }

        private static string CaseOnlyArtistEntry(string[] entries, string artist)
        {
            if (entries == null) return null;
            foreach (string e in entries)
                if (e != null && string.Equals(e, artist, StringComparison.OrdinalIgnoreCase) && !string.Equals(e, artist, StringComparison.Ordinal))
                    return e;
            return null;
        }

        /// <summary>
        /// Replaces every Performers and AlbumArtists entry equal to oldEntry (exact match) with newEntry, then saves.
        /// Other entries are kept as they are. A tag field with no matching entry is not written.
        /// </summary>
        private static void ReplaceArtistTagEntry(string path, string oldEntry, string newEntry)
        {
            using (var tf = TagLib.File.Create(path))
            {
                if (tf.Tag.Performers != null && tf.Tag.Performers.Contains(oldEntry))
                    tf.Tag.Performers = tf.Tag.Performers.Select(e => e == oldEntry ? newEntry : e).ToArray();
                if (tf.Tag.AlbumArtists != null && tf.Tag.AlbumArtists.Contains(oldEntry))
                    tf.Tag.AlbumArtists = tf.Tag.AlbumArtists.Select(e => e == oldEntry ? newEntry : e).ToArray();
                tf.Save();
            }
        }

        /// <summary>
        /// Undoes a Misc migration from its journal (misc-migration-*.json), newest move first. A song moves back only when
        /// its migrated copy exists and its old path is free, so nothing is overwritten. A move carrying a "retag" gets its
        /// old ARTIST tag entry restored BEFORE the move back, so a failed retag leaves the file unmoved (Failed). A failed
        /// move after a retag puts the migrated tag back (best effort). Every from and to must resolve under the library root
        /// (_libraryPath), else that entry is refused (Failed) and nothing is touched. Dry run prints what would be reverted and changes nothing;
        /// its counts are the moves that would be reverted. Throws when the journal is not a Misc migration journal.
        /// </summary>
        internal MiscRevertResult RevertMiscMigration(string journalPath)
        {
            var result = new MiscRevertResult();
            var ser = new System.Web.Script.Serialization.JavaScriptSerializer();
            var root = ser.DeserializeObject(File.ReadAllText(journalPath)) as Dictionary<string, object>;
            if (root == null || !"miscMigration".Equals(root.TryGetValue("recordType", out object rt) ? rt as string : null))
                throw new InvalidDataException("not a Misc migration journal: " + journalPath);
            var moves = root.TryGetValue("moves", out object mv) ? mv as object[] : null;
            if (moves == null) return result;
            // Every journal path must resolve under the library root: the Misc migration only ever moves files inside it
            string libRoot = Path.GetFullPath(_libraryPath).TrimEnd('\\', '/') + "\\";
            for (int i = moves.Length - 1; i >= 0; i--)
            {
                string fileName = null;
                try
                {
                    var m = (Dictionary<string, object>)moves[i];
                    string from = Path.GetFullPath((string)m["from"]);
                    string to = Path.GetFullPath((string)m["to"]);
                    fileName = Path.GetFileName(from);
                    if (!from.StartsWith(libRoot, StringComparison.OrdinalIgnoreCase) || !to.StartsWith(libRoot, StringComparison.OrdinalIgnoreCase))
                    {
                        Console.WriteLine($"  [ERROR] Revert refused for {fileName}: a journal path is outside the library");
                        result.Failed++;
                        continue;
                    }
                    if (!File.Exists(to) || File.Exists(from))
                    {
                        Console.WriteLine($"  [SKIP] Revert: {fileName} is not at its migrated place, or its old place is taken");
                        result.Skipped++;
                        continue;
                    }
                    if (dryRun)
                    {
                        Console.WriteLine($"[DRY RUN] Would revert: {fileName}");
                        result.Reverted++;
                        continue;
                    }

                    // Retag where the file is now, before the move: a failed retag leaves the file in place and counted as failed,
                    // so a re-run still sees it at its migrated place
                    Dictionary<string, object> retag = m.ContainsKey("retag") ? (Dictionary<string, object>)m["retag"] : null;
                    if (retag != null)
                        ReplaceArtistTagEntry(to, (string)retag["to"], (string)retag["from"]);
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(from));
                        File.Move(to, from);
                    }
                    catch
                    {
                        // The move failed after a successful retag: put the migrated tag back so the file matches its journal state
                        if (retag != null) RestoreMigratedTagBestEffort(to, (string)retag["from"], (string)retag["to"]);
                        throw;
                    }
                    Console.WriteLine($"  Reverted: {fileName}");
                    result.Reverted++;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  [ERROR] Revert failed for {fileName ?? "a journal entry"}: {ex.Message}");
                    result.Failed++;
                }
            }
            return result;
        }

        /// <summary>Best-effort undo of a retag whose move failed. A failure here is printed, never thrown.</summary>
        private static void RestoreMigratedTagBestEffort(string path, string fromEntry, string toEntry)
        {
            try
            {
                ReplaceArtistTagEntry(path, fromEntry, toEntry);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  [WARN] Could not restore the migrated artist tag on {Path.GetFileName(path)}: {ex.Message}");
            }
        }

        /// <summary>
        /// Cleans up empty subdirectories in the NewMusic staging folder after integration.
        /// Removes every subfolder with no file at any depth, deepest first. A folder holding any file, and its
        /// parents, are kept; the root is never removed. Remaining files are reported, never blocking the cleanup.
        /// No-op if NewMusic doesn't exist or has no subdirectories to clean.
        /// </summary>
        private void CleanupNewMusicFolder() => CleanupNewMusicFolder(_roots.NewMusic);

        /// <summary>Forwards to RoutingJsonWriter.ComputeLeftovers.</summary>
        internal static LeftoversSnapshot ComputeLeftovers(string newMusicPath, int declinedCount) =>
            RoutingJsonWriter.ComputeLeftovers(newMusicPath, declinedCount);

        /// <summary>Path seam for CleanupNewMusicFolder: same behaviour, operating on newMusicPath.</summary>
        internal void CleanupNewMusicFolder(string newMusicPath)
        {
            if (!Directory.Exists(newMusicPath)) return;

            // A folder is kept when a file sits anywhere under it (its parents included); files that remain
            // (integration skipped them, or they are leftovers like .tagfix.bak) never block the removal of
            // folders with no file at any depth.
            var remainingFiles = Directory.GetFiles(newMusicPath, "*", SearchOption.AllDirectories);
            var nonEmpty = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string file in remainingFiles)
            {
                string dir = Path.GetDirectoryName(file);
                while (dir != null && dir.Length > newMusicPath.Length && nonEmpty.Add(dir))
                    dir = Path.GetDirectoryName(dir);
            }

            // Collect empty subdirectories, deepest first so parents are empty by the time we reach them
            var emptyDirs = Directory.GetDirectories(newMusicPath, "*", SearchOption.AllDirectories)
                .Where(d => !nonEmpty.Contains(d))
                .OrderByDescending(d => d.Length)
                .ToList();

            if (emptyDirs.Count == 0)
            {
                if (remainingFiles.Length > 0)
                    Console.WriteLine($"\n[INFO] NewMusic cleanup: {remainingFiles.Length} file(s) remain, 0 empty folder(s) removed. Review remaining files manually.");
                return;
            }

            Console.WriteLine("\n===========================================================================");
            Console.WriteLine(dryRun ? "NewMusic Cleanup (Dry Run)" : "NewMusic Cleanup");
            Console.WriteLine("===========================================================================");

            int cleanedCount = 0;
            foreach (var dir in emptyDirs)
            {
                string relDir = dir.StartsWith(newMusicPath, StringComparison.OrdinalIgnoreCase)
                    ? dir.Substring(newMusicPath.Length).TrimStart('\\', '/')
                    : dir;

                if (dryRun)
                {
                    Console.WriteLine($"[DRY RUN] Would remove empty folder: {relDir}");
                    cleanedCount++;
                }
                else
                {
                    try
                    {
                        if (Directory.Exists(dir) && Directory.GetFileSystemEntries(dir).Length == 0)
                        {
                            Directory.Delete(dir);
                            Console.WriteLine($"  Removed empty folder: {relDir}");
                            cleanedCount++;
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"  [WARN] Could not remove '{relDir}': {ex.Message}");
                    }
                }
            }

            Console.WriteLine();
            Console.WriteLine(dryRun
                ? $"Would remove: {cleanedCount} empty folder(s)"
                : $"Cleaned up: {cleanedCount} empty folder(s)");
            if (remainingFiles.Length > 0)
                Console.WriteLine(dryRun
                    ? $"[INFO] NewMusic cleanup: {remainingFiles.Length} file(s) remain, {cleanedCount} empty folder(s) would be removed. Review remaining files manually."
                    : $"[INFO] NewMusic cleanup: {remainingFiles.Length} file(s) remain, {cleanedCount} empty folder(s) removed. Review remaining files manually.");
        }

        /// <summary>
        /// Forwards to RoutingJsonWriter.BuildJson, which holds the routing and execution JSON contract.
        /// </summary>
        internal static string BuildJson(List<LogEntry> entries, BatchSummary summary = null, bool dryRun = true, ConfidenceRecord confidence = null,
            MirrorPathBlock blocked = null, IList<(string original, string held)> replaced = null,
            string newMusicFingerprint = null, string previewFingerprint = null, IList<ArtistSpellingCandidate> artistSpellingCandidates = null,
            LeftoversSnapshot leftovers = null) =>
            RoutingJsonWriter.BuildJson(entries, summary, dryRun, confidence, blocked, replaced, newMusicFingerprint, previewFingerprint, artistSpellingCandidates, leftovers);

        /// <summary>Builds the BatchSummary from the integrator's current batch-level state
        /// (Misc auto-migration candidates, detected compilation albums). Shared by WriteJsonOutput
        /// and WriteExecutionRecord so both emit an identical summary block for the same run.</summary>
        /// <summary>Forwards to RoutingJsonWriter.NewMusicFingerprint.</summary>
        internal static string NewMusicFingerprint(string folder) =>
            RoutingJsonWriter.NewMusicFingerprint(folder);

        /// <summary>Forwards to RoutingJsonWriter.ReadLatestPreviewFingerprint.</summary>
        internal static string ReadLatestPreviewFingerprint(string logsDir) =>
            RoutingJsonWriter.ReadLatestPreviewFingerprint(logsDir);

        private string _startFingerprint;
        private string _previewFingerprint;

        private BatchSummary BuildCurrentSummary()
        {
            var summary = new BatchSummary();
            foreach (var kvp in _miscMigrationCandidates)
                summary.MiscAutoMigrations[kvp.Key] = kvp.Value?.Count ?? 0;
            if (_compilationAlbums != null)
                summary.CompilationAlbums.AddRange(_compilationAlbums);
            return summary;
        }

        private void WriteJsonOutput(List<LogEntry> entries, MirrorPathBlock blocked = null)
        {
            try
            {
                string timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
                if (!Directory.Exists(_roots.Logs))
                    Directory.CreateDirectory(_roots.Logs);
                string jsonPath = Path.Combine(_roots.Logs, $"routing-{timestamp}.json");

                AtomicFile.WriteAllText(jsonPath, BuildJson(entries, BuildCurrentSummary(), dryRun: dryRun, blocked: blocked, newMusicFingerprint: _startFingerprint,
                    artistSpellingCandidates: _artistSpellingCandidates), System.Text.Encoding.UTF8);
                Console.WriteLine($"\n  JSON: {jsonPath}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"\n  [WARN] JSON output failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Writes a real run's execution record to data/logs/execution-{timestamp}.json - the durable,
        /// structured account of what actually moved, distinct from the dry run's routing-*.json
        /// (see the execution record contract, section 2). Unconditional on a
        /// real run, not gated on jsonOutput. A failed write must never fail an integration run that
        /// already moved files, so failures are caught and warned, never thrown.
        ///
        /// On a write failure, attempts exactly one retry to an alternate "-retry" filename before
        /// giving up - the
        /// dominant real-world cause of the primary write failing is transient (antivirus/indexer
        /// holding the just-created file), and a retry to a different filename costs nothing on the
        /// success path. A retry success prints the SAME "EXECUTION JSON:" marker a client matches on,
        /// so no client-side change is needed to recognise it. Total failure is promoted from [WARN] to
        /// [ERROR] and reworded, since a client now treats an absent record as a hard verification
        /// failure rather than a soft degrade - this line is for a human reading
        /// the run logs folder, not parsed by a client.
        /// </summary>
        private void WriteExecutionRecord(List<LogEntry> entries, ConfidenceRecord confidence)
        {
            string timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            string json = BuildJson(entries, BuildCurrentSummary(), dryRun: false, confidence: confidence, replaced: ReplaceGuard.Retired,
                newMusicFingerprint: _startFingerprint, previewFingerprint: _previewFingerprint, artistSpellingCandidates: _artistSpellingCandidates,
                leftovers: ComputeLeftovers(_roots.NewMusic, _declinedCount));
            WriteExecutionRecordWithRetry(_roots.Logs, timestamp, json, Console.WriteLine);
        }

        /// <summary>Forwards to RoutingJsonWriter.WriteExecutionRecordWithRetry.</summary>
        internal static bool WriteExecutionRecordWithRetry(string logsPath, string timestamp, string json, Action<string> log) =>
            RoutingJsonWriter.WriteExecutionRecordWithRetry(logsPath, timestamp, json, log);

        private static string JStr(string s) => RoutingJsonWriter.JStr(s);

        internal ConfidenceRecord PrintConfidenceReport(List<LogEntry> entries, int totalFiles, int movedCount, int skippedCount) => Report.PrintConfidenceReport(entries, totalFiles, movedCount, skippedCount);

        // Per call, not cached: the test constructor sets _roots after chaining, so a cached instance could hold stale roots.
        private MirrorIndex Mirror => new MirrorIndex(_roots);

        internal static Dictionary<string, string> LoadArtistAliases(string path = null) => MirrorIndex.LoadArtistAliases(path);

        internal static IEnumerable<string> GetAliasExpandedKeys(string primaryArtist, string title, Dictionary<string, string> aliases, Dictionary<string, List<string>> reverseAliases) => MirrorIndex.GetAliasExpandedKeys(primaryArtist, title, aliases, reverseAliases);

        internal static string NormaliseTitleForDedup(string title) => MirrorIndex.NormaliseTitleForDedup(title);

        internal static void AddIndexKey(ConcurrentDictionary<string, string> index, string key, string xmlFile) => MirrorIndex.AddIndexKey(index, key, xmlFile);

        private Dictionary<string, string> BuildMirrorIndex() => Mirror.BuildMirrorIndex();

        private string FindDuplicateInMirror(Track track, Dictionary<string, string> mirrorIndex) => Mirror.FindDuplicateInMirror(track, mirrorIndex);

        internal string DeriveLibraryPathFromMirrorPath(string mirrorXmlPath) => Mirror.DeriveLibraryPathFromMirrorPath(mirrorXmlPath);

        internal static string GetRouteCategory(string destDir) => DestinationRouter.GetRouteCategory(destDir);

        internal static string GetRouteCategory(string destDir, string libraryRoot) => DestinationRouter.GetRouteCategory(destDir, libraryRoot);

        private string GetRouteSummary(string relativeDest) => Router.GetRouteSummary(relativeDest);

        private void ReadMirrorTrackInfo(string xmlPath, out string trackDisplay, out string album) => Mirror.ReadMirrorTrackInfo(xmlPath, out trackDisplay, out album);

        private static int CountLibraryAlbumTracks(string libraryFilePath) => LibraryCounter.CountLibraryAlbumTracks(libraryFilePath);

        internal static bool IsDeluxeVersionOf(string candidate, string baseAlbum) => LibraryCounter.IsDeluxeVersionOf(candidate, baseAlbum);

        internal bool IsAlbumFolderCompilation(string xmlPath) => Mirror.IsAlbumFolderCompilation(xmlPath);

        /// <summary>
        /// Flags ScannedFile entries where the same normalised artist+title appears more than once
        /// in the batch. Sets InBatchDuplicate = true on all files sharing a key. Non-blocking.
        /// </summary>
        internal static void MarkInBatchDuplicates(List<ScannedFile> scannedFiles)
        {
            var keyGroups = new Dictionary<string, List<ScannedFile>>(StringComparer.Ordinal);
            foreach (var sf in scannedFiles.Where(f => f.IsReadable && f.Track != null))
            {
                string key = sf.Track.PrimaryArtist.ToLowerInvariant() + "\0" + NormaliseTitleForDedup(sf.Track.Title).ToLowerInvariant();
                if (!keyGroups.ContainsKey(key)) keyGroups[key] = new List<ScannedFile>();
                keyGroups[key].Add(sf);
            }
            foreach (var group in keyGroups.Values.Where(g => g.Count > 1))
                foreach (var sf in group)
                    if (group.Any(o => !ReferenceEquals(o, sf) && !LengthsDiffer(sf.LengthSec, o.LengthSec)))
                        sf.InBatchDuplicate = true;
        }

        /// <summary>
        /// Pre-scans all files in the batch: reads and cleans tags, finds duplicates.
        /// No UI interaction. Returns a ScannedFile list for the duplicate review and routing phases.
        /// </summary>
        private List<ScannedFile> PreScanFiles(string[] files, Dictionary<string, List<string>> tagChanges, Dictionary<string, TagFixer.FixLog> tagLogs = null)
        {
            var mirrorIndex = BuildMirrorIndex();
            var tagExceptions = TagFixExceptions.Load(); // NotDuplicate rows in config/tagfix-exceptions.xml
            var result = new List<ScannedFile>();
            foreach (var sourcePath in files)
            {
                var entry = new LogEntry { Filename = Path.GetFileName(sourcePath), SourceRel = RelToNewMusic(sourcePath, _roots.NewMusic) };
                var sf = new ScannedFile { SourcePath = sourcePath, LogEntry = entry };
                try
                {
                    Track track = new Track();
                    if (_batchTagCache != null && _batchTagCache.TryGetValue(sourcePath, out var cached))
                    {
                        // Use cached tags from RunScanAhead - avoids a second TagLib read per file
                        track.Title   = string.IsNullOrEmpty(cached.title)   ? Constants.MissingTag : cached.title;
                        track.Artists = string.IsNullOrEmpty(cached.artists) ? Constants.MissingTag : cached.artists;
                        track.Album   = string.IsNullOrEmpty(cached.album)   ? Constants.MissingTag : cached.album;
                        track.Genres  = string.IsNullOrEmpty(cached.genres)  ? Constants.MissingTag : cached.genres;
                        track.Year    = (cached.year == 0)                   ? Constants.MissingTag : cached.year.ToString();
                    }
                    else
                    {
                        using (TagLib.File tagFile = TagLib.File.Create(sourcePath))
                        {
                            Tag tag = tagFile.Tag;
                            track.Title   = string.IsNullOrEmpty(tag.Title)           ? Constants.MissingTag : tag.Title;
                            track.Artists = string.IsNullOrEmpty(tag.JoinedPerformers) ? Constants.MissingTag : tag.JoinedPerformers;
                            track.Album   = string.IsNullOrEmpty(tag.Album)           ? Constants.MissingTag : tag.Album;
                            track.Genres  = string.IsNullOrEmpty(tag.JoinedGenres)    ? Constants.MissingTag : tag.JoinedGenres;
                            track.Year    = (tag.Year == 0)                           ? Constants.MissingTag : tag.Year.ToString();
                        }
                    }

                    // Inherit album from immediate subfolder name when tag is missing.
                    // Files grouped under an album folder in NewMusic but lacking the album tag
                    // would otherwise all route to Singles regardless of batch count.
                    if (track.Album.Equals(Constants.MissingTag, StringComparison.OrdinalIgnoreCase)
                        && sourcePath.StartsWith(_roots.NewMusic, StringComparison.OrdinalIgnoreCase))
                    {
                        string relPath = sourcePath.Substring(_roots.NewMusic.Length).TrimStart(Path.DirectorySeparatorChar);
                        int sep = relPath.IndexOf(Path.DirectorySeparatorChar);
                        if (sep > 0)
                            track.Album = relPath.Substring(0, sep);
                    }

                    if (dryRun && !track.Title.Equals(Constants.MissingTag) && !track.Artists.Equals(Constants.MissingTag))
                    {
                        string rawTitle = track.Title;
                        if (_previewTagFixer != null)
                        {
                            // Same keep= flags and custom rules the real run applies.
                            string rawAlbum = track.Album.Equals(Constants.MissingTag) ? "" : track.Album;
                            var p = _previewTagFixer.PreviewFields(sourcePath, rawTitle, track.Artists, rawAlbum, track.Genres);
                            track.Title = p.title;
                            track.Artists = p.artists;
                            track.Genres = p.genres;
                            if (!track.Album.Equals(Constants.MissingTag))
                                track.Album = RulesEngine.NormalizeAlbum(p.album);
                        }
                        else
                        {
                            var simArtistList = TagFixer.ExtractAndFixArtists(rawTitle, track.Artists);
                            track.Title = TagFixer.RemoveParentheticals(rawTitle);
                            track.Artists = string.Join("; ", simArtistList);
                            if (!track.Album.Equals(Constants.MissingTag))
                                track.Album = RulesEngine.NormalizeAlbum(track.Album);
                            if (TagFixer.ShouldFixGenre(track.Artists, track.Genres))
                                track.Genres = TagFixer.DetermineGenre(track.Artists);
                        }
                    }

                    entry.Title = track.Title;
                    entry.Artists = track.Artists;
                    entry.Album = track.Album;
                    sf.Track = track;
                    sf.LengthSec = ReadLengthSeconds(sourcePath);
                    sf.IsReadable = true;

                    // Populate tag changes for combined routing display
                    string filename = Path.GetFileName(sourcePath);
                    if (tagChanges != null && tagChanges.TryGetValue(filename, out var changes))
                    {
                        entry.TagChanges.AddRange(changes);
                        if (tagLogs != null && tagLogs.TryGetValue(filename, out var tagLog))
                            entry.TagFix = tagLog;
                    }

                    // NotDuplicate matches the ORIGINAL NewMusic name; a tag fix may have renamed the file already.
                    string exceptionKey = tagLogs != null && tagLogs.TryGetValue(filename, out var nameLog) ? nameLog.OriginalFilename : filename;
                    // Same-folder rename: the original path is the current folder plus the original name.
                    string currentRel = RelToNewMusic(sourcePath, _roots.NewMusic);
                    string originalRel = currentRel == null ? null : Path.Combine(Path.GetDirectoryName(currentRel) ?? "", exceptionKey);
                    entry.OriginalSourceRel = originalRel ?? entry.SourceRel;
                    string duplicatePath = tagExceptions.IsNotDuplicate(exceptionKey, originalRel) ? null : FindDuplicateInMirror(track, mirrorIndex);
                    if (!string.IsNullOrEmpty(duplicatePath))
                    {
                        sf.Duplicate = BuildDupData(sourcePath, track, duplicatePath);
                        // Populated once here regardless of which D/L/K decision is made later,
                        // so the JSON always carries the library-duplicate block for review.
                        entry.LibraryDuplicate = true;
                        entry.DupLibraryPath = sf.Duplicate.RelMirrorPath;
                        entry.DupLibraryTrack = sf.Duplicate.MirrorTrack;
                        entry.DupLibraryAlbum = sf.Duplicate.MirrorAlbum;
                        entry.DupNewAlbum = track.Album;
                        entry.DupRecommendationKey = sf.Duplicate.RecommendedKey == '\0'
                            ? "K" : sf.Duplicate.RecommendedKey.ToString();
                        entry.DupRecommendation = DupRecommendationText(sf.Duplicate.RecommendedKey);
                        entry.DupReason = sf.Duplicate.DupReason;
                    }
                }
                catch (Exception ex)
                {
                    sf.IsReadable = false;
                    sf.ReadError = ex.Message;
                    sf.ReadException = ex;
                }
                result.Add(sf);
            }
            return result;
        }

        /// <summary>Lengths further apart than this (seconds) mean two different recordings.</summary>
        internal const double DistinctLengthToleranceSec = 3.0;

        /// <summary>True when both lengths are known and differ by more than the tolerance (remix, edit, other take).</summary>
        internal static bool LengthsDiffer(double a, double b) =>
            a > 0 && b > 0 && Math.Abs(a - b) > DistinctLengthToleranceSec;

        /// <summary>
        /// The one duplicate recommendation. Order: distinct recording (keep both), richer metadata,
        /// same album, deluxe, single, compilation, then the album-size rule (tie keeps incoming).
        /// </summary>
        internal static void RecommendDuplicate(DupFacts f, out char key, out string reason)
        {
            key = '\0'; reason = "";
            if (LengthsDiffer(f.IncomingLengthSec, f.LibraryLengthSec))
            {
                reason = $"Different recording: incoming is {f.IncomingLengthSec:F0}s, library copy is {f.LibraryLengthSec:F0}s - keep both";
                return;
            }
            if (f.LibraryArtists > 0 && f.IncomingArtists > f.LibraryArtists)
            {
                key = 'L';
                reason = $"Incoming has richer metadata ({f.IncomingArtists} artists vs {f.LibraryArtists} in library) - keep incoming";
                return;
            }
            if (f.SameAlbum)
            {
                key = 'D'; reason = $"Same song from same album ('{f.NewAlbum}') - already in library";
            }
            else if (f.NewIsAlbum && IsDeluxeVersionOf(f.NewAlbum, f.LibraryAlbum))
            {
                key = 'L'; reason = $"New file album '{f.NewAlbum}' is a deluxe/expanded version of library album '{f.LibraryAlbum}' - deluxe preferred";
            }
            else if (f.NewIsAlbum && IsDeluxeVersionOf(f.LibraryAlbum, f.NewAlbum))
            {
                key = 'D'; reason = $"Library album '{f.LibraryAlbum}' is a deluxe/expanded version of new file album '{f.NewAlbum}' - keep library deluxe";
            }
            else if (f.LibraryIsSingle && f.NewIsAlbum)
            {
                key = 'L'; reason = $"Library has single; new file is from album '{f.NewAlbum}' - album preferred";
            }
            else if (f.LibraryIsCompilation && f.NewIsAlbum)
            {
                key = 'L'; reason = $"Library copy is from a compilation; new file is from artist album '{f.NewAlbum}'";
            }
            else if (f.NewIsAlbum && f.IncomingAlbumTracks > 0 && f.LibraryAlbumTracks > 0)
            {
                key = f.IncomingAlbumTracks >= f.LibraryAlbumTracks ? 'L' : 'D';
                string sizeNote = f.IncomingAlbumTracks > f.LibraryAlbumTracks ? "larger album preferred"
                    : f.IncomingAlbumTracks < f.LibraryAlbumTracks ? "library album is larger, keep library"
                    : "same size, incoming copy kept";
                reason = $"New album '{f.NewAlbum}' has {f.IncomingAlbumTracks} tracks, library album '{f.LibraryAlbum}' has {f.LibraryAlbumTracks} tracks - {sizeNote}";
            }
            else
            {
                // GUI-54: a row with no key still explains itself, so JSON and console agree.
                reason = "No rule decides this duplicate - keep both or choose manually";
            }
        }

        /// <summary>Audio length in seconds from the tags, or 0 when unreadable.</summary>
        private static double ReadLengthSeconds(string path)
        {
            try
            {
                using (TagLib.File f = TagLib.File.Create(path))
                    return f.Properties.Duration.TotalSeconds;
            }
            catch { return 0; }
        }

        /// <summary>Length (seconds) and artist count of a mirror XML; 0 when missing.</summary>
        private static void ReadMirrorFacts(string xmlPath, out double lengthSec, out int artistCount)
        {
            lengthSec = 0; artistCount = 0;
            try
            {
                var doc = new System.Xml.XmlDocument();
                doc.Load(xmlPath);
                string len = doc.SelectSingleNode("//Length")?.InnerText;
                if (TimeSpan.TryParse(len, System.Globalization.CultureInfo.InvariantCulture, out TimeSpan ts)) lengthSec = ts.TotalSeconds;
                string artists = doc.SelectSingleNode("//Artists")?.InnerText;
                if (!string.IsNullOrEmpty(artists))
                    artistCount = Track.ProcessProperty(artists).Count(x => !string.IsNullOrWhiteSpace(x));
            }
            catch { /* unknown facts stand the new rules down */ }
        }

        /// <summary>
        /// Builds a DupData object for a duplicate found during pre-scan.
        /// Computes all display strings and the [L]/[D] recommendation.
        /// </summary>
        internal DupData BuildDupData(string sourcePath, Track track, string duplicatePath)
        {
            string libraryFilePath = DeriveLibraryPathFromMirrorPath(duplicatePath);
            string relLibraryPath = libraryFilePath.StartsWith(_roots.Library, StringComparison.OrdinalIgnoreCase)
                ? libraryFilePath.Substring(_roots.Library.Length).TrimStart('\\', '/')
                : libraryFilePath;
            string relMirrorPath = duplicatePath.StartsWith(_roots.Mirror, StringComparison.OrdinalIgnoreCase)
                ? duplicatePath.Substring(_roots.Mirror.Length).TrimStart('\\', '/')
                : Path.GetFileName(duplicatePath);
            string relNewPath = Path.GetFileName(sourcePath);

            bool libraryIsSingle = relLibraryPath.IndexOf("\\Singles\\", StringComparison.OrdinalIgnoreCase) >= 0
                                || relLibraryPath.StartsWith("Singles\\", StringComparison.OrdinalIgnoreCase);
            bool libraryIsCompilation = relMirrorPath.StartsWith("Compilations\\", StringComparison.OrdinalIgnoreCase)
                                     || relMirrorPath.StartsWith("Compilations/", StringComparison.OrdinalIgnoreCase)
                                     || IsAlbumFolderCompilation(duplicatePath);
            bool newIsAlbum = !string.IsNullOrEmpty(track.Album)
                           && !track.Album.Equals(Constants.MissingTag, StringComparison.OrdinalIgnoreCase)
                           && !track.Album.Equals(track.Title, StringComparison.OrdinalIgnoreCase);

            string libraryAlbum = Path.GetFileName(Path.GetDirectoryName(duplicatePath));
            bool sameAlbum = newIsAlbum && libraryAlbum.Equals(track.Album, StringComparison.OrdinalIgnoreCase);
            // Track counts for the size rule; 0 means unknown and the rule does not fire.
            int libraryAlbumTracks = CountLibraryAlbumTracks(libraryFilePath);
            int incomingAlbumTracks = newIsAlbum ? CountAlbumSongs(track.PrimaryArtist, track.Album) : 0;

            ReadMirrorFacts(duplicatePath, out double libraryLen, out int libraryArtists);
            var facts = new DupFacts
            {
                NewAlbum = track.Album, LibraryAlbum = libraryAlbum, NewIsAlbum = newIsAlbum, SameAlbum = sameAlbum,
                LibraryIsSingle = libraryIsSingle, LibraryIsCompilation = libraryIsCompilation,
                IncomingAlbumTracks = incomingAlbumTracks, LibraryAlbumTracks = libraryAlbumTracks,
                IncomingArtists = Track.ProcessProperty(track.Artists).Count(x => !string.IsNullOrWhiteSpace(x)),
                LibraryArtists = libraryArtists,
                IncomingLengthSec = ReadLengthSeconds(sourcePath), LibraryLengthSec = libraryLen
            };
            RecommendDuplicate(facts, out char recommendedKey, out string dupReason);

            string optD = "[D] Delete NewMusic copy (keep library)";
            string optL = "[L] Delete library copy (keep new file - will be routed in the next step)";
            string optK = "[K] Keep both";
            string optQ = "[Q] Quit";
            if (recommendedKey == 'L') optL += " (recommended)";
            else if (recommendedKey == 'D') optD += " (recommended)";
            string optionsLine = recommendedKey == 'L'
                ? $"  {optL}   {optD}   {optK}   {optQ}"
                : $"  {optD}   {optL}   {optK}   {optQ}";

            string displayNewFilename = $"{track.Artists} - {track.Title}.mp3";
            ReadMirrorTrackInfo(duplicatePath, out string mirrorTrack, out string mirrorAlbum);

            return new DupData
            {
                DuplicatePath = duplicatePath,
                LibraryFilePath = libraryFilePath,
                RelLibraryPath = relLibraryPath,
                RelMirrorPath = relMirrorPath,
                RelNewPath = relNewPath,
                DisplayNewFilename = displayNewFilename,
                MirrorTrack = mirrorTrack,
                MirrorAlbum = mirrorAlbum,
                DupReason = dupReason,
                RecommendedKey = recommendedKey,
                OptionsLine = optionsLine
            };
        }

        /// <summary>Maps a recommendation key to the exact phrase the CLI and clients display.</summary>
        private static string DupRecommendationText(char key)
        {
            switch (key)
            {
                case 'L': return "Delete library copy";
                case 'D': return "Delete new file";
                default: return "Keep both";
            }
        }

        /// <summary>
        /// Presents one duplicate to the user and collects their D/L/K/Q decision.
        /// Sets sf.Duplicate.Decision. Returns false if user pressed Q, true otherwise.
        /// Internal (not private) so tests can exercise the noInput/manifest-override path directly.
        /// </summary>
        internal bool PresentDuplicateAndDecide(ScannedFile sf)
        {
            var dup = sf.Duplicate;
            Track track = sf.Track;

            string dupProposed = dup.RecommendedKey == 'L'
                ? "Delete library copy, keep new file"
                : dup.RecommendedKey == 'D'
                    ? "Delete NewMusic copy, keep library"
                    : "No version preference";

            Console.WriteLine();
            Console.WriteLine("===========================================================================");
            Console.WriteLine("  DUPLICATE FOUND");
            Console.WriteLine("===========================================================================");
            Console.WriteLine();
            Console.WriteLine($"  In AudioMirror: {dup.RelMirrorPath}");
            if (!string.IsNullOrEmpty(dup.MirrorTrack))
                Console.WriteLine($"  Track:          {dup.MirrorTrack}");
            if (!string.IsNullOrEmpty(dup.MirrorAlbum))
                Console.WriteLine($"  Album:          {dup.MirrorAlbum}");
            Console.WriteLine();
            Console.WriteLine($"  New file:   {dup.DisplayNewFilename}");
            Console.WriteLine($"  Album:      {track.Album}");
            Console.WriteLine();
            Console.WriteLine($"  Proposed:   {dupProposed}");
            if (!string.IsNullOrEmpty(dup.DupReason))
                Console.WriteLine($"  Reason:     {dup.DupReason}");
            Console.WriteLine();
            Console.WriteLine("---------------------------------------------------------------------------");
            Console.WriteLine(dup.OptionsLine);
            Console.WriteLine("---------------------------------------------------------------------------");

            if (noInput)
            {
                // Review-stage resolution wins over the exe's own recommendation on a real
                // run, carried through the same --manifest mechanism as accept/decline. A duplicate left
                // untouched in Review has no manifest entry for it, so this falls through to
                // the exact same auto-accept-recommendation behavior as before - unresolved
                // batches stay safe by construction, not by a second code path.
                char manifestDecision = _manifest?.GetDupResolution(
                    Path.GetFileName(sf.SourcePath), track.Artists, track.Title, RelToNewMusic(sf.SourcePath, _roots.NewMusic)) ?? '\0';
                if (manifestDecision != '\0')
                {
                    dup.Decision = manifestDecision;
                    Console.WriteLine($"  [MANIFEST] {manifestDecision} (user-resolved in review)");
                    Console.WriteLine();
                    return true;
                }

                char autoDecision = dup.RecommendedKey != '\0' ? dup.RecommendedKey : 'K';
                // Safety: L replaces the library copy and D sends the new file to the Recycle Bin;
                // no human confirmed either, so an unattended run downgrades both to K (Keep Both,
                // non-destructive). Only an explicit manifest resolution (above) can choose D or L.
                if (autoDecision == 'L' || autoDecision == 'D')
                {
                    Console.WriteLine($"  [SAFETY] {autoDecision} not auto-accepted under no-input: {Path.GetFileName(sf.SourcePath)} downgraded to K (keep both)");
                    autoDecision = 'K';
                }
                dup.Decision = autoDecision;
                string autoReason = dup.RecommendedKey == '\0'
                    ? "K (no recommendation - defaulting to Keep Both)"
                    : dup.RecommendedKey != 'K' && autoDecision == 'K'
                        ? $"K ({dup.RecommendedKey} downgraded - no-input never deletes without an explicit resolution)"
                        : $"{autoDecision} (auto-accepted recommended)";
                Console.WriteLine($"  [AUTO] {autoReason}");
                Console.WriteLine();
                return true;
            }

            while (true)
            {
                var key = ReadMenuKey();
                if (key == ConsoleKey.D) { dup.Decision = 'D'; Console.WriteLine(); return true; }
                else if (key == ConsoleKey.L) { dup.Decision = 'L'; Console.WriteLine(); return true; }
                else if (key == ConsoleKey.K) { dup.Decision = 'K'; Console.WriteLine(); return true; }
                else if (key == ConsoleKey.Q) { dup.Decision = 'Q'; Console.WriteLine(); return false; }
            }
        }

        internal static string SanitiseFolderName(string component) => DestinationRouter.SanitiseFolderName(component);

        internal string GetDestDir(Track track, HashSet<string> newArtistFolders, HashSet<string> compilationAlbums, out string reason, out bool isNewFolder) => Router.GetDestDir(track, newArtistFolders, compilationAlbums, out reason, out isNewFolder);

        /// <summary>Most frequent spelling among the tags (ordinal compare); a tie picks the ordinal-first spelling.</summary>
        /// <summary>
        /// Folder spelling for an artist: the manifest-chosen spelling when one exists for its key, otherwise the
        /// frequency pick (fallback). A chosen spelling always wins, so the folder follows the tags it was given.
        /// </summary>
        internal static string FolderSpellingFor(string spellingKey, IDictionary<string, string> choices, string fallback)
        {
            if (choices != null && spellingKey != null &&
                choices.TryGetValue(ArtistSpellingCandidates.SpellingKey(spellingKey), out string chosen))
                return chosen;
            return fallback;
        }

        internal static string ChooseCanonicalSpelling(IEnumerable<string> tags)
        {
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (string t in tags) counts[t] = counts.ContainsKey(t) ? counts[t] + 1 : 1;
            string best = null;
            foreach (var kv in counts)
                if (best == null || kv.Value > counts[best] || (kv.Value == counts[best] && string.CompareOrdinal(kv.Key, best) < 0))
                    best = kv.Key;
            return best;
        }

        /// <summary>Existing Artists folders whose name differs from the tag's folder name ONLY by case, as {on-disk name, tag spelling}. Never plans a rename across different names.</summary>
        internal List<KeyValuePair<string, string>> PlanArtistFolderCaseRenames(IEnumerable<string> primaryTags)
        {
            var plan = new List<KeyValuePair<string, string>>();
            string root = Path.Combine(_libraryPath, Constants.ArtistsDir);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string tag in primaryTags)
            {
                if (string.IsNullOrEmpty(tag)) continue;
                string want = SanitiseFolderName(tag);
                string onDisk = FindDirNameIgnoreCase(root, want);
                if (onDisk != null && !onDisk.Equals(want, StringComparison.Ordinal)
                    && onDisk.Equals(want, StringComparison.OrdinalIgnoreCase) && seen.Add(onDisk))
                    plan.Add(new KeyValuePair<string, string>(onDisk, want));
            }
            return plan;
        }

        /// <summary>Case-only renames under Artists, journaled BEFORE the first rename (artist-folder-rename-*.json). Returns the journal path, or null when the plan is empty.</summary>
        internal string ApplyArtistFolderCaseRenames(List<KeyValuePair<string, string>> plan)
        {
            if (plan == null || plan.Count == 0) return null;
            string root = Path.Combine(_libraryPath, Constants.ArtistsDir);
            string dir = MigrationJournalDir ?? _roots.Logs;
            Directory.CreateDirectory(dir);
            string journal = Path.Combine(dir, "artist-folder-rename-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + ".json");
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("{\"recordType\": \"artistFolderCaseRename\", \"schemaVersion\": 1, \"root\": " + JsonText.Quote(root) + ", \"renames\": [");
            for (int i = 0; i < plan.Count; i++)
                sb.AppendLine($"  {{\"from\": {JsonText.Quote(plan[i].Key)}, \"to\": {JsonText.Quote(plan[i].Value)}}}" + (i < plan.Count - 1 ? "," : ""));
            sb.AppendLine("]}");
            AtomicFile.WriteAllText(journal, sb.ToString(), new System.Text.UTF8Encoding(false));
            foreach (var r in plan) CaseOnlyFolderMove(root, r.Key, r.Value);
            return journal;
        }

        /// <summary>Undoes ApplyArtistFolderCaseRenames from its journal: each folder goes back to its old spelling.</summary>
        internal void RevertArtistFolderCaseRenames(string journalPath)
        {
            string text = File.ReadAllText(journalPath);
            var rootMatch = System.Text.RegularExpressions.Regex.Match(text, "\"root\": \"((?:[^\"\\\\]|\\\\.)*)\"");
            if (!rootMatch.Success) throw new InvalidDataException("not an artist folder rename journal: " + journalPath);
            string root = rootMatch.Groups[1].Value.Replace("\\\\", "\\");
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(text, "\"from\": \"([^\"]*)\", \"to\": \"([^\"]*)\""))
                CaseOnlyFolderMove(root, m.Groups[2].Value, m.Groups[1].Value);
        }

        /// <summary>Case-only folder rename through a temporary name (a direct move onto itself is a no-op on Windows).</summary>
        private static void CaseOnlyFolderMove(string root, string from, string to)
        {
            if (!from.Equals(to, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("not a case-only rename: " + from + " -> " + to);
            string src = Path.Combine(root, from);
            if (!Directory.Exists(src)) return;
            string tmp = Path.Combine(root, to + ".casetmp-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.Move(src, tmp);
            Directory.Move(tmp, Path.Combine(root, to));
        }

        /// <summary>The on-disk name of a child folder of parent matching name ignoring case, or null.</summary>
        internal static string FindDirNameIgnoreCase(string parent, string name)
        {
            if (!Directory.Exists(parent)) return null;
            foreach (string d in Directory.GetDirectories(parent))
            {
                string n = Path.GetFileName(d);
                if (n.Equals(name, StringComparison.OrdinalIgnoreCase)) return n;
            }
            // Windows normalises some names (a trailing dot is dropped), so the folder exists though no listed name matches.
            return Directory.Exists(Path.Combine(parent, name)) ? name : null;
        }

        private string FindAlbumFolder(string artistFolder, string cleanAlbum) => Counter.FindAlbumFolder(artistFolder, cleanAlbum);

        internal int CountAlbumSongs(string artist, string album) => Counter.CountAlbumSongs(artist, album);

        private int CountLibrarySinglesOfAlbum(string artist, string artistFolder, string album) => Counter.CountLibrarySinglesOfAlbum(artist, artistFolder, album);

        private int CountAkiraTheDonPersonSongs(string sampledPerson, string libraryPath = null) => Counter.CountAkiraTheDonPersonSongs(sampledPerson, libraryPath);

        private int CountAkiraTheDonPersonAlbumSongs(string sampledPerson, string album, string libraryPath = null) => Counter.CountAkiraTheDonPersonAlbumSongs(sampledPerson, album, libraryPath);

        /// <summary>
        /// Reads a menu key using ReadLine so the user must press Enter to confirm.
        /// Prevents right-click paste in Windows Terminal from silently triggering actions.
        /// </summary>
        private ConsoleKey ReadMenuKey() => ReadMenuKeyFrom(Console.In, Console.Out);

        /// <summary>Testable core. End of input (closed stdin) quits instead of re-prompting forever.</summary>
        internal static ConsoleKey ReadMenuKeyFrom(TextReader reader, TextWriter writer)
        {
            while (true)
            {
                writer.Write("> ");
                string line = reader.ReadLine();
                if (line == null) { writer.WriteLine(); writer.WriteLine("[INPUT] stdin closed, quitting."); return ConsoleKey.Q; }
                string input = line.Trim().ToUpperInvariant();
                switch (input)
                {
                    case "Y": return ConsoleKey.Y;
                    case "N": return ConsoleKey.N;
                    case "D": return ConsoleKey.D;
                    case "L": return ConsoleKey.L;
                    case "K": return ConsoleKey.K;
                    case "Q": return ConsoleKey.Q;
                }
            }
        }

        // ---- Projected LibChecker ----

        /// <summary>
        /// Pure function: builds the projected post-integration tag list.
        /// currentTags: current library state. removals: RelPaths to remove. additions: new TrackTags.
        /// Extracted as a static so it is unit-testable without filesystem access.
        /// </summary>
        internal static List<TrackTag> BuildProjection(
            IEnumerable<TrackTag> currentTags,
            IEnumerable<TrackTag> additions,
            ISet<string> removals)
        {
            var projected = currentTags
                .Where(t => !removals.Contains(t.RelPath))
                .ToList();
            projected.AddRange(additions);
            return projected;
        }

        /// <summary>
        /// Loads current library tags, builds the projected post-integration tag list
        /// (models all moves/deletes/misc-migrations), then runs LibChecker on the projection
        /// and prints the result in a dry-run section.
        /// Silently skips if AudioMirror is unavailable.
        /// </summary>
        private void RunProjectedLibChecker(List<ScannedFile> scannedFiles, HashSet<string> newArtistFolders)
        {
            Console.WriteLine("\n===========================================================================");
            Console.WriteLine("Projected LibChecker (Dry Run)");
            Console.WriteLine("===========================================================================");

            List<TrackTag> currentTags;
            try
            {
                var parser = new Parser(_roots.Mirror);
                currentTags = parser.audioTags;
            }
            catch (Exception ex)
            {
                Console.WriteLine($" - SKIP: could not load current library tags: {ex.Message}");
                return;
            }

            // Removals: L-decided library copies + Misc migration sources
            var removals = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var sf in scannedFiles)
            {
                if (sf.Duplicate != null && sf.Duplicate.Decision == 'L')
                    removals.Add(GetMirrorRelPath(sf.Duplicate.DuplicatePath));
            }

            foreach (var kvp in _miscMigrationCandidates)
            {
                foreach (var xmlPath in kvp.Value)
                    removals.Add(GetMirrorRelPath(xmlPath));
            }

            // Additions: all files routed to the library (not D-decided) + Misc migration destinations
            var additions = new List<TrackTag>();

            foreach (var sf in scannedFiles)
            {
                if (!sf.IsReadable) continue;
                if (sf.Duplicate?.Decision == 'D') continue; // deleted from NewMusic, library unchanged

                Track track = sf.Track;
                if (track.Title.Equals(Constants.MissingTag) || track.Artists.Equals(Constants.MissingTag)) continue;

                string destDir = GetDestDir(track, newArtistFolders, _compilationAlbums, out _, out _);
                string sanitisedArtists = Reflector.SanitiseFilename(track.Artists);
                string sanitisedTitle = Reflector.SanitiseFilename(track.Title);
                string destFilename = sanitisedArtists + " - " + sanitisedTitle + ".xml";

                // RelPath: strip AudioFolderPath prefix, append filename - gives "\Artists\...\file.xml"
                string destRelDir = destDir.StartsWith(_roots.Library, StringComparison.OrdinalIgnoreCase)
                    ? destDir.Substring(_roots.Library.Length)
                    : "\\" + Path.GetFileName(destDir);
                string relPath = destRelDir + Path.DirectorySeparatorChar + destFilename;

                var synthesized = new TrackTag(relPath,
                    track.Title, track.Artists, track.Album,
                    track.Year ?? Constants.MissingTag,
                    track.TrackNumber ?? "0",
                    track.Genres ?? Constants.MissingTag,
                    track.Length ?? "00:00:00.0000000",
                    "1",     // AlbumCoverCount: assume art present
                    "True",  // Compilation: TagFixer sets TCMP=1
                    "0", "0" // CoverWidth/Height: unknown (dimensions check disabled)
                );
                additions.Add(synthesized);
            }

            // Misc migration destinations: synthesize tag at Artists/{artist}/Singles/ location
            foreach (var kvp in _miscMigrationCandidates)
            {
                string artist = kvp.Key;
                string destRelDir = "\\" + Constants.ArtistsDir + "\\" + SanitiseFolderName(artist) + "\\" + Constants.SinglesDir;
                foreach (var xmlPath in kvp.Value)
                {
                    string srcRelPath = GetMirrorRelPath(xmlPath);
                    // Find existing tag to copy metadata from
                    var srcTag = currentTags.FirstOrDefault(
                        t => string.Equals(t.RelPath, srcRelPath, StringComparison.OrdinalIgnoreCase));
                    if (srcTag == null) continue;

                    string filename = Path.GetFileName(xmlPath);
                    string newRelPath = destRelDir + "\\" + filename;
                    var migratedTag = new TrackTag(newRelPath,
                        srcTag.Title, srcTag.Artists, srcTag.Album, srcTag.Year,
                        srcTag.TrackNumber, srcTag.Genres, srcTag.Length,
                        srcTag.AlbumCoverCount, srcTag.Compilation,
                        srcTag.CoverWidth, srcTag.CoverHeight);
                    additions.Add(migratedTag);
                }
            }

            var projected = BuildProjection(currentTags, additions, removals);
            Console.WriteLine($" - Projected library: {currentTags.Count} current, -{removals.Count} removals, +{additions.Count} additions = {projected.Count} projected");
            new LibChecker(projected);
        }

        private static string GetMirrorRelPath(string mirrorXmlPath) => MirrorIndex.GetMirrorRelPath(mirrorXmlPath);

    }
}
