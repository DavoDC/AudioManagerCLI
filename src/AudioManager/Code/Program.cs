using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace AudioManager
{
    internal class Program
    {
        /// <summary>
        /// Main function
        /// </summary>
        /// <param name="args">Arguments given to program</param>
        static void Main(string[] args)
        {
            try
            {
                // Start message
                Console.WriteLine("\n###### Audio Manager ######");

                // Get the path of the executable
                string progExecPath = AppDomain.CurrentDomain.BaseDirectory;

                // Set mirror path relative to the executable
                string mirrorPath = Path.GetFullPath(Path.Combine(progExecPath, Constants.MirrorFolderPath));

                // Test entry points (--test, --verify) live in the separate AudioManager.Tests project, not in this exe.

                // --mirror-retry: re-run ONLY the post-integration mirror step after a failed one. Returns before
                // any mode parsing and never constructs MusicIntegrator, so no file is moved again.
                if (args.Any(a => a.Equals("--mirror-retry", StringComparison.OrdinalIgnoreCase)))
                {
                    int retryCode = RunMirrorRetry(mirrorPath, args.Any(a => a.Equals("--no-auto-commit", StringComparison.OrdinalIgnoreCase)));
                    if (retryCode != 0) Environment.Exit(retryCode);
                    return;
                }

                // --fingerprint: read-only. Prints one JSON line with the current NewMusic fingerprint (same algorithm as the
                // dry-run stamp) and returns before any mode parsing; it writes and moves nothing.
                if (args.Any(a => a.Equals("--fingerprint", StringComparison.OrdinalIgnoreCase)))
                {
                    Console.WriteLine(FingerprintJsonLine(Constants.NewMusicPath));
                    return;
                }

                // Detect CLI args: "analysis [--force-regen]" or "integrate [--dry-run] [--no-input]"
                int mode;
                bool forceMirrorRegen;
                bool dryRun = false;
                bool noInput = false;
                bool jsonOutput = false;
                bool noAutoCommit = false;
                bool interactiveMode = false;
                string manifestPath = null;
                if (args.Length > 0)
                {
                    string unknownFlag = FindUnknownFlag(args);
                    if (unknownFlag != null)
                    {
                        // A mistyped --dry-run must never fall through to a live run.
                        Console.WriteLine($" - ERROR: unknown option '{unknownFlag}'. Nothing was run.");
                        Environment.Exit(1);
                        return;
                    }
                    string modeArg = args[0].ToLowerInvariant();
                    bool forceRegen = args.Any(a => a.Equals("--force-regen", StringComparison.OrdinalIgnoreCase));
                    dryRun = args.Any(a => a.Equals("--dry-run", StringComparison.OrdinalIgnoreCase));
                    noInput = args.Any(a => a.Equals("--no-input", StringComparison.OrdinalIgnoreCase));
                    jsonOutput = args.Any(a => a.Equals("--json-output", StringComparison.OrdinalIgnoreCase));
                    noAutoCommit = args.Any(a => a.Equals("--no-auto-commit", StringComparison.OrdinalIgnoreCase));
                    int manifestIdx = Array.FindIndex(args, a => a.Equals("--manifest", StringComparison.OrdinalIgnoreCase));
                    if (manifestIdx >= 0)
                    {
                        if (manifestIdx + 1 >= args.Length)
                        {
                            Console.WriteLine(" - ERROR: --manifest requires a path argument.");
                            Environment.Exit(1);
                            return;
                        }
                        manifestPath = args[manifestIdx + 1];
                    }

                    if (modeArg == "analysis" || modeArg == "analyse" || modeArg == "analyze")
                    {
                        mode = 1;
                        forceMirrorRegen = forceRegen;
                    }
                    else if (modeArg == "integrate" || modeArg == "integration")
                    {
                        mode = 2;
                        forceMirrorRegen = false;
                    }
                    else if (modeArg == "tagfix" || modeArg == "fix-tags")
                    {
                        mode = 3;
                        forceMirrorRegen = false;
                    }
                    else
                    {
                        Console.WriteLine($"Unknown mode '{args[0]}'. Use: analysis [--force-regen] [--json-output] | integrate [--dry-run] [--json-output] | tagfix [--dry-run] [--json-output]");
                        Environment.Exit(1);
                        return;
                    }
                }
                else
                {
                    // Interactive menu: 3 options, Force Regen collapsed into menu (no separate prompt)
                    interactiveMode = true;
                    int choice = PromptMode();
                    switch (choice)
                    {
                        case 1: mode = 1; forceMirrorRegen = false; break; // Analysis
                        case 2: mode = 1; forceMirrorRegen = true;  break; // Analysis (Force Regen)
                        default: mode = 2; forceMirrorRegen = false; break; // Integrate
                    }
                }

                // Analysis has no dry-run path: --dry-run there would regen the mirror and auto-commit.
                if (AnalysisRejectsDryRun(mode, dryRun))
                {
                    Console.WriteLine(" - ERROR: --dry-run is not supported in analysis mode.");
                    Environment.Exit(1);
                    return;
                }

                // Set up file logging for all modes (console output tee'd to file + console simultaneously)
                string timestamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss", CultureInfo.InvariantCulture);
                string modeLabel = (mode == 1) ? "analysis" : (mode == 2) ? "integrate" : "tagfix";
                bool isIntegrate = (mode == 2);
                string logPrefix = isIntegrate ? "run" : modeLabel;
                string logFilePath = Path.Combine(Constants.LogsPath, $"{logPrefix}-{timestamp}.log");
                StringWriter captureWriter = (mode == 1) ? new StringWriter() : null;
                TeeWriter teeWriter = new TeeWriter(Console.Out, logFilePath, captureWriter, addFileTimestamps: isIntegrate);
                Console.SetOut(teeWriter);

                try
                {
                    if (mode == 1)
                    {
                        // Analysis mode
                        // 1) Check the age of the mirror
                        AgeChecker ac = new AgeChecker(forceMirrorRegen);

                        // 2) Create mirror of audio folder
                        // Note: Files created at this stage just contain paths to the actual file, not metadata info.
                        Reflector r = new Reflector(mirrorPath);

                        // 3) Parse metadata into XML files and tag list
                        // Note: The file contents get overwritten with actual XML content in this stage.
                        Parser p = new Parser(mirrorPath);

                        // 4) Analyse metadata and print statistics
                        Analyser a = new Analyser(p.audioTags);

                        // 5) Do audio library organisational/metadata checks
                        LibChecker lc = new LibChecker(p.audioTags);

                        // Print total time taken (captured into report)
                        Doer.PrintTotalTimeTaken();

                        // 6) Auto-commit AudioMirror if LibChecker was clean
                        // Check this before saving report so ReportWriter knows where to save it.
                        bool libCheckerClean = lc.IsClean;

                        // Save report (reports with issues go to gitignored folder to avoid cluttering diffs)
                        ReportWriter.Save(captureWriter.ToString() + lc.LowResCoverReport, libCheckerClean);

                        // Emit structured stats JSON for a client (opt-in via --json-output).
                        // Reuses the same StatList primitives the report uses, so JSON can't drift from the report.
                        if (jsonOutput)
                        {
                            string statsJsonPath = StatsJson.Write(p.audioTags);
                            Console.WriteLine($"\nStats JSON written: {statsJsonPath}");
                            string tracksJsonPath = TracksJson.Write(p.audioTags);
                            Console.WriteLine($"Tracks JSON written: {tracksJsonPath}");
                        }

                        // Auto-commit AudioMirror after report save
                        // Runs after report save so the commit output is not captured into the report.
                        var trigger = forceMirrorRegen
                            ? CommitTrigger.AnalysisForceRegen
                            : CommitTrigger.AnalysisIncremental;
                        if (noAutoCommit)
                            Console.WriteLine("\nAudioMirror auto-commit: skipped (--no-auto-commit).");
                        else
                            AudioMirrorCommitter.TryCommit(libCheckerClean, trigger);

                        // Finish message
                        Console.WriteLine("\nFinished!\n");

                        // Analysis mode complete
                        return;
                    }
                    else if (mode == 2)
                    {
                        // Selective integration: load + validate the manifest BEFORE the gate,
                        // so a bad manifest fails fast and never means "integrate everything".
                        IntegrationManifest manifest = null;
                        if (manifestPath != null)
                        {
                            manifest = IntegrationManifest.Load(manifestPath, out string manifestError);
                            if (manifest == null)
                            {
                                Console.WriteLine($" - ERROR: {manifestError}");
                                teeWriter.CloseLogFile(); // Exit skips finally, so close the log first
                                Environment.Exit(1);
                            }
                            Console.WriteLine($"Manifest: {manifest.Entries.Count} accepted track(s) from {Path.GetFileName(manifestPath)}");
                        }

                        // Integrate mode - run pre-integration gate first
                        if (!RunPreIntegrationGate(mirrorPath, jsonOutput))
                        {
                            Console.WriteLine("\nFix library issues before adding new songs.\n");
                            teeWriter.CloseLogFile(); // Exit skips finally, so close the log first
                            Environment.Exit(1);
                        }

                        // Interactive mode: dry-run preview first, then confirm before real integration
                        if (interactiveMode)
                        {
                            new MusicIntegrator(dryRun: true, noInput: false, jsonOutput: false);
                            Console.WriteLine("\n--- DRY RUN COMPLETE ---\n");
                            Console.Write("Proceed with real integration? [y/N]: ");
                            string answer = (Console.ReadLine() ?? "").Trim().ToUpperInvariant();
                            if (answer != "Y")
                            {
                                Console.WriteLine("Cancelled.");
                                return;
                            }
                            Console.WriteLine();
                            dryRun = false;
                            noInput = false;
                        }

                        // Replaced library originals are held by rename, so the holding folder must share the library's volume.
                        // Checked before anything moves; a dry run never retires anything, so HoldingRoot stays null.
                        if (!dryRun)
                        {
                            if (!ReplaceGuard.SameVolume(Constants.AudioFolderPath, Constants.ReplacedHoldingPath))
                            {
                                Console.WriteLine($"[ERROR] The replaced-original holding folder must be on the same volume as the library.");
                                Console.WriteLine($"  Library: {Constants.AudioFolderPath}");
                                Console.WriteLine($"  Holding folder: {Constants.ReplacedHoldingPath}");
                                Console.WriteLine("Nothing was moved.");
                                teeWriter.CloseLogFile(); // Exit skips finally, so close the log first
                                Environment.Exit(1);
                            }
                            ReplaceGuard.HoldingRoot = Constants.ReplacedHoldingPath;
                            ReplaceGuard.ResetRetired();
                        }

                        // Restore any replace interrupted by a previous crash before scanning the library
                        if (!dryRun) ReplaceGuard.SweepOrphans(Constants.AudioFolderPath);

                        // Gate passed - proceed with integration
                        MusicIntegrator mi = new MusicIntegrator(dryRun, noInput, jsonOutput, manifest);

                        // Pre-move MAX_PATH gate blocked the batch: nothing moved, so skip validation and exit nonzero
                        if (mi.Blocked != null)
                        {
                            teeWriter.CloseLogFile(); // Exit skips finally, so close the log first
                            Environment.Exit(1);
                        }

                        // Post-integration validation: regenerate mirror and run LibChecker
                        if (!dryRun)
                        {
                            Console.WriteLine("\nPost-integration validation...");
                            PostIntegrationMirrorResult pim = RunPostIntegrationMirror(mirrorPath, noAutoCommit);
                            if (pim.Status != "ok" && pim.Status != "skipped")
                            {
                                // Integration already moved files; only the mirror step failed. Say so, machine- and human-readable.
                                Console.WriteLine($" - Mirror status: {pim.Status}. Integration files ALREADY MOVED; do not re-run integrate.");
                                Console.WriteLine($" - Retry the mirror step only with: AudioManagerCLI.exe --mirror-retry (result in {Path.Combine(Constants.LogsPath, MirrorResultJsonFileName)})\n");
                            }
                        }
                    }
                    else if (mode == 3)
                    {
                        // Guarded apply of the reviewed plan, or revert of a journal (no plain scan in these modes).
                        int revertIdx = Array.FindIndex(args, a => a.Equals("--revert", StringComparison.OrdinalIgnoreCase));
                        if (revertIdx >= 0 && revertIdx + 1 < args.Length)
                        {
                            var rev = TagFixApplier.Revert(args[revertIdx + 1], Constants.NewMusicPath, Constants.QuarantinePath);
                            Console.WriteLine($" - Revert: {rev.Reverted} reverted, {rev.SkippedModified} left alone (edited since), {rev.Failed} failed");
                            foreach (string note in rev.Notes) Console.WriteLine($"   {note}");
                            if (rev.Failed > 0) { teeWriter.CloseLogFile(); Environment.Exit(1); }
                            return;
                        }
                        if (args.Any(a => a.Equals("--apply-plan", StringComparison.OrdinalIgnoreCase)))
                        {
                            string planFp = TagFixApplier.ReadPlanFingerprint(Path.Combine(Constants.LogsPath, TagFixPlanJson.OutputFileName));
                            var ar = TagFixApplier.Apply(Constants.NewMusicPath, planFp, Constants.LogsPath, Constants.BackupsPath, Constants.QuarantinePath, DateTime.Now);
                            Console.WriteLine($" - Tag Fix apply: {ar.Status} (applied {ar.Applied}, already done {ar.AlreadyDone}, quarantined {ar.Quarantined}, failed {ar.Failed}, pending after {ar.PendingAfter})");
                            Console.WriteLine($" - Result JSON written: {Path.Combine(Constants.LogsPath, TagFixApplier.ResultFileName)}");
                            if (ar.Status != "applied" && ar.Status != "nothing_to_do") { teeWriter.CloseLogFile(); Environment.Exit(1); }
                            return;
                        }

                        // TagFix mode - clean tags in NewMusic folder
                        TagFixer tf = new TagFixer(dryRun);
                        // Plan JSON is read-only and only written for a dry run; a real run never writes it.
                        if (jsonOutput && dryRun)
                            Console.WriteLine($"\nTagfix plan JSON written: {TagFixPlanJson.Write(tf)}");
                        else if (jsonOutput)
                            Console.WriteLine(" - [WARN] --json-output for tagfix is only written with --dry-run; no JSON written.");
                    }

                    // Print total time taken (Integrate and TagFix modes only)
                    if (mode == 2 || mode == 3)
                    {
                        Doer.PrintTotalTimeTaken();
                    }
                }
                finally
                {
                    // Restore real console output and close file logger
                    teeWriter?.CloseLogFile();
                    Console.SetOut(teeWriter.ConsoleWriter);
                    Console.WriteLine($"Log file saved: data/logs/{logPrefix}-{timestamp}.log");
                }
            }
            catch (Exception ex)
            {
                // Handle exceptions
                Console.WriteLine($"\nEXCEPTION ENCOUNTERED");
                Console.WriteLine($"\nMessage: {ex.Message}");
                Console.WriteLine($"\nStack Trace: \n{ex.StackTrace}");
                Console.WriteLine("\n");
                Environment.Exit(123);
            }
        }

        /// <summary>One-line JSON for --fingerprint: the NewMusic fingerprint of folder (same value a dry run stamps).</summary>
        internal static string FingerprintJsonLine(string folder)
        {
            return "{\"newMusicFingerprint\": \"" + MusicIntegrator.NewMusicFingerprint(folder) + "\"}";
        }

        internal const string MirrorResultJsonFileName = "mirror-result.json";
        internal const string MirrorRetryCommand = "AudioManagerCLI.exe --mirror-retry";

        /// <summary>
        /// Outcome of the post-integration mirror step. Status: ok, mirror_commit_failed, libchecker_issues or skipped.
        /// </summary>
        internal sealed class PostIntegrationMirrorResult
        {
            public string Status;
            public string Error;
            public string Detail;
        }

        /// <summary>
        /// Post-integration mirror step with the real regenerate, LibChecker and commit. Writes logs/mirror-result.json.
        /// </summary>
        internal static PostIntegrationMirrorResult RunPostIntegrationMirror(string mirrorPath, bool noAutoCommit)
        {
            return RunPostIntegrationMirrorWith(
                Constants.LogsPath,
                noAutoCommit,
                () => RegenerateAndValidateMirror(mirrorPath),
                () => AudioMirrorCommitter.TryCommit(libCheckerClean: true, CommitTrigger.Integration));
        }

        /// <summary>
        /// Seamed core of the post-integration mirror step. regenerateAndValidate returns LibChecker clean;
        /// commit returns true when no commit failure was reported. Any exception maps to mirror_commit_failed.
        /// </summary>
        internal static PostIntegrationMirrorResult RunPostIntegrationMirrorWith(string logsDir, bool noAutoCommit, Func<bool> regenerateAndValidate, Func<bool> commit)
        {
            PostIntegrationMirrorResult r;
            try
            {
                if (!regenerateAndValidate())
                {
                    Console.WriteLine(" - Post-integration validation: ISSUES FOUND (see above)\n");
                    r = new PostIntegrationMirrorResult { Status = "libchecker_issues", Detail = "LibChecker reported issues; AudioMirror was not committed." };
                }
                else
                {
                    Console.WriteLine(" - Post-integration validation: CLEAN (no issues found)\n");
                    if (noAutoCommit)
                    {
                        Console.WriteLine("AudioMirror auto-commit: skipped (--no-auto-commit).");
                        r = new PostIntegrationMirrorResult { Status = "skipped", Detail = "--no-auto-commit was given." };
                    }
                    else if (!commit())
                    {
                        r = new PostIntegrationMirrorResult { Status = "mirror_commit_failed", Error = "AudioMirror commit failed; see the git error above.", Detail = "git commit reported a failure." };
                    }
                    else
                    {
                        r = new PostIntegrationMirrorResult { Status = "ok", Detail = "" };
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($" - WARNING: Could not run post-integration validation: {ex.Message}\n");
                r = new PostIntegrationMirrorResult { Status = "mirror_commit_failed", Error = ex.Message, Detail = ex.GetType().Name };
            }

            try
            {
                string jsonPath = WriteMirrorResult(logsDir, r);
                Console.WriteLine($" - Mirror result JSON written: {jsonPath}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($" - WARNING: Could not write {MirrorResultJsonFileName}: {ex.Message}");
            }
            return r;
        }

        /// <summary>Force-regenerates the mirror and returns whether LibChecker is clean.</summary>
        private static bool RegenerateAndValidateMirror(string mirrorPath)
        {
            // Force-regen: integration deleted/moved library files, so incremental
            // Reflector would leave orphaned XMLs that trigger ghost LibChecker warnings.
            AgeChecker.ForceRegen();

            // Regenerate mirror (force-regen mode now active via AgeChecker.RegenMirror)
            Console.WriteLine(" - Regenerating AudioMirror XMLs (force)...");
            Reflector r = new Reflector(mirrorPath);
            Parser p = new Parser(mirrorPath);

            // Run LibChecker to validate the updated library
            Console.WriteLine(" - Running library validation...");
            LibChecker lc = new LibChecker(p.audioTags);
            return lc.IsClean;
        }

        /// <summary>Writes logs/mirror-result.json atomically and returns its path.</summary>
        internal static string WriteMirrorResult(string logsDir, PostIntegrationMirrorResult r)
        {
            if (!Directory.Exists(logsDir))
                Directory.CreateDirectory(logsDir);
            string outPath = Path.Combine(logsDir, MirrorResultJsonFileName);
            AtomicFile.WriteAllText(outPath, BuildMirrorResultJson(r, DateTime.Now), new UTF8Encoding(false));
            return outPath;
        }

        /// <summary>Pure builder for mirror-result.json (schemaVersion 1).</summary>
        internal static string BuildMirrorResultJson(PostIntegrationMirrorResult r, DateTime generatedAt)
        {
            var sb = new StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine("  \"recordType\": \"mirrorResult\",");
            sb.AppendLine("  \"schemaVersion\": 1,");
            sb.AppendLine($"  \"generatedAt\": {JsonString(generatedAt.ToString("s", CultureInfo.InvariantCulture))},");
            sb.AppendLine($"  \"status\": {JsonString(r.Status)},");
            sb.AppendLine($"  \"error\": {JsonString(r.Error)},");
            sb.AppendLine($"  \"retry\": {JsonString(MirrorRetryCommand)}");
            sb.AppendLine("}");
            return sb.ToString();
        }

        /// <summary>
        /// When the last post-integration mirror step did not finish (mirror_commit_failed or libchecker_issues), returns
        /// the line telling the user to run --mirror-retry rather than integrate again; otherwise null (also for a missing
        /// or unreadable mirror-result.json).
        /// </summary>
        internal static string PendingMirrorRetryHint(string logsDir)
        {
            try
            {
                string path = Path.Combine(logsDir, MirrorResultJsonFileName);
                if (!File.Exists(path)) return null;
                var m = System.Text.RegularExpressions.Regex.Match(File.ReadAllText(path), "\"status\"\\s*:\\s*\"([a-z_]+)\"");
                if (!m.Success) return null;
                string status = m.Groups[1].Value;
                if (status != "mirror_commit_failed" && status != "libchecker_issues") return null;
                return $"The last integrate already moved its files but its mirror step ended as {status}; do not integrate again, run {MirrorRetryCommand} instead.";
            }
            catch (Exception) { return null; }
        }

        /// <summary>Runs ONLY the post-integration mirror step. Never constructs MusicIntegrator and never moves files.</summary>
        internal static int RunMirrorRetry(string mirrorPath, bool noAutoCommit)
        {
            Console.WriteLine("\n###### Mirror retry (post-integration mirror step only; no files are moved) ######");
            PostIntegrationMirrorResult r = RunPostIntegrationMirror(mirrorPath, noAutoCommit);
            Console.WriteLine($"Mirror retry status: {r.Status}");
            return MirrorRetryExitCode(r);
        }

        /// <summary>Exit code for the retry: 0 only when the status is ok, otherwise 1.</summary>
        internal static int MirrorRetryExitCode(PostIntegrationMirrorResult r)
        {
            return r != null && r.Status == "ok" ? 0 : 1;
        }

        private static string JsonString(string s)
        {
            if (s == null) return "null";
            var sb = new StringBuilder("\"");
            foreach (char c in s)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"': sb.Append("\\\""); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default: sb.Append(c); break;
                }
            }
            return sb.Append('"').ToString();
        }

        /// <summary>
        /// True when the parsed mode is analysis (mode 1) and --dry-run was given. Analysis has no dry path.
        /// </summary>
        /// <param name="mode">Parsed mode: 1 = analysis, 2 = integrate, 3 = tagfix.</param>
        /// <param name="dryRun">Whether --dry-run was passed.</param>
        internal static bool AnalysisRejectsDryRun(int mode, bool dryRun) => mode == 1 && dryRun;

        /// <summary>Returns the first dash-prefixed arg that is not a known flag (null when all are known).</summary>
        internal static string FindUnknownFlag(string[] args)
        {
            var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "--force-regen", "--dry-run", "--no-input", "--json-output", "--no-auto-commit",
                "--manifest", "--revert", "--apply-plan", "--mirror-retry", "--fingerprint"
            };
            for (int i = 1; i < args.Length; i++)
            {
                string a = args[i];
                if (a.StartsWith("-") && !known.Contains(a)) return a;
            }
            return null;
        }

        /// <summary>
        /// Pre-integration validation gate: ensures AudioMirror is fresh and LibChecker is clean.
        /// </summary>
        /// <param name="mirrorPath">Path to the AudioMirror folder.</param>
        /// <returns>true if gate passes (mirror fresh and LibChecker clean), false otherwise.</returns>
        private static bool RunPreIntegrationGate(string mirrorPath, bool jsonOutput)
        {
            Console.WriteLine("\nPre-integration validation...");
            string retryHint = PendingMirrorRetryHint(Constants.LogsPath);
            if (retryHint != null) Console.WriteLine(" - NOTE: " + retryHint);

            try
            {
                // Get mirror repo path
                string mirrorRepoPath = Path.GetFullPath(Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory, Constants.MirrorRepoPath));

                // The steps run lazily inside DecidePreIntegrationGate, in the same order as before:
                // repo check, then regen + status, then LibChecker only when the mirror is fresh.
                Parser p = null;
                string statusOutput = "";
                int libIssues = 0;
                PreIntegrationGateResult result = DecidePreIntegrationGate(
                    Directory.Exists(mirrorRepoPath),
                    () =>
                    {
                        // Step 1: Regenerate AudioMirror XMLs
                        Console.WriteLine(" - Regenerating AudioMirror XMLs...");
                        Reflector r = new Reflector(mirrorPath);
                        p = new Parser(mirrorPath);

                        // Step 2: Check if AudioMirror XMLs changed (is it fresh?)
                        Console.WriteLine(" - Checking if AudioMirror is fresh...");
                        statusOutput = RunGitStatus(mirrorRepoPath);
                        return statusOutput;
                    },
                    () =>
                    {
                        // Step 3: Verify LibChecker is clean
                        Console.WriteLine(" - Running library validation...");
                        LibChecker lc = new LibChecker(p.audioTags);
                        libIssues = lc.TotalHits;
                        return lc.IsClean;
                    });

                if (jsonOutput)
                {
                    var statusLines = statusOutput.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
                    Console.WriteLine($" - Gate JSON written: {GateResultJson.Write(result, statusLines, libIssues)}");
                }

                switch (result)
                {
                    case PreIntegrationGateResult.MirrorRepoMissing:
                        Console.WriteLine(" - ERROR: AudioMirror repo not found. Cannot validate library state.");
                        return false;
                    case PreIntegrationGateResult.MirrorStale:
                        Console.WriteLine(" - ERROR: AudioMirror is out of sync with the library.");
                        Console.WriteLine("   AudioMirror is the source of truth for all library validation.");
                        Console.WriteLine("   Commit the mirror changes (git commit in AudioMirror/) before integrating new songs.");
                        return false;
                    case PreIntegrationGateResult.GitStatusFailed:
                        Console.WriteLine(" - ERROR: Could not read AudioMirror git status (git failed or is not a repo). Fix git in the AudioMirror folder, then retry.");
                        return false;
                    case PreIntegrationGateResult.LibCheckerDirty:
                        Console.WriteLine(" - ERROR: LibChecker found issues in the library.");
                        return false;
                    default:
                        Console.WriteLine(" - Pre-integration validation passed.\n");
                        return true;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($" - ERROR during pre-integration validation: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Outcome of the pre-integration gate. Checks run in this order: repo exists, mirror fresh, LibChecker clean.
        /// </summary>
        internal enum PreIntegrationGateResult
        {
            Passed,
            MirrorRepoMissing,
            MirrorStale,
            LibCheckerDirty,
            GitStatusFailed,
        }

        /// <summary>
        /// Pure decision for the pre-integration gate. mirrorStatus and libCheckerClean are only invoked
        /// when the earlier checks have not already decided the outcome.
        /// </summary>
        internal static PreIntegrationGateResult DecidePreIntegrationGate(
            bool mirrorRepoExists, Func<string> mirrorStatus, Func<bool> libCheckerClean)
        {
            if (!mirrorRepoExists) return PreIntegrationGateResult.MirrorRepoMissing;
            string status = mirrorStatus();
            if (status == "error") return PreIntegrationGateResult.GitStatusFailed;
            if (IsMirrorStale(status)) return PreIntegrationGateResult.MirrorStale;
            if (!libCheckerClean()) return PreIntegrationGateResult.LibCheckerDirty;
            return PreIntegrationGateResult.Passed;
        }

        /// <summary>
        /// True when 'git status --porcelain' output lists any change other than LastRunInfo.txt.
        /// The "error" sentinel from RunGitStatus is not a porcelain line, so it counts as stale (fails closed).
        /// </summary>
        internal static bool IsMirrorStale(string mirrorStatusOutput)
        {
            // Ignore LastRunInfo.txt (just a timestamp) - only care about XML changes
            return mirrorStatusOutput
                .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
                .Any(line => !line.Contains("LastRunInfo.txt"));
        }

        /// <summary>
        /// Runs 'git status --porcelain' and returns stdout. Returns "error" when git cannot run or
        /// exits non-zero (not a repo, dubious ownership), so the gate fails closed instead of reading
        /// empty output as a fresh mirror.
        /// </summary>
        internal static string RunGitStatus(string repoPath)
        {
            var result = AudioMirrorCommitter.RunGitDetailed(repoPath, "-c core.quotepath=false status --porcelain");
            return result.ExitCode == 0 ? result.Stdout : "error";
        }

        /// <summary>
        /// Prompt the user to select the program mode.
        /// Returns 1 = Analysis, 2 = Analysis (Force Regen), 3 = Integrate.
        /// </summary>
        private static int PromptMode()
        {
            int selected = PromptMenu("Select mode:", new[] { "Analysis", "Analysis (Force Regen)", "Integrate" });
            return selected + 1; // 1, 2, or 3
        }

        /// <summary>
        /// Displays an arrow-key navigable menu and returns the index of the selected option.
        /// </summary>
        /// <param name="title">The title shown above the options.</param>
        /// <param name="options">The options to display.</param>
        /// <returns>The zero-based index of the selected option.</returns>
        private static int PromptMenu(string title, string[] options)
        {
            int selected = 0;
            while (true)
            {
                Console.Clear();
                Console.WriteLine("###### Audio Manager ######\n");
                Console.WriteLine(title + "\n");
                for (int i = 0; i < options.Length; i++)
                {
                    Console.WriteLine(i == selected ? $"  > {options[i]}" : $"    {options[i]}");
                }
                var key = Console.ReadKey(intercept: true).Key;
                if (key == ConsoleKey.UpArrow) selected = (selected == 0) ? options.Length - 1 : selected - 1;
                if (key == ConsoleKey.DownArrow) selected = (selected == options.Length - 1) ? 0 : selected + 1;
                if (key == ConsoleKey.Enter) return selected;
            }
        }
    }
}