using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace AudioManager
{
    /// <summary>
    /// Tests for AudioMirrorCommitter.GetSkipReason (pure gate logic) and RunGit (subprocess).
    /// RunGit tests use a temp git repo to exercise the actual subprocess code path and verify
    /// stdout/stderr are both captured without deadlock.
    /// TryCommitAt, GetTrackedArtists, AddAlbum and IsNothingToCommit run against throwaway temp repos.
    /// Program.DecidePreIntegrationGate (the pre-integration gate decision) is covered at the end.
    /// </summary>
    internal static class AudioMirrorCommitterTests
    {
        private static string CreateTempGitRepo()
        {
            string dir = Path.Combine(Path.GetTempPath(), "am_gittest_" + System.Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            // Bootstrap without RunGit so tests don't depend on the method under test
            RunProcess("git", "init", dir);
            RunProcess("git", "config user.email test@test.com", dir);
            RunProcess("git", "config user.name Test", dir);
            return dir;
        }

        private static void RunProcess(string exe, string args, string workingDir)
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                WorkingDirectory = workingDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using (var p = Process.Start(psi))
            {
                p.StandardOutput.ReadToEnd();
                p.StandardError.ReadToEnd();
                p.WaitForExit();
            }
        }

        // Runs git and returns stdout+stderr trimmed. Used to inspect the throwaway repo independently of the code under test.
        private static string RunGitOut(string workingDir, string args)
        {
            var psi = new ProcessStartInfo("git", args)
            {
                WorkingDirectory = workingDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using (var p = Process.Start(psi))
            {
                string stdout = p.StandardOutput.ReadToEnd();
                string stderr = p.StandardError.ReadToEnd();
                p.WaitForExit();
                return (stdout + stderr).Trim();
            }
        }

        private static void WriteFile(string repoDir, string relPath, string content)
        {
            string full = Path.Combine(repoDir, relPath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full));
            File.WriteAllText(full, content);
        }

        // Repo with exactly one commit: LastRunInfo.txt, plus Artists/Alpha/Album1 when withArtist is true.
        // LastRunInfo.txt is always committed so 'git add AUDIO_MIRROR/ LastRunInfo.txt' never fails on a missing path.
        private static string CreateRepoWithInitialCommit(bool withArtist)
        {
            string dir = CreateTempGitRepo();
            WriteFile(dir, "LastRunInfo.txt", "run");
            if (withArtist) WriteFile(dir, "AUDIO_MIRROR/Artists/Alpha/Album1/a.xml", "<a/>");
            else WriteFile(dir, "AUDIO_MIRROR/readme.txt", "no artists");
            RunProcess("git", "add -A", dir);
            RunProcess("git", "commit -m initial", dir);
            return dir;
        }

        private static void DeleteQuietly(string dir)
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }

        private static string Join(IEnumerable<string> items)
        {
            return string.Join(",", items.OrderBy(s => s, StringComparer.Ordinal));
        }

        public static void GetSkipReason_IncrementalTrigger_ReturnsIncremental()
        {
            // Incremental analysis may have stale XMLs - must skip regardless of clean state
            string reason = AudioMirrorCommitter.GetSkipReason(libCheckerClean: true, trigger: CommitTrigger.AnalysisIncremental);
            Assert.Equal("incremental", reason, "incremental trigger should always return skip reason");
        }

        public static void GetSkipReason_IncrementalDirty_ReturnsIncremental()
        {
            // Incremental + dirty -> incremental check fires first
            string reason = AudioMirrorCommitter.GetSkipReason(libCheckerClean: false, trigger: CommitTrigger.AnalysisIncremental);
            Assert.Equal("incremental", reason, "incremental fires before dirty check");
        }

        public static void GetSkipReason_DirtyForceRegen_ReturnsDirty()
        {
            // Force regen is reliable, but dirty library must prevent commit
            string reason = AudioMirrorCommitter.GetSkipReason(libCheckerClean: false, trigger: CommitTrigger.AnalysisForceRegen);
            Assert.Equal("dirty", reason, "dirty LibChecker should block commit on force regen");
        }

        public static void GetSkipReason_DirtyIntegration_ReturnsDirty()
        {
            // Integration is reliable, but dirty library must prevent commit
            string reason = AudioMirrorCommitter.GetSkipReason(libCheckerClean: false, trigger: CommitTrigger.Integration);
            Assert.Equal("dirty", reason, "dirty LibChecker should block commit on integration");
        }

        public static void GetSkipReason_CleanForceRegen_ReturnsNull()
        {
            // Clean + force regen: no gate applies -> should proceed to git
            string reason = AudioMirrorCommitter.GetSkipReason(libCheckerClean: true, trigger: CommitTrigger.AnalysisForceRegen);
            Assert.True(reason == null, "clean force regen has no skip reason (would proceed to git)");
        }

        public static void GetSkipReason_CleanIntegration_ReturnsNull()
        {
            // Clean + integration: no gate applies -> should proceed to git
            string reason = AudioMirrorCommitter.GetSkipReason(libCheckerClean: true, trigger: CommitTrigger.Integration);
            Assert.True(reason == null, "clean integration has no skip reason (would proceed to git)");
        }

        // RunGit subprocess tests - verify stdout and stderr are both captured without deadlock

        public static void RunGit_CapturesStdoutFromStatusCommand()
        {
            string dir = CreateTempGitRepo();
            try
            {
                string result = AudioMirrorCommitter.RunGit(dir, "status --porcelain");
                // Fresh repo with no changes - result is empty string (clean) or ?? entries
                Assert.True(result != null, "RunGit should return a non-null string");
            }
            finally { try { Directory.Delete(dir, recursive: true); } catch { } }
        }

        public static void RunGit_CapturesStderrFromFailedCommand()
        {
            // rev-parse on a nonexistent ref exits non-zero and writes to stderr.
            // With the sequential ReadToEnd bug, large stderr output causes a deadlock.
            // This test verifies stderr is captured (not swallowed or hung) for failed commands.
            string dir = CreateTempGitRepo();
            try
            {
                string result = AudioMirrorCommitter.RunGit(dir, "rev-parse --verify nonexistent-ref-abc123");
                Assert.True(result != null && result.Length > 0, "stderr from failed git command should be returned, not swallowed");
            }
            finally { try { Directory.Delete(dir, recursive: true); } catch { } }
        }

        public static void RunGit_InvalidWorkingDirectory_ReturnsErrorMessage()
        {
            string result = AudioMirrorCommitter.RunGit(@"C:\nonexistent\path\abc123", "status");
            Assert.True(result != null && result.StartsWith("error:"), "invalid working dir should return error: message");
        }

        // TryCommitAt: the commit step against a throwaway repo

        public static void TryCommit_IncrementalTrigger_DoesNotCommit()
        {
            // Incremental mirror may be stale: skip even when clean and changes exist
            string dir = CreateRepoWithInitialCommit(withArtist: true);
            try
            {
                WriteFile(dir, "AUDIO_MIRROR/Artists/Beta/Album2/b.xml", "<b/>");
                AudioMirrorCommitter.TryCommitAt(dir, libCheckerClean: true, trigger: CommitTrigger.AnalysisIncremental);
                Assert.Equal("1", RunGitOut(dir, "rev-list --count HEAD"), "incremental trigger must not commit");
                Assert.True(RunGitOut(dir, "status --porcelain").Contains("Beta"), "untracked changes must be left in the tree");
            }
            finally { DeleteQuietly(dir); }
        }

        public static void TryCommit_DirtyLibrary_DoesNotCommit()
        {
            // LibChecker issues block the commit on a reliable trigger
            string dir = CreateRepoWithInitialCommit(withArtist: true);
            try
            {
                WriteFile(dir, "AUDIO_MIRROR/Artists/Beta/Album2/b.xml", "<b/>");
                AudioMirrorCommitter.TryCommitAt(dir, libCheckerClean: false, trigger: CommitTrigger.Integration);
                Assert.Equal("1", RunGitOut(dir, "rev-list --count HEAD"), "dirty library must not commit");
            }
            finally { DeleteQuietly(dir); }
        }

        public static void TryCommit_MissingRepo_DoesNotCreateRepo()
        {
            // A missing mirror is reported and skipped; git must not be run to create it
            string missing = Path.Combine(Path.GetTempPath(), "am_missing_" + System.Guid.NewGuid().ToString("N").Substring(0, 8));
            AudioMirrorCommitter.TryCommitAt(missing, libCheckerClean: true, trigger: CommitTrigger.Integration);
            Assert.True(!Directory.Exists(missing), "missing repo path must not be created by the commit step");
        }

        public static void TryCommit_CleanTreeNoChanges_LeavesHeadUnchanged()
        {
            string dir = CreateRepoWithInitialCommit(withArtist: true);
            try
            {
                AudioMirrorCommitter.TryCommitAt(dir, libCheckerClean: true, trigger: CommitTrigger.Integration);
                Assert.Equal("1", RunGitOut(dir, "rev-list --count HEAD"), "clean tree should not create a commit");
            }
            finally { DeleteQuietly(dir); }
        }

        public static void TryCommit_NothingStaged_LeavesHeadUnchanged()
        {
            // Only a file outside AUDIO_MIRROR/ is untracked: status is non-empty but nothing is staged,
            // so git reports 'nothing added to commit' and the commit step must treat that as not a failure.
            string dir = CreateRepoWithInitialCommit(withArtist: true);
            try
            {
                WriteFile(dir, "notes.txt", "outside the mirror");
                AudioMirrorCommitter.TryCommitAt(dir, libCheckerClean: true, trigger: CommitTrigger.Integration);
                Assert.Equal("1", RunGitOut(dir, "rev-list --count HEAD"), "nothing staged should not create a commit");
            }
            finally { DeleteQuietly(dir); }
        }

        public static void TryCommit_NewArtist_CommitsWithNewArtistBody()
        {
            string dir = CreateRepoWithInitialCommit(withArtist: true);
            try
            {
                WriteFile(dir, "AUDIO_MIRROR/Artists/Beta/Album2/b.xml", "<b/>");
                AudioMirrorCommitter.TryCommitAt(dir, libCheckerClean: true, trigger: CommitTrigger.Integration);
                Assert.Equal("2", RunGitOut(dir, "rev-list --count HEAD"), "clean mirror with a new artist should commit once");
                string subject = RunGitOut(dir, "log -1 --format=%s");
                Assert.True(subject.EndsWith(" Update"), "commit title should end with ' Update', got: " + subject);
                string body = RunGitOut(dir, "log -1 --format=%b");
                Assert.True(body.Contains("New artists:") && body.Contains("- Beta (Album2)"), "body should list Beta under New artists, got: " + body);
                Assert.Equal("", RunGitOut(dir, "status --porcelain"), "tree should be clean after the commit");
            }
            finally { DeleteQuietly(dir); }
        }

        public static void TryCommit_ExistingArtistNewAlbum_ListedUnderExistingArtists()
        {
            // Alpha is already tracked in HEAD, so a new Alpha album is an existing-artist entry, not a new one
            string dir = CreateRepoWithInitialCommit(withArtist: true);
            try
            {
                WriteFile(dir, "AUDIO_MIRROR/Artists/Alpha/Album3/c.xml", "<c/>");
                AudioMirrorCommitter.TryCommitAt(dir, libCheckerClean: true, trigger: CommitTrigger.AnalysisForceRegen);
                string body = RunGitOut(dir, "log -1 --format=%b");
                Assert.True(body.Contains("Existing artists:") && body.Contains("Alpha (Album3)"), "body should list Alpha under Existing artists, got: " + body);
                Assert.True(!body.Contains("New artists:"), "tracked artist must not be listed as new");
            }
            finally { DeleteQuietly(dir); }
        }

        // GetTrackedArtists: artist folders committed to HEAD

        public static void GetTrackedArtists_ReturnsOnlyArtistsCommittedToHead()
        {
            // Alpha is committed; Beta exists only as an untracked folder
            string dir = CreateRepoWithInitialCommit(withArtist: true);
            try
            {
                WriteFile(dir, "AUDIO_MIRROR/Artists/Beta/Album2/b.xml", "<b/>");
                HashSet<string> tracked = AudioMirrorCommitter.GetTrackedArtists(dir);
                Assert.Equal("Alpha", Join(tracked), "only artists committed in HEAD should be tracked");
            }
            finally { DeleteQuietly(dir); }
        }

        public static void GetTrackedArtists_NoArtistsFolderInHead_ReturnsEmpty()
        {
            string dir = CreateRepoWithInitialCommit(withArtist: false);
            try
            {
                HashSet<string> tracked = AudioMirrorCommitter.GetTrackedArtists(dir);
                Assert.Equal("", Join(tracked), "a HEAD without an Artists folder should track no artists");
            }
            finally { DeleteQuietly(dir); }
        }

        // AddAlbum: per-artist album sets

        public static void AddAlbum_NewArtist_CreatesEntryWithAlbum()
        {
            var dict = new Dictionary<string, SortedSet<string>>(StringComparer.OrdinalIgnoreCase);
            AudioMirrorCommitter.AddAlbum(dict, "Artist", "Album1");
            Assert.Equal("1", dict.Count.ToString(), "one artist should create one entry");
            Assert.Equal("Album1", Join(dict["Artist"]), "album should be stored under the artist");
        }

        public static void AddAlbum_DuplicateAlbumDifferentCase_KeptOnce()
        {
            // Album names compare case-insensitively: 'a' and 'A' are one album, and albums sort case-insensitively
            var dict = new Dictionary<string, SortedSet<string>>(StringComparer.OrdinalIgnoreCase);
            AudioMirrorCommitter.AddAlbum(dict, "Artist", "B");
            AudioMirrorCommitter.AddAlbum(dict, "Artist", "a");
            AudioMirrorCommitter.AddAlbum(dict, "artist", "A");
            Assert.Equal("1", dict.Count.ToString(), "artist key should match case-insensitively");
            Assert.Equal("a,B", string.Join(",", dict["Artist"]), "duplicate album in another case should be kept once");
        }

        // IsNothingToCommit: git's 'nothing to commit' wording is not a failure

        public static void IsNothingToCommit_CleanTree_True()
        {
            Assert.True(AudioMirrorCommitter.IsNothingToCommit("nothing to commit, working tree clean"), "clean tree message should count as nothing to commit");
        }

        public static void IsNothingToCommit_UntrackedOnly_True()
        {
            Assert.True(AudioMirrorCommitter.IsNothingToCommit("nothing added to commit but untracked files present (use \"git add\" to track)"),
                "untracked-only message should count as nothing to commit");
        }

        public static void IsNothingToCommit_ModifiedOnly_True()
        {
            Assert.True(AudioMirrorCommitter.IsNothingToCommit("no changes added to commit (use \"git add\" and/or \"git commit -a\")"),
                "modified-only message should count as nothing to commit");
        }

        public static void IsNothingToCommit_CommitSucceeded_False()
        {
            Assert.True(!AudioMirrorCommitter.IsNothingToCommit("[main 1a2b3c4] Nov 5 Update\n 2 files changed, 4 insertions(+)"),
                "a successful commit summary is not nothing-to-commit");
        }

        // Program.DecidePreIntegrationGate: one test per PreIntegrationGateResult value.
        // Status and LibChecker are injected so no real git, parser or library is touched.

        public static void DecidePreIntegrationGate_MissingRepo_ReturnsMirrorRepoMissing_SkipsStatusAndLib()
        {
            bool statusCalled = false, libCalled = false;
            Program.PreIntegrationGateResult result = Program.DecidePreIntegrationGate(
                mirrorRepoExists: false,
                mirrorStatus: () => { statusCalled = true; return ""; },
                libCheckerClean: () => { libCalled = true; return true; });
            Assert.True(result == Program.PreIntegrationGateResult.MirrorRepoMissing, "missing repo should be MirrorRepoMissing");
            Assert.True(!statusCalled && !libCalled, "missing repo must decide before regen status or LibChecker run");
        }

        public static void DecidePreIntegrationGate_StaleMirror_ReturnsMirrorStale_SkipsLib()
        {
            bool libCalled = false;
            Program.PreIntegrationGateResult result = Program.DecidePreIntegrationGate(
                mirrorRepoExists: true,
                mirrorStatus: () => " M AUDIO_MIRROR/Artists/Alpha/Album1/a.xml\n",
                libCheckerClean: () => { libCalled = true; return true; });
            Assert.True(result == Program.PreIntegrationGateResult.MirrorStale, "changed XML should be MirrorStale");
            Assert.True(!libCalled, "stale mirror must decide before LibChecker runs");
        }

        public static void DecidePreIntegrationGate_DirtyLibrary_ReturnsLibCheckerDirty()
        {
            Program.PreIntegrationGateResult result = Program.DecidePreIntegrationGate(
                mirrorRepoExists: true,
                mirrorStatus: () => "",
                libCheckerClean: () => false);
            Assert.True(result == Program.PreIntegrationGateResult.LibCheckerDirty, "fresh mirror with LibChecker issues should be LibCheckerDirty");
        }

        public static void DecidePreIntegrationGate_FreshMirrorCleanLibrary_ReturnsPassed()
        {
            Program.PreIntegrationGateResult result = Program.DecidePreIntegrationGate(
                mirrorRepoExists: true,
                mirrorStatus: () => "",
                libCheckerClean: () => true);
            Assert.True(result == Program.PreIntegrationGateResult.Passed, "fresh mirror with clean library should pass");
        }

        public static void DecidePreIntegrationGate_OnlyLastRunInfoChanged_IsNotStale()
        {
            // LastRunInfo.txt is a timestamp only: it must not make the mirror stale
            Program.PreIntegrationGateResult result = Program.DecidePreIntegrationGate(
                mirrorRepoExists: true,
                mirrorStatus: () => " M LastRunInfo.txt\n",
                libCheckerClean: () => true);
            Assert.True(result == Program.PreIntegrationGateResult.Passed, "LastRunInfo.txt alone should not be stale");
        }

        public static void DecidePreIntegrationGate_GitErrorSentinel_ReturnsMirrorStale()
        {
            // RunGitStatus returns "error" when git cannot run: the gate must fail closed
            Program.PreIntegrationGateResult result = Program.DecidePreIntegrationGate(
                mirrorRepoExists: true,
                mirrorStatus: () => "error",
                libCheckerClean: () => true);
            Assert.True(result == Program.PreIntegrationGateResult.MirrorStale, "git error sentinel should fail closed as MirrorStale");
        }
    }
}
