using System;
using System.Collections.Generic;
using System.IO;

namespace AudioManager
{
    /// <summary>
    /// Pins the exact console line formats a client parses. If one of these fails, the
    /// client-side parsers must change in the same commit.
    /// </summary>
    internal static class MusicIntegratorConsoleFormatTests
    {
        public static void AutoLine_IsArtistsDashTitleThenAlbumSuffix()
        {
            Assert.Equal("[AUTO] A; B - Song [Album]", MusicIntegrator.FormatAutoLine("A; B", "Song", " [Album]"), "moved line format");
            Assert.Equal("[AUTO] A - Song", MusicIntegrator.FormatAutoLine("A", "Song", ""), "no album suffix");
        }

        public static void SkipLine_IsArtistsDashTitleThenReason()
        {
            Assert.Equal("[SKIP] A - Song: already exists at destination", MusicIntegrator.FormatSkipLine("A", "Song"), "skip line format");
        }

        public static void ExecutionJsonLine_CarriesMarkerAndPath()
        {
            Assert.Equal("\n  EXECUTION JSON: C:/data/logs/execution-20260101-000000.json",
                MusicIntegrator.FormatExecutionJsonLine("C:/data/logs/execution-20260101-000000.json"), "marker line format");
        }

        // Pins the printed confidence block as it is today. Runs on a temp library path with no filesystem
        // reads: a dry run skips the sanity pass, and the real-run case has no moved entries, so
        // Constants.AudioFolderPath is never read.
        private static string CaptureConfidence(bool dryRun, List<MusicIntegrator.LogEntry> entries, int totalFiles, int movedCount,
            int skippedCount, out MusicIntegrator.ConfidenceRecord record)
        {
            string dir = Path.Combine(Path.GetTempPath(), "am_confidence_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            var originalOut = Console.Out;
            var capture = new StringWriter();
            MusicIntegrator.ConfidenceRecord result = null;
            try
            {
                Console.SetOut(capture);
                var integrator = new MusicIntegrator(dir, dryRun);
                result = integrator.PrintConfidenceReport(entries, totalFiles, movedCount, skippedCount);
            }
            finally
            {
                Console.SetOut(originalOut);
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            }
            record = result;
            return capture.ToString();
        }

        public static void PrintConfidenceReport_CleanDryRun_PrintsDryRunBlockAndNoErrors()
        {
            var entries = new List<MusicIntegrator.LogEntry>
            {
                new MusicIntegrator.LogEntry { Filename = "A.mp3", Status = "would-move", Destination = "Artists\\X\\A.mp3" },
                new MusicIntegrator.LogEntry { Filename = "B.mp3", Status = "would-move", Destination = "Artists\\Y\\B.mp3" }
            };

            string output = CaptureConfidence(true, entries, 2, 2, 0, out MusicIntegrator.ConfidenceRecord record);

            Assert.True(output.IndexOf("  CONFIDENCE REPORT (Dry Run)", StringComparison.Ordinal) >= 0, "dry run header");
            Assert.True(output.IndexOf("  Files in NewMusic: 2  |  Moved: 2  |  Skipped: 0", StringComparison.Ordinal) >= 0, "count line");
            Assert.True(output.IndexOf("  [WOULD-MOVE] A.mp3\n    -> Artists\\X\\A.mp3", StringComparison.Ordinal) >= 0, "per-file row with destination");
            Assert.True(output.IndexOf("  [WOULD-MOVE] B.mp3\n    -> Artists\\Y\\B.mp3", StringComparison.Ordinal) >= 0, "second per-file row");
            Assert.True(output.IndexOf("No errors.", StringComparison.Ordinal) >= 0, "clean run prints No errors.");
            Assert.True(output.IndexOf("[ERROR", StringComparison.Ordinal) < 0, "clean dry run prints no ERROR line");
            Assert.True(output.IndexOf("Sanity check", StringComparison.Ordinal) < 0, "a dry run never prints a sanity line");
            Assert.True(record.CountOk, "a dry run always reports a passing count check");
            Assert.Equal("2", record.ExpectedMoved.ToString(), "expectedMoved is total minus skipped");
            Assert.Equal("False", record.SanityRan.ToString(), "a dry run does not run the sanity pass");
            Assert.Equal("0", record.Errors.Count.ToString(), "clean run records no errors");
        }

        public static void PrintConfidenceReport_RealRunWithFailures_PrintsCountMismatchAndErrorSummary()
        {
            var entries = new List<MusicIntegrator.LogEntry>
            {
                new MusicIntegrator.LogEntry { Filename = "B.mp3", Status = "error", Detail = "boom" }
            };

            string output = CaptureConfidence(false, entries, 2, 0, 0, out MusicIntegrator.ConfidenceRecord record);

            Assert.True(output.IndexOf("  CONFIDENCE REPORT" + Environment.NewLine, StringComparison.Ordinal) >= 0, "real run header has no Dry Run suffix");
            Assert.True(output.IndexOf("  Files in NewMusic: 2  |  Moved: 0  |  Skipped: 0", StringComparison.Ordinal) >= 0, "count line");
            Assert.True(output.IndexOf("  [ERROR] Count mismatch! Expected 2 moved, got 0.", StringComparison.Ordinal) >= 0, "count mismatch line");
            Assert.True(output.IndexOf("  [ERROR] B.mp3 (boom)", StringComparison.Ordinal) >= 0, "per-file error row");
            Assert.True(output.IndexOf("[ERRORS: 1]", StringComparison.Ordinal) >= 0, "error summary count");
            Assert.True(output.IndexOf("- B.mp3: boom", StringComparison.Ordinal) >= 0, "error summary row");
            Assert.True(output.IndexOf("No errors.", StringComparison.Ordinal) < 0, "a failed run must not print No errors.");
            Assert.True(output.IndexOf("Sanity check", StringComparison.Ordinal) < 0, "no moved entries means no sanity line");
            Assert.True(!record.CountOk, "a real run with a count mismatch must record CountOk false");
            Assert.Equal("2", record.ExpectedMoved.ToString(), "expectedMoved is total minus skipped");
            Assert.Equal("True", record.SanityRan.ToString(), "a real run runs the sanity pass");
            Assert.Equal("1", record.Errors.Count.ToString(), "one error entry is recorded");
        }
    }
}
