using System;
using System.IO;

namespace AudioManager
{
    /// <summary>ReplaceGuard.ListOrphans is the read-only dry-run twin of SweepOrphans: it lists, never changes.</summary>
    internal static class ReplaceGuardListOrphansTests
    {
        public static void ListOrphans_ListsBakAndMarker_AndChangesNothing()
        {
            string root = Path.Combine(Path.GetTempPath(), "am_orphans_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                string dir = Path.Combine(root, "Artists", "A", "Singles");
                Directory.CreateDirectory(dir);
                string bak = Path.Combine(dir, "a - b.mp3.replaced.bak");
                string marker = Path.Combine(dir, "a - b.mp3.replaced.dest");
                string lookalike = Path.Combine(dir, "c.mp3.replaced.bakX");
                File.WriteAllBytes(bak, new byte[] { 1 });
                File.WriteAllBytes(marker, new byte[] { 2 });
                File.WriteAllBytes(lookalike, new byte[] { 3 });

                var found = ReplaceGuard.ListOrphans(root);

                Assert.Equal("2", found.Count.ToString(), "bak and marker listed, the longer-extension lookalike is not");
                Assert.True(found.Contains(bak) && found.Contains(marker), "both true leftovers are named");
                Assert.True(File.Exists(bak) && File.Exists(marker) && File.Exists(lookalike), "listing must not move or delete anything");
                Assert.Equal("0", ReplaceGuard.ListOrphans(Path.Combine(root, "missing")).Count.ToString(), "a missing root lists nothing");
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }
    }
}
