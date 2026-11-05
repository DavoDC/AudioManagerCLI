using System;
using System.IO;

namespace AudioManager
{
    /// <summary>
    /// Unit coverage for ReplaceGuard.SweepOrphans, the startup sweep that restores a staged
    /// ".replaced.bak" only when its library sibling is missing. Uses real temp dirs only.
    /// </summary>
    internal static class ReplaceGuardSweepTests
    {
        private static string MakeTempDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), "AudioManagerTest_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            return dir;
        }

        public static void Sweep_BakWithMissingSibling_IsRestoredWithSameBytes()
        {
            string dir = MakeTempDir();
            try
            {
                string libPath = Path.Combine(dir, "Track.mp3");
                string bakPath = libPath + ".replaced.bak";
                byte[] payload = new byte[] { 4, 5, 6, 7 };
                File.WriteAllBytes(bakPath, payload);

                int restored = ReplaceGuard.SweepOrphans(dir);

                Assert.Equal("1", restored.ToString(), "sweep must count one restored bak");
                Assert.True(File.Exists(libPath), "sweep must restore the library copy when its sibling is missing");
                Assert.True(!File.Exists(bakPath), "sweep must consume the bak it restored");
                Assert.Equal(Convert.ToBase64String(payload), Convert.ToBase64String(File.ReadAllBytes(libPath)), "restored bytes must match the bak");
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            }
        }

        public static void Sweep_BakWithExistingSibling_LeavesBothUntouched()
        {
            string dir = MakeTempDir();
            try
            {
                string libPath = Path.Combine(dir, "Track.mp3");
                string bakPath = libPath + ".replaced.bak";
                byte[] libBytes = new byte[] { 1, 2, 3 };
                byte[] bakBytes = new byte[] { 9 };
                File.WriteAllBytes(libPath, libBytes);
                File.WriteAllBytes(bakPath, bakBytes);

                int restored = ReplaceGuard.SweepOrphans(dir);

                Assert.Equal("0", restored.ToString(), "sweep must not count a bak whose sibling exists");
                Assert.True(File.Exists(bakPath), "sweep must keep the bak when the library copy exists");
                Assert.Equal(Convert.ToBase64String(libBytes), Convert.ToBase64String(File.ReadAllBytes(libPath)), "library copy bytes must be unchanged");
                Assert.Equal(Convert.ToBase64String(bakBytes), Convert.ToBase64String(File.ReadAllBytes(bakPath)), "bak bytes must be unchanged");
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            }
        }

        public static void Sweep_NonexistentRoot_ReturnsZero()
        {
            string missing = Path.Combine(Path.GetTempPath(), "AudioManagerTest_missing_" + Guid.NewGuid().ToString("N").Substring(0, 8));

            int restored = ReplaceGuard.SweepOrphans(missing);

            Assert.Equal("0", restored.ToString(), "sweep over a nonexistent root must return 0");
            Assert.True(!Directory.Exists(missing), "sweep must not create the missing root");
        }

        public static void Sweep_BakInNestedFolder_IsRestoredInPlace()
        {
            string dir = MakeTempDir();
            try
            {
                string nested = Path.Combine(dir, "Artist", "Album");
                Directory.CreateDirectory(nested);
                string libPath = Path.Combine(nested, "Song.flac");
                string bakPath = libPath + ".replaced.bak";
                File.WriteAllBytes(bakPath, new byte[] { 8, 8 });

                int restored = ReplaceGuard.SweepOrphans(dir);

                Assert.Equal("1", restored.ToString(), "sweep must count a nested bak it restores");
                Assert.True(File.Exists(libPath), "sweep must restore the library copy beside its nested bak");
                Assert.True(!File.Exists(bakPath), "sweep must consume the nested bak");
                Assert.Equal("2", new FileInfo(libPath).Length.ToString(), "restored nested file must keep its length");
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            }
        }
    }
}
