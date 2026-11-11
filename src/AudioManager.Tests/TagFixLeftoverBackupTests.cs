using System;
using System.IO;

namespace AudioManager
{
    /// <summary>TagFixer reports leftover .tagfix.bak and .tagfix.bak.stale-N files read-only.</summary>
    internal static class TagFixLeftoverBackupTests
    {
        public static void ListLeftoverBackups_FindsBakAndStale_IgnoresOthers_ChangesNothing()
        {
            string root = Path.Combine(Path.GetTempPath(), "am_tfbak_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                Directory.CreateDirectory(Path.Combine(root, "sub"));
                string bak = Path.Combine(root, "a.mp3.tagfix.bak");
                string stale = Path.Combine(root, "sub", "b.mp3.tagfix.bak.stale-2");
                string other = Path.Combine(root, "c.mp3");
                string lookalike = Path.Combine(root, "d.tagfix.bakery");
                foreach (string p in new[] { bak, stale, other, lookalike }) File.WriteAllBytes(p, new byte[] { 1 });

                var found = TagFixer.ListLeftoverBackups(root);

                Assert.Equal("2", found.Count.ToString(), "only the bak and the stale copy are listed");
                Assert.True(found.Contains(bak) && found.Contains(stale), "both named");
                Assert.True(File.Exists(bak) && File.Exists(stale) && File.Exists(other) && File.Exists(lookalike), "nothing moved or deleted");
                Assert.Equal("0", TagFixer.ListLeftoverBackups(Path.Combine(root, "nope")).Count.ToString(), "missing folder lists nothing");
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }
    }
}
