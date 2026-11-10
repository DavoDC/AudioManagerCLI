using System;
using System.IO;

namespace AudioManager
{
    // Keeps rules in one canonical doc (docs/README.md, "Where rules live"): README.md and CLAUDE.md
    // must not carry rule-only keywords; those belong in docs/Rules/MusicLibraryRules.md.
    internal static class DocSingleSourceTests
    {
        private static readonly string[] RuleKeywords =
        {
            "TCMP", "feat.", "ft.", "featuring", "3-song", "NormaliseTitleForDedup", "keep=", "mid-title",
        };

        private static string FindRepoRoot()
        {
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            while (!string.IsNullOrEmpty(dir))
            {
                if (File.Exists(Path.Combine(dir, "README.md")) && Directory.Exists(Path.Combine(dir, "docs", "Rules")))
                    return dir;
                dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
            }
            return null;
        }

        public static void TopLevelDocsCarryNoRuleKeywords()
        {
            string root = FindRepoRoot();
            Assert.True(root != null, "repo root with README.md and docs/Rules not found from the test base directory");
            foreach (string name in new[] { "README.md", "CLAUDE.md" })
            {
                string text = File.ReadAllText(Path.Combine(root, name));
                foreach (string kw in RuleKeywords)
                    Assert.True(text.IndexOf(kw, StringComparison.Ordinal) < 0,
                        name + " restates a rule (found '" + kw + "'); keep it in docs/Rules/MusicLibraryRules.md and link");
            }
        }

        public static void CanonicalRuleDocHoldsTheRules()
        {
            string root = FindRepoRoot();
            Assert.True(root != null, "repo root not found");
            string rules = File.ReadAllText(Path.Combine(root, "docs", "Rules", "MusicLibraryRules.md"));
            foreach (string kw in new[] { "Trailing bare credit", "Duplicate Matching Rule", "TCMP" })
                Assert.True(rules.IndexOf(kw, StringComparison.Ordinal) >= 0, "MusicLibraryRules.md lost '" + kw + "'");
        }
    }
}
