using System;
using System.Collections.Generic;
using System.IO;
using AudioManager.Code.Modules;

namespace AudioManager
{
    /// <summary>
    /// Data-safety guard for the D decision in ExecuteDuplicateDecisions. A NewMusic file that cannot be
    /// sent to the Recycle Bin (read-only here) must be reported as an error, must not abort the batch, and the
    /// next duplicate decision must still run and write its record. The D decision sends the file through
    /// RecycleBin.Sender; every test here replaces that seam with a fake so no real file reaches the user's
    /// Recycle Bin. Uses the test-only MusicIntegrator(testLibraryPath, dryRun) constructor and real temp files
    /// only - never touches the real Constants.AudioFolderPath library.
    /// </summary>
    internal static class MusicIntegratorDeleteGuardTests
    {
        /// <summary>A D decision is only sent when its library duplicate exists, so every D fixture writes one.</summary>
        private static string WriteLibraryCopy(string lib, string fileName)
        {
            string dir = Path.Combine(lib, "Old Place");
            Directory.CreateDirectory(dir);
            string libPath = Path.Combine(dir, fileName);
            File.WriteAllBytes(libPath, new byte[] { 1, 2, 3 });
            return libPath;
        }

        private static MusicIntegrator.ScannedFile MakeDeleteDuplicate(string sourcePath, string artist, string title, string libPath = null)
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
                    LibraryFilePath = libPath,
                    RelLibraryPath = libPath == null ? null : "Old Place/" + Path.GetFileName(libPath),
                    RelNewPath = Path.GetFileName(sourcePath)
                }
            };
        }

        /// <summary>
        /// Runs ExecuteDuplicateDecisions (real run) over a read-only D file followed by a normal D file.
        /// The fake sender stands in for the Recycle Bin: it records each path, refuses read-only files, and
        /// otherwise removes the file the way the bin would. The first send must fail as an error, and the
        /// second must still be sent and logged.
        /// </summary>
        public static void DeleteFailure_ReportsErrorAndContinuesBatch()
        {
            string lib = RoutingFixtures.CreateLibraryFixture();
            string newMusic = Path.Combine(Path.GetTempPath(), "AudioManagerTest_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(newMusic);
            string lockedPath = Path.Combine(newMusic, "Locked Artist - Locked Song.mp3");
            string okPath = Path.Combine(newMusic, "Ok Artist - Ok Song.mp3");
            var sent = new List<string>();
            Action<string> original = RecycleBin.Sender;
            try
            {
                RecycleBin.Sender = path =>
                {
                    sent.Add(path);
                    if ((File.GetAttributes(path) & FileAttributes.ReadOnly) != 0)
                        throw new IOException("fake recycle refused read-only file");
                    File.Delete(path);
                };
                var locked = MakeDeleteDuplicate(lockedPath, "Locked Artist", "Locked Song", WriteLibraryCopy(lib, "Locked Artist - Locked Song.mp3"));
                var ok = MakeDeleteDuplicate(okPath, "Ok Artist", "Ok Song", WriteLibraryCopy(lib, "Ok Artist - Ok Song.mp3"));
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
                Assert.True(!File.Exists(okPath), "the second D file must be sent to the Recycle Bin");
                Assert.Equal("2", logEntries.Count.ToString(), "both D decisions must write an execution record");
                Assert.Equal("2", skippedCount.ToString(), "both D decisions must be counted");
                Assert.Equal("2", sent.Count.ToString(), "both D files must be handed to the Recycle Bin sender");
            }
            finally
            {
                RecycleBin.Sender = original;
                if (File.Exists(lockedPath)) File.SetAttributes(lockedPath, FileAttributes.Normal);
                RoutingFixtures.Cleanup(lib);
                if (Directory.Exists(newMusic)) Directory.Delete(newMusic, recursive: true);
            }
        }

        /// <summary>
        /// A D decision must hand the NewMusic source path to RecycleBin.Sender and must not delete it itself.
        /// The fake only records, so the file is still on disk afterwards: that proves File.Delete was not used.
        /// </summary>
        public static void DuplicateD_SendsSourceToRecycleBinNotDelete()
        {
            string lib = RoutingFixtures.CreateLibraryFixture();
            string newMusic = Path.Combine(Path.GetTempPath(), "AudioManagerTest_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(newMusic);
            string srcPath = Path.Combine(newMusic, "Bin Artist - Bin Song.mp3");
            var sent = new List<string>();
            Action<string> original = RecycleBin.Sender;
            try
            {
                RecycleBin.Sender = path => sent.Add(path);
                var sf = MakeDeleteDuplicate(srcPath, "Bin Artist", "Bin Song", WriteLibraryCopy(lib, "Bin Artist - Bin Song.mp3"));
                var scannedFiles = new List<MusicIntegrator.ScannedFile> { sf };
                var logEntries = new List<MusicIntegrator.LogEntry>();
                int skippedCount = 0;
                var integrator = new MusicIntegrator(lib, dryRun: false);

                integrator.ExecuteDuplicateDecisions(scannedFiles, new HashSet<string>(), logEntries, ref skippedCount);

                Assert.Equal("1", sent.Count.ToString(), "a D decision must send exactly one file to the Recycle Bin");
                Assert.Equal(srcPath, sent[0], "the Recycle Bin sender must receive the NewMusic source path");
                Assert.True(File.Exists(srcPath), "the D branch must not File.Delete the source itself");
                Assert.Equal("deleted", sf.LogEntry.Status, "a successful send is logged with the existing deleted status");
                Assert.Equal("1", skippedCount.ToString(), "a sent D file must be counted as skipped");
            }
            finally
            {
                RecycleBin.Sender = original;
                RoutingFixtures.Cleanup(lib);
                if (File.Exists(srcPath)) File.Delete(srcPath);
                if (Directory.Exists(newMusic)) Directory.Delete(newMusic, recursive: true);
            }
        }

        /// <summary>
        /// When the Recycle Bin send throws, the D entry must be an error, the source must stay in NewMusic,
        /// the batch must continue, and the failure must be counted and logged like the delete-failure case.
        /// </summary>
        public static void DuplicateD_RecycleBinSendFails_ReportsErrorAndKeepsSource()
        {
            string lib = RoutingFixtures.CreateLibraryFixture();
            string newMusic = Path.Combine(Path.GetTempPath(), "AudioManagerTest_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(newMusic);
            string srcPath = Path.Combine(newMusic, "Fail Artist - Fail Song.mp3");
            Action<string> original = RecycleBin.Sender;
            try
            {
                RecycleBin.Sender = path => throw new IOException("fake recycle bin unavailable");
                var sf = MakeDeleteDuplicate(srcPath, "Fail Artist", "Fail Song", WriteLibraryCopy(lib, "Fail Artist - Fail Song.mp3"));
                var scannedFiles = new List<MusicIntegrator.ScannedFile> { sf };
                var logEntries = new List<MusicIntegrator.LogEntry>();
                int skippedCount = 0;
                var integrator = new MusicIntegrator(lib, dryRun: false);

                bool completed = integrator.ExecuteDuplicateDecisions(scannedFiles, new HashSet<string>(), logEntries, ref skippedCount);

                Assert.True(completed, "run must return normally after a Recycle Bin send failure");
                Assert.Equal("error", sf.LogEntry.Status, "a failed Recycle Bin send must be logged as an error");
                Assert.True(sf.LogEntry.Detail.StartsWith("duplicate (delete failed", StringComparison.Ordinal),
                    "error detail must keep the delete-failed wording, got: " + sf.LogEntry.Detail);
                Assert.True(File.Exists(srcPath), "the source must stay in NewMusic when the send fails");
                Assert.Equal("1", logEntries.Count.ToString(), "the failed D decision must write an execution record");
                Assert.Equal("1", skippedCount.ToString(), "the failed D decision must be counted");
            }
            finally
            {
                RecycleBin.Sender = original;
                RoutingFixtures.Cleanup(lib);
                if (File.Exists(srcPath)) File.Delete(srcPath);
                if (Directory.Exists(newMusic)) Directory.Delete(newMusic, recursive: true);
            }
        }

        /// <summary>
        /// A D decision whose library duplicate is gone (removed, or staged aside by an L decision for another
        /// source in the same run) must not send the NewMusic file to the Recycle Bin: it would be the only copy.
        /// The decision is kept as an error, the source stays in NewMusic, and the batch continues.
        /// </summary>
        public static void DuplicateD_LibraryCopyMissing_KeepsSourceAndDoesNotRecycle()
        {
            string lib = RoutingFixtures.CreateLibraryFixture();
            string newMusic = Path.Combine(Path.GetTempPath(), "AudioManagerTest_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(newMusic);
            string srcPath = Path.Combine(newMusic, "Gone Artist - Gone Song.mp3");
            string libPath = Path.Combine(lib, "Old Place", "Gone Artist - Gone Song.mp3");
            var sent = new List<string>();
            Action<string> original = RecycleBin.Sender;
            try
            {
                // Recording only: if the bug is present the file is "sent" here and stays on disk, so the
                // assertions below fail on the send count rather than on a missing file.
                RecycleBin.Sender = path => sent.Add(path);
                var sf = MakeDeleteDuplicate(srcPath, "Gone Artist", "Gone Song", libPath);
                var scannedFiles = new List<MusicIntegrator.ScannedFile> { sf };
                var logEntries = new List<MusicIntegrator.LogEntry>();
                int skippedCount = 0;
                var integrator = new MusicIntegrator(lib, dryRun: false);

                // Library copy never existed on disk at libPath (the removed or staged case).
                bool completed = integrator.ExecuteDuplicateDecisions(scannedFiles, new HashSet<string>(), logEntries, ref skippedCount);

                Assert.True(completed, "run must return normally when the library copy is missing");
                Assert.Equal("0", sent.Count.ToString(), "a D decision with no library copy must not reach the Recycle Bin");
                Assert.True(File.Exists(srcPath), "the NewMusic file is the only copy and must stay in place");
                Assert.Equal("error", sf.LogEntry.Status, "a kept D file must be logged as an error");
                Assert.Equal("duplicate (kept: library copy missing)", sf.LogEntry.Detail, "kept D detail must name the missing library copy");
                Assert.Equal("1", logEntries.Count.ToString(), "the kept D decision must write an execution record");
                Assert.Equal("1", skippedCount.ToString(), "the kept D decision must be counted like the failure branch");
                Assert.True(sf.Duplicate.SkipRouting, "the kept D file must not be routed");
            }
            finally
            {
                RecycleBin.Sender = original;
                RoutingFixtures.Cleanup(lib);
                if (File.Exists(srcPath)) File.Delete(srcPath);
                if (Directory.Exists(newMusic)) Directory.Delete(newMusic, recursive: true);
            }
        }
    }
}
