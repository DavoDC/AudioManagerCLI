using System;
using System.IO;
using System.Linq;

namespace AudioManager
{
    /// <summary>
    /// The integrate-path tag write (TagFixer scanning constructor) journals every file before writing and keeps a
    /// byte backup; the result JSONs keep their fixed name and gain a timestamped copy. Temp fixtures only.
    /// </summary>
    internal static class TagFixIntegrateJournalTests
    {
        private static readonly DateTime Now = new DateTime(2026, 11, 21, 9, 30, 15, 123);

        private static string NewRoot()
        {
            string root = Path.Combine(Path.GetTempPath(), "AudioManagerIntJ_" + Guid.NewGuid());
            Directory.CreateDirectory(Path.Combine(root, "NewMusic"));
            return root;
        }

        private static string Messy(string root, string name) =>
            TagFixerTests.CreateSilentMp3Fixture(Path.Combine(root, "NewMusic"), name, "Song " + name + " (feat. Guest)", "Album (Deluxe Edition)", "Main Artist");

        private static string JournalText(string logs)
        {
            string j = Directory.GetFiles(logs, "tagfix-journal-*.json").Single();
            return File.ReadAllText(j);
        }

        private static int Count(string text, string needle)
        {
            int n = 0, i = 0;
            while ((i = text.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
            return n;
        }

        public static void IntegrateWrite_JournalsBeforeWrite_AndKeepsByteBackup()
        {
            string root = NewRoot();
            var origSaver = TagFixer.Saver;
            try
            {
                string folder = Path.Combine(root, "NewMusic"), logs = Path.Combine(root, "logs"), backups = Path.Combine(root, "backups");
                string path = Messy(root, "a.mp3");
                byte[] original = File.ReadAllBytes(path);
                var fixer = TagFixer.ForTesting(dryRun: false, quarantineRoot: Path.Combine(root, "Q"));
                fixer.EnableRunJournal(new TagFixApplier.RunJournal(folder, logs, backups, Path.Combine(root, "Q"), Now));
                string seenInSaver = null;
                TagFixer.Saver = f => { seenInSaver = JournalText(logs); f.Save(); };

                var log = fixer.ProcessFile(path);

                Assert.Equal("fixed", log.Status, "fixture must take the real write path");
                Assert.True(seenInSaver != null && seenInSaver.Contains("\"status\": \"pending\"") && seenInSaver.Contains("\"oldTitle\"") && seenInSaver.Contains("\"newTitle\"") && seenInSaver.Contains("\"backup\""),
                    "journal with old and new tags, backup path and pending status must exist before the save");
                string after = JournalText(logs);
                Assert.True(after.Contains("\"status\": \"done\"") && after.Contains("\"oldName\": \"a.mp3\"") && after.Contains("\"newName\""), "done entry records old and new name");
                string[] baks = Directory.GetFiles(backups, "*", SearchOption.AllDirectories);
                Assert.Equal("1", baks.Length.ToString(), "byte backup must be kept after success");
                Assert.True(File.ReadAllBytes(baks[0]).SequenceEqual(original), "backup holds the original bytes");
                Assert.Equal("0", Directory.GetFiles(folder, "*.tagfix.bak*").Length.ToString(), "no .tagfix.bak beside the mp3");
            }
            finally { TagFixer.Saver = origSaver; try { Directory.Delete(root, true); } catch (IOException) { } }
        }

        public static void IntegrateWrite_MidBatchFailure_JournalShowsFailedAndPending()
        {
            string root = NewRoot();
            var origSaver = TagFixer.Saver; var origRestorer = TagFixer.Restorer;
            try
            {
                string folder = Path.Combine(root, "NewMusic"), logs = Path.Combine(root, "logs"), backups = Path.Combine(root, "backups");
                Messy(root, "a.mp3"); Messy(root, "b.mp3"); Messy(root, "c.mp3");
                int calls = 0; string atFailure = null;
                TagFixer.Saver = f =>
                {
                    calls++;
                    if (calls == 2) { atFailure = JournalText(logs); throw new IOException("simulated save failure"); }
                    f.Save();
                };
                TagFixer.Restorer = (bak, src) => throw new IOException("simulated restore failure");

                TagFixer.ScanFolderForTesting(folder, false, quarantineRoot: Path.Combine(root, "Q"),
                    runJournal: new TagFixApplier.RunJournal(folder, logs, backups, Path.Combine(root, "Q"), Now));

                string text = atFailure ?? "";
                Assert.Equal("1", Count(text, "\"status\": \"done\"").ToString(), "first file done when the second write fails");
                Assert.Equal("2", Count(text, "\"status\": \"pending\"").ToString(), "the file being written and the unreached file are pending");
                string final = JournalText(logs);
                Assert.Equal("1", Count(final, "\"status\": \"failed\"").ToString(), "the file whose write failed is journaled failed");
                Assert.True(Directory.GetFiles(backups, "*", SearchOption.AllDirectories).Length >= 2, "byte backups kept for written files");
            }
            finally { TagFixer.Saver = origSaver; TagFixer.Restorer = origRestorer; try { Directory.Delete(root, true); } catch (IOException) { } }
        }

        public static void TagFixResult_KeepsFixedName_AndWritesTimestampedCopy()
        {
            string root = NewRoot();
            try
            {
                string folder = Path.Combine(root, "NewMusic"), logs = Path.Combine(root, "logs");
                Messy(root, "a.mp3");
                var tf = TagFixer.ForFolder(folder, true, Path.Combine(root, "Q"));
                string fp = TagFixPlanJson.Fingerprint(folder, tf.Scanned);
                TagFixApplier.Apply(folder, fp, logs, Path.Combine(root, "backups"), Path.Combine(root, "Q"), Now);

                string fixedPath = Path.Combine(logs, "tagfix-result.json");
                string copy = Path.Combine(logs, "tagfix-result-20261121-093015-123.json");
                Assert.True(File.Exists(fixedPath) && File.Exists(copy), "fixed-name file and timestamped copy both exist");
                Assert.Equal(File.ReadAllText(fixedPath), File.ReadAllText(copy), "the copy matches the fixed-name file");
            }
            finally { try { Directory.Delete(root, true); } catch (IOException) { } }
        }

        public static void LibFixResult_KeepsFixedName_AndWritesTimestampedCopy()
        {
            string root = NewRoot();
            try
            {
                string logs = Path.Combine(root, "logs");
                var r = new LibraryFixer.FixResult { Operation = "apply", Status = "applied", Message = "ok" };
                LibraryFixer.WriteResult(r, Path.Combine(logs, LibraryFixer.ResultFileName), Now);
                string fixedPath = Path.Combine(logs, "libfix-result.json");
                string copy = Path.Combine(logs, "libfix-result-20261121-093015-123.json");
                Assert.True(File.Exists(fixedPath) && File.Exists(copy), "fixed-name file and timestamped copy both exist");
                Assert.Equal(File.ReadAllText(fixedPath), File.ReadAllText(copy), "the copy matches the fixed-name file");
            }
            finally { try { Directory.Delete(root, true); } catch (IOException) { } }
        }
    }
}
