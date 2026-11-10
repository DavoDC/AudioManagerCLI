using System;
using System.IO;
using System.Linq;

namespace AudioManager
{
    /// <summary>
    /// Case-only rename ("artist - song.mp3" -> "Artist - Song.mp3") must actually rename the file,
    /// log the new name, and leave no extra folder behind. Runs in a throwaway temp folder only.
    /// </summary>
    internal static class CaseRenameLogTests
    {
        private static string NewTempDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), "AudioManagerTest_" + Guid.NewGuid());
            Directory.CreateDirectory(dir);
            return dir;
        }

        public static void ProcessFile_CaseOnlyRename_LogsNewNameAndRenamesOnDisk()
        {
            string dir = NewTempDir();
            try
            {
                string fixturePath = TagFixerTests.CreateSilentMp3Fixture(dir, "artist - song.mp3", "Song", "Album", "Artist");

                var fixer = TagFixer.ForTesting(dryRun: false);
                var log = fixer.ProcessFile(fixturePath);

                Assert.Equal("Artist - Song.mp3", log.Filename, "FixLog should record the new name after a case-only rename");
                string[] onDisk = Directory.GetFiles(dir).Select(Path.GetFileName).ToArray();
                Assert.True(onDisk.Length == 1 && onDisk[0] == "Artist - Song.mp3",
                    $"Disk should hold exactly 'Artist - Song.mp3' (case-exact), got: {string.Join(", ", onDisk)}");
                Assert.True(log.Changes.Any(c => c.StartsWith("Filename:", StringComparison.Ordinal) && c.Contains("\"Artist - Song.mp3\"")),
                    $"Changes should carry a Filename line for the case-only rename, got: {string.Join(" | ", log.Changes)}");
            }
            finally { Directory.Delete(dir, true); }
        }

        public static void ProcessFile_CaseOnlyRename_LeavesNoStrayFolder()
        {
            string dir = NewTempDir();
            try
            {
                string fixturePath = TagFixerTests.CreateSilentMp3Fixture(dir, "artist - song.mp3", "Song", "Album", "Artist");

                var fixer = TagFixer.ForTesting(dryRun: false);
                fixer.ProcessFile(fixturePath);

                Assert.True(Directory.GetDirectories(dir).Length == 0,
                    "A case-only rename must not leave any extra folder in the source directory");
            }
            finally { Directory.Delete(dir, true); }
        }
    }
}
