using System;
using System.IO;

namespace AudioManager
{
    /// <summary>
    /// Tests for the post-integration mirror step (Program.RunPostIntegrationMirrorWith) and its
    /// machine-readable result file (logs/mirror-result.json). The seams stand in for the regenerate +
    /// LibChecker step and the AudioMirror commit, so no test touches the real AudioMirror repo.
    /// Every test writes its JSON into a throwaway temp folder.
    /// </summary>
    internal static class MirrorResultTests
    {
        private static string NewTempLogsDir()
        {
            return Path.Combine(Path.GetTempPath(), "am_mirrorresult_" + Guid.NewGuid().ToString("N").Substring(0, 8));
        }

        private static string ReadResultJson(string logsDir)
        {
            string path = Path.Combine(logsDir, "mirror-result.json");
            Assert.True(File.Exists(path), "mirror-result.json must be written to the logs folder");
            return File.ReadAllText(path);
        }

        public static void MirrorResult_CommitFails_GivesMirrorCommitFailedAndWritesErrorJson()
        {
            string logsDir = NewTempLogsDir();
            try
            {
                Program.PostIntegrationMirrorResult r = Program.RunPostIntegrationMirrorWith(
                    logsDir, false,
                    () => true,
                    () => false);

                Assert.Equal("mirror_commit_failed", r.Status, "a failed commit must map to mirror_commit_failed");
                Assert.True(!string.IsNullOrEmpty(r.Error), "a failed commit must carry an error text");

                string json = ReadResultJson(logsDir);
                Assert.True(json.Contains("\"status\": \"mirror_commit_failed\""), "JSON status must be mirror_commit_failed");
                Assert.True(json.Contains("\"error\": \"" + r.Error + "\""), "JSON error must carry the same error text");
            }
            finally
            {
                if (Directory.Exists(logsDir)) Directory.Delete(logsDir, true);
            }
        }

        public static void MirrorResult_StepThrows_GivesMirrorCommitFailedWithExceptionText()
        {
            string logsDir = NewTempLogsDir();
            try
            {
                Program.PostIntegrationMirrorResult r = Program.RunPostIntegrationMirrorWith(
                    logsDir, false,
                    () => { throw new InvalidOperationException("boom in mirror step"); },
                    () => true);

                Assert.Equal("mirror_commit_failed", r.Status, "a thrown exception must map to mirror_commit_failed");
                Assert.True(r.Error != null && r.Error.Contains("boom in mirror step"), "the exception message must reach Error");

                string json = ReadResultJson(logsDir);
                Assert.True(json.Contains("\"status\": \"mirror_commit_failed\""), "JSON status must be mirror_commit_failed");
                Assert.True(json.Contains("boom in mirror step"), "JSON error must carry the exception message");
            }
            finally
            {
                if (Directory.Exists(logsDir)) Directory.Delete(logsDir, true);
            }
        }

        public static void MirrorResult_CleanAndCommitted_GivesOkAndNullError()
        {
            string logsDir = NewTempLogsDir();
            try
            {
                Program.PostIntegrationMirrorResult r = Program.RunPostIntegrationMirrorWith(
                    logsDir, false,
                    () => true,
                    () => true);

                Assert.Equal("ok", r.Status, "a clean mirror that commits must be ok");
                Assert.True(r.Error == null, "ok must carry no error");

                string json = ReadResultJson(logsDir);
                Assert.True(json.Contains("\"status\": \"ok\""), "JSON status must be ok");
                Assert.True(json.Contains("\"error\": null"), "JSON error must be null on ok");
            }
            finally
            {
                if (Directory.Exists(logsDir)) Directory.Delete(logsDir, true);
            }
        }

        public static void MirrorResult_LibCheckerIssues_GivesLibcheckerIssuesAndNeverCommits()
        {
            string logsDir = NewTempLogsDir();
            try
            {
                bool committed = false;
                Program.PostIntegrationMirrorResult r = Program.RunPostIntegrationMirrorWith(
                    logsDir, false,
                    () => false,
                    () => { committed = true; return true; });

                Assert.Equal("libchecker_issues", r.Status, "dirty LibChecker must map to libchecker_issues");
                Assert.True(!committed, "a dirty library must never reach the commit step");
                Assert.True(ReadResultJson(logsDir).Contains("\"status\": \"libchecker_issues\""), "JSON status must be libchecker_issues");
            }
            finally
            {
                if (Directory.Exists(logsDir)) Directory.Delete(logsDir, true);
            }
        }

        public static void MirrorResult_NoAutoCommit_GivesSkippedAndNeverCommits()
        {
            string logsDir = NewTempLogsDir();
            try
            {
                bool committed = false;
                Program.PostIntegrationMirrorResult r = Program.RunPostIntegrationMirrorWith(
                    logsDir, true,
                    () => true,
                    () => { committed = true; return true; });

                Assert.Equal("skipped", r.Status, "--no-auto-commit must map to skipped");
                Assert.True(!committed, "--no-auto-commit must never reach the commit step");
                Assert.True(ReadResultJson(logsDir).Contains("\"status\": \"skipped\""), "JSON status must be skipped");
            }
            finally
            {
                if (Directory.Exists(logsDir)) Directory.Delete(logsDir, true);
            }
        }

        public static void MirrorResult_JsonCarriesRecordTypeSchemaAndRetryCommand()
        {
            string logsDir = NewTempLogsDir();
            try
            {
                Program.RunPostIntegrationMirrorWith(logsDir, false, () => true, () => false);

                string json = ReadResultJson(logsDir);
                Assert.True(json.Contains("\"recordType\": \"mirrorResult\""), "recordType must be mirrorResult");
                Assert.True(json.Contains("\"schemaVersion\": 1"), "schemaVersion must be 1");
                Assert.True(json.Contains("\"generatedAt\": \""), "generatedAt must be present");
                Assert.True(json.Contains("\"retry\": \"AudioManagerCLI.exe --mirror-retry\""), "retry must name the retry command");
            }
            finally
            {
                if (Directory.Exists(logsDir)) Directory.Delete(logsDir, true);
            }
        }

        public static void MirrorRetry_ExitCodeIsZeroOnlyForOk()
        {
            Assert.Equal("0", Program.MirrorRetryExitCode(new Program.PostIntegrationMirrorResult { Status = "ok" }).ToString(), "ok exits 0");
            Assert.Equal("1", Program.MirrorRetryExitCode(new Program.PostIntegrationMirrorResult { Status = "mirror_commit_failed" }).ToString(), "mirror_commit_failed exits 1");
            Assert.Equal("1", Program.MirrorRetryExitCode(new Program.PostIntegrationMirrorResult { Status = "libchecker_issues" }).ToString(), "libchecker_issues exits 1");
            Assert.Equal("1", Program.MirrorRetryExitCode(new Program.PostIntegrationMirrorResult { Status = "skipped" }).ToString(), "skipped exits 1 (not ok)");
        }
    }
}
