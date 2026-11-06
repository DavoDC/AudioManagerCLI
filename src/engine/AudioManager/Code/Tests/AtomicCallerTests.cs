using System;
using System.IO;
using System.Text;

namespace AudioManager
{
    /// <summary>
    /// Tests that the two state/report writers use AtomicFile: no .tmp is left behind, the content is
    /// correct, and the output stays UTF-8 without a BOM (the encoding the old File.WriteAllText used).
    /// </summary>
    internal static class AtomicCallerTests
    {
        private static string NewDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), "am_atomic_caller_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static void Cleanup(string dir)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
            catch (Exception) { }
        }

        private static bool HasUtf8Bom(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            return bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        }

        public static void AgeChecker_MarkRegenerated_WritesDateWithNoTmpAndNoBom()
        {
            string dir = NewDir();
            try
            {
                string path = Path.Combine(dir, "LastRunInfo.txt");
                AgeChecker.MarkRegenerated(new DateTime(2026, 11, 5, 9, 30, 15), path);
                Assert.Equal("2026-11-05 09:30:15", File.ReadAllText(path, Encoding.UTF8), "stamp should hold the fixed-format date");
                Assert.True(!File.Exists(path + ".tmp"), "stamp write should leave no .tmp");
                Assert.True(!HasUtf8Bom(path), "stamp should be UTF-8 without a BOM");
            }
            finally { Cleanup(dir); }
        }

        public static void ReportWriter_Save_WritesHeaderAndContentWithNoTmpAndNoBom()
        {
            string dir = NewDir();
            try
            {
                DateTime now = DateTime.Now;
                string filename = now.ToString("yyyy-MM-dd") + " - AudioReport.md";
                string fullPath = Path.Combine(dir, now.Year.ToString(), filename);

                ReportWriter.Save("UNIQUE_REPORT_BODY_MARKER", true, dir);

                Assert.True(File.Exists(fullPath), "clean report should be saved under reportsRoot/{year}");
                string text = File.ReadAllText(fullPath, Encoding.UTF8);
                Assert.True(text.StartsWith("# Audio Report"), "report should start with the generated header");
                Assert.True(text.Contains("UNIQUE_REPORT_BODY_MARKER"), "report should contain the report content");
                Assert.True(!File.Exists(fullPath + ".tmp"), "report write should leave no .tmp");
                Assert.True(!HasUtf8Bom(fullPath), "report should be UTF-8 without a BOM");
            }
            finally { Cleanup(dir); }
        }

        public static void Save_TempRootOutsideAnyRepo_HeaderCommitIsUnavailable()
        {
            string root = Path.Combine(Path.GetTempPath(), "am_report_root_" + Guid.NewGuid().ToString("N"));
            try
            {
                DateTime now = DateTime.Now;
                string filename = now.ToString("yyyy-MM-dd") + " - AudioReport.md";
                string fullPath = Path.Combine(root, now.Year.ToString(), filename);

                ReportWriter.Save("UNIQUE_REPORT_ROOT_MARKER", true, root);

                Assert.True(File.Exists(fullPath), "clean report should be saved under the temp root/{year}");
                string text = File.ReadAllText(fullPath, Encoding.UTF8);
                Assert.True(text.Contains("**Commit:** unavailable"), "temp root outside any repo should give an unavailable commit, not the real repo hash");
            }
            finally
            {
                try { if (Directory.Exists(root)) Directory.Delete(root, true); }
                catch (Exception) { }
            }
        }
    }
}
