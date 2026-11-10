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
    }
}
