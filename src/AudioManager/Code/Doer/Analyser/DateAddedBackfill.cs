using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using AudioManager.Code.Modules;

namespace AudioManager
{
    /// <summary>
    /// analysis --backfill-date-added [--apply]: fills the optional DateAdded element in every AudioMirror XML that lacks
    /// one. The date is the commit date of the commit that first added the file, from ONE bulk git log (never one git call
    /// per file). A rename inherits the old path's date. An existing DateAdded is never overwritten, and a path with no
    /// history stays unset and counts as unresolved. Dry run is the default and writes nothing; --apply rewrites one file
    /// at a time through TrackXML. Git is injected so tests fake it.
    /// </summary>
    internal static class DateAddedBackfill
    {
        private const string Flag = "--backfill-date-added";
        private const string ApplyFlag = "--apply";
        private const string MirrorPrefix = "AUDIO_MIRROR/";
        private const string CommitTag = "COMMIT ";
        private const string LogArgs = "-c core.quotepath=false log --reverse --root -M --diff-filter=AR --name-status --format=\"COMMIT %cs\"";
        private const string StatusArgs = "-c core.quotepath=false status --porcelain -uall -- AUDIO_MIRROR";

        /// <summary>What a scan found. FillDates maps each mirror-relative key (backslashes) to the date it will receive.</summary>
        internal sealed class Report
        {
            public int Scanned;
            public int ToFill;
            public int AlreadySet;
            public int Unresolved;
            public int Unreadable;
            public string Earliest;
            public string Latest;
            public SortedDictionary<int, int> PerYear = new SortedDictionary<int, int>();
            public Dictionary<string, string> FillDates = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Parses "git log --reverse --name-status --format=COMMIT %cs" output into mirror-relative key -> first-add date.
        /// Oldest commit first: an A sets the date once (a later re-add keeps the first); an R copies the old key's date to
        /// the new key when the new key has none. Only AUDIO_MIRROR XML paths are keyed.
        /// </summary>
        internal static Dictionary<string, string> FirstAddDates(string logOutput)
        {
            // Tracked by full repo path: older history kept XML outside AUDIO_MIRROR/, and a rename can start there.
            var repoDates = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string commitDate = null;
            foreach (string raw in (logOutput ?? "").Split('\n'))
            {
                string line = raw.TrimEnd('\r');
                if (line.StartsWith(CommitTag, StringComparison.Ordinal))
                {
                    commitDate = line.Substring(CommitTag.Length).Trim();
                    continue;
                }
                if (commitDate == null || line.Length == 0) continue;
                string[] parts = line.Split('\t');
                if (parts[0].StartsWith("A", StringComparison.Ordinal) && parts.Length >= 2)
                {
                    if (IsXmlPath(parts[1]) && !repoDates.ContainsKey(parts[1])) repoDates[parts[1]] = commitDate;
                }
                else if (parts[0].StartsWith("R", StringComparison.Ordinal) && parts.Length >= 3)
                {
                    if (!IsXmlPath(parts[2]) || repoDates.ContainsKey(parts[2])) continue;
                    if (IsXmlPath(parts[1]) && repoDates.TryGetValue(parts[1], out string inherited)) repoDates[parts[2]] = inherited;
                }
            }
            var dates = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, string> entry in repoDates)
            {
                string key = KeyFromGitPath(entry.Key);
                if (key != null) dates[key] = entry.Value;
            }
            return dates;
        }

        private static bool IsXmlPath(string gitPath) => gitPath.EndsWith(".xml", StringComparison.OrdinalIgnoreCase);

        /// <summary>Mirror-relative key (backslashes) for a repo-relative git path, or null when it is not an AUDIO_MIRROR XML.</summary>
        private static string KeyFromGitPath(string gitPath)
        {
            if (!gitPath.StartsWith(MirrorPrefix, StringComparison.Ordinal)) return null;
            if (!IsXmlPath(gitPath)) return null;
            return gitPath.Substring(MirrorPrefix.Length).Replace('/', '\\');
        }

        /// <summary>Counts every XML under the mirror folder without writing. A file is filled only when it lacks DateAdded and has a date.</summary>
        internal static Report Scan(string mirrorRoot, Dictionary<string, string> firstAdds)
        {
            var report = new Report();
            foreach (string file in Directory.GetFiles(mirrorRoot, "*.xml", SearchOption.AllDirectories))
            {
                // *.xml also matches longer extensions on .NET Framework, so filter exactly.
                if (!file.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)) continue;
                report.Scanned++;
                string key = FirstSeenDates.KeyFor(mirrorRoot, file);
                if (key == null) { report.Unreadable++; continue; }
                if (!TryReadDateAdded(file, out string existing)) { report.Unreadable++; continue; }
                firstAdds.TryGetValue(key, out string gitDate);
                DateTime? mp3 = Mp3Created?.Invoke(key);
                // Best guess = the earliest known date: an existing value is never moved later, and a wrong today-stamp loses to any real source.
                string date = Earliest(existing, gitDate, mp3?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                if (date == null) { report.Unresolved++; continue; }
                if (date == existing) { report.AlreadySet++; continue; }

                report.ToFill++;
                report.FillDates[key] = date;
                if (report.Earliest == null || string.CompareOrdinal(date, report.Earliest) < 0) report.Earliest = date;
                if (report.Latest == null || string.CompareOrdinal(date, report.Latest) > 0) report.Latest = date;
                int year = int.Parse(date.Substring(0, 4), CultureInfo.InvariantCulture);
                report.PerYear[year] = (report.PerYear.TryGetValue(year, out int n) ? n : 0) + 1;
            }
            return report;
        }

        /// <summary>MP3 creation date for a mirror key (null = unknown). Real lookup by default; tests may replace it.</summary>
        internal static Func<string, DateTime?> Mp3Created = RealMp3Created;

        private static DateTime? RealMp3Created(string key)
        {
            try
            {
                string mp3 = Path.ChangeExtension(Path.Combine(Constants.AudioFolderPath, key.TrimStart('\\', '/')), ".mp3");
                return File.Exists(mp3) ? File.GetCreationTime(mp3).Date : (DateTime?)null;
            }
            catch (Exception) { return null; }
        }

        /// <summary>Earliest valid yyyy-MM-dd among the candidates (nulls and malformed values ignored), or null.</summary>
        internal static string Earliest(params string[] candidates)
        {
            string best = null;
            foreach (string c in candidates)
            {
                if (!DateTime.TryParseExact(c, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) continue;
                if (best == null || string.CompareOrdinal(c, best) < 0) best = c;
            }
            return best;
        }

        /// <summary>False when the file cannot be read as XML; existing is the valid DateAdded text or null (absent, empty or malformed).</summary>
        private static bool TryReadDateAdded(string path, out string existing)
        {
            existing = null;
            try
            {
                string v = XDocument.Load(path).Root?.Element("DateAdded")?.Value;
                if (DateTime.TryParseExact(v, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) existing = v;
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Entry point from Main, before any library scan. Returns false when --backfill-date-added is absent (normal flow).
        /// Otherwise it handles the request, sets exitCode and returns true.
        /// </summary>
        internal static bool TryRun(string[] args, Func<string, AudioMirrorCommitter.GitRunResult> git, string mirrorRoot, out int exitCode)
        {
            exitCode = 0;
            if (!args.Any(a => a.Equals(Flag, StringComparison.OrdinalIgnoreCase))) return false;

            if (args.Length == 0 || !IsAnalysisWord(args[0]))
                return Refuse("--backfill-date-added is only valid with analysis (e.g. analysis --backfill-date-added).", out exitCode);
            bool apply = false;
            for (int i = 1; i < args.Length; i++)
            {
                string a = args[i];
                if (a.Equals(Flag, StringComparison.OrdinalIgnoreCase)) continue;
                if (a.Equals(ApplyFlag, StringComparison.OrdinalIgnoreCase)) { apply = true; continue; }
                return Refuse($"'{a}' is not valid with --backfill-date-added (only --apply is).", out exitCode);
            }
            if (!Directory.Exists(mirrorRoot))
                return Refuse("AUDIO_MIRROR folder not found: " + mirrorRoot, out exitCode);

            AudioMirrorCommitter.GitRunResult log = git(LogArgs);
            if (log.ExitCode != 0) return Refuse("git log failed: " + FirstLine(log), out exitCode);
            Dictionary<string, string> firstAdds = FirstAddDates(log.Stdout);
            Report report = Scan(mirrorRoot, firstAdds);
            PrintCounts(report, apply);

            if (!apply)
            {
                Console.WriteLine(" - Nothing was written. Run with --apply to write DateAdded.");
                return true;
            }

            AudioMirrorCommitter.GitRunResult status = git(StatusArgs);
            if (status.ExitCode != 0) return Refuse("git status failed, nothing was written: " + FirstLine(status), out exitCode);
            int modified = CountModifiedXml(status.Stdout);
            Console.WriteLine($" - WARNING: {modified} AUDIO_MIRROR XML file(s) already modified in the working tree (git status) before this run.");

            int written = 0, failed = 0;
            foreach (KeyValuePair<string, string> fill in report.FillDates.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
            {
                string path = Path.Combine(mirrorRoot, fill.Key);
                try
                {
                    var tag = new TrackTag(fill.Key, "", "", "", "", "", "", "", "", "", "", "");
                    TrackXML.ReadLenient(path, tag);
                    tag.DateAdded = fill.Value;
                    TrackXML.Write(path, tag);
                    written++;
                }
                catch (Exception ex)
                {
                    failed++;
                    Console.WriteLine($" - [!] Could not write DateAdded to {fill.Key}: {ex.Message}");
                }
            }
            Console.WriteLine($" - Applied: {written} written, {failed} failed.");
            exitCode = failed == 0 ? 0 : 1;
            return true;
        }

        /// <summary>Porcelain lines whose path (after the two status columns, and after " -> " for a rename) ends in .xml.</summary>
        private static int CountModifiedXml(string porcelain)
        {
            int n = 0;
            foreach (string raw in (porcelain ?? "").Split('\n'))
            {
                string line = raw.TrimEnd('\r');
                if (line.Length <= 3) continue;
                string path = line.Substring(3);
                int arrow = path.IndexOf(" -> ", StringComparison.Ordinal);
                if (arrow >= 0) path = path.Substring(arrow + 4);
                if (path.Trim('"').StartsWith(MirrorPrefix, StringComparison.Ordinal) && path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)) n++;
            }
            return n;
        }

        private static void PrintCounts(Report r, bool apply)
        {
            Console.WriteLine($" - DateAdded backfill ({(apply ? "apply" : "dry run")}): {r.Scanned} XML files under AUDIO_MIRROR");
            Console.WriteLine($" - To fill: {r.ToFill}");
            Console.WriteLine($" - Already set: {r.AlreadySet}");
            Console.WriteLine($" - Unresolved (no git history): {r.Unresolved}");
            Console.WriteLine($" - Unreadable XML (skipped): {r.Unreadable}");
            Console.WriteLine($" - Earliest fill: {r.Earliest ?? "none"}; latest fill: {r.Latest ?? "none"}");
            Console.WriteLine(" - Per year (to fill):");
            foreach (KeyValuePair<int, int> year in r.PerYear)
                Console.WriteLine($"     {year.Key}: {year.Value}");
        }

        private static bool IsAnalysisWord(string word) =>
            word.Equals("analysis", StringComparison.OrdinalIgnoreCase) ||
            word.Equals("analyse", StringComparison.OrdinalIgnoreCase) ||
            word.Equals("analyze", StringComparison.OrdinalIgnoreCase);

        private static string FirstLine(AudioMirrorCommitter.GitRunResult r)
        {
            string text = ((r.Stderr ?? "") + "\n" + (r.Stdout ?? "")).Trim();
            return text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "(no output)";
        }

        private static bool Refuse(string message, out int exitCode)
        {
            string text = message.EndsWith(".", StringComparison.Ordinal) ? message : message + ".";
            Console.WriteLine($" - ERROR: {text} Nothing was run.");
            exitCode = 1;
            return true;
        }
    }
}
