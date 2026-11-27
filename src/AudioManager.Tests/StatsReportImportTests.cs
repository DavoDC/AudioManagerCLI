using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AudioManager
{
    /// <summary>
    /// Tests for analysis --import-report (StatsReportImport): one old AudioReport.txt becomes a
    /// stats-&lt;date&gt;-report.json snapshot under the history folder. The golden excerpts are tiny, hand-written
    /// and use fake names, one per report era: A (artists, genre, year), B (adds decade), C (adds the
    /// Excluding Musivation and All artist tables, rank-prefixed rows). No real report is read.
    /// </summary>
    internal static class StatsReportImportTests
    {
        private const string EraA = @"###### Audio Mirror ######

Parsing audio metadata...
 - Tags parsed: 12
 - Time taken: 1.276 seconds

Analysing tags...

# Artists Statistics
%          Artists                                  Occurrences
50.00%     Fake Artist One                          6
25.00%     Fake Artist Two Plus                     3
8.33%      2Pac Example                             1

# Genre Statistics
%          Genre                                    Occurrences
50.00%     Test Genre                               6
25.00%     Other Genre                              3

# Year Statistics
%          Year                                     Occurrences
50.00%     2020                                     6
25.00%     2019                                     3

 - Time taken: 0.027 seconds
";

        private const string EraB = @"###### Audio Mirror ######

Parsing audio metadata...
 - Tags parsed: 12
 - Time taken: 1.276 seconds

Analysing tags...

# Artists Statistics
%          Artists                                  Occurrences
50.00%     Fake Artist One                          6
25.00%     Fake Artist Two Plus                     3
8.33%      2Pac Example                             1

# Genre Statistics
%          Genre                                    Occurrences
50.00%     Test Genre                               6
25.00%     Other Genre                              3

# Year Statistics
%          Year                                     Occurrences
50.00%     2020                                     6
25.00%     2019                                     3

# Decade Statistics
%          Decade                                   Occurrences
50.00%     2020s                                    6
25.00%     2010s                                    3

 - Time taken: 0.027 seconds
";

        private const string EraC = @"###### Audio Mirror ######

Parsing audio metadata...
 - Tags parsed: 12
 - Time taken: 1.276 seconds

Analysing tags...

# Artists Statistics (Excluding Musivation)
#     %          Artists                             Occurrences
1     50.00%     Fake Artist One                     6
2     25.00%     Fake Artist Two Plus                3
3     8.33%      2Pac Example                        1

# Artists Statistics (All)
#     %          Artists                             Occurrences
1     50.00%     Fake Artist One                     6
2     25.00%     Fake Artist Two Plus                3
3     8.33%      2Pac Example                        1
4     8.33%      Musivation Only Act                 1

# Genre Statistics
#     %          Genre                               Occurrences
1     50.00%     Test Genre                          6
2     25.00%     Other Genre                         3

# Year Statistics
#     %          Year                                Occurrences
1     50.00%     2020                                6
2     25.00%     2019                                3

# Decade Statistics
#     %          Decade                              Occurrences
1     50.00%     2020s                               6
2     25.00%     2010s                               3

 - Time taken: 0.027 seconds
";

        private static string[] Lines(string text) => text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);

        private static string NewTempDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), "ImportReportTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static string WriteReport(string dir, string fileName, string text)
        {
            string path = Path.Combine(dir, fileName);
            File.WriteAllText(path, text);
            return path;
        }

        private static string CaptureConsole(Action action)
        {
            var capture = new StringWriter();
            TextWriter original = Console.Out;
            Console.SetOut(capture);
            try { action(); }
            finally { Console.SetOut(original); }
            return capture.ToString();
        }

        // ---- parsing: the three eras ----

        public static void Parse_EraA_ReadsTrackCountArtistsGenreAndYear()
        {
            StatsReportImport.Parsed p;
            string error;
            Assert.True(StatsReportImport.TryParse(Lines(EraA), out p, out error), "era A parses: " + error);
            Assert.Equal("12", p.TrackCount.ToString(), "trackCount from ' - Tags parsed:'");
            Assert.Equal("3", p.Artists.Count.ToString(), "artist rows");
            Assert.Equal("Fake Artist One", p.Artists[0].Key, "first artist name");
            Assert.Equal("6", p.Artists[0].Value.ToString(), "first artist count");
            Assert.Equal("2Pac Example", p.Artists[2].Key, "digit-leading name kept whole");
            Assert.Equal("1", p.Artists[2].Value.ToString(), "digit-leading name count");
            Assert.Equal("2", p.Genre.Count.ToString(), "genre rows");
            Assert.Equal("Other Genre", p.Genre[1].Key, "second genre name");
            Assert.Equal("2", p.Year.Count.ToString(), "year rows");
            Assert.Equal("2020", p.Year[0].Key, "year label kept as text");
            Assert.Equal("0", p.Decade.Count.ToString(), "era A has no decade table");
            Assert.Equal("0", p.ArtistsExcl.Count.ToString(), "era A has no excluding-Musivation table");
        }

        public static void Parse_EraB_AddsDecadeTable()
        {
            StatsReportImport.Parsed p;
            string error;
            Assert.True(StatsReportImport.TryParse(Lines(EraB), out p, out error), "era B parses: " + error);
            Assert.Equal("2", p.Decade.Count.ToString(), "decade rows");
            Assert.Equal("2020s", p.Decade[0].Key, "decade label");
            Assert.Equal("6", p.Decade[0].Value.ToString(), "decade count");
            Assert.Equal("3", p.Artists.Count.ToString(), "artist rows unchanged");
        }

        public static void Parse_EraC_ReadsRankedRowsAndBothArtistTables()
        {
            StatsReportImport.Parsed p;
            string error;
            Assert.True(StatsReportImport.TryParse(Lines(EraC), out p, out error), "era C parses: " + error);
            Assert.Equal("12", p.TrackCount.ToString(), "trackCount");
            Assert.Equal("4", p.Artists.Count.ToString(), "All table rows");
            Assert.Equal("Musivation Only Act", p.Artists[3].Key, "All table keeps the Musivation-only artist");
            Assert.Equal("3", p.ArtistsExcl.Count.ToString(), "Excluding Musivation rows");
            Assert.Equal("Fake Artist One", p.ArtistsExcl[0].Key, "rank prefix not part of the name");
            Assert.Equal("6", p.ArtistsExcl[0].Value.ToString(), "rank-prefixed count");
            Assert.Equal("2", p.Genre.Count.ToString(), "genre rows");
            Assert.Equal("2", p.Decade.Count.ToString(), "decade rows");
        }

        public static void Parse_MissingTagsLine_IsRefusedNamingTheLine()
        {
            string text = EraA.Replace(" - Tags parsed: 12", "");
            StatsReportImport.Parsed p;
            string error;
            Assert.True(!StatsReportImport.TryParse(Lines(text), out p, out error), "no trackCount is an error");
            Assert.True(error.Contains("Tags parsed"), "error names the missing line");
        }

        public static void Parse_MissingGenreTable_IsRefused()
        {
            int start = EraA.IndexOf("# Genre Statistics", StringComparison.Ordinal);
            int end = EraA.IndexOf("# Year Statistics", StringComparison.Ordinal);
            string text = EraA.Remove(start, end - start);
            StatsReportImport.Parsed p;
            string error;
            Assert.True(!StatsReportImport.TryParse(Lines(text), out p, out error), "a report without a genre table is refused");
            Assert.True(error.Contains("Genre"), "error names the missing table");
        }

        // ---- snapshot document ----

        public static void Build_CarriesReportFlagsAndSameKeysAsLive()
        {
            StatsReportImport.Parsed p;
            string error;
            StatsReportImport.TryParse(Lines(EraC), out p, out error);
            string json = StatsReportImport.Build(p, "2025-01-02", new DateTime(2026, 11, 28, 9, 0, 0));
            Assert.True(json.Contains("\"schemaVersion\": 1"), "schema version 1");
            Assert.True(json.Contains("\"asOf\": {\"date\": \"2025-01-02\", \"commit\": \"report\"}"), "asOf date from filename, commit report");
            Assert.True(json.Contains("\"sizeKnown\": false"), "sizeKnown false");
            Assert.True(json.Contains("\"coverKnown\": false"), "coverKnown false");
            Assert.True(json.Contains("\"lyricsKnown\": false"), "lyricsKnown false");
            Assert.True(json.Contains("\"lengthKnown\": false"), "lengthKnown false");
            Assert.True(json.Contains("\"trackCount\": 12"), "trackCount in summary");
            Assert.True(json.Contains("\"artistCount\": 4"), "artistCount from the All table size");
            Assert.True(json.Contains("\"genreCount\": 2"), "genreCount from the genre table size");
            Assert.True(json.Contains("{\"label\": \"Fake Artist One\", \"count\": 6}"), "artist distribution row");
            Assert.True(json.Contains("\"exclMusivation\": ["), "exclMusivation section present");
            Assert.True(json.Contains("\"ageStats\": {"), "ageStats section kept for the same shape");
            Assert.True(json.Contains("\"lyricsCoverage\": {"), "lyricsCoverage section kept for the same shape");
        }

        // ---- entry point ----

        public static void TryRun_NoImportFlag_IsNotHandled()
        {
            int code = -1;
            bool handled = StatsReportImport.TryRun(new[] { "analysis", "--as-of", "2025-01-02" }, NewTempDir(), out code);
            Assert.True(!handled, "as-of args are left to StatsAsOf");
        }

        public static void TryRun_ImportWritesStatsFileNamedByFilenameDate()
        {
            string dir = NewTempDir();
            string history = Path.Combine(dir, "history");
            string report = WriteReport(dir, "2024-11-30-AudioReport.txt", EraB);
            int code = -1;
            string output = CaptureConsole(() => StatsReportImport.TryRun(new[] { "analysis", "--import-report", report }, history, out code));
            Assert.Equal("0", code.ToString(), "exit 0");
            string expected = Path.Combine(history, "stats-2024-11-30-report.json");
            Assert.True(File.Exists(expected), "snapshot named stats-<date>-report.json");
            Assert.True(File.ReadAllText(expected).Contains("\"trackCount\": 12"), "trackCount written");
            Assert.True(output.Contains("12 tracks"), "console reports the track count");
        }

        public static void TryRun_RerunOverwritesTheSameFile()
        {
            string dir = NewTempDir();
            string history = Path.Combine(dir, "history");
            string report = WriteReport(dir, "2024-11-30-AudioReport.txt", EraB);
            int code = -1;
            CaptureConsole(() => StatsReportImport.TryRun(new[] { "analysis", "--import-report", report }, history, out code));
            CaptureConsole(() => StatsReportImport.TryRun(new[] { "analysis", "--import-report", report }, history, out code));
            Assert.Equal("0", code.ToString(), "second run exit 0");
            Assert.Equal("1", Directory.GetFiles(history).Length.ToString(), "rerun leaves one file, not a copy");
        }

        public static void TryRun_FilenameWithoutDate_IsRefusedBeforeWriting()
        {
            string dir = NewTempDir();
            string history = Path.Combine(dir, "history");
            string report = WriteReport(dir, "AudioReport.txt", EraA);
            int code = -1;
            string output = CaptureConsole(() => StatsReportImport.TryRun(new[] { "analysis", "--import-report", report }, history, out code));
            Assert.Equal("1", code.ToString(), "exit 1");
            Assert.True(output.Contains("ERROR"), "refusal printed");
            Assert.True(!Directory.Exists(history), "nothing written");
        }

        public static void TryRun_MissingFile_IsRefused()
        {
            string dir = NewTempDir();
            int code = -1;
            string output = CaptureConsole(() => StatsReportImport.TryRun(
                new[] { "analysis", "--import-report", Path.Combine(dir, "2024-11-30-AudioReport.txt") }, Path.Combine(dir, "history"), out code));
            Assert.Equal("1", code.ToString(), "missing file exit 1");
            Assert.True(output.Contains("ERROR"), "refusal printed");
        }

        public static void TryRun_ImportWithoutPath_IsRefused()
        {
            int code = -1;
            string output = CaptureConsole(() => StatsReportImport.TryRun(new[] { "analysis", "--import-report" }, NewTempDir(), out code));
            Assert.Equal("1", code.ToString(), "no path exit 1");
            Assert.True(output.Contains("--import-report requires"), "message names the missing path");
        }

        public static void TryRun_ExtraFlag_IsRefusedNotIgnored()
        {
            string dir = NewTempDir();
            string report = WriteReport(dir, "2024-11-30-AudioReport.txt", EraA);
            int code = -1;
            string output = CaptureConsole(() => StatsReportImport.TryRun(
                new[] { "analysis", "--import-report", report, "--dry-run" }, Path.Combine(dir, "history"), out code));
            Assert.Equal("1", code.ToString(), "unknown combination exit 1");
            Assert.True(output.Contains("not valid with --import-report"), "refusal names the combination");
        }
    }
}
