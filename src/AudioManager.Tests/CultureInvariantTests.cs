using AudioManager.Code.Modules;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;

namespace AudioManager
{
    /// <summary>
    /// Culture-invariance tests for the parse/format calls fixed to InvariantCulture. The thread culture is
    /// switched to de-DE (decimal comma) and the output must match the invariant result. Only the JSON
    /// builders are covered: Analyser's TimeSpan.Parse sites are private and print to the console.
    /// </summary>
    internal static class CultureInvariantTests
    {
        // relPath, title, artists, album, year, trackNo, genres, length, coverCount, compilation, coverW, coverH
        private static TrackTag Tag(string year, string length, string w, string h) =>
            new TrackTag("\\Artists\\Artist A\\Singles\\Artist A - Song.xml",
                "Song", "Artist A", "Album", year, "1", "Rock", length, "1", "False", w, h);

        private static List<TrackTag> Sample() => new List<TrackTag>
        {
            Tag("2020", "00:03:25.5000000", "1200", "1200"),
            Tag("1995", "00:00:59.2500000", "800", "600"),
            Tag("Missing", "01:02:03.0000000", "Unknown", "Unknown"),
        };

        // Runs an action with the thread culture set to de-DE, restoring the previous culture in finally.
        private static T UnderGerman<T>(Func<T> action)
        {
            CultureInfo previous = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                return action();
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = previous;
            }
        }

        // The generatedAt line is a wall-clock timestamp, so it is removed before comparing two builds.
        private static string WithoutTimestamp(string json) =>
            string.Join("\n", json.Split('\n').Where(line => !line.Contains("\"generatedAt\"")));

        public static void GermanCulture_IsEffective_ForDecimalFormatting()
        {
            // Precondition: the de-DE switch really changes number formatting, so the equality tests below mean something.
            Assert.Equal("1,5", UnderGerman(() => ((double)1.5).ToString()), "de-DE must format 1.5 with a decimal comma");
            Assert.True(TimeSpan.Parse("00:00:01.5", CultureInfo.InvariantCulture).TotalSeconds == 1.5,
                "invariant TimeSpan.Parse must accept 00:00:01.5");
        }

        public static void UnderGerman_RestoresPreviousCulture()
        {
            CultureInfo before = Thread.CurrentThread.CurrentCulture;
            UnderGerman(() => 0);
            Assert.True(ReferenceEquals(before, Thread.CurrentThread.CurrentCulture), "culture must be restored");
        }

        public static void TracksJson_Build_IsSameUnderGermanCulture()
        {
            string invariant = WithoutTimestamp(TracksJson.Build(Sample()));
            string german = WithoutTimestamp(UnderGerman(() => TracksJson.Build(Sample())));
            Assert.Equal(invariant, german, "TracksJson output must not depend on the thread culture");
        }

        public static void TracksJson_Build_DecimalSecondsAndDimensionsUnderGermanCulture()
        {
            string json = UnderGerman(() => TracksJson.Build(Sample()));
            Assert.True(json.Contains("\"lengthSeconds\": 205.5"), "3:25.5 must parse to 205.5 seconds under de-DE");
            Assert.True(json.Contains("\"coverWidth\": 1200"), "cover width must format without culture separators");
        }

        public static void StatsJson_Build_IsSameUnderGermanCulture()
        {
            string invariant = WithoutTimestamp(StatsJson.Build(Sample(), 30_000_000L));
            string german = WithoutTimestamp(UnderGerman(() => StatsJson.Build(Sample(), 30_000_000L)));
            Assert.Equal(invariant, german, "StatsJson output must not depend on the thread culture");
        }

        public static void StatsJson_Build_AgeYearsUnderGermanCulture()
        {
            string json = UnderGerman(() => StatsJson.Build(Sample(), 0L));
            int oldest = DateTime.Now.Year - 1995;
            Assert.True(json.Contains("\"oldestYears\": " + oldest.ToString(CultureInfo.InvariantCulture)),
                "oldest year must be computed from the parsed year under de-DE");
        }
    }
}
