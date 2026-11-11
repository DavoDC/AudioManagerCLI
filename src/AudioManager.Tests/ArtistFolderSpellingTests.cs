using System;
using System.Collections.Generic;
using System.IO;
using AudioManager.Code.Modules;

namespace AudioManager
{
    /// <summary>Artist folders are named as close to the artist tag as possible; a folder that cannot match the tag is flagged for review, never renamed.</summary>
    internal static class ArtistFolderSpellingTests
    {
        public static void ChooseCanonicalSpelling_MostFrequentWins_TieIsOrdinalFirst()
        {
            Assert.Equal("Charli XCX", MusicIntegrator.ChooseCanonicalSpelling(new[] { "Charli xcx", "Charli XCX", "Charli XCX" }), "most frequent spelling wins");
            Assert.Equal("TJ Hickey", MusicIntegrator.ChooseCanonicalSpelling(new[] { "Tj Hickey", "TJ Hickey" }), "a tie picks the ordinal-first spelling, deterministically");
            Assert.Equal("TJ Hickey", MusicIntegrator.ChooseCanonicalSpelling(new[] { "TJ Hickey", "Tj Hickey" }), "input order does not matter");
        }

        public static void GetDestDir_ExistingFolderWithOtherCase_UsesOnDiskNameAndFlagsReview()
        {
            string lib = Path.Combine(Path.GetTempPath(), "am_spell_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                Directory.CreateDirectory(Path.Combine(lib, "Artists", "Charli xcx", "Singles"));
                var integrator = new MusicIntegrator(lib, dryRun: true);
                var track = new Track { Artists = "Charli XCX", Title = "Song", Album = "Missing", Genres = "Missing" };

                string dest = integrator.GetDestDir(track, new HashSet<string>(StringComparer.OrdinalIgnoreCase), new HashSet<string>(StringComparer.OrdinalIgnoreCase), out string reason, out _);

                Assert.Equal("Charli xcx", Path.GetFileName(Path.GetDirectoryName(dest)), "the existing on-disk spelling is used verbatim");
                Assert.True(reason.Contains("review") && reason.Contains("Charli XCX") && reason.Contains("Charli xcx"), "the reason names both spellings and says review: " + reason);
                Assert.True(Directory.GetDirectories(Path.Combine(lib, "Artists")).Length == 1, "no folder was created or renamed");
            }
            finally
            {
                if (Directory.Exists(lib)) Directory.Delete(lib, true);
            }
        }

        public static void GetDestDir_ExistingFolderSameSpelling_NoReviewNote()
        {
            string lib = Path.Combine(Path.GetTempPath(), "am_spell_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                Directory.CreateDirectory(Path.Combine(lib, "Artists", "Charli XCX", "Singles"));
                var integrator = new MusicIntegrator(lib, dryRun: true);
                var track = new Track { Artists = "Charli XCX", Title = "Song", Album = "Missing", Genres = "Missing" };

                integrator.GetDestDir(track, new HashSet<string>(StringComparer.OrdinalIgnoreCase), new HashSet<string>(StringComparer.OrdinalIgnoreCase), out string reason, out _);

                Assert.True(!reason.Contains("review"), "a matching folder needs no note: " + reason);
            }
            finally
            {
                if (Directory.Exists(lib)) Directory.Delete(lib, true);
            }
        }
    }
}
