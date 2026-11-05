using System;
using System.IO;

namespace AudioManager
{
    /// <summary>
    /// Fault-path coverage for ReplaceGuard: a missing or locked target, a locked staged backup,
    /// rollback with nothing staged or after a commit, and sweeps that must leave unrelated files
    /// alone. Uses real temp dirs only, never the real library.
    /// </summary>
    internal static class ReplaceGuardFaultTests
    {
        private static string MakeTempDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), "AudioManagerTest_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static void Cleanup(string dir)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
            catch (Exception) { }
        }

        private static FileStream HoldExclusive(string path)
        {
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
        }

        private static string Base64(string path)
        {
            return Convert.ToBase64String(File.ReadAllBytes(path));
        }

        public static void ReplaceGuard_StageMissingTarget_ThrowsAndCreatesNoBak()
        {
            string dir = MakeTempDir();
            try
            {
                string libPath = Path.Combine(dir, "Track.mp3");
                string bakPath = libPath + ".replaced.bak";

                bool threw = false;
                try { ReplaceGuard.Stage(libPath); }
                catch (Exception) { threw = true; }

                Assert.True(threw, "stage must throw when the library target is missing");
                Assert.True(!File.Exists(bakPath), "a failed stage must not create a bak");
                Assert.Equal("0", Directory.GetFiles(dir).Length.ToString(), "a failed stage must leave the folder empty");
            }
            finally { Cleanup(dir); }
        }

        public static void ReplaceGuard_StageLockedTarget_ThrowsAndOriginalIntact()
        {
            string dir = MakeTempDir();
            try
            {
                string libPath = Path.Combine(dir, "Track.mp3");
                string bakPath = libPath + ".replaced.bak";
                File.WriteAllBytes(libPath, new byte[] { 1, 2, 3 });

                bool threw = false;
                FileStream held = null;
                try
                {
                    held = HoldExclusive(libPath);
                    try { ReplaceGuard.Stage(libPath); }
                    catch (Exception) { threw = true; }
                }
                finally
                {
                    if (held != null) held.Dispose();
                }

                Assert.True(threw, "stage must throw when the library file is locked");
                Assert.Equal(Convert.ToBase64String(new byte[] { 1, 2, 3 }), Base64(libPath), "a locked original must keep its bytes");
                Assert.True(!File.Exists(bakPath), "a failed stage must not leave a bak behind");
            }
            finally { Cleanup(dir); }
        }

        public static void ReplaceGuard_CommitLockedBak_SwallowsErrorAndKeepsBakBytes()
        {
            string dir = MakeTempDir();
            try
            {
                string libPath = Path.Combine(dir, "Track.mp3");
                File.WriteAllBytes(libPath, new byte[] { 1, 2, 3 });
                string bakPath = ReplaceGuard.Stage(libPath);

                FileStream held = null;
                try
                {
                    held = HoldExclusive(bakPath);
                    ReplaceGuard.Commit(bakPath);
                }
                finally
                {
                    if (held != null) held.Dispose();
                }

                Assert.True(File.Exists(bakPath), "a bak that cannot be deleted must stay on disk");
                Assert.Equal(Convert.ToBase64String(new byte[] { 1, 2, 3 }), Base64(bakPath), "the kept bak must keep its bytes");
                Assert.True(!File.Exists(libPath), "commit must not restore the library copy");
            }
            finally { Cleanup(dir); }
        }

        public static void ReplaceGuard_RollbackAfterFailedCommit_NeverOverwritesReplacement()
        {
            string dir = MakeTempDir();
            try
            {
                string libPath = Path.Combine(dir, "Track.mp3");
                File.WriteAllBytes(libPath, new byte[] { 1, 2, 3 });
                string bakPath = ReplaceGuard.Stage(libPath);
                File.WriteAllBytes(libPath, new byte[] { 9, 9 });

                FileStream held = null;
                try
                {
                    held = HoldExclusive(bakPath);
                    ReplaceGuard.Commit(bakPath);
                }
                finally
                {
                    if (held != null) held.Dispose();
                }

                ReplaceGuard.Rollback(bakPath, libPath);

                Assert.Equal(Convert.ToBase64String(new byte[] { 9, 9 }), Base64(libPath), "rollback must never overwrite the landed replacement");
                Assert.True(File.Exists(bakPath), "rollback must keep the bak when the library path is occupied");
                Assert.Equal(Convert.ToBase64String(new byte[] { 1, 2, 3 }), Base64(bakPath), "the kept bak must keep the original bytes");
            }
            finally { Cleanup(dir); }
        }

        public static void ReplaceGuard_RollbackNothingStaged_LeavesLibraryIntact()
        {
            string dir = MakeTempDir();
            try
            {
                string libPath = Path.Combine(dir, "Track.mp3");
                string bakPath = libPath + ".replaced.bak";
                File.WriteAllBytes(libPath, new byte[] { 5, 5, 5 });

                ReplaceGuard.Rollback(bakPath, libPath);

                Assert.Equal(Convert.ToBase64String(new byte[] { 5, 5, 5 }), Base64(libPath), "rollback with nothing staged must leave the library copy alone");
                Assert.True(!File.Exists(bakPath), "rollback with nothing staged must not create a bak");
                Assert.Equal("1", Directory.GetFiles(dir).Length.ToString(), "rollback with nothing staged must not add files");
            }
            finally { Cleanup(dir); }
        }

        public static void ReplaceGuard_RollbackAfterCommit_KeepsReplacement()
        {
            string dir = MakeTempDir();
            try
            {
                string libPath = Path.Combine(dir, "Track.mp3");
                File.WriteAllBytes(libPath, new byte[] { 1, 2, 3 });
                string bakPath = ReplaceGuard.Stage(libPath);
                File.WriteAllBytes(libPath, new byte[] { 9, 9 });
                ReplaceGuard.Commit(bakPath);

                ReplaceGuard.Rollback(bakPath, libPath);

                Assert.Equal(Convert.ToBase64String(new byte[] { 9, 9 }), Base64(libPath), "a rollback after a committed replace must not resurrect the old copy");
                Assert.True(!File.Exists(bakPath), "a committed bak must stay gone after rollback");
                Assert.Equal("1", Directory.GetFiles(dir).Length.ToString(), "only the replacement may remain in the folder");
            }
            finally { Cleanup(dir); }
        }

        public static void ReplaceGuard_RollbackLockedBak_KeepsBakAndDoesNotThrow()
        {
            string dir = MakeTempDir();
            try
            {
                string libPath = Path.Combine(dir, "Track.mp3");
                string bakPath = libPath + ".replaced.bak";
                File.WriteAllBytes(bakPath, new byte[] { 7, 7 });

                FileStream held = null;
                try
                {
                    held = HoldExclusive(bakPath);
                    ReplaceGuard.Rollback(bakPath, libPath);
                }
                finally
                {
                    if (held != null) held.Dispose();
                }

                Assert.True(!File.Exists(libPath), "a locked bak must not be half-restored into the library path");
                Assert.Equal(Convert.ToBase64String(new byte[] { 7, 7 }), Base64(bakPath), "a locked bak must keep its bytes for a later retry");
            }
            finally { Cleanup(dir); }
        }

        public static void ReplaceGuard_SweepLockedBak_NotCountedAndKept()
        {
            string dir = MakeTempDir();
            try
            {
                string libPath = Path.Combine(dir, "Track.mp3");
                string bakPath = libPath + ".replaced.bak";
                File.WriteAllBytes(bakPath, new byte[] { 8, 8 });

                int restored;
                FileStream held = null;
                try
                {
                    held = HoldExclusive(bakPath);
                    restored = ReplaceGuard.SweepOrphans(dir);
                }
                finally
                {
                    if (held != null) held.Dispose();
                }

                Assert.Equal("0", restored.ToString(), "sweep must not count a locked bak it could not restore");
                Assert.True(!File.Exists(libPath), "sweep must not create the library copy when the move failed");
                Assert.Equal(Convert.ToBase64String(new byte[] { 8, 8 }), Base64(bakPath), "a locked bak must keep its bytes");
            }
            finally { Cleanup(dir); }
        }

        public static void ReplaceGuard_SweepLeavesUnrelatedFilesAlone()
        {
            string dir = MakeTempDir();
            try
            {
                string trackPath = Path.Combine(dir, "Track.mp3");
                string notesPath = Path.Combine(dir, "Notes.txt");
                string oldBakPath = Path.Combine(dir, "Other.replaced.bak.old");
                File.WriteAllBytes(trackPath, new byte[] { 1, 2, 3 });
                File.WriteAllBytes(notesPath, new byte[] { 4 });
                File.WriteAllBytes(oldBakPath, new byte[] { 6 });

                int restored = ReplaceGuard.SweepOrphans(dir);

                Assert.Equal("0", restored.ToString(), "sweep must not restore anything when no bak exists");
                Assert.Equal(Convert.ToBase64String(new byte[] { 1, 2, 3 }), Base64(trackPath), "an ordinary library file must be untouched");
                Assert.Equal(Convert.ToBase64String(new byte[] { 4 }), Base64(notesPath), "an unrelated file must be untouched");
                Assert.Equal(Convert.ToBase64String(new byte[] { 6 }), Base64(oldBakPath), "a name that does not end in .replaced.bak must be ignored");
                Assert.Equal("3", Directory.GetFiles(dir).Length.ToString(), "sweep must neither add nor remove files");
            }
            finally { Cleanup(dir); }
        }
    }
}
