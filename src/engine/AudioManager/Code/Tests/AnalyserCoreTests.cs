using AudioManager.Code.Modules;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Threading;

namespace AudioManager
{
    /// <summary>
    /// Coverage for Analyser (empty-input guards), Statistic (percentage and row formatting) and Doer
    /// (time formatting). Only console output and in-memory tag lists are used: no real library, no
    /// Constants.AudioFolderPath. Analyser's non-empty path reads the MP3 folder, so it needs a seam first.
    /// </summary>
    internal static class AnalyserCoreTests
    {
        // Captures console output with the invariant culture (percentages and times use the current culture).
        private static string Capture(Action action)
        {
            TextWriter previousOut = Console.Out;
            CultureInfo previousCulture = Thread.CurrentThread.CurrentCulture;
            var capture = new StringWriter();
            try
            {
                Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
                Console.SetOut(capture);
                action();
            }
            finally
            {
                Console.SetOut(previousOut);
                Thread.CurrentThread.CurrentCulture = previousCulture;
            }
            return capture.ToString();
        }

        // relPath, title, artists, album, year, trackNo, genres, length, coverCount, compilation, coverW, coverH
        private static TrackTag TagWithYear(string year) =>
            new TrackTag("\\Artists\\Artist A\\Singles\\Artist A - Song.xml",
                "Song", "Artist A", "Album", year, "1", "Rock", "00:03:25.5000000", "1", "False", "1200", "1200");

        public static void Analyser_EmptyInput_DoesNotThrow()
        {
            // Currently throws ArgumentException from TimeSpan.FromSeconds(NaN) on the average-length line.
            Capture(() => new Analyser(new List<TrackTag>()));
        }

        public static void Analyser_AgeStats_OnlyMissingYears_DoesNotThrow()
        {
            // With no parsable year, ages is empty: Average/First/Last must be guarded.
            var tags = new List<TrackTag> { TagWithYear("Missing") };
            MethodInfo printAgeStats = typeof(Analyser).GetMethod("PrintAgeStats", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.True(printAgeStats != null, "PrintAgeStats must exist for this test to reach it");
            try
            {
                Capture(() => printAgeStats.Invoke(null, new object[] { tags }));
            }
            catch (TargetInvocationException tie)
            {
                throw new Exception("PrintAgeStats threw on empty ages: " + tie.InnerException?.GetType().Name + ": " + tie.InnerException?.Message);
            }
        }

        public static void Statistic_Print_AboveCutoff_WritesRowWithPercentage()
        {
            // 1 of 4 items is 25.00%; cutoff 10 is below it, so the row is written.
            string output = Capture(() => new Statistic("Rock", 1, 4).Print(3, 10.0));
            Assert.Equal("| 3 | 25.00% | Rock | 1 |", output.TrimEnd('\r', '\n'), "data row format");
        }

        public static void Statistic_Print_AtOrBelowCutoff_WritesNothing()
        {
            // The cutoff is strict (cutoff < percentage): exactly 25% against a cutoff of 25 prints nothing.
            string output = Capture(() => new Statistic("Rock", 1, 4).Print(1, 25.0));
            Assert.Equal("", output, "statistic at the cutoff must be skipped");
        }

        public static void Statistic_PrintColumns_Header_WritesHeaderAndSeparator()
        {
            string output = Capture(() => Statistic.PrintColumns("#", "%", "Artists", "Occurrences", isHeader: true));
            string[] lines = output.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
            Assert.True(lines.Length == 2, "header prints a row and a separator");
            Assert.Equal("| # | % | Artists | Occurrences |", lines[0], "header row");
            Assert.Equal("|---|---|---------|----------|", lines[1], "separator row");
        }

        public static void Statistic_PrintColumns_DataRow_WritesSingleLine()
        {
            string output = Capture(() => Statistic.PrintColumns("1", "50.00%", "Rock", "2"));
            Assert.Equal("| 1 | 50.00% | Rock | 2 |", output.TrimEnd('\r', '\n'), "data row only, no separator");
        }

        public static void Doer_ConvertTimeSpanToString_RoundsToThreeDecimals()
        {
            string text = Doer.ConvertTimeSpanToString(TimeSpan.FromSeconds(1.23456));
            Assert.Equal("1.235 seconds", text, "seconds rounded to 3 decimal places");
        }

        public static void Doer_ConvertTimeSpanToString_Zero_IsZeroSeconds()
        {
            Assert.Equal("0 seconds", Doer.ConvertTimeSpanToString(TimeSpan.Zero), "zero span");
        }

        public static void Doer_PrintTotalTimeTaken_PrintsLabelAndDuration()
        {
            string output = Capture(() => Doer.PrintTotalTimeTaken());
            Assert.True(output.Contains("Total time taken: ") && output.Contains(" seconds"),
                "total line must carry the label and a seconds value, got: " + output);
        }
    }
}
