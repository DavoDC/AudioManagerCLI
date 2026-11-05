using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace AudioManager
{
    internal enum CommitTrigger
    {
        AnalysisForceRegen,  // Force regen: mirror is reliable -> auto-commit if clean
        AnalysisIncremental, // Non-force regen: mirror may have stale XMLs -> skip auto-commit
        Integration,         // Post-integration: mirror is reliable -> auto-commit if clean
    }

    /// <summary>
    /// Auto-commits AudioMirror repo after a clean analysis or integration run.
    /// Rules: only commits if LibChecker was clean AND trigger mode is reliable
    /// (force regen or integration). Never pushes - user pushes manually.
    /// </summary>
    internal static class AudioMirrorCommitter
    {
        /// <summary>
        /// Auto-commit AudioMirror if LibChecker was clean and trigger is a reliable mode.
        /// Skips silently for incremental analysis (mirror may have stale XMLs).
        /// </summary>
        /// <summary>
        /// Returns the reason to skip auto-commit ("incremental" or "dirty"), or null if the commit
        /// should proceed to git. Pure function - no I/O. Extracted for testability.
        /// </summary>
        internal static string GetSkipReason(bool libCheckerClean, CommitTrigger trigger)
        {
            if (trigger == CommitTrigger.AnalysisIncremental) return "incremental";
            if (!libCheckerClean) return "dirty";
            return null; // no skip reason - commit should proceed
        }

        /// <param name="libCheckerClean">Whether LibChecker reported zero issues.</param>
        /// <param name="trigger">What triggered this commit attempt.</param>
        public static void TryCommit(bool libCheckerClean, CommitTrigger trigger)
        {
            // Incremental analysis produces an unreliable mirror state - skip
            if (trigger == CommitTrigger.AnalysisIncremental)
            {
                Console.WriteLine("\nAudioMirror: skipping auto-commit (incremental mirror - run force regen for auto-commit).");
                return;
            }

            string repoPath = Path.GetFullPath(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, Constants.MirrorRepoPath));

            Console.WriteLine("\nAudioMirror auto-commit:");

            if (!libCheckerClean)
            {
                Console.WriteLine(" - Skipped: LibChecker reported issues. Fix them first, then re-run.");
                return;
            }

            if (!Directory.Exists(repoPath))
            {
                Console.WriteLine($" - ERROR: AudioMirror repo not found at: {repoPath}");
                return;
            }

            // Check if there are uncommitted changes
            string statusOutput = RunGit(repoPath, "-c core.quotepath=false status --porcelain");
            if (string.IsNullOrWhiteSpace(statusOutput))
            {
                Console.WriteLine(" - Clean (no changes to commit).");
                return;
            }

            // Build commit message with artist breakdown
            string commitTitle = DateTime.Now.ToString("MMM d") + " Update";
            string commitBody = BuildCommitBody(
                RunGit(repoPath, "-c core.quotepath=false status --porcelain=v1 -u all"),
                GetTrackedArtists(repoPath));
            string fullMessage = string.IsNullOrWhiteSpace(commitBody)
                ? commitTitle
                : commitTitle + "\n" + commitBody;

            // Stage AUDIO_MIRROR folder + LastRunInfo.txt (audit metadata at repo root)
            RunGit(repoPath, "add AUDIO_MIRROR/ LastRunInfo.txt");
            GitRunResult commit = RunGitDetailed(repoPath, $"commit -m \"{fullMessage.Replace("\"", "\\\"")}\"");
            string commitResult = commit.Stdout + commit.Stderr;

            if (IsNothingToCommit(commitResult))
            {
                Console.WriteLine(" - Nothing staged to commit.");
                return;
            }

            if (IsCommitFailure(commit.ExitCode, commitResult))
            {
                string firstLine = commitResult.Trim().Split('\n')[0].Trim();
                Console.WriteLine($" - FAILED: git commit exited {commit.ExitCode}. {firstLine}");
                Console.WriteLine(" - Nothing was committed. Fix the git error above, then re-run.");
                return;
            }

            if (string.IsNullOrWhiteSpace(commitResult))
            {
                Console.WriteLine(" - Nothing staged to commit.");
                return;
            }

            Console.WriteLine($" - Committed: {commitTitle}");
            if (!string.IsNullOrWhiteSpace(commitBody))
                Console.WriteLine(commitBody);
            Console.WriteLine(" - Push AudioMirror manually when ready.");
        }

        /// <summary>
        /// Builds the commit message body by parsing new files in the AudioMirror repo.
        /// Only counts untracked (new) files - modified files are regen noise.
        /// Sections: New artists / Existing artists / Musivation / Misc.
        /// </summary>
        /// <param name="statusOutput">Output of 'git status --porcelain=v1 -u all' (run with core.quotepath=false).</param>
        /// <param name="trackedArtists">Artist dirs already tracked in HEAD, to tell new artists from existing ones.</param>
        internal static string BuildCommitBody(string statusOutput, HashSet<string> trackedArtists)
        {
            if (string.IsNullOrWhiteSpace(statusOutput))
                return string.Empty;

            // Buckets
            var newArtistAlbums    = new Dictionary<string, SortedSet<string>>(StringComparer.OrdinalIgnoreCase);
            var existingArtistAlbums = new Dictionary<string, SortedSet<string>>(StringComparer.OrdinalIgnoreCase);
            var musivationTracks   = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            var miscArtists        = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string line in statusOutput.Split('\n'))
            {
                if (!line.StartsWith("??")) continue;

                // Path starts after "?? ", normalize separators
                string path = line.Substring(3).Trim().Replace('\\', '/');

                // git quotes paths with special characters even when quotepath is off; drop the quotes
                if (path.Length >= 2 && path[0] == '"' && path[path.Length - 1] == '"')
                    path = path.Substring(1, path.Length - 2);

                // Strip leading AUDIO_MIRROR/ prefix
                const string prefix = "AUDIO_MIRROR/";
                if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                string rel = path.Substring(prefix.Length);

                string[] parts = rel.Split('/');
                if (parts.Length < 2) continue;
                string topFolder = parts[0];

                if (topFolder.Equals(Constants.ArtistsDir, StringComparison.OrdinalIgnoreCase)
                    && parts.Length >= 3)
                {
                    string artist = parts[1];
                    string album  = parts[2];

                    if (!trackedArtists.Contains(artist))
                    {
                        AddAlbum(newArtistAlbums, artist, album);
                    }
                    else
                    {
                        AddAlbum(existingArtistAlbums, artist, album);
                    }
                }
                else if (topFolder.Equals(Constants.MusivDir, StringComparison.OrdinalIgnoreCase))
                {
                    string fileName = parts[parts.Length - 1];
                    if (fileName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                        fileName = fileName.Substring(0, fileName.Length - 4);
                    if (!string.IsNullOrWhiteSpace(fileName))
                        musivationTracks.Add(fileName);
                }
                else if (topFolder.Equals(Constants.MiscDir, StringComparison.OrdinalIgnoreCase))
                {
                    string fileName = parts[parts.Length - 1];
                    if (fileName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                        fileName = fileName.Substring(0, fileName.Length - 4);
                    // Extract artist part before " - "
                    int dashIdx = fileName.IndexOf(" - ", StringComparison.Ordinal);
                    string artist = dashIdx > 0 ? fileName.Substring(0, dashIdx) : fileName;
                    if (!string.IsNullOrWhiteSpace(artist))
                        miscArtists.Add(artist);
                }
            }

            if (newArtistAlbums.Count == 0 && existingArtistAlbums.Count == 0
                && musivationTracks.Count == 0 && miscArtists.Count == 0)
                return string.Empty;

            var sb = new StringBuilder();

            if (newArtistAlbums.Count > 0)
            {
                sb.AppendLine("\nNew artists:");
                foreach (var kvp in newArtistAlbums.OrderBy(k => k.Key))
                {
                    string albumList = string.Join(", ", kvp.Value);
                    sb.AppendLine($"- {kvp.Key} ({albumList})");
                }
            }

            if (existingArtistAlbums.Count > 0)
            {
                sb.AppendLine("\nExisting artists:");
                var entries = existingArtistAlbums.OrderBy(k => k.Key).Select(kvp =>
                {
                    bool singlesOnly = kvp.Value.Count == 1
                        && kvp.Value.First().Equals(Constants.SinglesDir, StringComparison.OrdinalIgnoreCase);
                    return singlesOnly
                        ? kvp.Key
                        : $"{kvp.Key} ({string.Join(", ", kvp.Value)})";
                });
                sb.AppendLine("- " + string.Join(", ", entries));
            }

            if (musivationTracks.Count > 0)
            {
                sb.AppendLine("\nMusivation:");
                foreach (string track in musivationTracks)
                    sb.AppendLine($"- {track}");
            }

            if (miscArtists.Count > 0)
                sb.AppendLine("\nMisc: " + string.Join(", ", miscArtists));

            return sb.ToString().TrimEnd();
        }

        /// <summary>
        /// Returns artist directory names already tracked in HEAD (i.e. not new this run).
        /// </summary>
        private static HashSet<string> GetTrackedArtists(string repoPath)
        {
            string output = RunGit(repoPath, $"ls-tree --name-only HEAD AUDIO_MIRROR/{Constants.ArtistsDir}/");
            return new HashSet<string>(
                output.Split('\n')
                      .Select(l => l.Trim())
                      .Where(l => !string.IsNullOrEmpty(l)),
                StringComparer.OrdinalIgnoreCase);
        }

        private static void AddAlbum(Dictionary<string, SortedSet<string>> dict, string artist, string album)
        {
            if (!dict.TryGetValue(artist, out var albums))
            {
                albums = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                dict[artist] = albums;
            }
            albums.Add(album);
        }

        /// <summary>
        /// Runs a git command in the given working directory and returns stdout+stderr.
        /// stderr is read on a background thread to prevent deadlock when both pipes fill.
        /// </summary>
        internal static string RunGit(string workingDir, string args)
        {
            GitRunResult result = RunGitDetailed(workingDir, args);
            return result.Stdout + result.Stderr;
        }

        /// <summary>
        /// Runs a git command and returns stdout, stderr and the exit code separately.
        /// Exit code -1 means git could not be started; Stderr then holds the error message.
        /// </summary>
        internal static GitRunResult RunGitDetailed(string workingDir, string args)
        {
            try
            {
                var psi = new ProcessStartInfo("git", args)
                {
                    WorkingDirectory = workingDir,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using (var proc = Process.Start(psi))
                {
                    // Read stderr on a background thread - prevents deadlock when git writes
                    // enough to stderr to fill the pipe buffer (e.g. CRLF warnings for many files).
                    var stderrTask = System.Threading.Tasks.Task.Run(() => proc.StandardError.ReadToEnd());
                    string stdout = proc.StandardOutput.ReadToEnd();
                    string stderr = stderrTask.Result;
                    proc.WaitForExit();
                    return new GitRunResult { ExitCode = proc.ExitCode, Stdout = stdout, Stderr = stderr };
                }
            }
            catch (Exception ex)
            {
                return new GitRunResult { ExitCode = -1, Stdout = string.Empty, Stderr = $"error: {ex.Message}" };
            }
        }

        /// <summary>
        /// True when a git commit did not complete: a non-zero exit, or output starting fatal: or error:.
        /// Pure function - no I/O. Extracted for testability.
        /// </summary>
        internal static bool IsCommitFailure(int exitCode, string output)
        {
            if (exitCode != 0) return true;
            string text = (output ?? string.Empty).TrimStart();
            return text.StartsWith("fatal:", StringComparison.Ordinal)
                || text.StartsWith("error:", StringComparison.Ordinal);
        }

        /// <summary>
        /// True when git reports there was nothing to commit (not a failure).
        /// </summary>
        private static bool IsNothingToCommit(string output)
        {
            return output.Contains("nothing to commit")
                || output.Contains("nothing added to commit")
                || output.Contains("no changes added to commit");
        }

        /// <summary>
        /// Result of a git subprocess: exit code plus stdout and stderr kept apart.
        /// </summary>
        internal sealed class GitRunResult
        {
            public int ExitCode;
            public string Stdout;
            public string Stderr;
        }
    }
}
