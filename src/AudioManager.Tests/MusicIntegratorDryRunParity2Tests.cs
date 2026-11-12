using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AudioManager.Code.Modules;

namespace AudioManager
{
    /// <summary>
    /// Dry-run parity for two destination checks the real run applies: the L-replacement destination
    /// (3a, then the 3b routing check) and the Misc migration destination (RunMiscMigration). Each test
    /// runs the dry and real paths on separate temp libraries and asserts the same outcome. Uses the
    /// test-only MusicIntegrator(testLibraryPath, dryRun) constructor and real temp files only - never
    /// touches the real Constants.AudioFolderPath library. The dry half runs first in each test so a
    /// failing dry assertion stops before any real-run file operation.
    /// </summary>
    internal static class MusicIntegratorDryRunParity2Tests
    {
        private static MusicIntegrator.ScannedFile MakeReplacementDuplicate(string sourcePath, string artist, string title, string libPath)
        {
            File.WriteAllBytes(sourcePath, new byte[0]);
            var track = new Track { Artists = artist, Title = title, Album = "Missing", Genres = "Missing" };
            return new MusicIntegrator.ScannedFile
            {
                SourcePath = sourcePath,
                Track = track,
                IsReadable = true,
                LogEntry = new MusicIntegrator.LogEntry { Filename = Path.GetFileName(sourcePath), Title = title, Artists = artist },
                Duplicate = new MusicIntegrator.DupData
                {
                    Decision = 'L',
                    LibraryFilePath = libPath,
                    RelLibraryPath = "Old Place/" + Path.GetFileName(libPath),
                    RelNewPath = Path.GetFileName(sourcePath)
                }
            };
        }

        private static string MakeNewMusicDir()
        {
            string newMusic = Path.Combine(Path.GetTempPath(), "AudioManagerTest_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(newMusic);
            return newMusic;
        }

        /// <summary>
        /// Runs the L-replacement path (3a then 3b) on a fresh temp library where the new track's
        /// routed destination is already occupied by another file. Reports the final log status/detail,
        /// the skipped count, and whether the library copy and the occupying file are still in place.
        /// </summary>
        private static void RunOccupiedReplacement(bool dryRun, out string status, out string detail, out int skipped, out bool libKept, out bool occupiedKept)
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
                int moved = 0, skippedCount = 0;

                integrator.ExecuteDuplicateDecisions(scannedFiles, new HashSet<string>(), logEntries, ref skippedCount);
                integrator.RouteAllFiles(scannedFiles, new HashSet<string>(), logEntries, ref moved, ref skippedCount);

                status = sf.LogEntry.Status;
                detail = sf.LogEntry.Detail;
                skipped = skippedCount;
                libKept = File.Exists(libCopy);
                occupiedKept = File.Exists(occupied) && File.ReadAllBytes(occupied).Length == 1 && File.ReadAllBytes(occupied)[0] == 2;
            }
            finally
            {
                RoutingFixtures.Cleanup(lib);
                if (Directory.Exists(newMusic)) Directory.Delete(newMusic, recursive: true);
            }
        }

        public static void DryRun_ReplacementDestinationOccupied_MatchesRealRunSkip()
        {
            RunOccupiedReplacement(dryRun: true, out string dryStatus, out string dryDetail, out int drySkipped, out bool dryLibKept, out bool dryOccupiedKept);
            Assert.Equal("would-skip", dryStatus, "dry run must mark an L replacement with an occupied destination would-skip, like the real run's skip");
            Assert.Equal("already exists at destination", dryDetail, "dry-run detail must match the real run's occupied-destination message");
            Assert.Equal("1", drySkipped.ToString(), "dry run must count the occupied L replacement as skipped");
            Assert.True(dryLibKept, "dry run must leave the library copy in place");
            Assert.True(dryOccupiedKept, "dry run must leave the occupying file untouched");

            RunOccupiedReplacement(dryRun: false, out string realStatus, out string realDetail, out int realSkipped, out bool realLibKept, out bool realOccupiedKept);
            Assert.Equal("skipped", realStatus, "real run must skip an L replacement whose destination is occupied");
            Assert.Equal(realDetail, dryDetail, "dry-run detail must equal the real run's detail");
            Assert.Equal(realSkipped.ToString(), drySkipped.ToString(), "dry-run skipped count must equal the real run's");
            Assert.True(realLibKept, "real run must restore the library copy after the skip");
            Assert.True(realOccupiedKept, "real run must leave the occupying file untouched");
        }

        /// <summary>Sets the internal _miscMigrationCandidates map that RunMiscMigration reads.</summary>
        private static void SetMiscCandidates(MusicIntegrator integrator, string artist, List<string> xmlPaths)
        {
            integrator._miscMigrationCandidates[artist] = xmlPaths;
        }

        /// <summary>Invokes RunMiscMigration and returns its failed count.</summary>
        private static int InvokeMiscMigration(MusicIntegrator integrator)
        {
            return integrator.RunMiscMigration();
        }

        /// <summary>
        /// Runs RunMiscMigration on a fresh temp library where the Misc song's Singles destination is
        /// already occupied. Reports the failed count, whether the source is still in the library, and
        /// whether the occupying file is unchanged.
        /// </summary>
        private static void RunMiscMigrationOccupied(bool dryRun, out int failed, out bool sourceKept, out bool occupiedUnchanged)
        {
            string lib = RoutingFixtures.CreateLibraryFixture();
            try
            {
                string libSong = Path.Combine(lib, Constants.MiscDir, "Good Artist - Good Song.mp3");
                Directory.CreateDirectory(Path.GetDirectoryName(libSong));
                File.WriteAllBytes(libSong, new byte[] { 1 });

                string destFolder = Path.Combine(lib, Constants.ArtistsDir, "Good Artist", Constants.SinglesDir);
                Directory.CreateDirectory(destFolder);
                string occupied = Path.Combine(destFolder, "Good Artist - Good Song.mp3");
                File.WriteAllBytes(occupied, new byte[] { 2 });

                var integrator = new MusicIntegrator(lib, dryRun: dryRun);
                SetMiscCandidates(integrator, "Good Artist", new List<string> { libSong });

                failed = InvokeMiscMigration(integrator);
                sourceKept = File.Exists(libSong);
                occupiedUnchanged = File.Exists(occupied) && File.ReadAllBytes(occupied).Length == 1 && File.ReadAllBytes(occupied)[0] == 2;
            }
            finally
            {
                RoutingFixtures.Cleanup(lib);
            }
        }

        public static void DryRun_MiscMigrationDestinationOccupied_CountsFailureLikeRealRun()
        {
            RunMiscMigrationOccupied(dryRun: true, out int dryFailed, out bool dryKept, out bool dryUnchanged);
            Assert.Equal("1", dryFailed.ToString(), "dry run must count an occupied Misc migration destination as failed, like the real run");
            Assert.True(dryKept, "dry run must leave the Misc song in the library");
            Assert.True(dryUnchanged, "dry run must leave the occupying destination file untouched");

            RunMiscMigrationOccupied(dryRun: false, out int realFailed, out bool realKept, out bool realUnchanged);
            Assert.Equal("1", realFailed.ToString(), "real run must count an occupied Misc migration destination as failed");
            Assert.Equal(realFailed.ToString(), dryFailed.ToString(), "dry-run failed count must equal the real run's");
            Assert.True(realKept, "real run must leave the Misc song in place when its move fails");
            Assert.True(realUnchanged, "real run must leave the occupying destination file untouched");
        }
    }
}
