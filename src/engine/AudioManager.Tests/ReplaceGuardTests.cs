using System;
using System.IO;

namespace AudioManager
{
    /// <summary>
    /// Unit coverage for ReplaceGuard's stage-then-commit primitives used by the L (replace
    /// library copy) duplicate decision. Uses real temp files only, never the real library.
    /// </summary>
    internal static class ReplaceGuardTests
    {
        private static string MakeTempDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), "AudioManagerTest_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            return dir;
        }

        public static void Stage_MovesLibraryFileToBak_OriginalGone()
        {
            string dir = MakeTempDir();
            try
            {
                string libPath = Path.Combine(dir, "Track.mp3");
                File.WriteAllBytes(libPath, new byte[] { 1, 2, 3 });

                string bakPath = ReplaceGuard.Stage(libPath);

                Assert.Equal(libPath + ".replaced.bak", bakPath, "stage must return the .replaced.bak path");
                Assert.True(File.Exists(bakPath), "bak must hold the staged bytes");
                Assert.True(!File.Exists(libPath), "original library path must be vacated by staging");
                Assert.Equal("3", new FileInfo(bakPath).Length.ToString(), "staged bak must keep the original length");
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            }
        }

        public static void Stage_ExistingBak_ThrowsAndOriginalUntouched()
        {
            string dir = MakeTempDir();
            try
            {
                string libPath = Path.Combine(dir, "Track.mp3");
                string bakPath = libPath + ".replaced.bak";
                File.WriteAllBytes(libPath, new byte[] { 1, 2, 3 });
                File.WriteAllBytes(bakPath, new byte[] { 9 });

                bool threw = false;
                try { ReplaceGuard.Stage(libPath); }
                catch (Exception) { threw = true; }

                Assert.True(threw, "stage must throw when a bak already exists");
                Assert.True(File.Exists(libPath), "original library file must be untouched when stage refuses");
                Assert.Equal("3", new FileInfo(libPath).Length.ToString(), "original bytes must be intact");
                Assert.Equal("1", new FileInfo(bakPath).Length.ToString(), "existing bak must never be overwritten");
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            }
        }

        public static void Commit_DeletesBak_MissingBakDoesNotThrow()
        {
            string dir = MakeTempDir();
            try
            {
                string bakPath = Path.Combine(dir, "Track.mp3.replaced.bak");
                File.WriteAllBytes(bakPath, new byte[] { 1 });

                ReplaceGuard.Commit(bakPath);
                Assert.True(!File.Exists(bakPath), "commit must delete the bak");

                ReplaceGuard.Commit(bakPath);
                Assert.True(!File.Exists(bakPath), "commit on a missing bak must be a silent no-op");
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            }
        }

        public static void Rollback_RestoresWhenLibPathAbsent()
        {
            string dir = MakeTempDir();
            try
            {
                string libPath = Path.Combine(dir, "Track.mp3");
                string bakPath = libPath + ".replaced.bak";
                File.WriteAllBytes(bakPath, new byte[] { 7, 7 });

                ReplaceGuard.Rollback(bakPath, libPath);

                Assert.True(File.Exists(libPath), "rollback must restore the library copy");
                Assert.True(!File.Exists(bakPath), "rollback must consume the bak");
                Assert.Equal("2", new FileInfo(libPath).Length.ToString(), "restored file must keep its bytes");
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            }
        }

        public static void Rollback_LibPathExists_LeavesBakInPlace()
        {
            string dir = MakeTempDir();
            try
            {
                string libPath = Path.Combine(dir, "Track.mp3");
                string bakPath = libPath + ".replaced.bak";
                File.WriteAllBytes(bakPath, new byte[] { 7 });
                File.WriteAllBytes(libPath, new byte[] { 5, 5, 5 });

                ReplaceGuard.Rollback(bakPath, libPath);

                Assert.True(File.Exists(bakPath), "rollback must keep the bak when the library path is occupied");
                Assert.Equal("3", new FileInfo(libPath).Length.ToString(), "occupying library file must not be overwritten");
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            }
        }

        public static void Stage_StaleBakProvenLanded_ResolvesThenStages()
        {
            string dir = MakeTempDir();
            try
            {
                string libPath = Path.Combine(dir, "Track.mp3");
                string bakPath = libPath + ".replaced.bak";
                string markerPath = libPath + ".replaced.dest";
                string otherPath = Path.Combine(dir, "Other.mp3");
                byte[] libBytes = new byte[] { 1, 2, 3 };
                File.WriteAllBytes(libPath, libBytes);
                File.WriteAllBytes(bakPath, new byte[] { 9 });
                File.WriteAllBytes(otherPath, new byte[] { 5 });
                File.WriteAllText(markerPath, otherPath);

                string staged = ReplaceGuard.Stage(libPath);

                Assert.Equal(bakPath, staged, "stage must succeed and return the .replaced.bak path");
                Assert.True(File.Exists(bakPath), "stage must leave a new bak in place");
                Assert.Equal(Convert.ToBase64String(libBytes), Convert.ToBase64String(File.ReadAllBytes(bakPath)), "new bak must hold libPath's bytes, not the stale bak's");
                Assert.True(!File.Exists(markerPath), "the proven-landed marker must be gone");
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            }
        }

        public static void Rollback_DeletesMarkerBeforeRestoring()
        {
            string dir = MakeTempDir();
            try
            {
                string libPath = Path.Combine(dir, "Track.mp3");
                string bakPath = libPath + ".replaced.bak";
                string markerPath = libPath + ".replaced.dest";
                string otherPath = Path.Combine(dir, "Other.mp3");
                byte[] bakBytes = new byte[] { 7, 7 };
                File.WriteAllBytes(bakPath, bakBytes);
                File.WriteAllBytes(otherPath, new byte[] { 4 });
                File.WriteAllText(markerPath, otherPath);

                ReplaceGuard.Rollback(bakPath, libPath);

                Assert.True(File.Exists(libPath), "rollback must restore the library copy");
                Assert.Equal(Convert.ToBase64String(bakBytes), Convert.ToBase64String(File.ReadAllBytes(libPath)), "restored library copy must hold the bak's bytes");
                Assert.True(!File.Exists(markerPath), "rollback must delete the marker before restoring");

                int restored = ReplaceGuard.SweepOrphans(dir);

                Assert.Equal("0", restored.ToString(), "sweep after rollback must restore nothing");
                Assert.True(File.Exists(libPath), "sweep after rollback must leave the library copy alone");
                Assert.True(File.Exists(otherPath), "sweep after rollback must delete nothing");
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            }
        }
    }
}
