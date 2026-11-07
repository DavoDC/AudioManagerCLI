using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace AudioManager
{
    /// <summary>
    /// Tests for the git gate fixes: a broken git fails closed (Program.RunGitStatus), a failed
    /// commit is reported as a failure (AudioMirrorCommitter.IsCommitFailure), and non-ASCII
    /// artists survive quoted porcelain paths (AudioMirrorCommitter.BuildCommitBody).
    /// </summary>
    internal static class GitGateTests
    {
        private static string CreateTempDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), "am_gitgate_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static void DeleteQuietly(string dir)
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }

        public static void RunGitStatus_NonRepoDirectory_ReturnsErrorSentinel()
        {
            // git status outside a repository exits non-zero with empty stdout.
            // The gate must see the error sentinel, not empty output (which reads as "fresh").
            string dir = CreateTempDir();
            try
            {
                string result = Program.RunGitStatus(dir);
                Assert.Equal("error", result, "git status outside a repo must return the error sentinel");
            }
            finally { DeleteQuietly(dir); }
        }

        public static void RunGitStatus_CleanRepo_ReturnsEmptyOutput()
        {
            // Success path is unchanged: a fresh repo with nothing untracked returns empty output.
            string dir = CreateTempDir();
            try
            {
                var psi = new ProcessStartInfo("git", "init")
                {
                    WorkingDirectory = dir,
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
                string result = Program.RunGitStatus(dir);
                Assert.Equal("", result, "clean repo should report empty status");
            }
            finally { DeleteQuietly(dir); }
        }

        public static void BuildCommitBody_QuotedNonAsciiPath_KeepsArtist()
        {
            // With core.quotepath on, git quotes non-ASCII paths. The artist must survive the quotes.
            string status = "?? \"AUDIO_MIRROR/Artists/Björk/Homogenic/01.xml\"\n";
            var tracked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string body = AudioMirrorCommitter.BuildCommitBody(status, tracked);
            Assert.True(body.Contains("- Björk (Homogenic)"), "quoted non-ASCII artist should appear in the commit body");
        }

        public static void BuildCommitBody_UnquotedNonAsciiPath_KeepsArtist()
        {
            // With core.quotepath off, git emits the raw UTF-8 path with no quotes.
            string status = "?? AUDIO_MIRROR/Artists/Björk/Homogenic/01.xml\n";
            var tracked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string body = AudioMirrorCommitter.BuildCommitBody(status, tracked);
            Assert.True(body.Contains("- Björk (Homogenic)"), "unquoted non-ASCII artist should appear in the commit body");
        }

        public static void IsCommitFailure_FatalOutput_ReturnsTrue()
        {
            bool failed = AudioMirrorCommitter.IsCommitFailure(128, "fatal: Unable to create '.git/index.lock': File exists.");
            Assert.True(failed, "a fatal: commit result must be classified as failure");
        }

        public static void IsCommitFailure_ErrorOutput_ReturnsTrue()
        {
            bool failed = AudioMirrorCommitter.IsCommitFailure(1, "error: gpg failed to sign the data");
            Assert.True(failed, "an error: commit result must be classified as failure");
        }

        public static void IsCommitFailure_NonZeroExitWithoutMessage_ReturnsTrue()
        {
            bool failed = AudioMirrorCommitter.IsCommitFailure(1, "");
            Assert.True(failed, "a non-zero exit must be classified as failure even with no message");
        }

        public static void IsCommitFailure_SuccessOutput_ReturnsFalse()
        {
            bool failed = AudioMirrorCommitter.IsCommitFailure(0, "[main 1a2b3c4] Nov 5 Update\n 2 files changed, 4 insertions(+)");
            Assert.True(!failed, "a successful commit must not be classified as failure");
        }
    }
}
