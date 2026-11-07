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
    }
}
