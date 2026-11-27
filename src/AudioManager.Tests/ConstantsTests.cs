using System;
using System.IO;

namespace AudioManager
{
    internal static class ConstantsTests
    {
        // ---- Constants.ReportsPath ----

        public static void ReportsPath_IsDataReportsUnderRepoRoot()
        {
            string expected = Path.Combine(Constants.RepoRoot, "data", "reports");
            Assert.Equal(expected, Constants.ReportsPath, "reports live in data/reports under the repo root");
        }

        public static void ReportsPathFor_TestRoot_RedirectsUnderRoot_NoRootKeepsRepoDefault()
        {
            Assert.Equal(System.IO.Path.Combine("T", "reports"), Constants.ReportsPathFor("T", "R"), "test root redirects reports");
            Assert.Equal(System.IO.Path.Combine("R", "data", "reports"), Constants.ReportsPathFor(null, "R"), "no test root keeps repo default");
            Assert.Equal(System.IO.Path.Combine("R", "data", "reports"), Constants.ReportsPathFor("  ", "R"), "blank test root keeps repo default");
        }

        public static void ReportsPath_IsNotTopLevelReports()
        {
            string oldPath = Path.Combine(Constants.RepoRoot, "reports");
            Assert.True(Constants.ReportsPath != oldPath, "reports must not sit at the repo root");
        }

        // ---- Constants.LastRunInfoFilePath (local state, never inside the AudioMirror repo) ----

        public static void LastRunInfoPath_TestRoot_PutsFileUnderTestRoot()
        {
            Assert.Equal(Path.Combine("T", "LastRunInfo.txt"), Constants.ResolveLastRunInfoPath("T", "L"), "test root redirects the last-run file");
        }

        public static void LastRunInfoPath_NoTestRoot_UsesLocalAppDataState()
        {
            string expected = Path.Combine("L", "AudioManager", "LastRunInfo.txt");
            Assert.Equal(expected, Constants.ResolveLastRunInfoPath(null, "L"), "no test root keeps the local state folder");
            Assert.Equal(expected, Constants.ResolveLastRunInfoPath("  ", "L"), "blank test root keeps the local state folder");
        }

        public static void LastRunInfoFilePath_IsNotInsideMirrorRepo()
        {
            Assert.True(!Constants.LastRunInfoFilePath.StartsWith(Constants.MirrorRepoPath, StringComparison.OrdinalIgnoreCase),
                "the stamp must never be written inside the AudioMirror repo: " + Constants.LastRunInfoFilePath);
        }

        public static void LegacyLastRunInfoFilePath_IsTheOldRepoFile()
        {
            Assert.Equal(Constants.MirrorRepoPath + "LastRunInfo.txt", Constants.LegacyLastRunInfoFilePath, "legacy path is the old repo-root file, read only");
        }
    }
}
