using System;
using System.Globalization;
using System.IO;
using System.Threading;

namespace AudioManager
{
    /// <summary>
    /// Tests for AgeChecker - all 5 decision branches.
    /// Uses a temp lastRunInfo file via the injected path to avoid touching Constants.LastRunInfoFilePath.
    /// Note: AgeChecker.RegenMirror is static; tests are ordered so each sets it explicitly.
    /// </summary>
    internal static class AgeCheckerTests
    {
        private static string TempInfoFile() =>
            Path.Combine(Path.GetTempPath(), $"am_agecheck_{Guid.NewGuid():N}.txt");

        private static string TempStateDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), $"am_lastrun_{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static void DeleteDirQuietly(string dir)
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }

        public static void AgeChecker_NewFileMissing_ReadsLegacyFile()
        {
            // First run after the move: the stamp still sits in the old repo file. A fresh legacy stamp must not force a regen.
            string dir = TempStateDir();
            try
            {
                string newPath = Path.Combine(dir, "state", "LastRunInfo.txt");
                string legacy = Path.Combine(dir, "LastRunInfo.txt");
                File.WriteAllText(legacy, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
                new AgeChecker(forceMirrorRegen: false, lastRunInfoPath: newPath, legacyLastRunInfoPath: legacy);
                Assert.True(!AgeChecker.RegenMirror, "fresh legacy stamp should be read, so no regen is forced");
                Assert.True(!File.Exists(newPath), "AgeChecker only reads: it must not create the new file");
            }
            finally { DeleteDirQuietly(dir); }
        }

        public static void AgeChecker_NewFilePresent_IgnoresLegacyFile()
        {
            // The new file wins: a stale new stamp regenerates even when the legacy file is fresh.
            string dir = TempStateDir();
            try
            {
                string newPath = Path.Combine(dir, "state", "LastRunInfo.txt");
                string legacy = Path.Combine(dir, "LastRunInfo.txt");
                Directory.CreateDirectory(Path.GetDirectoryName(newPath));
                File.WriteAllText(newPath, DateTime.Now.AddDays(-10).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
                File.WriteAllText(legacy, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
                new AgeChecker(forceMirrorRegen: false, lastRunInfoPath: newPath, legacyLastRunInfoPath: legacy);
                Assert.True(AgeChecker.RegenMirror, "the new stale stamp must win over the legacy fresh one");
            }
            finally { DeleteDirQuietly(dir); }
        }

        public static void MarkRegenerated_MissingParentFolder_CreatesItAndWritesOnlyNewPath()
        {
            string dir = TempStateDir();
            try
            {
                string newPath = Path.Combine(dir, "state", "LastRunInfo.txt");
                string legacy = Path.Combine(dir, "LastRunInfo.txt");
                AgeChecker.MarkRegenerated(new DateTime(2026, 11, 27, 10, 0, 0), newPath);
                Assert.True(Directory.Exists(Path.GetDirectoryName(newPath)), "parent state folder is created");
                Assert.Equal("2026-11-27 10:00:00", File.ReadAllText(newPath), "stamp written in the invariant format");
                Assert.True(!File.Exists(legacy), "MarkRegenerated never writes the legacy repo file");
            }
            finally { DeleteDirQuietly(dir); }
        }

        public static void AgeChecker_MissingFile_SetsRegenTrue()
        {
            // No last-run file -> age unknown -> must regenerate
            string path = TempInfoFile();
            try
            {
                new AgeChecker(forceMirrorRegen: false, lastRunInfoPath: path);
                Assert.True(AgeChecker.RegenMirror, "missing last-run file should set RegenMirror=true");
                Assert.True(!File.Exists(path), "AgeChecker only decides: it must not create the last-run file");
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }

        public static void Stale_DoesNotRewriteStamp()
        {
            string path = TempInfoFile();
            try
            {
                string stale = DateTime.Now.AddDays(-10).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                File.WriteAllText(path, stale);
                new AgeChecker(forceMirrorRegen: false, lastRunInfoPath: path);
                Assert.True(AgeChecker.RegenMirror, "stale mirror should regen");
                Assert.Equal(stale, File.ReadAllText(path));
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }

        public static void AgeChecker_StaleFile_SetsRegenTrue()
        {
            // Date in file is older than AgeThreshold (7 days) -> regen required
            string path = TempInfoFile();
            try
            {
                // Write a date 10 days in the past
                File.WriteAllText(path, DateTime.Now.AddDays(-10).ToString("yyyy-MM-dd HH:mm:ss"));
                new AgeChecker(forceMirrorRegen: false, lastRunInfoPath: path);
                Assert.True(AgeChecker.RegenMirror, "mirror older than threshold should set RegenMirror=true");
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }

        public static void AgeChecker_FreshFile_ForceRegen_SetsRegenTrue()
        {
            // Date is recent but force regen is requested -> must regenerate
            string path = TempInfoFile();
            try
            {
                File.WriteAllText(path, DateTime.Now.AddHours(-1).ToString("yyyy-MM-dd HH:mm:ss"));
                new AgeChecker(forceMirrorRegen: true, lastRunInfoPath: path);
                Assert.True(AgeChecker.RegenMirror, "force regen on fresh mirror should set RegenMirror=true");
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }

        public static void AgeChecker_MalformedDate_ThrowsFileLoadException()
        {
            // File contains non-parseable content -> should throw FileLoadException
            string path = TempInfoFile();
            try
            {
                File.WriteAllText(path, "not-a-date");
                bool threw = false;
                try { new AgeChecker(forceMirrorRegen: false, lastRunInfoPath: path); }
                catch (System.IO.FileLoadException) { threw = true; }
                Assert.True(threw, "malformed date in last-run file should throw FileLoadException");
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }

        public static void AgeChecker_FreshFile_NoForce_SetsRegenFalse()
        {
            // Date is recent, no force -> no regen needed (placed last: explicitly sets false)
            string path = TempInfoFile();
            try
            {
                File.WriteAllText(path, DateTime.Now.AddHours(-1).ToString("yyyy-MM-dd HH:mm:ss"));
                new AgeChecker(forceMirrorRegen: false, lastRunInfoPath: path);
                Assert.True(!AgeChecker.RegenMirror, "fresh mirror with no force should set RegenMirror=false");
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }

        // --- ForceRegen tests ---

        public static void ForceRegen_SetsRegenMirrorTrue()
        {
            // ForceRegen must always set RegenMirror=true regardless of prior state
            AgeChecker.RegenMirror = false;
            string path = TempInfoFile();
            try
            {
                AgeChecker.ForceRegen(path);
                Assert.True(AgeChecker.RegenMirror, "ForceRegen should set RegenMirror=true");
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }

        public static void ForceRegen_DoesNotWriteStamp()
        {
            // ForceRegen only decides; the stamp is written by MarkRegenerated after a successful swap
            AgeChecker.RegenMirror = false;
            string path = TempInfoFile();
            try
            {
                AgeChecker.ForceRegen(path);
                Assert.True(AgeChecker.RegenMirror, "ForceRegen sets RegenMirror");
                Assert.True(!File.Exists(path), "ForceRegen must not create the stamp file");
                string old = "2020-01-02 03:04:05";
                File.WriteAllText(path, old);
                AgeChecker.ForceRegen(path);
                Assert.Equal(old, File.ReadAllText(path));
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }

        public static void MarkRegenerated_WritesInvariantStamp()
        {
            string path = TempInfoFile();
            CultureInfo original = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                AgeChecker.MarkRegenerated(new DateTime(2026, 11, 4, 9, 8, 7), path);
                Assert.Equal("2026-11-04 09:08:07", File.ReadAllText(path));
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = original;
                if (File.Exists(path)) File.Delete(path);
            }
        }

        // --- Culture-independent parsing ---

        public static void AgeChecker_GermanCulture_ParsesWriterFormat()
        {
            // The writer always uses yyyy-MM-dd HH:mm:ss, so a fresh stamp must parse under any culture
            string path = TempInfoFile();
            CultureInfo original = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                File.WriteAllText(path, DateTime.Now.AddHours(-1).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
                new AgeChecker(forceMirrorRegen: false, lastRunInfoPath: path);
                Assert.True(!AgeChecker.RegenMirror, "a fresh writer-format timestamp must parse under de-DE and not force regen");
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = original;
                if (File.Exists(path)) File.Delete(path);
            }
        }

        public static void AgeChecker_GermanCulture_RejectsNonWriterFormat()
        {
            // "04.11.2026 10:00:00" parses under de-DE with TryParse, but it is not the writer's format
            string path = TempInfoFile();
            CultureInfo original = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                File.WriteAllText(path, "04.11.2026 10:00:00");
                bool threw = false;
                try { new AgeChecker(forceMirrorRegen: false, lastRunInfoPath: path); }
                catch (System.IO.FileLoadException) { threw = true; }
                Assert.True(threw, "a culture-specific date that is not the writer format should throw FileLoadException");
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = original;
                if (File.Exists(path)) File.Delete(path);
            }
        }

        public static void AgeChecker_GermanCulture_TimestampRoundTrips()
        {
            // MarkRegenerated writes the stamp; a fresh AgeChecker under de-DE must read it back as fresh
            string path = TempInfoFile();
            CultureInfo original = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                AgeChecker.MarkRegenerated(DateTime.Now, path);
                new AgeChecker(forceMirrorRegen: false, lastRunInfoPath: path);
                Assert.True(!AgeChecker.RegenMirror, "a stamp written by ForceRegen must round-trip as fresh under de-DE");
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = original;
                if (File.Exists(path)) File.Delete(path);
            }
        }
    }
}
