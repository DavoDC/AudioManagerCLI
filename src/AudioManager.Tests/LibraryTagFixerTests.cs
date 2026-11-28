using System;
using System.IO;
using System.Linq;

namespace AudioManager
{
    /// <summary>Library tag fix: only a padded Title and a missing TCMP, journaled and revertable.</summary>
    internal static class LibraryTagFixerTests
    {
        private static string NewRoot()
        {
            string root = Path.Combine(Path.GetTempPath(), "AudioManagerTest_" + Guid.NewGuid());
            Directory.CreateDirectory(Path.Combine(root, "Artists", "A", "Singles"));
            return root;
        }

        private static string Song(string root, string file, string title, bool compilation)
        {
            string dir = Path.Combine(root, "Artists", "A", "Singles");
            string path = TagFixerTests.CreateSilentMp3Fixture(dir, file, title, "Al", "A", 2020, 30);
            using (var f = TagLib.File.Create(path))
            {
                ((TagLib.Id3v2.Tag)f.GetTag(TagLib.TagTypes.Id3v2, true)).IsCompilation = compilation;
                f.Save();
            }
            return path;
        }

        public static void Plan_FlagsPaddedTitleAndMissingTcmpOnly()
        {
            string root = NewRoot();
            try
            {
                Song(root, "A - Clean.mp3", "Clean", true);
                Song(root, "A - Padded .mp3", "Padded ", true);
                Song(root, "A - NoTcmp.mp3", "NoTcmp", false);
                var plan = LibraryTagFixer.BuildPlan(root);
                Assert.Equal("2", plan.Entries.Count.ToString(), "clean file is left out");
                var padded = plan.Entries.Single(e => e.Fixes.Contains("title_trim"));
                Assert.Equal("Padded", padded.NewTitle, "title trimmed");
                Assert.True(plan.Entries.Single(e => e.Fixes.Contains("tcmp")).Fixes.Count == 1, "tcmp entry has only tcmp");
            }
            finally { Directory.Delete(root, true); }
        }

        public static void Apply_FixesTagsRenamesPaddedFileAndRevertRestores()
        {
            string root = NewRoot();
            string journal = Path.Combine(root, "journal.json");
            try
            {
                string padded = Song(root, "A - Padded .mp3", "Padded ", false);
                var plan = LibraryTagFixer.BuildPlan(root);
                var result = LibraryTagFixer.Apply(plan, journal, root);
                Assert.Equal("applied", result.Status, "status");
                string renamed = Path.Combine(Path.GetDirectoryName(padded), "A - Padded.mp3");
                Assert.True(File.Exists(renamed) && !File.Exists(padded), "file renamed to trimmed title");
                using (var f = TagLib.File.Create(renamed))
                {
                    Assert.Equal("Padded", f.Tag.Title, "title trimmed on disk");
                    Assert.True(((TagLib.Id3v2.Tag)f.GetTag(TagLib.TagTypes.Id3v2, false)).IsCompilation, "TCMP set");
                }
                Assert.Equal("0", LibraryTagFixer.BuildPlan(root).Entries.Count.ToString(), "second plan finds nothing");

                var rev = LibraryTagFixer.Revert(journal, root);
                Assert.Equal("reverted", rev.Status, "revert status");
                Assert.True(File.Exists(padded) && !File.Exists(renamed), "original name back");
                using (var f = TagLib.File.Create(padded))
                {
                    Assert.Equal("Padded ", f.Tag.Title, "title restored");
                    Assert.True(!((TagLib.Id3v2.Tag)f.GetTag(TagLib.TagTypes.Id3v2, false)).IsCompilation, "TCMP restored");
                }
            }
            finally { Directory.Delete(root, true); }
        }

        public static void Apply_SkipsAStaleEntryAndNeverOverwritesARenameTarget()
        {
            string root = NewRoot();
            try
            {
                string padded = Song(root, "A - Padded .mp3", "Padded ", true);
                Song(root, "A - Padded.mp3", "Padded", true);
                var plan = LibraryTagFixer.BuildPlan(root);
                var result = LibraryTagFixer.Apply(plan, Path.Combine(root, "j.json"), root);
                Assert.Equal("1", result.Skipped.ToString(), "rename target exists -> entry skipped, not overwritten");
                Assert.True(File.Exists(padded), "padded file untouched");
            }
            finally { Directory.Delete(root, true); }
        }
    }
}
