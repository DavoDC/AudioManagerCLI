using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AudioManager.Code.Modules;

namespace AudioManager
{
    /// <summary>
    /// Dry-run parity with the real run: a dry run must apply the same blocked-destination check
    /// (existing destination, earlier file in the same batch) and the same missing-library-file
    /// handling. Uses the test-only MusicIntegrator(testLibraryPath, dryRun) constructor and real
    /// temp files/dirs - never touches the real Constants.AudioFolderPath library.
    /// </summary>
    internal static class MusicIntegratorDryRunParityTests
    {
        private static MusicIntegrator.ScannedFile MakeFile(string sourcePath, string artist, string title)
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

        private static MusicIntegrator.ScannedFile MakeReplacementDuplicate(string sourcePath, string artist, string title, string libPath)
        {
            var sf = MakeFile(sourcePath, artist, title);
            sf.Duplicate = new MusicIntegrator.DupData
            {
                Decision = 'L',
                LibraryFilePath = libPath,
                RelLibraryPath = "Old Place/" + Path.GetFileName(libPath),
                RelNewPath = Path.GetFileName(sourcePath)
            };
            return sf;
        }

        private static string MakeNewMusicDir()
        {
            string newMusic = Path.Combine(Path.GetTempPath(), "AudioManagerTest_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(newMusic);
            return newMusic;
        }

        public static void DryRun_DestinationExists_ReportsSkipNotWouldMove()
        {
            string lib = RoutingFixtures.CreateLibraryFixture();
            string newMusic = MakeNewMusicDir();
            try
            {
                // "Good Artist" has no artist folder, so GetDestDir routes it to lib/Miscellaneous Songs.
                string existing = Path.Combine(lib, Constants.MiscDir, "Good Artist - Good Song.mp3");
                Directory.CreateDirectory(Path.GetDirectoryName(existing));
                File.WriteAllBytes(existing, new byte[] { 1, 2, 3 });

                var integrator = new MusicIntegrator(lib, dryRun: true);
                var sf = MakeFile(Path.Combine(newMusic, "Good Artist - Good Song.mp3"), "Good Artist", "Good Song");
                var scannedFiles = new List<MusicIntegrator.ScannedFile> { sf };
                var logEntries = new List<MusicIntegrator.LogEntry>();
                int moved = 0, skipped = 0;

                integrator.RouteAllFiles(scannedFiles, new HashSet<string>(), logEntries, ref moved, ref skipped);

                Assert.Equal("0", moved.ToString(), "dry run must not report a blocked destination as would-move");
                Assert.Equal("1", skipped.ToString(), "dry run must count the blocked file as skipped, like the real run");
                Assert.Equal("would-skip", sf.LogEntry.Status, "blocked dry-run file must be marked would-skip");
                Assert.True(File.Exists(sf.SourcePath), "dry run must leave the source file in NewMusic");
                Assert.True(File.Exists(existing), "dry run must leave the existing destination untouched");
            }
            finally
            {
                RoutingFixtures.Cleanup(lib);
                if (Directory.Exists(newMusic)) Directory.Delete(newMusic, recursive: true);
            }
        }

        public static void DryRun_DestinationMissing_StillReportsWouldMove()
        {
            string lib = RoutingFixtures.CreateLibraryFixture();
            string newMusic = MakeNewMusicDir();
            try
            {
                var integrator = new MusicIntegrator(lib, dryRun: true);
                var sf = MakeFile(Path.Combine(newMusic, "Good Artist - Good Song.mp3"), "Good Artist", "Good Song");
                var scannedFiles = new List<MusicIntegrator.ScannedFile> { sf };
                var logEntries = new List<MusicIntegrator.LogEntry>();
                int moved = 0, skipped = 0;

                integrator.RouteAllFiles(scannedFiles, new HashSet<string>(), logEntries, ref moved, ref skipped);

                Assert.Equal("1", moved.ToString(), "dry run must still report a free destination as would-move");
                Assert.Equal("0", skipped.ToString(), "free destination must not be skipped in dry run");
                Assert.Equal("would-move", sf.LogEntry.Status, "free dry-run file must be marked would-move");
                Assert.True(File.Exists(sf.SourcePath), "dry run must leave the source file in NewMusic");
                string plannedDest = Path.Combine(lib, Constants.MiscDir, "Good Artist - Good Song.mp3");
                Assert.True(!File.Exists(plannedDest), "dry run must not create the planned destination");
            }
            finally
            {
                RoutingFixtures.Cleanup(lib);
                if (Directory.Exists(newMusic)) Directory.Delete(newMusic, recursive: true);
            }
        }

        public static void DryRun_TwoFilesSameDestination_SecondIsSkipped()
        {
            string lib = RoutingFixtures.CreateLibraryFixture();
            string newMusic = MakeNewMusicDir();
            try
            {
                var integrator = new MusicIntegrator(lib, dryRun: true);
                var first = MakeFile(Path.Combine(newMusic, "Good Artist - Good Song.mp3"), "Good Artist", "Good Song");
                var second = MakeFile(Path.Combine(newMusic, "Good Artist - Good Song (2).mp3"), "Good Artist", "Good Song");
                var scannedFiles = new List<MusicIntegrator.ScannedFile> { first, second };
                var logEntries = new List<MusicIntegrator.LogEntry>();
                int moved = 0, skipped = 0;

                integrator.RouteAllFiles(scannedFiles, new HashSet<string>(), logEntries, ref moved, ref skipped);

                Assert.Equal("1", moved.ToString(), "only the first file may claim the shared destination in dry run");
                Assert.Equal("1", skipped.ToString(), "the second file must be skipped as a batch collision, like the real run");
                Assert.Equal("would-move", first.LogEntry.Status, "first file must be would-move");
                Assert.Equal("would-skip", second.LogEntry.Status, "second file must be would-skip");
                Assert.True(File.Exists(first.SourcePath) && File.Exists(second.SourcePath), "dry run must leave both sources in NewMusic");
            }
            finally
            {
                RoutingFixtures.Cleanup(lib);
                if (Directory.Exists(newMusic)) Directory.Delete(newMusic, recursive: true);
            }
        }

        public static void IsDestBlocked_ExistingDestEqualToLibraryPath_IsNotBlocked()
        {
            string lib = RoutingFixtures.CreateLibraryFixture();
            try
            {
                string dest = Path.Combine(lib, "Old Place", "Good Artist - Good Song.mp3");
                Directory.CreateDirectory(Path.GetDirectoryName(dest));
                File.WriteAllBytes(dest, new byte[] { 1 });

                Assert.True(!MusicIntegrator.IsDestBlocked(dest, dest, null), "existing destination equal to the library path being replaced must not block");
                Assert.True(!MusicIntegrator.IsDestBlocked(dest.ToUpperInvariant(), dest, null), "library path exemption must ignore case");
                Assert.True(MusicIntegrator.IsDestBlocked(dest, null, null), "existing destination with no exemption must block");
                Assert.True(MusicIntegrator.IsDestBlocked(dest, Path.Combine(lib, "other.mp3"), null), "existing destination different from the replaced path must block");

                string absent = Path.Combine(lib, "absent.mp3");
                var planned = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { absent };
                Assert.True(MusicIntegrator.IsDestBlocked(absent, null, planned), "destination already planned in this batch must block");
                Assert.True(MusicIntegrator.IsDestBlocked(absent.ToUpperInvariant(), null, planned), "planned destination match must ignore case");
                Assert.True(!MusicIntegrator.IsDestBlocked(absent, null, new HashSet<string>(StringComparer.OrdinalIgnoreCase)), "free destination with no planned match must not block");
                Assert.True(!MusicIntegrator.IsDestBlocked(absent, null, null), "free destination with no batch set must not block");
            }
            finally
            {
                RoutingFixtures.Cleanup(lib);
            }
        }

        public static void DryRun_ReplacementWithMissingLibraryFile_ReportsErrorLikeRealRun()
        {
            string lib = RoutingFixtures.CreateLibraryFixture();
            string newMusic = MakeNewMusicDir();
            try
            {
                // The library copy named by the L decision does not exist.
                string libPath = Path.Combine(lib, "Old Place", "Bad Artist - Bad Song.mp3");

                var integrator = new MusicIntegrator(lib, dryRun: true);
                var sf = MakeReplacementDuplicate(Path.Combine(newMusic, "Bad Artist - Bad Song.mp3"), "Bad Artist", "Bad Song", libPath);
                var scannedFiles = new List<MusicIntegrator.ScannedFile> { sf };
                var logEntries = new List<MusicIntegrator.LogEntry>();
                int skipped = 0;

                integrator.ExecuteDuplicateDecisions(scannedFiles, new HashSet<string>(), logEntries, ref skipped);

                Assert.Equal("error", sf.LogEntry.Status, "dry-run L with a missing library file must be an error, like the real run");
                Assert.Equal("duplicate (library file not found)", sf.LogEntry.Detail, "detail must match the real run's missing-library message");
                Assert.Equal("1", skipped.ToString(), "missing library file must count as skipped");
                Assert.True(sf.Duplicate.SkipRouting, "missing library file must skip routing of the replacement");
                Assert.True(File.Exists(sf.SourcePath), "dry run must leave the replacement source in NewMusic");
            }
            finally
            {
                RoutingFixtures.Cleanup(lib);
                if (Directory.Exists(newMusic)) Directory.Delete(newMusic, recursive: true);
            }
        }

        public static void DryRun_ReplacementWithStaleBak_ReportsStageFailureLikeRealRun()
        {
            string lib = RoutingFixtures.CreateLibraryFixture();
            string newMusic = MakeNewMusicDir();
            try
            {
                // A stale .replaced.bak from an earlier stage blocks ReplaceGuard.Stage in the real run.
                string libCopy = Path.Combine(lib, "Old Place", "Good Artist - Good Song.mp3");
                Directory.CreateDirectory(Path.GetDirectoryName(libCopy));
                File.WriteAllBytes(libCopy, new byte[] { 1 });
                string bak = libCopy + ".replaced.bak";
                File.WriteAllBytes(bak, new byte[] { 3 });

                var integrator = new MusicIntegrator(lib, dryRun: true);
                var sf = MakeReplacementDuplicate(Path.Combine(newMusic, "Good Artist - Good Song.mp3"), "Good Artist", "Good Song", libCopy);
                var scannedFiles = new List<MusicIntegrator.ScannedFile> { sf };
                var logEntries = new List<MusicIntegrator.LogEntry>();
                int skipped = 0;

                integrator.ExecuteDuplicateDecisions(scannedFiles, new HashSet<string>(), logEntries, ref skipped);

                Assert.Equal("error", sf.LogEntry.Status, "dry-run L with a stale bak must be an error, like the real run's stage failure");
                Assert.Equal("duplicate (library stage failed)", sf.LogEntry.Detail, "detail must match the real run's stage-failure message");
                Assert.Equal("1", skipped.ToString(), "stage failure must count as skipped");
                Assert.True(sf.Duplicate.SkipRouting, "stage failure must skip routing of the replacement");
                Assert.True(File.Exists(libCopy) && File.Exists(bak), "dry run must leave the library copy and the stale bak untouched");
                Assert.True(File.Exists(sf.SourcePath), "dry run must leave the replacement source in NewMusic");
            }
            finally
            {
                RoutingFixtures.Cleanup(lib);
                if (Directory.Exists(newMusic)) Directory.Delete(newMusic, recursive: true);
            }
        }

        /// <summary>
        /// Runs the L-replacement path (3a then 3b) where the routed destination is occupied by another file,
        /// so the replacement is skipped. Reports the final reason, destination and status on the log entry.
        /// </summary>
        private static void RunBlockedReplacement(bool dryRun, out string reason, out string destination, out string status)
        {
            string lib = RoutingFixtures.CreateLibraryFixture();
            string newMusic = MakeNewMusicDir();
            try
            {
                string libCopy = Path.Combine(lib, "Old Place", "Good Artist - Good Song.mp3");
                Directory.CreateDirectory(Path.GetDirectoryName(libCopy));
                File.WriteAllBytes(libCopy, new byte[] { 1 });

                // "Good Artist" has no artist folder, so GetDestDir routes the replacement to lib/Miscellaneous Songs.
                string occupied = Path.Combine(lib, Constants.MiscDir, "Good Artist - Good Song.mp3");
                Directory.CreateDirectory(Path.GetDirectoryName(occupied));
                File.WriteAllBytes(occupied, new byte[] { 2 });

                var integrator = new MusicIntegrator(lib, dryRun: dryRun);
                var sf = MakeReplacementDuplicate(Path.Combine(newMusic, "Good Artist - Good Song.mp3"), "Good Artist", "Good Song", libCopy);
                var scannedFiles = new List<MusicIntegrator.ScannedFile> { sf };
                var logEntries = new List<MusicIntegrator.LogEntry>();
                int moved = 0, skipped = 0;

                integrator.ExecuteDuplicateDecisions(scannedFiles, new HashSet<string>(), logEntries, ref skipped);
                integrator.RouteAllFiles(scannedFiles, new HashSet<string>(), logEntries, ref moved, ref skipped);

                reason = sf.LogEntry.Reason;
                // Each run uses its own temp library, so compare the destination relative to that library.
                string rawDest = sf.LogEntry.Destination ?? "";
                destination = rawDest.StartsWith(lib, StringComparison.OrdinalIgnoreCase)
                    ? rawDest.Substring(lib.Length).TrimStart('\\', '/')
                    : rawDest;
                status = sf.LogEntry.Status;
            }
            finally
            {
                RoutingFixtures.Cleanup(lib);
                if (Directory.Exists(newMusic)) Directory.Delete(newMusic, recursive: true);
            }
        }

        public static void DryRun_BlockedReplacement_SetsReasonAndDestinationLikeRealRun()
        {
            RunBlockedReplacement(dryRun: true, out string dryReason, out string dryDest, out string dryStatus);
            RunBlockedReplacement(dryRun: false, out string realReason, out string realDest, out string realStatus);

            Assert.Equal("skipped", realStatus, "real run must skip an L replacement whose destination is occupied");
            Assert.True(!string.IsNullOrEmpty(realDest), "real run must record the destination on the skipped replacement");
            Assert.Equal(realReason, dryReason, "dry-run blocked L must record the same reason as the real run");
            Assert.Equal(realDest, dryDest, "dry-run blocked L must record the same destination as the real run");
            Assert.Equal("would-skip", dryStatus, "dry-run blocked L must stay would-skip");
        }

        public static void DryRun_ReplacementWithMissingTag_PinnedSkipKeepsLibraryCopy()
        {
            string lib = RoutingFixtures.CreateLibraryFixture();
            string newMusic = MakeNewMusicDir();
            try
            {
                // Pinned: the real run stages the library copy in 3a, then 3b skips the missing-tag file and rolls back.
                // The dry run must end in the same status and leave the library copy in place.
                string libCopy = Path.Combine(lib, "Old Place", "Good Artist - Missing.mp3");
                Directory.CreateDirectory(Path.GetDirectoryName(libCopy));
                File.WriteAllBytes(libCopy, new byte[] { 1 });

                var integrator = new MusicIntegrator(lib, dryRun: true);
                var sf = MakeReplacementDuplicate(Path.Combine(newMusic, "Good Artist - Missing.mp3"), "Good Artist", Constants.MissingTag, libCopy);
                var scannedFiles = new List<MusicIntegrator.ScannedFile> { sf };
                var logEntries = new List<MusicIntegrator.LogEntry>();
                int moved = 0, skipped = 0;

                integrator.ExecuteDuplicateDecisions(scannedFiles, new HashSet<string>(), logEntries, ref skipped);
                integrator.RouteAllFiles(scannedFiles, new HashSet<string>(), logEntries, ref moved, ref skipped);

                Assert.Equal("skipped", sf.LogEntry.Status, "dry-run L with a missing required tag must end skipped, like the real run");
                Assert.Equal("missing required tag", sf.LogEntry.Detail, "detail must match the real run's missing-tag message");
                Assert.Equal("0", moved.ToString(), "missing-tag replacement must not be counted as moved");
                Assert.True(File.Exists(libCopy), "dry run must leave the library copy in place");
                Assert.True(File.Exists(sf.SourcePath), "dry run must leave the replacement source in NewMusic");
            }
            finally
            {
                RoutingFixtures.Cleanup(lib);
                if (Directory.Exists(newMusic)) Directory.Delete(newMusic, recursive: true);
            }
        }
    }
}
