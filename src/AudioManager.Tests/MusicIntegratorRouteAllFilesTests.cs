using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AudioManager.Code.Modules;

namespace AudioManager
{
    /// <summary>
    /// Regression coverage for RouteAllFiles's "one bad file must not abort the whole batch"
    /// behavior (previously it threw and halted).
    /// Uses the test-only MusicIntegrator(testLibraryPath) constructor and real temp
    /// files/dirs - never touches the real Constants.AudioFolderPath library.
    /// </summary>
    internal static class MusicIntegratorRouteAllFilesTests
    {
        private static MusicIntegrator.ScannedFile MakeGoodFile(string sourcePath, string artist, string title)
        {
            File.WriteAllBytes(sourcePath, new byte[0]);
            var track = new Track { Artists = artist, Title = title, Album = "Missing", Genres = "Missing" };
            return new MusicIntegrator.ScannedFile
            {
                SourcePath = sourcePath,
                Track = track,
                IsReadable = true,
                LogEntry = new MusicIntegrator.LogEntry { Filename = Path.GetFileName(sourcePath), Title = title, Artists = artist }
            };
        }

        public static void RouteAllFiles_UnreadableFile_IsSkippedNotThrown_RestOfBatchStillRoutes()
        {
            string lib = RoutingFixtures.CreateLibraryFixture();
            string newMusic = Path.Combine(Path.GetTempPath(), "AudioManagerTest_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(newMusic);
            try
            {
                var integrator = new MusicIntegrator(lib);

                string goodPath = Path.Combine(newMusic, "Good Artist - Good Song.mp3");
                var good = MakeGoodFile(goodPath, "Good Artist", "Good Song");

                string badPath = Path.Combine(newMusic, "Bad Artist - Bad Song.mp3");
                var bad = new MusicIntegrator.ScannedFile
                {
                    SourcePath = badPath,
                    IsReadable = false,
                    ReadError = "simulated unreadable tag data",
                    LogEntry = new MusicIntegrator.LogEntry { Filename = Path.GetFileName(badPath) }
                };

                var scannedFiles = new List<MusicIntegrator.ScannedFile> { bad, good };
                var logEntries = new List<MusicIntegrator.LogEntry>();
                int moved = 0, skipped = 0;

                integrator.RouteAllFiles(scannedFiles, new HashSet<string>(), logEntries, ref moved, ref skipped);

                Assert.Equal("1", moved.ToString(), "unreadable file must not block the good file from moving");
                Assert.Equal("1", skipped.ToString(), "unreadable file counts as skipped, not a thrown exception");
                Assert.Equal("error", bad.LogEntry.Status, "unreadable file's log entry must be marked error");
                Assert.True(!File.Exists(goodPath), "good file should have moved out of NewMusic");
                string expectedDest = Path.Combine(lib, Constants.MiscDir, "Good Artist - Good Song.mp3");
                Assert.True(File.Exists(expectedDest), "good file should have arrived at its destination");
            }
            finally
            {
                RoutingFixtures.Cleanup(lib);
                if (Directory.Exists(newMusic)) Directory.Delete(newMusic, recursive: true);
            }
        }

        public static void RouteAllFiles_UnexpectedMoveException_IsSkippedNotThrown_RestOfBatchStillRoutes()
        {
            string lib = RoutingFixtures.CreateLibraryFixture();
            string newMusic = Path.Combine(Path.GetTempPath(), "AudioManagerTest_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(newMusic);
            try
            {
                // "Bad Artist" has no artist folder, so GetDestDir routes it to lib/Miscellaneous Songs.
                // Pre-creating a FILE at that exact path makes Directory.CreateDirectory(destDir) throw
                // a real IOException during routing - a deterministic, realistic move-time failure.
                string miscAsFile = Path.Combine(lib, Constants.MiscDir);
                File.WriteAllBytes(miscAsFile, new byte[0]);

                var integrator = new MusicIntegrator(lib);

                string badPath = Path.Combine(newMusic, "Bad Artist - Bad Song.mp3");
                var bad = MakeGoodFile(badPath, "Bad Artist", "Bad Song");

                var scannedFiles = new List<MusicIntegrator.ScannedFile> { bad };
                var logEntries = new List<MusicIntegrator.LogEntry>();
                int moved = 0, skipped = 0;

                integrator.RouteAllFiles(scannedFiles, new HashSet<string>(), logEntries, ref moved, ref skipped);

                Assert.Equal("0", moved.ToString(), "the failing file itself must not count as moved");
                Assert.Equal("1", skipped.ToString(), "move-time exception must be caught and counted as skipped, not thrown");
                Assert.Equal("error", bad.LogEntry.Status, "move failure must be recorded as an error log entry");
                Assert.True(File.Exists(badPath), "source file must be left in place when the move fails");
            }
            finally
            {
                RoutingFixtures.Cleanup(lib);
                if (Directory.Exists(newMusic)) Directory.Delete(newMusic, recursive: true);
            }
        }

        public static void RouteAllFiles_OneBadFileAmongMany_DoesNotStrandLaterGoodFiles()
        {
            string lib = RoutingFixtures.CreateLibraryFixture();
            string newMusic = Path.Combine(Path.GetTempPath(), "AudioManagerTest_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(newMusic);
            try
            {
                var integrator = new MusicIntegrator(lib);

                var before = MakeGoodFile(Path.Combine(newMusic, "Before - Track.mp3"), "Before", "Track");
                var badPath = Path.Combine(newMusic, "Broken - Track.mp3");
                var bad = new MusicIntegrator.ScannedFile
                {
                    SourcePath = badPath,
                    IsReadable = false,
                    ReadError = "simulated read failure",
                    LogEntry = new MusicIntegrator.LogEntry { Filename = Path.GetFileName(badPath) }
                };
                var after = MakeGoodFile(Path.Combine(newMusic, "After - Track.mp3"), "After", "Track");

                var scannedFiles = new List<MusicIntegrator.ScannedFile> { before, bad, after };
                var logEntries = new List<MusicIntegrator.LogEntry>();
                int moved = 0, skipped = 0;

                integrator.RouteAllFiles(scannedFiles, new HashSet<string>(), logEntries, ref moved, ref skipped);

                // This is the exact bug reported by David: a track positioned AFTER a bad file
                // was previously left NOT RUN because the bad file aborted the whole batch.
                Assert.Equal("2", moved.ToString(), "both the file before and after the bad one must move");
                Assert.Equal("1", skipped.ToString(), "exactly the bad file is skipped");
                Assert.True(logEntries.Any(e => e.Status == "moved" && e.Filename == "After - Track.mp3"),
                    "the file after the bad one must reach 'moved', not be stranded as not-run");
            }
            finally
            {
                RoutingFixtures.Cleanup(lib);
                if (Directory.Exists(newMusic)) Directory.Delete(newMusic, recursive: true);
            }
        }

        private static MusicIntegrator.ScannedFile MakeReplacementDuplicate(string sourcePath, string artist, string title, string libPath)
        {
            var sf = MakeGoodFile(sourcePath, artist, title);
            sf.Duplicate = new MusicIntegrator.DupData
            {
                Decision = 'L',
                LibraryFilePath = libPath,
                RelLibraryPath = "Old Place/" + Path.GetFileName(libPath),
                RelNewPath = Path.GetFileName(sourcePath)
            };
            return sf;
        }

        public static void RouteAllFiles_ReplacementBlocked_RestoresStagedLibraryCopy()
        {
            string lib = RoutingFixtures.CreateLibraryFixture();
            string newMusic = Path.Combine(Path.GetTempPath(), "AudioManagerTest_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(newMusic);
            try
            {
                string libDir = Path.Combine(lib, "Old Place");
                Directory.CreateDirectory(libDir);
                string libPath = Path.Combine(libDir, "Bad Artist - Bad Song.mp3");
                File.WriteAllBytes(libPath, new byte[] { 1, 2, 3 });

                // Block the replacement's destination (see the exception test above) so routing throws
                // after the library copy has been staged by the L decision.
                File.WriteAllBytes(Path.Combine(lib, Constants.MiscDir), new byte[0]);

                var integrator = new MusicIntegrator(lib);
                string badPath = Path.Combine(newMusic, "Bad Artist - Bad Song.mp3");
                var sf = MakeReplacementDuplicate(badPath, "Bad Artist", "Bad Song", libPath);
                var scannedFiles = new List<MusicIntegrator.ScannedFile> { sf };
                var logEntries = new List<MusicIntegrator.LogEntry>();
                int moved = 0, skipped = 0;

                integrator.ExecuteDuplicateDecisions(scannedFiles, new HashSet<string>(), logEntries, ref skipped);
                Assert.True(!File.Exists(libPath), "L decision must stage the library copy out of the library path");

                integrator.RouteAllFiles(scannedFiles, new HashSet<string>(), logEntries, ref moved, ref skipped);

                Assert.Equal("0", moved.ToString(), "blocked replacement must not count as moved");
                Assert.Equal("1", skipped.ToString(), "blocked replacement must be counted as skipped");
                Assert.True(File.Exists(libPath), "failed replacement must restore the original library copy");
                Assert.Equal("3", new FileInfo(libPath).Length.ToString(), "restored library copy must keep its bytes");
                Assert.True(!File.Exists(libPath + ".replaced.bak"), "rollback must consume the staged bak");
                Assert.True(File.Exists(badPath), "replacement source must stay in NewMusic when routing fails");
            }
            finally
            {
                RoutingFixtures.Cleanup(lib);
                if (Directory.Exists(newMusic)) Directory.Delete(newMusic, recursive: true);
            }
        }

        public static void RouteAllFiles_SuccessfulReplacement_LeavesNoBak()
        {
            string lib = RoutingFixtures.CreateLibraryFixture();
            string newMusic = Path.Combine(Path.GetTempPath(), "AudioManagerTest_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(newMusic);
            try
            {
                string libDir = Path.Combine(lib, "Old Place");
                Directory.CreateDirectory(libDir);
                string libPath = Path.Combine(libDir, "Good Artist - Good Song.mp3");
                File.WriteAllBytes(libPath, new byte[] { 1, 2, 3 });

                var integrator = new MusicIntegrator(lib);
                string goodPath = Path.Combine(newMusic, "Good Artist - Good Song.mp3");
                var sf = MakeReplacementDuplicate(goodPath, "Good Artist", "Good Song", libPath);
                var scannedFiles = new List<MusicIntegrator.ScannedFile> { sf };
                var logEntries = new List<MusicIntegrator.LogEntry>();
                int moved = 0, skipped = 0;

                integrator.ExecuteDuplicateDecisions(scannedFiles, new HashSet<string>(), logEntries, ref skipped);
                integrator.RouteAllFiles(scannedFiles, new HashSet<string>(), logEntries, ref moved, ref skipped);

                Assert.Equal("1", moved.ToString(), "replacement must route successfully");
                Assert.Equal("0", skipped.ToString(), "successful replacement must not be skipped");
                string expectedDest = Path.Combine(lib, Constants.MiscDir, "Good Artist - Good Song.mp3");
                Assert.True(File.Exists(expectedDest), "replacement must arrive at its destination");
                Assert.True(!File.Exists(libPath), "replaced library copy must stay gone");
                Assert.Equal("0", Directory.GetFiles(lib, "*.replaced.bak", SearchOption.AllDirectories).Length.ToString(),
                    "a successful replacement must commit (delete) the staged bak");
            }
            finally
            {
                RoutingFixtures.Cleanup(lib);
                if (Directory.Exists(newMusic)) Directory.Delete(newMusic, recursive: true);
            }
        }

        /// <summary>
        /// Real run, batch order K (keep) file B before L (replace) file A, both for the same library copy.
        /// 3a stages the library copy (frees its path). B routes first and must not take the staged slot:
        /// the staged L slot is reserved, so B is skipped as a duplicate, then A replaces the library copy.
        /// Asserts no .replaced.bak is stranded and the library path holds A's bytes, not B's.
        /// </summary>
        public static void RouteAllFiles_KFileBeforeReplacement_DoesNotTakeStagedSlot()
        {
            string lib = RoutingFixtures.CreateLibraryFixture();
            string newMusic = Path.Combine(Path.GetTempPath(), "AudioManagerTest_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(newMusic);
            try
            {
                // Library copy sits exactly where both files route ("Bad Artist" has no folder, so Misc).
                string miscDir = Path.Combine(lib, Constants.MiscDir);
                Directory.CreateDirectory(miscDir);
                string libPath = Path.Combine(miscDir, "Bad Artist - Bad Song.mp3");
                File.WriteAllBytes(libPath, new byte[] { 1, 2, 3 });

                // B: keep decision, enumerated first. Zero bytes, so a B-over-library result is detectable.
                string keepPath = Path.Combine(newMusic, "Bad Artist - Bad Song (keep).mp3");
                var kFile = MakeGoodFile(keepPath, "Bad Artist", "Bad Song");
                kFile.Duplicate = new MusicIntegrator.DupData
                {
                    Decision = 'K',
                    LibraryFilePath = libPath,
                    RelLibraryPath = "Miscellaneous Songs/Bad Artist - Bad Song.mp3",
                    RelNewPath = Path.GetFileName(keepPath)
                };

                // A: replace decision, enumerated second. Two bytes, distinct from B and from the library copy.
                string replPath = Path.Combine(newMusic, "Bad Artist - Bad Song.mp3");
                var lFile = MakeReplacementDuplicate(replPath, "Bad Artist", "Bad Song", libPath);
                File.WriteAllBytes(replPath, new byte[] { 9, 9 });

                var integrator = new MusicIntegrator(lib);
                var scannedFiles = new List<MusicIntegrator.ScannedFile> { kFile, lFile };
                var logEntries = new List<MusicIntegrator.LogEntry>();
                int moved = 0, skipped = 0;

                integrator.ExecuteDuplicateDecisions(scannedFiles, new HashSet<string>(), logEntries, ref skipped);
                integrator.RouteAllFiles(scannedFiles, new HashSet<string>(), logEntries, ref moved, ref skipped);

                Assert.Equal("0", Directory.GetFiles(lib, "*.replaced.bak", SearchOption.AllDirectories).Length.ToString(),
                    "no staged .replaced.bak may be stranded when a K file is enumerated before the L file");
                Assert.True(File.Exists(libPath), "the library path must be occupied after the batch");
                Assert.Equal("2", new FileInfo(libPath).Length.ToString(),
                    "the library path must hold the replacement (A, 2 bytes), not the keep file (B, 0 bytes)");
                Assert.True(File.Exists(keepPath), "the keep file must stay in NewMusic when its destination is reserved");
                Assert.Equal("1", moved.ToString(), "only the replacement may route into the staged slot");
            }
            finally
            {
                RoutingFixtures.Cleanup(lib);
                if (Directory.Exists(newMusic)) Directory.Delete(newMusic, recursive: true);
            }
        }
    }
}
