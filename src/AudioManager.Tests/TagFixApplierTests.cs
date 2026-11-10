using System;
using System.IO;
using System.Linq;

namespace AudioManager
{
    /// <summary>
    /// Tests for the guarded Tag Fix apply and revert (TagFixApplier). Every test works on a temp fixture
    /// folder, never the real NewMusic.
    /// </summary>
    internal static class TagFixApplierTests
    {
        private static readonly DateTime Now = new DateTime(2026, 11, 10, 12, 0, 0);

        private class Env
        {
            public string Root, Folder, Logs, Backups, Quarantine;
        }

        private static Env NewEnv()
        {
            string root = Path.Combine(Path.GetTempPath(), "AudioManagerApply_" + Guid.NewGuid());
            var e = new Env
            {
                Root = root,
                Folder = Path.Combine(root, "NewMusic"),
                Logs = Path.Combine(root, "logs"),
                Backups = Path.Combine(root, "backups"),
                Quarantine = Path.Combine(root, "NewMusic-Quarantine"),
            };
            Directory.CreateDirectory(e.Folder);
            return e;
        }

        private static string Plan(Env e)
        {
            var tf = TagFixer.ForFolder(e.Folder, true, e.Quarantine);
            return TagFixPlanJson.Fingerprint(e.Folder, tf.Scanned);
        }

        private static string Messy(Env e, string name = "messy.mp3") =>
            TagFixerTests.CreateSilentMp3Fixture(e.Folder, name, "Song Title (feat. Guest)", "Album (Deluxe Edition)", "Main Artist");

        private static void Cleanup(Env e) { try { Directory.Delete(e.Root, true); } catch (IOException) { } }

        public static void ScanFolder_FileFilterExcludesFile_LeavesItByteIdentical()
        {
            var e = NewEnv();
            try
            {
                string path = Messy(e);
                byte[] before = File.ReadAllBytes(path);
                var tf = TagFixer.ScanFolderForTesting(e.Folder, false, quarantineRoot: e.Quarantine, fileFilter: f => false);
                Assert.True(File.Exists(path), "a file the filter excludes must keep its name");
                Assert.Equal(Convert.ToBase64String(before), Convert.ToBase64String(File.ReadAllBytes(path)), "a file the filter excludes must not be rewritten");
                Assert.Equal("0", tf.Scanned.Count.ToString(), "an excluded file is not scanned");
            }
            finally { Cleanup(e); }
        }

        public static void ProcessFile_StaleTagfixBak_IsNeverDeleted()
        {
            var e = NewEnv();
            try
            {
                string path = Messy(e);
                string stale = path + ".tagfix.bak";
                File.WriteAllText(stale, "ONLY-GOOD-COPY");
                TagFixer.ForTesting(dryRun: false, quarantineRoot: e.Quarantine).ProcessFile(path);
                bool kept = Directory.GetFiles(e.Folder, "*.tagfix.bak*").Any(f => File.ReadAllText(f) == "ONLY-GOOD-COPY");
                Assert.True(kept, "a stale .tagfix.bak from a crashed run must be kept (moved aside), never deleted");
            }
            finally { Cleanup(e); }
        }

        public static void Apply_ExistingBackupOfSameStamp_IsNotOverwritten()
        {
            var e = NewEnv();
            try
            {
                string path = Messy(e);
                string fp = Plan(e);
                string stamp = Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
                string backup = Path.Combine(e.Backups, stamp, "messy.mp3");
                Directory.CreateDirectory(Path.GetDirectoryName(backup));
                File.WriteAllText(backup, "FIRST-RUN-ORIGINAL");
                TagFixApplier.Apply(e.Folder, fp, e.Logs, e.Backups, e.Quarantine, Now);
                Assert.Equal("FIRST-RUN-ORIGINAL", File.ReadAllText(backup), "an existing backup in the same run folder must not be overwritten");
            }
            finally { Cleanup(e); }
        }

        public static void Apply_StalePlan_RefusesAndTouchesNothing()
        {
            var e = NewEnv();
            try
            {
                string path = Messy(e);
                string fp = Plan(e);
                DateTime before = new FileInfo(path).LastWriteTimeUtc;
                File.SetLastWriteTimeUtc(path, before.AddMinutes(5)); // folder changed after the plan was made

                var r = TagFixApplier.Apply(e.Folder, fp, e.Logs, e.Backups, e.Quarantine, Now);

                Assert.Equal("stale_plan", r.Status, "status");
                Assert.True(r.PlanFingerprint == fp && r.CurrentFingerprint != fp, "old and new fingerprint carried");
                Assert.True(File.Exists(path), "file untouched");
                using (var f = TagLib.File.Create(path)) Assert.Equal("Song Title (feat. Guest)", f.Tag.Title, "tags untouched");
                string json = File.ReadAllText(Path.Combine(e.Logs, TagFixApplier.ResultFileName));
                Assert.True(json.Contains("\"status\": \"stale_plan\"") && json.Contains("\"oldFingerprint\"") && json.Contains("\"newFingerprint\""), "result json");
                Assert.True(!Directory.Exists(e.Backups) || !Directory.GetDirectories(e.Backups).Any(), "no backup made");
            }
            finally { Cleanup(e); }
        }

        public static void Apply_FreshPlan_FixesBacksUpAndJournals()
        {
            var e = NewEnv();
            try
            {
                string path = Messy(e);
                byte[] original = File.ReadAllBytes(path);
                var r = TagFixApplier.Apply(e.Folder, Plan(e), e.Logs, e.Backups, e.Quarantine, Now);

                Assert.Equal("applied", r.Status, "status");
                Assert.True(r.Applied == 1 && r.Failed == 0 && r.PendingAfter == 0, "counts: one applied, none pending after");
                Assert.True(File.Exists(r.JournalPath), "journal written");
                string journal = File.ReadAllText(r.JournalPath);
                Assert.True(journal.Contains("\"status\": \"done\"") && journal.Contains("\"oldTitle\": \"Song Title (feat. Guest)\""), "journal records old tags and done");
                string backup = Directory.GetFiles(e.Backups, "*.mp3", SearchOption.AllDirectories).Single();
                Assert.True(File.ReadAllBytes(backup).SequenceEqual(original), "byte backup is the original");
                Assert.True(!File.Exists(path), "file renamed");
            }
            finally { Cleanup(e); }
        }

        public static void Apply_Applied_NewFingerprintIsPostApplyAndOldPlanThenGoesStale()
        {
            var e = NewEnv();
            try
            {
                Messy(e);
                string oldPlan = Plan(e);
                var r = TagFixApplier.Apply(e.Folder, oldPlan, e.Logs, e.Backups, e.Quarantine, Now);
                Assert.Equal("applied", r.Status, "status");
                string after = Plan(e);
                Assert.True(r.NewFingerprint != r.PlanFingerprint, "applying changes must change the fingerprint: " + r.NewFingerprint);
                Assert.Equal(after, r.NewFingerprint, "newFingerprint is the folder fingerprint after the apply");
                string json = File.ReadAllText(Path.Combine(e.Logs, TagFixApplier.ResultFileName));
                Assert.True(json.Contains("\"newFingerprint\": \"" + after + "\""), "result json carries the post-apply fingerprint");
                var again = TagFixApplier.Apply(e.Folder, oldPlan, e.Logs, e.Backups, e.Quarantine, Now.AddMinutes(1));
                Assert.Equal("stale_plan", again.Status, "re-applying the old plan is stale");
            }
            finally { Cleanup(e); }
        }

        public static void Apply_SecondRunAfterReplan_IsIdempotentPendingZero()
        {
            var e = NewEnv();
            try
            {
                Messy(e);
                TagFixApplier.Apply(e.Folder, Plan(e), e.Logs, e.Backups, e.Quarantine, Now);
                var second = TagFixApplier.Apply(e.Folder, Plan(e), e.Logs, e.Backups, e.Quarantine, Now.AddMinutes(1));
                Assert.Equal("nothing_to_do", second.Status, "second run does nothing");
                Assert.True(second.Applied == 0 && second.PendingAfter == 0, "pending 0, applied 0");
            }
            finally { Cleanup(e); }
        }

        public static void Apply_InsufficientDisk_FailsBeforeTouchingAnything()
        {
            var e = NewEnv();
            try
            {
                string path = Messy(e);
                var r = TagFixApplier.Apply(e.Folder, Plan(e), e.Logs, e.Backups, e.Quarantine, Now, freeSpace: _ => 10);
                Assert.Equal("insufficient_disk", r.Status, "status");
                Assert.True(r.BytesFree == 10 && r.BytesNeeded > 10, "bytes reported");
                Assert.True(File.Exists(path), "file untouched");
                using (var f = TagLib.File.Create(path)) Assert.Equal("Song Title (feat. Guest)", f.Tag.Title, "tags untouched");
                Assert.True(r.JournalPath == null, "no journal for a refused apply");
            }
            finally { Cleanup(e); }
        }

        public static void Apply_CrashMidRun_JournalShowsDoneAndPending()
        {
            var e = NewEnv();
            var originalSaver = TagFixer.Saver;
            try
            {
                Messy(e, "a.mp3");
                Messy(e, "b.mp3");
                string fp = Plan(e);
                string journalSeenDuringSecondSave = null;
                int saves = 0;
                string journalDir = e.Logs;
                TagFixer.Saver = f =>
                {
                    saves++;
                    if (saves == 2)
                    {
                        // The process "dies" here: capture what a crash would leave on disk.
                        journalSeenDuringSecondSave = File.ReadAllText(Directory.GetFiles(journalDir, "tagfix-journal-*.json").Single());
                    }
                    f.Save();
                };
                TagFixApplier.Apply(e.Folder, fp, e.Logs, e.Backups, e.Quarantine, Now);

                Assert.True(journalSeenDuringSecondSave != null, "journal existed before the second file was written");
                Assert.True(journalSeenDuringSecondSave.Contains("\"status\": \"done\""), "first file already recorded done");
                Assert.True(journalSeenDuringSecondSave.Contains("\"status\": \"pending\""), "second file still recorded pending");
            }
            finally { TagFixer.Saver = originalSaver; Cleanup(e); }
        }

        public static void Apply_LockedFile_IsReportedAsFailureNotApplied()
        {
            var e = NewEnv();
            try
            {
                Messy(e, "ok.mp3");
                string locked = Messy(e, "locked.mp3");
                string fp = Plan(e);
                using (new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    var r = TagFixApplier.Apply(e.Folder, fp, e.Logs, e.Backups, e.Quarantine, Now);
                    Assert.Equal("partial", r.Status, "locked file makes the run partial");
                    Assert.True(r.Applied == 1 && r.Failed == 1 && r.Failures.Count == 1, "one applied, one failure listed");
                }
            }
            finally { Cleanup(e); }
        }

        public static void Apply_QuarantineIsJournaledAndRevertMovesItBack()
        {
            var e = NewEnv();
            try
            {
                string path = TagFixerTests.CreateSilentMp3Fixture(e.Folder, "inst.mp3", "Track (Instrumental)", "Album", "Akira The Don");
                var r = TagFixApplier.Apply(e.Folder, Plan(e), e.Logs, e.Backups, e.Quarantine, Now);
                Assert.True(r.Quarantined == 1 && !File.Exists(path), "instrumental quarantined");
                Assert.True(Directory.GetFiles(e.Quarantine).Length == 1, "file is in quarantine");

                var rev = TagFixApplier.Revert(r.JournalPath, e.Folder, e.Quarantine);
                Assert.True(rev.Reverted == 1 && File.Exists(path), "revert moved it back");
                Assert.True(Directory.GetFiles(e.Quarantine).Length == 0, "quarantine emptied");
            }
            finally { Cleanup(e); }
        }

        public static void Revert_RestoresOriginalTagsAndName()
        {
            var e = NewEnv();
            try
            {
                string path = Messy(e);
                var r = TagFixApplier.Apply(e.Folder, Plan(e), e.Logs, e.Backups, e.Quarantine, Now);
                var rev = TagFixApplier.Revert(r.JournalPath, e.Folder, e.Quarantine);
                Assert.True(rev.Reverted == 1 && rev.Failed == 0, "reverted");
                Assert.True(File.Exists(path), "original name back");
                using (var f = TagLib.File.Create(path)) Assert.Equal("Song Title (feat. Guest)", f.Tag.Title, "original title back");
                Assert.True(Directory.GetFiles(e.Folder, "*.mp3").Length == 1, "no leftover renamed file");
            }
            finally { Cleanup(e); }
        }

        public static void Revert_FileEditedSinceApply_IsLeftAlone()
        {
            var e = NewEnv();
            try
            {
                Messy(e);
                var r = TagFixApplier.Apply(e.Folder, Plan(e), e.Logs, e.Backups, e.Quarantine, Now);
                string fixedPath = Directory.GetFiles(e.Folder, "*.mp3").Single();
                using (var f = TagLib.File.Create(fixedPath)) { f.Tag.Title = "Edited by hand"; f.Save(); }

                var rev = TagFixApplier.Revert(r.JournalPath, e.Folder, e.Quarantine);
                Assert.True(rev.SkippedModified == 1 && rev.Reverted == 0, "edited file skipped");
                using (var f = TagLib.File.Create(fixedPath)) Assert.Equal("Edited by hand", f.Tag.Title, "manual edit preserved");
            }
            finally { Cleanup(e); }
        }

        public static void PruneOldBackups_RemovesOnlyRunsOlderThanSevenDays()
        {
            var e = NewEnv();
            try
            {
                string oldRun = Path.Combine(e.Backups, "old");
                string newRun = Path.Combine(e.Backups, "new");
                Directory.CreateDirectory(oldRun); Directory.CreateDirectory(newRun);
                Directory.SetCreationTime(oldRun, Now.AddDays(-8));
                Directory.SetCreationTime(newRun, Now.AddDays(-2));
                TagFixApplier.PruneOldBackups(e.Backups, Now);
                Assert.True(!Directory.Exists(oldRun), "8-day-old run pruned");
                Assert.True(Directory.Exists(newRun), "2-day-old run kept");
            }
            finally { Cleanup(e); }
        }

        public static void ReadPlanFingerprint_ReadsValueFromPlanJson()
        {
            var e = NewEnv();
            try
            {
                Messy(e);
                var tf = TagFixer.ForFolder(e.Folder, true, e.Quarantine);
                Directory.CreateDirectory(e.Logs);
                string planPath = Path.Combine(e.Logs, "tagfix-plan.json");
                File.WriteAllText(planPath, TagFixPlanJson.Build(e.Folder, tf.Scanned, tf.Results, Now));
                Assert.Equal(TagFixPlanJson.Fingerprint(e.Folder, tf.Scanned), TagFixApplier.ReadPlanFingerprint(planPath), "fingerprint round trip");
                Assert.True(TagFixApplier.ReadPlanFingerprint(Path.Combine(e.Logs, "missing.json")) == null, "missing plan gives null");
            }
            finally { Cleanup(e); }
        }

        public static void Apply_HeldLockFile_ReturnsApplyLockedAndTouchesNothing()
        {
            var e = NewEnv();
            try
            {
                string path = Messy(e);
                Directory.CreateDirectory(e.Logs);
                string fp = Plan(e);
                using (new FileStream(Path.Combine(e.Logs, "tagfix-apply.lock"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
                {
                    var r = TagFixApplier.Apply(e.Folder, fp, e.Logs, e.Backups, e.Quarantine, Now);
                    Assert.Equal("apply_locked", r.Status, "status");
                    Assert.True(r.Failures.Count == 1, "one failure line explains the lock");
                    Assert.True(!File.Exists(Path.Combine(e.Logs, TagFixApplier.ResultFileName)), "no tagfix-result.json written over an in-progress result");
                    Assert.True(File.Exists(path), "file untouched");
                    using (var f = TagLib.File.Create(path)) Assert.Equal("Song Title (feat. Guest)", f.Tag.Title, "tags untouched");
                    Assert.True(!Directory.Exists(e.Backups) || !Directory.GetDirectories(e.Backups).Any(), "no backup made");
                }
            }
            finally { Cleanup(e); }
        }

        public static void Apply_StaleUnheldLockFile_IsRemovedAndApplyProceeds()
        {
            var e = NewEnv();
            try
            {
                Messy(e);
                Directory.CreateDirectory(e.Logs);
                string lockPath = Path.Combine(e.Logs, "tagfix-apply.lock");
                File.WriteAllText(lockPath, "left behind by a crashed run");
                var r = TagFixApplier.Apply(e.Folder, Plan(e), e.Logs, e.Backups, e.Quarantine, Now);
                Assert.Equal("applied", r.Status, "a stale lock does not block the apply");
                Assert.True(!File.Exists(lockPath), "lock file gone afterwards");
            }
            finally { Cleanup(e); }
        }

        public static void Apply_NormalRun_LeavesNoLockFileBehind()
        {
            var e = NewEnv();
            try
            {
                Messy(e);
                var r = TagFixApplier.Apply(e.Folder, Plan(e), e.Logs, e.Backups, e.Quarantine, Now);
                Assert.Equal("applied", r.Status, "status");
                Assert.True(!File.Exists(Path.Combine(e.Logs, "tagfix-apply.lock")), "no lock file after a normal run");
            }
            finally { Cleanup(e); }
        }
    }
}
