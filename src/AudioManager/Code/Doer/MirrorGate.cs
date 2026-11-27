using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using AudioManager.Code.Modules;

namespace AudioManager
{
    /// <summary>One changed AudioMirror XML that failed the health gate. File is repo-relative with forward slashes.</summary>
    internal sealed class MirrorProblem
    {
        public string File;
        public string Reason;
    }

    /// <summary>
    /// Outcome of MirrorGate.Validate and the commit commands. Ok means the check ran and found nothing wrong
    /// (no git error and no problems). Committed is true only when --commit-mirror or the auto-commit made a commit.
    /// </summary>
    internal sealed class MirrorGateResult
    {
        public int Checked;
        public List<MirrorProblem> Problems = new List<MirrorProblem>();
        // GUI-90: every LibChecker hit over the AUDIO_MIRROR folder (fix-later kinds included). Only LibraryBlocked refuses.
        public List<MirrorLibraryHit> Library = new List<MirrorLibraryHit>();
        public bool LibraryBlocked;
        public string Error = string.Empty;
        public bool Committed;
        public bool Ok => Error.Length == 0 && Problems.Count == 0 && !LibraryBlocked;
    }

    /// <summary>One LibChecker hit for the "library" JSON key. Path is mirror-relative (LibChecker's own path, forward slashes).</summary>
    internal sealed class MirrorLibraryHit
    {
        public string Kind;
        public string Path;
        public string Detail;
    }

    /// <summary>
    /// The health gate every AudioMirror commit path runs before git add (BL-317 / GUI-140). A set of changed or
    /// untracked XML files is committed only when every file passes the per-file rules, which reuse the LibChecker
    /// and FieldRules (BL-319, Year through YearRule) rules. Local commits only: nothing here pushes.
    /// Rules run per file: well-formed XML, the FieldRules BLOCK results (Title, Artists, Album, Year, Length, cover size;
    /// whitespace only on added files), exactly one album cover, Compilation set. Deleted files are skipped. Library-wide LibChecker rules
    /// (filename match, unwanted text, duplicates, folder rules) need the whole library and are not run here.
    /// Output contract: docs/Contracts/MirrorCommitJSON.md.
    /// </summary>
    internal static class MirrorGate
    {
        internal const int ExitOk = 0;
        internal const int ExitError = 1;
        internal const int ExitRefused = 3;
        // Stages only the mirror folder and the audit file, the same as AudioMirrorCommitter (never add -A).
        internal const string CommitAddArgs = "add AUDIO_MIRROR/";

        internal static string DefaultRepoPath()
        {
            return Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, Constants.MirrorRepoPath));
        }

        /// <summary>Lists changed and untracked XML files in the repo and runs CheckXml on each one that still exists.</summary>
        internal static MirrorGateResult Validate(string repoPath)
        {
            var result = new MirrorGateResult();
            if (!Directory.Exists(repoPath))
            {
                result.Error = "AudioMirror repo not found at: " + repoPath;
                return result;
            }

            AudioMirrorCommitter.GitRunResult status = AudioMirrorCommitter.RunGitDetailed(repoPath, "-c core.quotepath=false status --porcelain -uall");
            if (status.ExitCode != 0)
            {
                result.Error = FirstLine(status.Stdout + status.Stderr, "git status failed");
                return result;
            }

            foreach (string rawLine in status.Stdout.Split('\n'))
            {
                string line = rawLine.TrimEnd('\r');
                if (line.Length < 4) continue;

                // Porcelain v1: two status columns, a space, then the path. A deleted file has nothing to validate.
                string statusCode = line.Substring(0, 2);
                if (statusCode.IndexOf('D') >= 0) continue;

                string path = PorcelainPath(line.Substring(3));
                if (!path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)) continue;

                string fullPath = Path.Combine(repoPath, path.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(fullPath)) continue;

                result.Checked++;
                // Untracked (??) or added (A) is a new file: its whitespace problems refuse. Any other status is an existing file: they warn.
                bool added = statusCode == "??" || statusCode[0] == 'A';
                var warnings = new List<string>();
                string reason = CheckXml(fullPath, added, warnings);
                foreach (string w in warnings)
                    Console.WriteLine($"[WARN]   {path.Replace('\\', '/')}: {w} (existing file, not refused)");
                if (reason != null)
                    result.Problems.Add(new MirrorProblem { File = path.Replace('\\', '/'), Reason = reason });
            }
            return result;
        }

        /// <summary>
        /// The per-file rules. Returns null when the XML is healthy, otherwise the reason it is refused. A whitespace
        /// problem refuses only a new file (added); on an existing file it is added to warnings instead.
        /// </summary>
        internal static string CheckXml(string fullPath, bool added, List<string> warnings)
        {
            var tag = new TrackTag("", "", "", "", "", "", "", "", "", "", "", "");
            try
            {
                TrackXML.Read(fullPath, tag);
            }
            catch (Exception ex)
            {
                string detail = ex.InnerException != null ? ex.InnerException.Message : ex.Message;
                return "not well-formed track XML: " + detail;
            }

            string fieldProblem = FirstBlockingFieldProblem(tag, added, warnings);
            if (fieldProblem != null) return fieldProblem;

            string coverProblem = LibChecker.CoverCountProblem(tag.AlbumCoverCount);
            if (coverProblem != null) return "track " + coverProblem;

            try
            {
                string compilationProblem = LibChecker.CompilationProblem(tag.Compilation);
                if (compilationProblem != null) return "track " + compilationProblem;
            }
            catch (FormatException)
            {
                return "track Compilation is not a boolean";
            }
            return null;
        }

        /// <summary>Prints one [WARN] line per problem. Used before the JSON line, never after it.</summary>
        internal static void PrintProblems(IList<MirrorProblem> problems)
        {
            foreach (MirrorProblem p in problems)
                Console.WriteLine($"[WARN]   {p.File}: {p.Reason}");
        }

        /// <summary>
        /// GUI-90: parses the AUDIO_MIRROR folder and runs LibChecker over it. Every hit goes to result.Library; the
        /// commit is refused only when LibChecker.HasBlockingHits (a kind outside FixLaterKinds). A Parser failure
        /// becomes result.Error so the JSON line stays the last stdout line.
        /// </summary>
        internal static void ValidateLibrary(string repoPath, MirrorGateResult result, string cachePath)
        {
            string mirrorFolder = Path.Combine(repoPath, Constants.MirrorFolderName);
            LibChecker checker;
            try
            {
                var parser = new Parser(mirrorFolder, cachePath);
                checker = new LibChecker(parser.audioTags);
            }
            catch (Exception ex)
            {
                string detail = ex.InnerException != null ? ex.InnerException.Message : ex.Message;
                result.Error = "library check failed: " + detail + ". Nothing was committed.";
                return;
            }

            foreach (LibChecker.LibCheckerHit hit in checker.Hits)
                result.Library.Add(new MirrorLibraryHit { Kind = hit.Kind, Path = hit.Path.Replace('\\', '/'), Detail = hit.Detail });
            result.LibraryBlocked = checker.HasBlockingHits;
        }

        /// <summary>Prints one [WARN] line per library hit. Used before the JSON line, never after it.</summary>
        internal static void PrintLibraryHits(IList<MirrorLibraryHit> hits)
        {
            foreach (MirrorLibraryHit h in hits)
                Console.WriteLine($"[WARN]   library {h.Kind} {h.Path}: {h.Detail}");
        }

        /// <summary>Pure builder for the one-line JSON object the GUI reads as the last stdout line.</summary>
        internal static string ToJson(MirrorGateResult r)
        {
            var sb = new StringBuilder();
            sb.Append("{\"ok\": ").Append(r.Ok ? "true" : "false");
            sb.Append(", \"committed\": ").Append(r.Committed ? "true" : "false");
            sb.Append(", \"checked\": ").Append(r.Checked.ToString(CultureInfo.InvariantCulture));
            sb.Append(", \"problems\": [");
            for (int i = 0; i < r.Problems.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append("{\"file\": ").Append(JsonText.Quote(r.Problems[i].File))
                  .Append(", \"reason\": ").Append(JsonText.Quote(r.Problems[i].Reason)).Append('}');
            }
            sb.Append("], \"library\": [");
            for (int i = 0; i < r.Library.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append("{\"kind\": ").Append(JsonText.Quote(r.Library[i].Kind))
                  .Append(", \"path\": ").Append(JsonText.Quote(r.Library[i].Path ?? string.Empty))
                  .Append(", \"detail\": ").Append(JsonText.Quote(r.Library[i].Detail ?? string.Empty)).Append('}');
            }
            sb.Append("], \"error\": ").Append(JsonText.Quote(r.Error ?? string.Empty));
            sb.Append('}');
            return sb.ToString();
        }

        /// <summary>
        /// Runs --validate-mirror (commit false) or --commit-mirror (commit true) against repoPath. Prints any
        /// [WARN] problem lines and commit output, then exactly one JSON line last. Returns the exit code:
        /// 0 ok, 3 refused (problems), 1 any other error.
        /// </summary>
        internal static int RunCommand(string repoPath, bool commit, string message, string cachePath = null)
        {
            var result = new MirrorGateResult();
            int code;
            if (commit && string.IsNullOrWhiteSpace(message))
            {
                result.Error = "--message requires a non-empty commit message. Nothing was committed.";
                code = ExitError;
            }
            else
            {
                result = Validate(repoPath);
                // GUI-90: the library check runs even when per-file problems exist, so all issues surface in one round.
                if (result.Error.Length == 0) ValidateLibrary(repoPath, result, cachePath);
                if (result.Error.Length > 0)
                {
                    code = ExitError;
                }
                else if (result.Problems.Count > 0 || result.LibraryBlocked)
                {
                    PrintProblems(result.Problems);
                    if (result.LibraryBlocked) PrintLibraryHits(result.Library);
                    string refusedFiles = result.Problems.Count > 0 ? $"{result.Problems.Count} XML file(s) failed the health gate" : "the library check found blocking issues";
                    Console.WriteLine($"[WARN] AudioMirror commit refused: {refusedFiles}. Nothing was committed.");
                    code = ExitRefused;
                }
                else if (!commit)
                {
                    PrintLibraryHits(result.Library);
                    code = ExitOk;
                }
                else
                {
                    PrintLibraryHits(result.Library);
                    // Local commit of the whole AudioMirror repo. Never a push.
                    string failureDetail;
                    CommitOutcome outcome = AudioMirrorCommitter.StageAndCommit(repoPath, CommitAddArgs, message, out failureDetail);
                    if (outcome == CommitOutcome.Failure)
                    {
                        result.Error = failureDetail + ". Nothing was committed. Close any program using the AudioMirror repo (Desktop Plus) and try again.";
                        code = ExitError;
                    }
                    else
                    {
                        result.Committed = outcome == CommitOutcome.Success;
                        code = ExitOk;
                    }
                }
            }
            Console.WriteLine(ToJson(result));
            return code;
        }

        /// <summary>
        /// Handles --validate-mirror and --commit-mirror --message "text". Returns false when neither flag is present
        /// so Main carries on. Any other flag combination is reported as a JSON error with exit 1.
        /// </summary>
        internal static bool TryRunCommand(string[] args, out int exitCode)
        {
            bool validate = args.Any(a => a.Equals("--validate-mirror", StringComparison.OrdinalIgnoreCase));
            bool commit = args.Any(a => a.Equals("--commit-mirror", StringComparison.OrdinalIgnoreCase));
            exitCode = ExitOk;
            if (!validate && !commit) return false;

            if (validate && commit)
            {
                exitCode = ExitError;
                Console.WriteLine(ToJson(new MirrorGateResult { Error = "use only one of --validate-mirror, --commit-mirror. Nothing was run." }));
                return true;
            }

            int messageIdx = Array.FindIndex(args, a => a.Equals("--message", StringComparison.OrdinalIgnoreCase));
            string message = null;
            if (messageIdx >= 0 && messageIdx + 1 < args.Length && !args[messageIdx + 1].StartsWith("--", StringComparison.Ordinal))
                message = args[messageIdx + 1];

            if (commit && messageIdx < 0)
            {
                exitCode = ExitError;
                Console.WriteLine(ToJson(new MirrorGateResult { Error = "--commit-mirror requires --message \"text\". Nothing was committed." }));
                return true;
            }
            if (commit && message == null)
            {
                exitCode = ExitError;
                Console.WriteLine(ToJson(new MirrorGateResult { Error = "--message requires a text argument. Nothing was committed." }));
                return true;
            }

            exitCode = RunCommand(DefaultRepoPath(), commit, message);
            return true;
        }

        // BL-319: the FieldRules BLOCK results over the fields this gate reads. WARN results never refuse a commit.
        // Genres and TrackNumber have no FieldRules rule (David 2026-11-27), so they are not checked here.
        private static string FirstBlockingFieldProblem(TrackTag tag, bool added, List<string> warnings)
        {
            var checks = new[]
            {
                ("Title", tag.Title), ("Artists", tag.Artists), ("Album", tag.Album), ("Year", tag.Year),
                ("Length", tag.Length), ("CoverWidth", tag.CoverWidth), ("CoverHeight", tag.CoverHeight),
            };
            foreach (var check in checks)
            {
                FieldProblem p = FieldRules.Check(check.Item1, check.Item2);
                if (p == null || p.Severity != FieldSeverity.Block) continue;
                if (p.Whitespace && !added)
                {
                    warnings.Add(check.Item1 + " " + p.Reason);
                    continue;
                }
                return check.Item1 + " " + p.Reason;
            }
            // FieldRules accepts Missing (a stored value), so a new file with no Year is refused here (David 2026-11-27).
            if (added && tag.Year == Constants.MissingTag) return "Year is missing (a new file needs a year)";
            return null;
        }

        // Porcelain path after the status columns: a rename shows 'old -> new' (keep new), a quoted path loses its quotes.
        private static string PorcelainPath(string field)
        {
            string path = field;
            int arrow = path.IndexOf(" -> ", StringComparison.Ordinal);
            if (arrow >= 0) path = path.Substring(arrow + 4);
            if (path.Length >= 2 && path[0] == '"' && path[path.Length - 1] == '"')
                path = path.Substring(1, path.Length - 2);
            return path;
        }

        private static string FirstLine(string text, string fallback)
        {
            string line = (text ?? string.Empty).Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
            return string.IsNullOrEmpty(line) ? fallback : line;
        }
    }
}
