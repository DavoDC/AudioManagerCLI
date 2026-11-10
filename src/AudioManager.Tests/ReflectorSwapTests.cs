using System;
using System.Globalization;
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
                string staging = Path.Combine(root, "mirror" + Reflector.StagingSuffix);
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
                string staging = Path.Combine(root, "mirror" + Reflector.StagingSuffix);
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
                string missingStaging = Path.Combine(root, "never" + Reflector.StagingSuffix);
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
                string staging = target + Reflector.LegacyStagingSuffix;
                Directory.CreateDirectory(staging);
                File.WriteAllText(Path.Combine(staging, "junk.xml"), "junk");

                Reflector.RecoverLeftovers(target);

                Assert.True(!Directory.Exists(staging), "a stale legacy .staging folder is always disposable and removed");
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

        // --- Staged rebuild (Reflector regen builds in .staging, swaps, then stamps) ---

        private static string RepoRootFor(string root)
        {
            return Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        }

        public static void IsInsideMirrorRepo_StagingAndOldCountAsInside()
        {
            Assert.True(Reflector.IsInsideMirrorRepo(Constants.MirrorFolderPath + Reflector.StagingSuffix), "staging sibling is inside the mirror repo");
            Assert.True(Reflector.IsInsideMirrorRepo(Constants.MirrorFolderPath + ".old"), ".old sibling is inside the mirror repo");
        }

        public static void RecoverLeftovers_OldRestoredWhenTargetMissingAndStagingGone()
        {
            string root = NewTempRoot();
            try
            {
                string target = Path.Combine(root, "mirror");
                Directory.CreateDirectory(target + ".old");
                File.WriteAllText(Path.Combine(target + ".old", "backup.xml"), "backup");

                Reflector.RecoverLeftovers(target);

                Assert.True(File.Exists(Path.Combine(target, "backup.xml")), "crash after parking .old restores it when staging is gone");
                Assert.True(!Directory.Exists(target + Reflector.StagingSuffix), "no staging folder appears");
            }
            finally { Cleanup(root); }
        }

        private static string MakeAudio(string root)
        {
            string audio = Path.Combine(root, "Audio");
            Directory.CreateDirectory(Path.Combine(audio, "Artist"));
            File.WriteAllText(Path.Combine(audio, "Artist", "a.mp3"), "x");
            return audio;
        }

        public static void Reflector_ForcedRegen_EndToEnd_SwapsAndStamps()
        {
            string root = NewTempRoot();
            bool saved = AgeChecker.RegenMirror;
            try
            {
                string audio = MakeAudio(root);
                string mirror = Path.Combine(root, "AUDIO_MIRROR");
                string stamp = Path.Combine(root, "LastRunInfo.txt");
                Directory.CreateDirectory(mirror);
                File.WriteAllText(Path.Combine(mirror, "stale.xml"), "stale");
                AgeChecker.RegenMirror = true;

                new Reflector(audio, mirror, RepoRootFor(root), stamp);

                Assert.True(File.Exists(Path.Combine(mirror, "Artist", "a.xml")), "new XML is in the target");
                Assert.True(!File.Exists(Path.Combine(mirror, "stale.xml")), "stale XML is gone after a regen");
                Assert.True(!Directory.Exists(mirror + Reflector.StagingSuffix), "no staging folder left");
                Assert.True(!Directory.Exists(mirror + ".old"), "no .old left");
                Assert.True(File.Exists(stamp), "stamp is written after the swap");
                DateTime parsed;
                Assert.True(DateTime.TryParseExact(File.ReadAllText(stamp).Trim(), "yyyy-MM-dd HH:mm:ss",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed), "stamp is in the fixed format");
            }
            finally { AgeChecker.RegenMirror = saved; Cleanup(root); }
        }

        public static void Reflector_ForcedRegen_FailedBuild_KeepsLiveMirror()
        {
            string root = NewTempRoot();
            bool saved = AgeChecker.RegenMirror;
            try
            {
                string mirror = Path.Combine(root, "AUDIO_MIRROR");
                Directory.CreateDirectory(mirror);
                File.WriteAllText(Path.Combine(mirror, "live.xml"), "live");
                AgeChecker.RegenMirror = true;

                bool threw = false;
                try { new Reflector(Path.Combine(root, "NoSuchAudio"), mirror, RepoRootFor(root), Path.Combine(root, "LastRunInfo.txt")); }
                catch (Exception) { threw = true; }

                Assert.True(threw, "a missing audio root fails the build");
                Assert.True(File.Exists(Path.Combine(mirror, "live.xml")), "the live mirror is not deleted by a failed rebuild");
            }
            finally { AgeChecker.RegenMirror = saved; Cleanup(root); }
        }

        public static void Reflector_SecondBuildWhileFirstRuns_RefusesAndLeavesStagingAlone()
        {
            string root = NewTempRoot();
            bool saved = AgeChecker.RegenMirror;
            try
            {
                string audio = MakeAudio(root);
                string mirror = Path.Combine(root, "AUDIO_MIRROR");
                Directory.CreateDirectory(mirror);
                string staging = mirror + Reflector.StagingSuffix;
                Directory.CreateDirectory(staging);
                File.WriteAllText(Path.Combine(staging, "other-run.xml"), "in progress");
                AgeChecker.RegenMirror = true;

                bool threw = false;
                using (Reflector.AcquireBuildLock(mirror)) // stands in for a running build
                {
                    try { new Reflector(audio, mirror, RepoRootFor(root), null); }
                    catch (IOException) { threw = true; }
                }

                Assert.True(threw, "a second build must refuse while another holds the lock");
                Assert.True(File.Exists(Path.Combine(staging, "other-run.xml")), "the running build's staging is not deleted");
                using (Reflector.AcquireBuildLock(mirror)) { } // released afterwards
            }
            finally { AgeChecker.RegenMirror = saved; Cleanup(root); }
        }

        public static void Reflector_ForcedRegen_EmptyAudioRoot_RefusesAndKeepsLiveMirror()
        {
            string root = NewTempRoot();
            bool saved = AgeChecker.RegenMirror;
            try
            {
                string audio = Path.Combine(root, "Audio");
                Directory.CreateDirectory(audio); // exists but holds no mp3 (unmounted volume, wrong folder)
                string mirror = Path.Combine(root, "AUDIO_MIRROR");
                string stamp = Path.Combine(root, "LastRunInfo.txt");
                Directory.CreateDirectory(mirror);
                File.WriteAllText(Path.Combine(mirror, "live.xml"), "live");
                AgeChecker.RegenMirror = true;

                bool threw = false;
                try { new Reflector(audio, mirror, RepoRootFor(root), stamp); }
                catch (Exception) { threw = true; }

                Assert.True(threw, "an audio root with no mp3 must not be swapped in as the mirror");
                Assert.True(File.Exists(Path.Combine(mirror, "live.xml")), "the live mirror survives");
                Assert.True(!File.Exists(stamp), "no stamp is written");
                Assert.True(!Directory.Exists(mirror + Reflector.StagingSuffix), "no staging folder left behind");
            }
            finally { AgeChecker.RegenMirror = saved; Cleanup(root); }
        }

        public static void Reflector_ForcedRegen_FailedBuild_LeavesStampUntouched()
        {
            string root = NewTempRoot();
            bool saved = AgeChecker.RegenMirror;
            try
            {
                string mirror = Path.Combine(root, "AUDIO_MIRROR");
                string stamp = Path.Combine(root, "LastRunInfo.txt");
                Directory.CreateDirectory(mirror);
                File.WriteAllText(stamp, "2020-01-02 03:04:05");
                AgeChecker.RegenMirror = true;

                try { new Reflector(Path.Combine(root, "NoSuchAudio"), mirror, RepoRootFor(root), stamp); }
                catch (Exception) { }

                Assert.Equal("2020-01-02 03:04:05", File.ReadAllText(stamp));
            }
            finally { AgeChecker.RegenMirror = saved; Cleanup(root); }
        }
    }
}
