using System;
using System.Collections.Generic;
using System.IO;
using AudioManager.Code.Modules;

namespace AudioManager
{
    /// <summary>
    /// Data-safety guard for the D decision in ExecuteDuplicateDecisions. A NewMusic file that cannot be
    /// deleted (read-only here) must be reported as an error, must not abort the batch, and the next
    /// duplicate decision must still run and write its record. Uses the test-only
    /// MusicIntegrator(testLibraryPath, dryRun) constructor and real temp files only - never touches the
    /// real Constants.AudioFolderPath library.
    /// </summary>
    internal static class MusicIntegratorDeleteGuardTests
    {
        private static MusicIntegrator.ScannedFile MakeDeleteDuplicate(string sourcePath, string artist, string title)
        {
            File.WriteAllBytes(sourcePath, new byte[] { 3 });
            var track = new Track { Artists = artist, Title = title, Album = "Missing", Genres = "Missing" };
            return new MusicIntegrator.ScannedFile
            {
                SourcePath = sourcePath,
                Track = track,
                IsReadable = true,
                LogEntry = new MusicIntegrator.LogEntry { Filename = Path.GetFileName(sourcePath), Title = title, Artists = artist },
                Duplicate = new MusicIntegrator.DupData
                {
                    Decision = 'D',
                    RelNewPath = Path.GetFileName(sourcePath)
                }
            };
        }

        /// <summary>
        /// Runs ExecuteDuplicateDecisions (real run) over a read-only D file followed by a normal D file.
        /// The first delete must fail as an error, and the second must still be deleted and logged.
        /// </summary>
        public static void DeleteFailure_ReportsErrorAndContinuesBatch()
        {
            string lib = RoutingFixtures.CreateLibraryFixture();
            string newMusic = Path.Combine(Path.GetTempPath(), "AudioManagerTest_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(newMusic);
            string lockedPath = Path.Combine(newMusic, "Locked Artist - Locked Song.mp3");
            string okPath = Path.Combine(newMusic, "Ok Artist - Ok Song.mp3");
            try
            {
                var locked = MakeDeleteDuplicate(lockedPath, "Locked Artist", "Locked Song");
                var ok = MakeDeleteDuplicate(okPath, "Ok Artist", "Ok Song");
                File.SetAttributes(lockedPath, FileAttributes.ReadOnly);

                var scannedFiles = new List<MusicIntegrator.ScannedFile> { locked, ok };
                var logEntries = new List<MusicIntegrator.LogEntry>();
                int skippedCount = 0;
                var integrator = new MusicIntegrator(lib, dryRun: false);

                bool completed = integrator.ExecuteDuplicateDecisions(scannedFiles, new HashSet<string>(), logEntries, ref skippedCount);

                Assert.True(completed, "run must return normally after a delete failure");
                Assert.Equal("error", locked.LogEntry.Status, "a locked D file must be logged as an error");
                Assert.True(locked.LogEntry.Detail.StartsWith("duplicate (delete failed", StringComparison.Ordinal),
                    "error detail must name the delete failure, got: " + locked.LogEntry.Detail);
                Assert.True(File.Exists(lockedPath), "the locked file must stay in NewMusic");
                Assert.Equal("deleted", ok.LogEntry.Status, "the decision after a failed delete must still run");
                Assert.True(!File.Exists(okPath), "the second D file must be deleted");
                Assert.Equal("2", logEntries.Count.ToString(), "both D decisions must write an execution record");
                Assert.Equal("2", skippedCount.ToString(), "both D decisions must be counted");
            }
            finally
            {
                if (File.Exists(lockedPath)) File.SetAttributes(lockedPath, FileAttributes.Normal);
                RoutingFixtures.Cleanup(lib);
                if (Directory.Exists(newMusic)) Directory.Delete(newMusic, recursive: true);
            }
        }
    }
}
