using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AudioManager
{
    /// <summary>
    /// Coverage for the execution record's top-level "leftovers" block: MusicIntegrator.ComputeLeftovers
    /// snapshots NewMusic after cleanup (empty folders, non-audio files, manifest-declined count), and
    /// BuildJson writes it on every execution record and never on a dry run. Temp trees only.
    /// </summary>
    internal static class LeftoversTests
    {
        private static string NewRoot()
        {
            return Path.Combine(Path.GetTempPath(), "am_leftovers_" + Guid.NewGuid().ToString("N").Substring(0, 8));
        }

        public static void Leftovers_ComputeSnapshot_ListsEmptyFoldersAndNonAudioFiles()
        {
            string root = NewRoot();
            try
            {
                Directory.CreateDirectory(Path.Combine(root, "EmptyArtist"));
                Directory.CreateDirectory(Path.Combine(root, "Artist", "Nested"));
                Directory.CreateDirectory(Path.Combine(root, "Full"));
                File.WriteAllText(Path.Combine(root, "desktop.ini"), "x");
                File.WriteAllText(Path.Combine(root, "Full", "cover.jpg"), "x");
                File.WriteAllText(Path.Combine(root, "Full", "Kept Song.mp3"), "x");

                var snap = MusicIntegrator.ComputeLeftovers(root, declinedCount: 1);

                Assert.Equal("Artist,Artist\\Nested,EmptyArtist", string.Join(",", snap.EmptyFolders), "empty folders (ordinal order), including the nested one and its empty parent");
                Assert.Equal("Full\\cover.jpg,desktop.ini", string.Join(",", snap.OtherFiles), "non-audio files only, relative with backslashes");
                Assert.Equal("1", snap.DeclinedCount.ToString(), "declined count passes through");
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        }

        public static void Leftovers_MissingFolder_IsEmpty()
        {
            var snap = MusicIntegrator.ComputeLeftovers(Path.Combine(Path.GetTempPath(), "am_leftovers_missing_" + Guid.NewGuid().ToString("N")), declinedCount: 0);
            Assert.Equal("0", snap.EmptyFolders.Count.ToString(), "no folder means no empty folders");
            Assert.Equal("0", snap.OtherFiles.Count.ToString(), "no folder means no other files");
        }

        public static void BuildJson_ExecutionRecord_WritesLeftoversBlock()
        {
            var snap = new MusicIntegrator.LeftoversSnapshot
            {
                DeclinedCount = 2,
            };
            snap.EmptyFolders.Add("Artist\\Nested");
            snap.OtherFiles.Add("desktop.ini");

            string json = MusicIntegrator.BuildJson(new List<MusicIntegrator.LogEntry>(), dryRun: false, leftovers: snap);

            Assert.True(json.Contains("\"leftovers\": {\"emptyFolders\": [\"Artist\\\\Nested\"], \"otherFiles\": [\"desktop.ini\"], \"declinedCount\": 2},"),
                "execution record carries the leftovers block with JStr-escaped paths");
        }

        public static void BuildJson_ExecutionRecord_LeftoversAlwaysPresentWhenNotPassed()
        {
            string json = MusicIntegrator.BuildJson(new List<MusicIntegrator.LogEntry>(), dryRun: false);
            Assert.True(json.Contains("\"leftovers\": {\"emptyFolders\": [], \"otherFiles\": [], \"declinedCount\": 0},"),
                "an execution record without a snapshot still carries an empty leftovers block");
        }

        public static void BuildJson_DryRun_HasNoLeftoversBlock()
        {
            string json = MusicIntegrator.BuildJson(new List<MusicIntegrator.LogEntry>(), dryRun: true);
            Assert.True(!json.Contains("\"leftovers\""), "a dry run writes no leftovers block");
        }
    }
}
