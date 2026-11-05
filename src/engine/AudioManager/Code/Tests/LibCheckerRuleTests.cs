using AudioManager.Code.Modules;
using System;
using System.Collections.Generic;
using System.IO;

namespace AudioManager
{
    // Direct behaviour pins for the TIER 1 LibChecker rules: CheckForDuplicates,
    // CheckAlbumSubfolderRule, CheckMainFolderForGenre and CheckGenreVsFolder.
    // Each test asserts on the console issue text the rule reports, not only on IsClean.
    // RelPaths carry the leading backslash that the AudioMirror format produces (see DevContext.md).
    internal static class LibCheckerRuleTests
    {
        private static TrackTag Track(string relPath, string title, string artists, string album,
            string genres = "Hip-Hop", string albumCoverCount = "1", string compilation = "True")
        {
            return new TrackTag(relPath, title, artists, album, "2020", "1", genres,
                "00:03:00.0000000", albumCoverCount, compilation, "1200", "1200");
        }

        // Runs LibChecker with Console redirected and returns everything it printed.
        private static string RunLibChecker(List<TrackTag> tags, out bool isClean)
        {
            var original = Console.Out;
            var capture = new StringWriter();
            Console.SetOut(capture);
            bool clean;
            try
            {
                clean = new LibChecker(tags).IsClean;
            }
            finally
            {
                Console.SetOut(original);
            }
            isClean = clean;
            return capture.ToString();
        }

        private static int CountOf(string text, string needle)
        {
            return text.Split(new[] { needle }, StringSplitOptions.None).Length - 1;
        }

        // ---- CheckForDuplicates ----

        public static void LibCheckerRule_Duplicates_ReportsBothCopies()
        {
            // Same title and artists, different albums so the album-subfolder rule stays out of the way
            var tagA = Track("\\Artists\\Artist Alpha\\Singles\\Artist Alpha - Song A.xml",
                "Song A", "Artist Alpha", "Album One");
            var tagB = Track("\\Artists\\Artist Alpha\\Singles\\Artist Alpha - Song A (2).xml",
                "Song A", "Artist Alpha", "Album Two");
            bool clean;
            string console = RunLibChecker(new List<TrackTag> { tagA, tagB }, out clean);

            Assert.True(!clean, "duplicate title and artists should be dirty");
            Assert.True(console.Contains("'\\Artists\\Artist Alpha\\Singles\\Artist Alpha - Song A.xml' duplicates another track!"),
                "first copy should be reported by RelPath");
            Assert.True(console.Contains("'\\Artists\\Artist Alpha\\Singles\\Artist Alpha - Song A (2).xml' duplicates another track!"),
                "second copy should be reported by RelPath");
            Assert.True(CountOf(console, "duplicates another track!") == 2,
                "both members of the duplicate pair should be reported, had " + CountOf(console, "duplicates another track!"));
        }

        public static void LibCheckerRule_Duplicates_SameTitleDifferentArtist_NotFlagged()
        {
            // Grouping key is Title + Artists, so the same song title by two artists is not a duplicate
            var tagA = Track("\\Artists\\Artist Alpha\\Singles\\Artist Alpha - Song A.xml",
                "Song A", "Artist Alpha", "Album One");
            var tagB = Track("\\Artists\\Artist Beta\\Singles\\Artist Beta - Song A.xml",
                "Song A", "Artist Beta", "Album Two");
            bool clean;
            string console = RunLibChecker(new List<TrackTag> { tagA, tagB }, out clean);

            Assert.True(clean, "same title by different artists should be clean");
            Assert.True(!console.Contains("duplicates another track!"),
                "no duplicate message expected for different artists");
        }

        // ---- CheckAlbumSubfolderRule ----

        public static void LibCheckerRule_AlbumSubfolder_TwoSongsInSingles_ReportsEachTrack()
        {
            var tagA = Track("\\Artists\\Artist Alpha\\Singles\\Artist Alpha - Song A.xml",
                "Song A", "Artist Alpha", "Real Album");
            var tagB = Track("\\Artists\\Artist Alpha\\Singles\\Artist Alpha - Song B.xml",
                "Song B", "Artist Alpha", "Real Album");
            bool clean;
            string console = RunLibChecker(new List<TrackTag> { tagA, tagB }, out clean);

            Assert.True(!clean, "two songs from one album in Singles/ should be dirty");
            Assert.True(console.Contains("'\\Artists\\Artist Alpha\\Singles\\Artist Alpha - Song A.xml': 2 songs from album 'Real Album' but in Singles/ (use album subfolder)"),
                "song A should be reported with the album count and the album subfolder advice");
            Assert.True(console.Contains("'\\Artists\\Artist Alpha\\Singles\\Artist Alpha - Song B.xml': 2 songs from album 'Real Album' but in Singles/ (use album subfolder)"),
                "song B should be reported with the album count and the album subfolder advice");
            Assert.True(CountOf(console, "(use album subfolder)") == 2,
                "both album tracks should be reported, had " + CountOf(console, "(use album subfolder)"));
        }

        public static void LibCheckerRule_AlbumSubfolder_MixedPlacement_OnlySinglesTrackFlagged()
        {
            // Song A sits in Singles/ and is flagged; song B is correctly in its album subfolder
            var tagA = Track("\\Artists\\Artist Alpha\\Singles\\Artist Alpha - Song A.xml",
                "Song A", "Artist Alpha", "Real Album");
            var tagB = Track("\\Artists\\Artist Alpha\\Real Album\\Artist Alpha - Song B.xml",
                "Song B", "Artist Alpha", "Real Album");
            bool clean;
            string console = RunLibChecker(new List<TrackTag> { tagA, tagB }, out clean);

            Assert.True(!clean, "a Singles/ track from a multi-song album should be dirty");
            Assert.True(CountOf(console, "(use album subfolder)") == 1,
                "only the Singles/ track should be flagged, had " + CountOf(console, "(use album subfolder)"));
            Assert.True(console.Contains("'\\Artists\\Artist Alpha\\Singles\\Artist Alpha - Song A.xml': 2 songs from album 'Real Album'"),
                "the flagged line should name the Singles/ track");
            Assert.True(!console.Contains("'\\Artists\\Artist Alpha\\Real Album\\Artist Alpha - Song B.xml': 2 songs"),
                "the track already in its album subfolder must not be flagged");
        }

        public static void LibCheckerRule_AlbumSubfolder_OneSongInAlbumFolder_ReportsTrack()
        {
            var tag = Track("\\Artists\\Artist Alpha\\Real Album\\Artist Alpha - Song A.xml",
                "Song A", "Artist Alpha", "Real Album");
            bool clean;
            string console = RunLibChecker(new List<TrackTag> { tag }, out clean);

            Assert.True(!clean, "a lone album track in an album subfolder should be dirty");
            Assert.True(console.Contains("'\\Artists\\Artist Alpha\\Real Album\\Artist Alpha - Song A.xml': only 1 song from album 'Real Album' but in subfolder 'Real Album/' (should be Singles/)"),
                "the lone track should be reported with the Singles/ advice and its parent folder");
        }

        public static void LibCheckerRule_AlbumSubfolder_SingleSongInSingles_NotFlagged()
        {
            var tag = Track("\\Artists\\Artist Alpha\\Singles\\Artist Alpha - Song A.xml",
                "Song A", "Artist Alpha", "Real Album");
            bool clean;
            string console = RunLibChecker(new List<TrackTag> { tag }, out clean);

            Assert.True(clean, "a lone album track already in Singles/ should be clean");
            Assert.True(!console.Contains("should be Singles/"), "no single-song placement message expected");
        }

        public static void LibCheckerRule_AlbumSubfolder_MissingAlbum_Skipped()
        {
            // Album "Missing" is excluded from the grouping, so two Singles/ tracks are not an album group.
            // They are reported by the missing-field check instead.
            var tagA = Track("\\Artists\\Artist Alpha\\Singles\\Artist Alpha - Song A.xml",
                "Song A", "Artist Alpha", "Missing");
            var tagB = Track("\\Artists\\Artist Alpha\\Singles\\Artist Alpha - Song B.xml",
                "Song B", "Artist Alpha", "Missing");
            bool clean;
            string console = RunLibChecker(new List<TrackTag> { tagA, tagB }, out clean);

            Assert.True(!clean, "tracks with no album should still be dirty via the missing-field check");
            Assert.True(console.Contains("has no Album set!"), "missing album should be reported by the field check");
            Assert.True(!console.Contains("(use album subfolder)"),
                "a Missing album must not form an album group for the subfolder rule");
        }

        // ---- CheckMainFolderForGenre ----

        public static void LibCheckerRule_MusivationFolder_TrackWithoutGenre_ReportsTrack()
        {
            var tag = Track("\\Musivation\\Some Artist\\Singles\\Some Artist - Song A.xml",
                "Song A", "Some Artist", "Test Album", "Hip-Hop");
            bool clean;
            string console = RunLibChecker(new List<TrackTag> { tag }, out clean);

            Assert.True(!clean, "a Musivation/ track without the Musivation genre should be dirty");
            Assert.True(console.Contains("Some Artist - Song A does not have the Musivation genre!"),
                "the Musivation/ track should be reported by artist and title");
        }

        public static void LibCheckerRule_MotivationFolder_TrackWithoutGenre_ReportsTrack()
        {
            var tag = Track("\\Motivation\\Some Artist\\Singles\\Some Artist - Song A.xml",
                "Song A", "Some Artist", "Test Album", "Hip-Hop");
            bool clean;
            string console = RunLibChecker(new List<TrackTag> { tag }, out clean);

            Assert.True(!clean, "a Motivation/ track without the Motivation genre should be dirty");
            Assert.True(console.Contains("Some Artist - Song A does not have the Motivation genre!"),
                "the Motivation/ track should be reported by artist and title");
        }

        public static void LibCheckerRule_MusivationFolder_GenreAmongOthers_NotFlagged()
        {
            // The genre check is a Contains on the joined genre string, so the tag may carry other genres too
            var tag = Track("\\Musivation\\Some Artist\\Singles\\Some Artist - Song A.xml",
                "Song A", "Some Artist", "Test Album", "Hip-Hop; Musivation");
            bool clean;
            string console = RunLibChecker(new List<TrackTag> { tag }, out clean);

            Assert.True(clean, "Musivation listed among several genres should be clean");
            Assert.True(!console.Contains("does not have the Musivation genre"), "no genre message expected");
        }

        // ---- CheckGenreVsFolder ----

        public static void LibCheckerRule_GenreVsFolder_MusivationGenreOutsideFolder_ReportsTrack()
        {
            // The Artists/ track is flagged; the track already inside Musivation/ is not
            var outside = Track("\\Artists\\Known Artist\\Singles\\Known Artist - Song A.xml",
                "Song A", "Known Artist", "Test Album", "Musivation");
            var inside = Track("\\Musivation\\Other Artist\\Singles\\Other Artist - Song B.xml",
                "Song B", "Other Artist", "Test Album", "Musivation");
            bool clean;
            string console = RunLibChecker(new List<TrackTag> { outside, inside }, out clean);

            Assert.True(!clean, "a Musivation genre outside Musivation/ should be dirty");
            Assert.True(console.Contains("'\\Artists\\Known Artist\\Singles\\Known Artist - Song A.xml' has genre 'Musivation' but is not in Musivation/"),
                "the outside track should be reported with its RelPath and the expected folder");
            Assert.True(CountOf(console, "but is not in Musivation/") == 1,
                "only the track outside Musivation/ should be reported, had " + CountOf(console, "but is not in Musivation/"));
        }

        public static void LibCheckerRule_GenreVsFolder_MotivationGenreOutsideFolder_ReportsTrack()
        {
            var tag = Track("\\Artists\\Known Artist\\Singles\\Known Artist - Song A.xml",
                "Song A", "Known Artist", "Test Album", "Motivation");
            bool clean;
            string console = RunLibChecker(new List<TrackTag> { tag }, out clean);

            Assert.True(!clean, "a Motivation genre outside Motivation/ should be dirty");
            Assert.True(console.Contains("'\\Artists\\Known Artist\\Singles\\Known Artist - Song A.xml' has genre 'Motivation' but is not in Motivation/"),
                "the track should be reported with its RelPath and the expected folder");
        }
    }
}
