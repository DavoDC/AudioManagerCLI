using System;
using System.Collections.Generic;
using System.IO;
using AudioManager.Code.Modules;

namespace AudioManager
{
    /// <summary>David 2026-11-11: the artist folder matches the tag, the folder is renamed to the tag, tag data takes precedence. Case-only, journaled, revertable; tags are never edited.</summary>
    internal static class ArtistFolderSpellingTests
    {
        private static string TempLib()
        {
            return Path.Combine(Path.GetTempPath(), "am_spell_" + Guid.NewGuid().ToString("N").Substring(0, 8));
        }

        private static string Route(MusicIntegrator integrator, string artist, out string reason)
        {
            var track = new Track { Artists = artist, Title = "Song", Album = "Missing", Genres = "Missing" };
            return integrator.GetDestDir(track, new HashSet<string>(StringComparer.OrdinalIgnoreCase), new HashSet<string>(StringComparer.OrdinalIgnoreCase), out reason, out _);
        }

        public static void ChooseCanonicalSpelling_MostFrequentWins_TieIsOrdinalFirst()
        {
            Assert.Equal("Charli XCX", MusicIntegrator.ChooseCanonicalSpelling(new[] { "Charli xcx", "Charli XCX", "Charli XCX" }), "most frequent spelling wins");
            Assert.Equal("TJ Hickey", MusicIntegrator.ChooseCanonicalSpelling(new[] { "Tj Hickey", "TJ Hickey" }), "a tie picks the ordinal-first spelling, deterministically");
            Assert.Equal("TJ Hickey", MusicIntegrator.ChooseCanonicalSpelling(new[] { "TJ Hickey", "Tj Hickey" }), "input order does not matter");
        }

        public static void GetDestDir_ExistingFolderWithOtherCase_RoutesToTagSpellingAndSaysRename()
        {
            string lib = TempLib();
            try
            {
                Directory.CreateDirectory(Path.Combine(lib, "Artists", "Charli xcx", "Singles"));
                string dest = Route(new MusicIntegrator(lib, dryRun: true), "Charli XCX", out string reason);

                Assert.Equal("Charli XCX", Path.GetFileName(Path.GetDirectoryName(dest)), "the destination uses the tag spelling");
                Assert.True(reason.Contains("renamed") && reason.Contains("case only") && reason.Contains("Charli xcx") && reason.Contains("Charli XCX"), "the plan says the folder is renamed: " + reason);
                Assert.Equal("Charli xcx", Path.GetFileName(Directory.GetDirectories(Path.Combine(lib, "Artists"))[0]), "a dry plan renames nothing");
            }
            finally { if (Directory.Exists(lib)) Directory.Delete(lib, true); }
        }

        public static void GetDestDir_SymbolStrippedByFolderRule_IsNotAReviewItem()
        {
            string lib = TempLib();
            try
            {
                Directory.CreateDirectory(Path.Combine(lib, "Artists", "BNYX", "Singles"));
                Route(new MusicIntegrator(lib, dryRun: true), "BNYX®", out string reason);
                Assert.True(!reason.Contains("review") && !reason.Contains("renamed"), "BNYX with the registered mark sanitises to the folder BNYX, so nothing to flag: " + reason);
            }
            finally { if (Directory.Exists(lib)) Directory.Delete(lib, true); }
        }

        public static void GetDestDir_ExistingFolderSameSpelling_NoNote()
        {
            string lib = TempLib();
            try
            {
                Directory.CreateDirectory(Path.Combine(lib, "Artists", "Charli XCX", "Singles"));
                Route(new MusicIntegrator(lib, dryRun: true), "Charli XCX", out string reason);
                Assert.True(!reason.Contains("review") && !reason.Contains("renamed"), "a matching folder needs no note: " + reason);
            }
            finally { if (Directory.Exists(lib)) Directory.Delete(lib, true); }
        }

        public static void FolderCaseRename_RenamesOnlyCaseDifferences_Journals_AndReverts()
        {
            string lib = TempLib();
            string journalDir = Path.Combine(lib, "jl");
            try
            {
                string old = Path.Combine(lib, "Artists", "Charli xcx");
                Directory.CreateDirectory(Path.Combine(old, "Singles"));
                File.WriteAllBytes(Path.Combine(old, "Singles", "a.mp3"), new byte[] { 7 });
                Directory.CreateDirectory(Path.Combine(lib, "Artists", "Charlie Puth"));
                var integrator = new MusicIntegrator(lib, dryRun: false);
                integrator.MigrationJournalDir = journalDir;

                var plan = integrator.PlanArtistFolderCaseRenames(new[] { "Charli XCX", "Charlie Puth", "Somebody New" });
                Assert.Equal("1", plan.Count.ToString(), "only the case-only difference is planned, never a different name");
                Assert.Equal("Charli xcx", plan[0].Key, "from the on-disk name");
                Assert.Equal("Charli XCX", plan[0].Value, "to the tag spelling");

                string journal = integrator.ApplyArtistFolderCaseRenames(plan);
                Assert.True(journal != null && File.Exists(journal), "a journal is written");
                Assert.Equal("Charli XCX", Path.GetFileName(Directory.GetDirectories(Path.Combine(lib, "Artists"), "Charli X*")[0]), "folder now has the tag spelling on disk");
                Assert.True(File.Exists(Path.Combine(lib, "Artists", "Charli XCX", "Singles", "a.mp3")), "contents untouched");

                integrator.RevertArtistFolderCaseRenames(journal);
                Assert.Equal("Charli xcx", Path.GetFileName(Directory.GetDirectories(Path.Combine(lib, "Artists"), "Charli x*")[0]), "revert restores the old spelling");
                Assert.True(File.Exists(Path.Combine(old, "Singles", "a.mp3")), "contents still intact after revert");
            }
            finally { if (Directory.Exists(lib)) Directory.Delete(lib, true); }
        }

        public static void FolderCaseRename_TwoFoldersDifferingOnlyByCase_AreLeftAlone()
        {
            string lib = TempLib();
            try
            {
                // Case-insensitive volumes cannot hold both, so a plan that finds the target already spelled right plans nothing.
                Directory.CreateDirectory(Path.Combine(lib, "Artists", "Charli XCX"));
                var plan = new MusicIntegrator(lib, dryRun: false).PlanArtistFolderCaseRenames(new[] { "Charli XCX" });
                Assert.Equal("0", plan.Count.ToString(), "a folder already spelled like the tag is not renamed");
            }
            finally { if (Directory.Exists(lib)) Directory.Delete(lib, true); }
        }

        public static void LibChecker_TagWithSymbolStrippedByFolderRule_IsNotAMismatch_ButCaseIs()
        {
            var ok = new TrackTag("\\Artists\\BNYX\\Singles\\BNYX - A.xml", "A", "BNYX®", "Missing", "2020", "1", "Pop", "00:03:00.0000000", "1", "True", "1200", "1200");
            var bad = new TrackTag("\\Artists\\Charli xcx\\Singles\\Charli XCX - B.xml", "B", "Charli XCX", "Missing", "2020", "1", "Pop", "00:03:00.0000000", "1", "True", "1200", "1200");
            var checker = new LibChecker(new List<TrackTag> { ok, bad });
            int mismatches = 0;
            foreach (var h in checker.Hits) if (h.Kind == "artist_folder_mismatch") mismatches++;
            Assert.Equal("1", mismatches.ToString(), "only the case mismatch is a hit; the stripped symbol is not");
        }
    }
}
