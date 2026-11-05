using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace AudioManager
{
    /// <summary>
    /// Saves the audio report to the reports folder (data/reports/).
    /// </summary>
    internal static class ReportWriter
    {
        /// <summary>
        /// Saves the given report content to a dated .txt file.
        /// If LibChecker found issues, saves to data\reports\with-issues\ (gitignored).
        /// If clean, saves to data\reports\{year}\ for records.
        /// Overwrites any existing report for today. Prepends a generated header.
        /// </summary>
        /// <param name="reportContent">The captured console output from the Analysis run.</param>
        /// <param name="libCheckerClean">Whether LibChecker reported zero issues.</param>
        /// <param name="reportsRoot">Override reports root folder. Null uses Constants.ReportsPath (production default).</param>
        public static void Save(string reportContent, bool libCheckerClean, string reportsRoot = null)
        {
            string effectiveRoot = reportsRoot ?? Constants.ReportsPath;
            DateTime now = DateTime.Now;
            string filename = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + " - AudioReport.md";

            // Determine save location based on LibChecker status
            string folder;
            string displayPath;
            if (libCheckerClean)
            {
                // Clean library - save to permanent records
                string year = now.Year.ToString();
                folder = Path.Combine(effectiveRoot, year);
                displayPath = $"data\\reports\\{year}\\{filename}";
            }
            else
            {
                // Issues found - save to gitignored folder to avoid cluttering diffs
                folder = Path.Combine(effectiveRoot, "with-issues");
                displayPath = $"data\\reports\\with-issues\\{filename}";
            }

            // Build and prepend header
            string header = BuildHeader(now);

            // Save to file (overwrite same-day report if it exists)
            Directory.CreateDirectory(folder);
            string fullPath = Path.Combine(folder, filename);
            AtomicFile.WriteAllText(fullPath, header + reportContent, new UTF8Encoding(false));

            Console.WriteLine($"\nReport saved: {fullPath}");
        }

        /// <summary>
        /// Builds the report file header with date, git commit hash and link in markdown format.
        /// </summary>
        private static string BuildHeader(DateTime now)
        {
            string date = now.ToString("dddd d MMMM yyyy, HH:mm", CultureInfo.InvariantCulture);
            string commitHash = GetGitCommitHash();
            string commitLink = string.IsNullOrEmpty(commitHash)
                ? "unavailable"
                : $"[{commitHash}](https://github.com/DavoDC/AudioManager/commit/{commitHash})";

            return "# Audio Report\n\n" +
                   $"**Generated:** {date}  \n" +
                   $"**Commit:** {commitLink}\n\n" +
                   "---\n\n";
        }

        /// <summary>
        /// Runs 'git rev-parse HEAD' and returns the full commit hash.
        /// Returns null if git is unavailable or the command fails.
        /// </summary>
        private static string GetGitCommitHash()
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("git", "rev-parse HEAD")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = Constants.ReportsPath
                };
                int pid;
                AudioMirrorCommitter.GitRunResult result = AudioMirrorCommitter.RunProcessBounded(psi, Constants.GitTimeoutMs, out pid);
                if (result.TimedOut)
                {
                    Console.WriteLine($"git rev-parse timed out after {Constants.GitTimeoutMs} ms; report commit shown as unavailable.");
                }
                string hash = result.Stdout.Trim();
                return string.IsNullOrEmpty(hash) ? null : hash;
            }
            catch
            {
                return null;
            }
        }
    }
}
