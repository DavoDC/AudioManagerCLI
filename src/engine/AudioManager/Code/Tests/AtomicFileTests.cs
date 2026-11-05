using System;
using System.IO;
using System.Text;

namespace AudioManager
{
    /// <summary>
    /// Tests for AtomicFile.WriteAllText: the JSON outputs read by the UI must never be torn or empty,
    /// so content goes to a sibling .tmp file and then replaces the destination.
    /// </summary>
    internal static class AtomicFileTests
    {
        private static string NewDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), "am_atomic_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static void Cleanup(string dir)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
            catch (Exception) { }
        }

        public static void AtomicFile_WritesNewFileContent()
        {
            string dir = NewDir();
            try
            {
                string path = Path.Combine(dir, "out.json");
                AtomicFile.WriteAllText(path, "{\"a\": 1}", Encoding.UTF8);
                Assert.Equal("{\"a\": 1}", File.ReadAllText(path, Encoding.UTF8), "new file should hold the written text");
            }
            finally { Cleanup(dir); }
        }

        public static void AtomicFile_OverwritesExistingContent()
        {
            string dir = NewDir();
            try
            {
                string path = Path.Combine(dir, "out.json");
                File.WriteAllText(path, "old content", Encoding.UTF8);
                AtomicFile.WriteAllText(path, "new content", Encoding.UTF8);
                Assert.Equal("new content", File.ReadAllText(path, Encoding.UTF8), "existing file should be replaced");
            }
            finally { Cleanup(dir); }
        }

        public static void AtomicFile_LeavesNoTmpOnSuccess()
        {
            string dir = NewDir();
            try
            {
                string path = Path.Combine(dir, "out.json");
                AtomicFile.WriteAllText(path, "first", Encoding.UTF8);
                Assert.True(!File.Exists(path + ".tmp"), "no .tmp after a first write");
                AtomicFile.WriteAllText(path, "second", Encoding.UTF8);
                Assert.True(!File.Exists(path + ".tmp"), "no .tmp after an overwrite");
            }
            finally { Cleanup(dir); }
        }

        public static void AtomicFile_DirectoryAsDestinationThrowsAndLeavesNoTmp()
        {
            string dir = NewDir();
            try
            {
                string path = Path.Combine(dir, "isadir.json");
                Directory.CreateDirectory(path);
                bool threw = false;
                try { AtomicFile.WriteAllText(path, "x", Encoding.UTF8); }
                catch (Exception) { threw = true; }
                Assert.True(threw, "a directory at the destination should throw");
                Assert.True(!File.Exists(path + ".tmp"), "failed write should leave no .tmp");
            }
            finally { Cleanup(dir); }
        }

        public static void AtomicFile_MissingParentFolderThrowsAndLeavesNoTmp()
        {
            string dir = NewDir();
            try
            {
                string path = Path.Combine(dir, "no_such_folder", "out.json");
                bool threw = false;
                try { AtomicFile.WriteAllText(path, "x", Encoding.UTF8); }
                catch (Exception) { threw = true; }
                Assert.True(threw, "a missing parent folder should throw");
                Assert.True(!File.Exists(path + ".tmp"), "failed write should leave no .tmp");
            }
            finally { Cleanup(dir); }
        }
    }
}
