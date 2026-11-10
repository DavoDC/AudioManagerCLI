using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
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
    /// <summary>Classification of a git commit result (BL-69).</summary>
    internal enum CommitOutcome
    {
        Success,
        NothingToCommit,
        Failure,
    }

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
            string repoPath = Path.GetFullPath(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, Constants.MirrorRepoPath));
            TryCommitAt(repoPath, libCheckerClean, trigger);
        }

        /// <summary>
        /// TryCommit against an explicit repo path. TryCommit resolves the real mirror path and calls this;
        /// tests pass a throwaway repo here.
        /// </summary>
        internal static void TryCommitAt(string repoPath, bool libCheckerClean, CommitTrigger trigger)
        {
            // Incremental analysis produces an unreliable mirror state - skip
            if (trigger == CommitTrigger.AnalysisIncremental)
            {
                Console.WriteLine("\nAudioMirror: skipping auto-commit (incremental mirror - run force regen for auto-commit).");
                return;
            }

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
            string commitTitle = DateTime.Now.ToString("MMM d", CultureInfo.InvariantCulture) + " Update";
            string commitBody = BuildCommitBody(
                RunGit(repoPath, "-c core.quotepath=false status --porcelain=v1 -uall"),
                GetTrackedArtists(repoPath));
            string fullMessage = string.IsNullOrWhiteSpace(commitBody)
                ? commitTitle
                : commitTitle + "\n\n" + commitBody; // blank line: git treats a single newline as part of the subject

            // Stage AUDIO_MIRROR folder + LastRunInfo.txt (audit metadata at repo root)
            GitRunResult add = RunGitDetailed(repoPath, "add AUDIO_MIRROR/ LastRunInfo.txt");
            if (add.ExitCode != 0)
            {
                string addOutput = add.Stdout + add.Stderr;
                string addFirstLine = addOutput.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? string.Empty;
                Console.WriteLine($" - FAILED: git add exited {add.ExitCode}. {addFirstLine}");
                Console.WriteLine(" - Nothing was committed. Fix the git error above, then re-run.");
                return;
            }
            GitRunResult commit = RunGitDetailed(repoPath, $"commit -m \"{fullMessage.Replace("\"", "\\\"")}\"");
            string commitResult = commit.Stdout + commit.Stderr;

            CommitOutcome outcome = ClassifyCommitResult(commit.ExitCode, commitResult);
            if (outcome == CommitOutcome.NothingToCommit)
            {
                Console.WriteLine(" - Nothing staged to commit.");
                return;
            }

            if (outcome == CommitOutcome.Failure)
            {
                string firstLine = commitResult.Trim().Split('\n')[0].Trim();
                Console.WriteLine($" - FAILED: git commit exited {commit.ExitCode}. {firstLine}");
                Console.WriteLine(" - Nothing was committed. Fix the git error above, then re-run.");
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
        /// <param name="statusOutput">Output of 'git status --porcelain=v1 -uall' (run with core.quotepath=false).</param>
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
                if (!line.StartsWith("??", StringComparison.Ordinal)) continue;

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
        internal static HashSet<string> GetTrackedArtists(string repoPath)
        {
            string output = RunGit(repoPath, $"-c core.quotepath=false ls-tree --name-only HEAD AUDIO_MIRROR/{Constants.ArtistsDir}/");
            return new HashSet<string>(
                output.Split('\n')
                      .Select(l => l.Trim().Replace('\\', '/'))
                      .Where(l => !string.IsNullOrEmpty(l))
                      .Select(l => l.Substring(l.LastIndexOf('/') + 1)),
                StringComparer.OrdinalIgnoreCase);
        }

        internal static void AddAlbum(Dictionary<string, SortedSet<string>> dict, string artist, string album)
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
            var psi = new ProcessStartInfo("git", args)
            {
                WorkingDirectory = workingDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            int pid;
            return RunProcessBounded(psi, Constants.GitTimeoutMs, out pid);
        }

        /// <summary>
        /// Starts a process and waits at most timeoutMs for it to exit. Redirected stdout and stderr are
        /// read on background tasks so the pipes always drain (a full pipe buffer cannot stall git).
        /// If the process is still running at the timeout it is killed and the result has TimedOut set,
        /// ExitCode -1 and a one-line Stderr message naming the timeout. Other failures return ExitCode -1
        /// with the exception message in Stderr. processId is the started process id (0 if it never started).
        /// </summary>
        internal static GitRunResult RunProcessBounded(ProcessStartInfo psi, int timeoutMs, out int processId)
        {
            processId = 0;
            try
            {
                using (var proc = Process.Start(psi))
                {
                    processId = proc.Id;
                    var stdoutTask = psi.RedirectStandardOutput
                        ? System.Threading.Tasks.Task.Run(() => proc.StandardOutput.ReadToEnd())
                        : System.Threading.Tasks.Task.FromResult(string.Empty);
                    var stderrTask = psi.RedirectStandardError
                        ? System.Threading.Tasks.Task.Run(() => proc.StandardError.ReadToEnd())
                        : System.Threading.Tasks.Task.FromResult(string.Empty);

                    var clock = Stopwatch.StartNew();
                    bool finished = proc.WaitForExit(timeoutMs)
                        && System.Threading.Tasks.Task.WaitAll(
                            new System.Threading.Tasks.Task[] { stdoutTask, stderrTask },
                            Math.Max(0, timeoutMs - (int)clock.ElapsedMilliseconds));
                    if (!finished)
                    {
                        KillQuietly(proc);
                        return new GitRunResult
                        {
                            ExitCode = -1,
                            Stdout = string.Empty,
                            Stderr = $"error: git timed out after {timeoutMs} ms and was killed",
                            TimedOut = true
                        };
                    }
                    return new GitRunResult { ExitCode = proc.ExitCode, Stdout = stdoutTask.Result, Stderr = stderrTask.Result };
                }
            }
            catch (Exception ex)
            {
                return new GitRunResult { ExitCode = -1, Stdout = string.Empty, Stderr = $"error: {ex.Message}" };
            }
        }

        // Kill is best effort: it throws when the process has already exited, which is fine here.
        private static void KillQuietly(Process proc)
        {
            try { proc.Kill(); }
            catch (Exception) { }
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
        internal static bool IsNothingToCommit(string output)
        {
            return output.Contains("nothing to commit")
                || output.Contains("nothing added to commit")
                || output.Contains("no changes added to commit");
        }

        /// <summary>
        /// Classifies a git commit result (BL-69). A non-zero exit is a failure unless it is git's own
        /// exit-1 'nothing to commit' message with no fatal:/error: line. Exit 0 is Success, or
        /// NothingToCommit when it prints no commit summary or the nothing-to-commit text.
        /// Pure function - no I/O.
        /// </summary>
        internal static CommitOutcome ClassifyCommitResult(int exitCode, string output)
        {
            output = output ?? string.Empty;
            if (exitCode == 0)
            {
                if (IsCommitFailure(0, output)) return CommitOutcome.Failure;
                if (string.IsNullOrWhiteSpace(output) || IsNothingToCommit(output)) return CommitOutcome.NothingToCommit;
                return CommitOutcome.Success;
            }

            bool hasFatalOrErrorLine = output.Split('\n')
                .Select(l => l.TrimStart())
                .Any(l => l.StartsWith("fatal:", StringComparison.Ordinal) || l.StartsWith("error:", StringComparison.Ordinal));
            if (exitCode == 1 && !hasFatalOrErrorLine && IsNothingToCommit(output))
                return CommitOutcome.NothingToCommit;
            return CommitOutcome.Failure;
        }

        /// <summary>
        /// Result of a git subprocess: exit code plus stdout and stderr kept apart.
        /// </summary>
        internal sealed class GitRunResult
        {
            public int ExitCode;
            public string Stdout;
            public string Stderr;
            public bool TimedOut;
        }
    }
}
