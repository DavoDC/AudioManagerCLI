using System;
using System.IO;

namespace AudioManager
{
    internal static class ReflectorSwapTests
    {
        private static string NewTempRoot()
        {
            return Path.Combine(Path.GetTempPath(), "am_swap_" + Guid.NewGuid().ToString("N").Substring(0, 8));
        }

        private static void Cleanup(string root)
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }

        public static void SwapDirectory_ReplacesContentsAndLeavesNoOld()
        {
            string root = NewTempRoot();
            try
            {
                string target = Path.Combine(root, "mirror");
                string staging = Path.Combine(root, "mirror.staging");
                Directory.CreateDirectory(target);
                File.WriteAllText(Path.Combine(target, "old.xml"), "old");
                Directory.CreateDirectory(staging);
                File.WriteAllText(Path.Combine(staging, "new.xml"), "new");

                Reflector.SwapDirectory(staging, target);

                Assert.True(File.Exists(Path.Combine(target, "new.xml")), "staged content is now at target");
                Assert.True(!File.Exists(Path.Combine(target, "old.xml")), "old content is gone from target");
                Assert.True(!Directory.Exists(target + ".old"), "the .old backup is deleted after a successful swap");
                Assert.True(!Directory.Exists(staging), "staging was moved, not copied");
            }
            finally { Cleanup(root); }
        }

        public static void SwapDirectory_TargetMissing_JustMovesStaging()
        {
            string root = NewTempRoot();
            try
            {
                string target = Path.Combine(root, "mirror");
                string staging = Path.Combine(root, "mirror.staging");
                Directory.CreateDirectory(staging);
                File.WriteAllText(Path.Combine(staging, "new.xml"), "new");

                Reflector.SwapDirectory(staging, target);

                Assert.True(File.Exists(Path.Combine(target, "new.xml")), "staging becomes target when target was missing");
                Assert.True(!Directory.Exists(staging), "staging no longer exists after the move");
                Assert.True(!Directory.Exists(target + ".old"), "no .old is created when there was no target");
            }
            finally { Cleanup(root); }
        }

        public static void SwapDirectory_SecondMoveFails_RestoresOriginal()
        {
            string root = NewTempRoot();
            try
            {
                string target = Path.Combine(root, "mirror");
                string missingStaging = Path.Combine(root, "never.staging");
                Directory.CreateDirectory(target);
                File.WriteAllText(Path.Combine(target, "old.xml"), "old");

                bool threw = false;
                try { Reflector.SwapDirectory(missingStaging, target); }
                catch (Exception) { threw = true; }

                Assert.True(threw, "a missing staging folder must make the swap throw");
                Assert.True(File.Exists(Path.Combine(target, "old.xml")), "original target is restored after the second move fails");
                Assert.True(!Directory.Exists(target + ".old"), ".old is moved back, not left behind");
            }
            finally { Cleanup(root); }
        }

        public static void RecoverLeftovers_RemovesStaleStaging()
        {
            string root = NewTempRoot();
            try
            {
                string target = Path.Combine(root, "mirror");
                string staging = target + ".staging";
                Directory.CreateDirectory(staging);
                File.WriteAllText(Path.Combine(staging, "junk.xml"), "junk");

                Reflector.RecoverLeftovers(target);

                Assert.True(!Directory.Exists(staging), "a stale .staging folder is always disposable and removed");
                Assert.True(!Directory.Exists(target), "target is not created by the sweep");
            }
            finally { Cleanup(root); }
        }

        public static void RecoverLeftovers_OrphanOld_IsRestored()
        {
            string root = NewTempRoot();
            try
            {
                string target = Path.Combine(root, "mirror");
                string old = target + ".old";
                Directory.CreateDirectory(old);
                File.WriteAllText(Path.Combine(old, "backup.xml"), "backup");

                Reflector.RecoverLeftovers(target);

                Assert.True(File.Exists(Path.Combine(target, "backup.xml")), "an orphan .old with no target is moved back to target");
                Assert.True(!Directory.Exists(old), "the .old folder no longer exists after restore");
            }
            finally { Cleanup(root); }
        }

        public static void RecoverLeftovers_OldAndTargetBothExist_DeletesOld()
        {
            string root = NewTempRoot();
            try
            {
                string target = Path.Combine(root, "mirror");
                string old = target + ".old";
                Directory.CreateDirectory(target);
                File.WriteAllText(Path.Combine(target, "current.xml"), "current");
                Directory.CreateDirectory(old);
                File.WriteAllText(Path.Combine(old, "stale.xml"), "stale");

                Reflector.RecoverLeftovers(target);

                Assert.True(!Directory.Exists(old), "a .old next to a live target is deleted");
                Assert.True(File.Exists(Path.Combine(target, "current.xml")), "the live target is left untouched");
                Assert.True(!File.Exists(Path.Combine(target, "stale.xml")), "stale backup content is not restored over the target");
            }
            finally { Cleanup(root); }
        }
    }
}
