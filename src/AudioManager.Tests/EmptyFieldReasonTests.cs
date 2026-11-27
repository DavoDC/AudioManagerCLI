using System;
using System.IO;

namespace AudioManager
{
    /// <summary>
    /// A blocked rename names the field that is empty. Title empty gives empty_title, artists empty gives empty_artists,
    /// both empty gives empty_artists_and_title. The old prefix "empty artist or title" still maps to empty_artist_or_title
    /// so stored plans keep their rule id.
    /// </summary>
    internal static class EmptyFieldReasonTests
    {
        private static string TempDir()
        {
            return Path.Combine(Path.GetTempPath(), "AmEmptyField_" + Guid.NewGuid().ToString("N").Substring(0, 8));
        }

        // Dry-run the tag fix on one fixture and return its log (the same path the needsManual rows come from).
        private static TagFixer.FixLog DryRun(string title, string artists, out string dir)
        {
            dir = TempDir();
            Directory.CreateDirectory(dir);
            string file = TagFixerTests.CreateSilentMp3Fixture(dir, "empty-field.mp3", title, "Album Name", artists, 2020);
            return TagFixer.ForTesting(dryRun: true).ProcessFile(file);
        }

        public static void EmptyTitle_ReasonNamesTitle_RuleEmptyTitle()
        {
            string dir = null;
            try
            {
                TagFixer.FixLog log = DryRun("", "Primary Artist", out dir);
                Assert.True(log.NeedsManual, "blank title must be a needsManual row");
                Assert.Equal("empty title after sanitisation", log.ManualReason, "blank title reason names the title");
                Assert.Equal("empty_title", TagFixPlanJson.ManualRule(log), "blank title rule id");
            }
            finally { DeleteDir(dir); }
        }

        public static void EmptyArtists_ReasonNamesArtists_RuleEmptyArtists()
        {
            string dir = null;
            try
            {
                TagFixer.FixLog log = DryRun("Song Title", "", out dir);
                Assert.True(log.NeedsManual, "blank artists must be a needsManual row");
                Assert.Equal("empty artists after sanitisation", log.ManualReason, "blank artists reason names the artists");
                Assert.Equal("empty_artists", TagFixPlanJson.ManualRule(log), "blank artists rule id");
            }
            finally { DeleteDir(dir); }
        }

        public static void EmptyArtistsAndTitle_ReasonNamesBoth_RuleEmptyArtistsAndTitle()
        {
            string dir = null;
            try
            {
                TagFixer.FixLog log = DryRun("", "", out dir);
                Assert.True(log.NeedsManual, "blank artists and title must be a needsManual row");
                Assert.Equal("empty artists and title after sanitisation", log.ManualReason, "both blank reason names both");
                Assert.Equal("empty_artists_and_title", TagFixPlanJson.ManualRule(log), "both blank rule id");
            }
            finally { DeleteDir(dir); }
        }

        public static void OldPrefix_StillMapsToEmptyArtistOrTitle()
        {
            var log = new TagFixer.FixLog
            {
                NeedsManual = true,
                ManualReason = "empty artist or title after sanitisation"
            };
            Assert.Equal("empty_artist_or_title", TagFixPlanJson.ManualRule(log), "stored plans with the old reason keep their rule id");
        }

        private static void DeleteDir(string dir)
        {
            if (dir != null && Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }
}
