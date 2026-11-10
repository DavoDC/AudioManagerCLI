using System;
using System.Collections.Generic;
using System.IO;

namespace AudioManager
{
    /// <summary>
    /// Tests for the structured LibChecker hit fields in logs/mirror-result.json: libcheckerHitCount,
    /// hits[] ({kind, path, detail}) and mirrorCommitted. Seams only, throwaway temp folders.
    /// </summary>
    internal static class MirrorResultHitsTests
    {
        private static string NewTempLogsDir()
        {
            return Path.Combine(Path.GetTempPath(), "am_mirrorhits_" + Guid.NewGuid().ToString("N").Substring(0, 8));
        }

        public static void MirrorResult_LibCheckerIssues_ListsHitsAndSaysNotCommitted()
        {
            string logsDir = NewTempLogsDir();
            try
            {
                var hits = new List<LibChecker.LibCheckerHit>
                {
                    new LibChecker.LibCheckerHit { Kind = "unwanted_text", Path = "Artists\\A\\Singles\\x.mp3", Detail = "found 'Alternate Version' in Title" },
                    new LibChecker.LibCheckerHit { Kind = "artist_folder_mismatch", Path = "Artists\\Charli xcx\\Singles\\y.mp3", Detail = "a \"quoted\" song" }
                };
                Program.PostIntegrationMirrorResult r = Program.RunPostIntegrationMirrorWith(
                    logsDir, false, () => false, () => true, () => hits);

                Assert.Equal("2", r.LibCheckerHitCount.ToString(), "hit count must be carried on the result");
                Assert.True(!r.MirrorCommitted, "libchecker_issues must not be committed");

                string json = File.ReadAllText(Path.Combine(logsDir, "mirror-result.json"));
                Assert.True(json.Contains("\"libcheckerHitCount\": 2"), "JSON must carry libcheckerHitCount");
                Assert.True(json.Contains("\"mirrorCommitted\": false"), "JSON must say mirrorCommitted false");
                Assert.True(json.Contains("\"kind\": \"unwanted_text\""), "JSON hits must carry kind");
                Assert.True(json.Contains("\"path\": \"Artists\\\\A\\\\Singles\\\\x.mp3\""), "JSON hits must carry the escaped path");
                Assert.True(json.Contains("a \\\"quoted\\\" song"), "JSON hit detail must be escaped");
            }
            finally
            {
                if (Directory.Exists(logsDir)) Directory.Delete(logsDir, true);
            }
        }

        public static void MirrorResult_OkCommit_HasZeroHitsAndCommittedTrue()
        {
            string logsDir = NewTempLogsDir();
            try
            {
                Program.PostIntegrationMirrorResult r = Program.RunPostIntegrationMirrorWith(logsDir, false, () => true, () => true);
                Assert.True(r.MirrorCommitted, "ok must be committed");
                string json = File.ReadAllText(Path.Combine(logsDir, "mirror-result.json"));
                Assert.True(json.Contains("\"libcheckerHitCount\": 0"), "clean run has zero hits");
                Assert.True(json.Contains("\"mirrorCommitted\": true"), "ok run says mirrorCommitted true");
                Assert.True(json.Contains("\"hits\": []"), "clean run has an empty hits array");
            }
            finally
            {
                if (Directory.Exists(logsDir)) Directory.Delete(logsDir, true);
            }
        }
    }
}
