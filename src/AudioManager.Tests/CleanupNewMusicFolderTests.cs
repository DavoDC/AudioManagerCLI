using System;
using System.IO;

namespace AudioManager
{
    /// <summary>
    /// Covers MusicIntegrator.CleanupNewMusicFolder(string) through the path seam, on temp trees only.
    /// Pins the real behaviour: the folder is left alone when any file remains (gate), and when it
    /// holds only empty subdirectories those are removed deepest first. A missing folder is a no-op.
    /// </summary>
    internal static class CleanupNewMusicFolderTests
    {
        private static string NewRoot()
        {
            return Path.Combine(Path.GetTempPath(), "am_cleanup_newmusic_" + Guid.NewGuid().ToString("N").Substring(0, 8));
        }

        // Runs the cleanup on folder with real deletion (dryRun false) and returns captured console output.
        private static string RunCleanup(string folder)
        {
            var originalOut = Console.Out;
            var capture = new StringWriter();
            try
            {
                Console.SetOut(capture);
                var integrator = new MusicIntegrator(folder, dryRun: false);
                integrator.CleanupNewMusicFolder(folder);
            }
            finally
            {
                Console.SetOut(originalOut);
            }
            return capture.ToString();
        }

        public static void CleanupNewMusicFolder_EmptyFolder_IsNoOpAndKeepsFolder()
        {
            string root = NewRoot();
            try
            {
                Directory.CreateDirectory(root);

                RunCleanup(root);

                Assert.True(Directory.Exists(root), "an empty NewMusic folder must stay in place");
                Assert.Equal("0", Directory.GetFileSystemEntries(root).Length.ToString(), "an empty NewMusic folder must stay empty");
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        }

        public static void CleanupNewMusicFolder_FileRemains_SkipsAndKeepsEverything()
        {
            string root = NewRoot();
            try
            {
                string emptySub = Path.Combine(root, "EmptyArtist");
                string filePath = Path.Combine(root, "Other Artist", "Song.mp3");
                Directory.CreateDirectory(emptySub);
                Directory.CreateDirectory(Path.GetDirectoryName(filePath));
                File.WriteAllText(filePath, "x");

                string output = RunCleanup(root);

                Assert.True(File.Exists(filePath), "a file still in NewMusic must not be deleted");
                Assert.True(Directory.Exists(emptySub), "the gate must skip removal of empty folders while files remain");
                Assert.True(output.Contains("NewMusic cleanup skipped: 1 file(s) still remain"),
                    "the skip must be reported with the remaining file count, got: " + output);
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        }

        public static void CleanupNewMusicFolder_OnlyEmptySubfolders_RemovesThemAndKeepsRoot()
        {
            string root = NewRoot();
            try
            {
                string deep = Path.Combine(root, "Artist", "Album");
                Directory.CreateDirectory(deep);
                Directory.CreateDirectory(Path.Combine(root, "Lonely"));

                RunCleanup(root);

                Assert.True(Directory.Exists(root), "the NewMusic root itself must never be removed");
                Assert.True(!Directory.Exists(deep), "a nested empty folder must be removed");
                Assert.True(!Directory.Exists(Path.Combine(root, "Artist")), "a parent emptied by the cleanup must be removed");
                Assert.True(!Directory.Exists(Path.Combine(root, "Lonely")), "a top-level empty folder must be removed");
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        }

        public static void CleanupNewMusicFolder_MissingFolder_DoesNotThrow()
        {
            string root = NewRoot();
            Assert.True(!Directory.Exists(root), "precondition: the test folder must not exist");

            RunCleanup(root);

            Assert.True(!Directory.Exists(root), "a missing NewMusic folder must not be created by the cleanup");
        }
    }
}
