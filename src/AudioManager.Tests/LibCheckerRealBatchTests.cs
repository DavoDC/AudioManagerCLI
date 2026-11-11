using AudioManager.Code.Modules;
using System.Collections.Generic;
using System.Linq;

namespace AudioManager
{
    // Permanent fixtures for the LibChecker hits a full-size real rehearsal produced (2026-11-11).
    // Each test pins one real shape so the report can never silently stop listing it.
    // Characterisation tests: they assert the exact hit kind and detail text LibChecker writes today.
    internal static class LibCheckerRealBatchTests
    {
        // Same 12-argument TrackTag construction as LibCheckerTests (cached fields, no file I/O).
        private static TrackTag Tag(string relPath, string title, string artist, string album, string year = "2020")
        {
            return new TrackTag(relPath, title, artist, album, year, "1", "Hip-Hop",
                "00:03:00.0000000", "1", "True", "1200", "1200");
        }

        private static void AssertHit(LibChecker checker, string kind, string detail)
        {
            bool found = checker.Hits.Any(h => h.Kind == kind && h.Detail == detail);
            string reported = string.Join(" | ", checker.Hits.Select(h => "[" + h.Kind + "] " + h.Detail));
            Assert.True(found, "expected hit [" + kind + "] '" + detail + "' but LibChecker reported: " + reported);
            Assert.True(!checker.IsClean, "a library with hit [" + kind + "] '" + detail + "' must be dirty");
        }

        // Shape 1: "(Alternate Version)" in title, album and filename.
        public static void LibChecker_RealBatch_AlternateVersionInTitleAlbumAndFilename_ReportsVersion()
        {
            var tag = Tag("\\Artists\\Artist Alpha\\Singles\\Artist Alpha - Song (Alternate Version).xml",
                "Song (Alternate Version)", "Artist Alpha", "Test Album (Alternate Version)");
            var checker = new LibChecker(new List<TrackTag> { tag });
            AssertHit(checker, "unwanted_text", "found 'version' in title");
            AssertHit(checker, "unwanted_text", "found 'version' in album");
            AssertHit(checker, "unwanted_text", "found 'version' in filename");
        }

        // Shape 2: "(Multilingual Version)" in the title.
        public static void LibChecker_RealBatch_MultilingualVersionInTitle_ReportsVersion()
        {
            var tag = Tag("\\Artists\\Artist Alpha\\Singles\\Artist Alpha - Song (Multilingual Version).xml",
                "Song (Multilingual Version)", "Artist Alpha", "Test Album");
            var checker = new LibChecker(new List<TrackTag> { tag });
            AssertHit(checker, "unwanted_text", "found 'version' in title");
        }

        // Shape 3: "[From GTAVI: Original Score]" in title and album.
        public static void LibChecker_RealBatch_FromGtaOriginalScoreInTitleAndAlbum_ReportsOriginal()
        {
            var tag = Tag("\\Artists\\Artist Alpha\\Singles\\Artist Alpha - Song [From GTAVI: Original Score].xml",
                "Song [From GTAVI: Original Score]", "Artist Alpha", "[From GTAVI: Original Score]");
            var checker = new LibChecker(new List<TrackTag> { tag });
            AssertHit(checker, "unwanted_text", "found 'original' in title");
            AssertHit(checker, "unwanted_text", "found 'original' in album");
        }

        // Shape 4: a Miscellaneous Songs track with no Year tag.
        public static void LibChecker_RealBatch_MiscTrackWithoutYear_ReportsNoYear()
        {
            var tag = Tag("\\Miscellaneous Songs\\Artist Beta - Song A.xml",
                "Song A", "Artist Beta", "Test Album", Constants.MissingTag);
            var checker = new LibChecker(new List<TrackTag> { tag });
            AssertHit(checker, "missing_tag", "has no Year set");
        }

        // Shape 5: artist tag "Tj Hickey" filed under the folder "TJ Hickey" (case differs).
        public static void LibChecker_RealBatch_ArtistTagCaseDiffersFromFolder_ReportsFolderMismatch()
        {
            var tag = Tag("\\Artists\\TJ Hickey\\Singles\\Tj Hickey - Song A.xml",
                "Song A", "Tj Hickey", "Test Album");
            var checker = new LibChecker(new List<TrackTag> { tag });
            AssertHit(checker, "artist_folder_mismatch", "a song by 'Tj Hickey' is in the 'TJ Hickey' folder");
        }

        // Shape 6 (two songs of one album in Singles/) is already pinned by
        // LibCheckerTests.LibChecker_TwoAlbumSongsInSingles_IsDirty, so it is not repeated here.
    }
}
