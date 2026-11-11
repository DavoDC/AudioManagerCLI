using System;
using System.Collections.Generic;
using System.IO;
using AudioManager.Code.Modules;

namespace AudioManager
{
    /// <summary>
    /// Covers two leftover-folder bugs on temp trees only, never the real library or NewMusic:
    /// (1) CleanupNewMusicFolder used to abort when ANY file remained, so empty folders elsewhere
    /// never went away and repeated runs did not converge; (2) RouteAllFiles created a destination
    /// folder before the move and left it empty when the move then failed.
    /// </summary>
    internal static class LeftoverFolderTests
    {
        private static string NewRoot()
        {
            return Path.Combine(Path.GetTempPath(), "am_leftover_" + Guid.NewGuid().ToString("N").Substring(0, 8));
        }

        // Runs the NewMusic cleanup through the path seam and returns captured console output.
        private static string RunCleanup(string folder, bool dryRun)
        {
            var originalOut = Console.Out;
            var capture = new StringWriter();
            try
            {
                Console.SetOut(capture);
                var integrator = new MusicIntegrator(folder, dryRun);
                integrator.CleanupNewMusicFolder(folder);
            }
            finally
            {
                Console.SetOut(originalOut);
            }
            return capture.ToString();
        }

        private static MusicIntegrator.ScannedFile MakeRoutableFile(string sourcePath, string artist, string title)
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

        // Leftover .tagfix.bak under one artist used to block removal of the empty folder beside it, forever.
        public static void LeftoverFolder_TagfixBakLeftover_EmptySiblingRemovedAndRunsConverge()
        {
            string root = NewRoot();
            try
            {
                string bakDir = Path.Combine(root, "Kept Artist");
                string bakFile = Path.Combine(bakDir, "x.mp3.tagfix.bak");
                string emptyDeep = Path.Combine(root, "Empty Artist", "Empty Album");
                Directory.CreateDirectory(bakDir);
                File.WriteAllText(bakFile, "x");
                Directory.CreateDirectory(emptyDeep);

                RunCleanup(root, dryRun: false);

                Assert.True(File.Exists(bakFile), "a leftover .bak must stay in place");
                Assert.True(Directory.Exists(bakDir), "the folder holding the .bak must stay");
                Assert.True(!Directory.Exists(emptyDeep), "the empty album must be removed");
                Assert.True(!Directory.Exists(Path.Combine(root, "Empty Artist")), "the emptied parent must be removed");

                string second = RunCleanup(root, dryRun: false);

                Assert.True(!second.Contains("Removed empty folder"), "a second run must find nothing left to remove, got: " + second);
                Assert.True(File.Exists(bakFile), "a second run must not touch the leftover file");
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        }

        public static void LeftoverFolder_DryRun_RemovesNothing()
        {
            string root = NewRoot();
            try
            {
                string emptySub = Path.Combine(root, "EmptyArtist");
                Directory.CreateDirectory(emptySub);

                string output = RunCleanup(root, dryRun: true);

                Assert.True(Directory.Exists(emptySub), "a dry run must not remove any folder");
                Assert.True(output.Contains("Would remove: 1 empty folder(s)"),
                    "a dry run must report what it would remove, got: " + output);
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        }

        // Source vanished, so File.Move throws AFTER routing has created the Misc destination folder.
        public static void LeftoverFolder_FailedMove_RemovesDestinationFolderItCreated()
        {
            string lib = RoutingFixtures.CreateLibraryFixture();
            string newMusic = NewRoot();
            Directory.CreateDirectory(newMusic);
            try
            {
                var integrator = new MusicIntegrator(lib);
                string srcPath = Path.Combine(newMusic, "Bad Artist - Bad Song.mp3");
                var sf = MakeRoutableFile(srcPath, "Bad Artist", "Bad Song");
                File.Delete(srcPath);

                var scannedFiles = new List<MusicIntegrator.ScannedFile> { sf };
                var logEntries = new List<MusicIntegrator.LogEntry>();
                int moved = 0, skipped = 0;

                integrator.RouteAllFiles(scannedFiles, new HashSet<string>(), logEntries, ref moved, ref skipped);

                Assert.Equal("0", moved.ToString(), "a failed move must not count as moved");
                Assert.Equal("1", skipped.ToString(), "a failed move must count as skipped");
                Assert.True(!Directory.Exists(Path.Combine(lib, Constants.MiscDir)),
                    "a destination folder created by this call must be removed when its move fails");
            }
            finally
            {
                RoutingFixtures.Cleanup(lib);
                if (Directory.Exists(newMusic)) Directory.Delete(newMusic, recursive: true);
            }
        }

        public static void LeftoverFolder_FailedMove_KeepsDestinationFolderThatAlreadyExisted()
        {
            string lib = RoutingFixtures.CreateLibraryFixture();
            string newMusic = NewRoot();
            Directory.CreateDirectory(newMusic);
            try
            {
                string miscDir = Path.Combine(lib, Constants.MiscDir);
                Directory.CreateDirectory(miscDir);

                var integrator = new MusicIntegrator(lib);
                string srcPath = Path.Combine(newMusic, "Bad Artist - Bad Song.mp3");
                var sf = MakeRoutableFile(srcPath, "Bad Artist", "Bad Song");
                File.Delete(srcPath);

                var scannedFiles = new List<MusicIntegrator.ScannedFile> { sf };
                var logEntries = new List<MusicIntegrator.LogEntry>();
                int moved = 0, skipped = 0;

                integrator.RouteAllFiles(scannedFiles, new HashSet<string>(), logEntries, ref moved, ref skipped);

                Assert.Equal("1", skipped.ToString(), "a failed move must count as skipped");
                Assert.True(Directory.Exists(miscDir), "a destination folder that existed before the call must never be removed");
            }
            finally
            {
                RoutingFixtures.Cleanup(lib);
                if (Directory.Exists(newMusic)) Directory.Delete(newMusic, recursive: true);
            }
        }

        public static void LeftoverFolder_DryRunRoute_CreatesNoDestinationFolder()
        {
            string lib = RoutingFixtures.CreateLibraryFixture();
            string newMusic = NewRoot();
            Directory.CreateDirectory(newMusic);
            try
            {
                var integrator = new MusicIntegrator(lib, dryRun: true);
                string srcPath = Path.Combine(newMusic, "Bad Artist - Bad Song.mp3");
                var sf = MakeRoutableFile(srcPath, "Bad Artist", "Bad Song");

                var scannedFiles = new List<MusicIntegrator.ScannedFile> { sf };
                var logEntries = new List<MusicIntegrator.LogEntry>();
                int moved = 0, skipped = 0;

                integrator.RouteAllFiles(scannedFiles, new HashSet<string>(), logEntries, ref moved, ref skipped);

                Assert.Equal("1", moved.ToString(), "a dry run must plan the move");
                Assert.True(File.Exists(srcPath), "a dry run must leave the source in NewMusic");
                Assert.True(!Directory.Exists(Path.Combine(lib, Constants.MiscDir)),
                    "a dry run must not create any destination folder");
            }
            finally
            {
                RoutingFixtures.Cleanup(lib);
                if (Directory.Exists(newMusic)) Directory.Delete(newMusic, recursive: true);
            }
        }
    }
}
