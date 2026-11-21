using System;
using System.Collections.Generic;
using System.IO;

namespace AudioManager
{
    /// <summary>
    /// Retired library originals and quarantined instrumentals go to the Recycle Bin (fake sender, so no real
    /// file reaches the bin), with the holding or quarantine folder kept only as the fallback; program-made
    /// folders left empty after a run are removed. Temp trees only.
    /// </summary>
    internal static class RecycleRoutingTests
    {
        private static string MakeTempDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), "AudioManagerRecycle_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            return dir;
        }

        private sealed class Seams : IDisposable
        {
            private readonly Action<string> _s; private readonly Func<string, bool> _c; private readonly Func<string, long> _m;
            public readonly List<string> Sent = new List<string>();
            public Seams(Func<string, long> max = null, bool failSend = false)
            {
                _s = RecycleBin.Sender; _c = RecycleBin.BinCheck; _m = RecycleBin.MaxBinBytes;
                RecycleBin.BinCheck = p => true;
                RecycleBin.MaxBinBytes = max ?? (p => long.MaxValue);
                RecycleBin.Sender = p =>
                {
                    if (failSend) throw new IOException("fake send failure");
                    Sent.Add(p);
                    File.Delete(p); // a real send removes the file from its folder
                };
                ReplaceGuard.UseRecycleBin = true;
                ReplaceGuard.ResetRetired();
            }
            public void Dispose()
            {
                RecycleBin.Sender = _s; RecycleBin.BinCheck = _c; RecycleBin.MaxBinBytes = _m;
                ReplaceGuard.UseRecycleBin = false;
                ReplaceGuard.HoldingRoot = null;
                ReplaceGuard.ResetRetired();
                TagFixer.QuarantineToRecycleBin = false;
            }
        }

        public static void Commit_RecycleBinOn_SendsBakToBinAndRecordsOriginal()
        {
            string dir = MakeTempDir();
            string holding = Path.Combine(dir, "Holding");
            try
            {
                string libPath = Path.Combine(dir, "Track.mp3");
                string bak = libPath + ".replaced.bak";
                File.WriteAllBytes(bak, new byte[] { 1, 2, 3 });
                File.WriteAllText(libPath + ".replaced.dest", libPath);
                File.WriteAllBytes(libPath, new byte[] { 9 });
                using (var s = new Seams())
                {
                    ReplaceGuard.HoldingRoot = holding;
                    ReplaceGuard.Commit(bak);
                    Assert.Equal("1", s.Sent.Count.ToString(), "the bak must be sent to the Recycle Bin");
                    Assert.Equal(Path.GetFullPath(bak), s.Sent[0], "the sender receives the staged bak path");
                    Assert.Equal("1", ReplaceGuard.Retired.Count.ToString(), "journal keeps one retired entry");
                    Assert.Equal(Path.GetFullPath(libPath), ReplaceGuard.Retired[0].original, "journal names the original library path");
                    Assert.Equal(RecycleBin.HeldMarker, ReplaceGuard.Retired[0].held, "journal says it went to the Recycle Bin");
                    Assert.True(!Directory.Exists(holding), "no holding folder may be created when the bin took the file");
                    Assert.True(!File.Exists(libPath + ".replaced.dest"), "landing marker is removed after the bin took the bak");
                }
            }
            finally { Directory.Delete(dir, true); }
        }

        public static void Retire_FileTooLargeForBin_FallsBackToHoldingFolderAndSaysSo()
        {
            string dir = MakeTempDir();
            string holding = Path.Combine(dir, "Holding");
            var origOut = Console.Out; var cap = new StringWriter();
            try
            {
                string libPath = Path.Combine(dir, "Big.mp3");
                string bak = libPath + ".replaced.bak";
                File.WriteAllBytes(bak, new byte[] { 7, 7, 7, 7 });
                using (var s = new Seams(max: p => 2))
                {
                    Console.SetOut(cap);
                    ReplaceGuard.HoldingRoot = holding;
                    string held = ReplaceGuard.Retire(bak);
                    Console.SetOut(origOut);
                    Assert.Equal("0", s.Sent.Count.ToString(), "an oversize file must never reach the sender");
                    Assert.True(held != null && held.StartsWith(Path.GetFullPath(holding), StringComparison.OrdinalIgnoreCase), "fallback holds it under the holding folder");
                    Assert.True(File.Exists(held), "held file exists");
                    Assert.True(cap.ToString().Contains("too large for Recycle Bin"), "the log says why the fallback was used");
                    Assert.Equal(held, ReplaceGuard.Retired[0].held, "journal records the real held path");
                }
            }
            finally { Console.SetOut(origOut); Directory.Delete(dir, true); }
        }

        public static void Retire_SendFailsAndNoHoldingRoot_KeepsBakAndReturnsNull()
        {
            string dir = MakeTempDir();
            try
            {
                string bak = Path.Combine(dir, "Song.mp3.replaced.bak");
                File.WriteAllBytes(bak, new byte[] { 5 });
                using (var s = new Seams(failSend: true))
                {
                    string held = ReplaceGuard.Retire(bak);
                    Assert.True(held == null, "a failed send with no fallback returns null");
                    Assert.True(File.Exists(bak), "the staged original is kept, never lost");
                    Assert.Equal("0", ReplaceGuard.Retired.Count.ToString(), "nothing is recorded as retired");
                }
            }
            finally { Directory.Delete(dir, true); }
        }

        public static void BuildJson_RecycledEntry_CarriesRecycleBinHeld()
        {
            var replaced = new List<(string original, string held)> { ("C:/Lib/A.mp3", RecycleBin.HeldMarker) };
            string json = MusicIntegrator.BuildJson(new List<MusicIntegrator.LogEntry>(), null, dryRun: false, replaced: replaced);
            Assert.True(json.Contains("{\"original\": \"C:/Lib/A.mp3\", \"held\": \"Recycle Bin\"}"), "execution JSON names the bin as the held location");
        }

        public static void CleanupQuarantineFolder_RemovesEmptyProgramFoldersAndRoot()
        {
            string root = Path.Combine(MakeTempDir(), "NewMusic-Quarantine");
            try
            {
                Directory.CreateDirectory(Path.Combine(root, "Replaced", "20261121-101010"));
                var mi = new MusicIntegrator(root, dryRun: false);
                var o = Console.Out; var cap = new StringWriter();
                try { Console.SetOut(cap); mi.CleanupQuarantineFolder(root); } finally { Console.SetOut(o); }
                Assert.True(!Directory.Exists(root), "the empty quarantine root is removed");
                Assert.True(cap.ToString().Contains("Removed empty folder"), "each removal is logged");
            }
            finally { string p = Path.GetDirectoryName(root); if (Directory.Exists(p)) Directory.Delete(p, true); }
        }

        public static void CleanupQuarantineFolder_FolderHoldingAFile_IsNeverRemoved()
        {
            string root = Path.Combine(MakeTempDir(), "NewMusic-Quarantine");
            try
            {
                string keep = Path.Combine(root, "Replaced", "20261121-101010", "A.mp3");
                Directory.CreateDirectory(Path.GetDirectoryName(keep));
                File.WriteAllBytes(keep, new byte[] { 1 });
                Directory.CreateDirectory(Path.Combine(root, "Replaced", "20261120-000000"));
                var mi = new MusicIntegrator(root, dryRun: false);
                var o = Console.Out;
                try { Console.SetOut(new StringWriter()); mi.CleanupQuarantineFolder(root); } finally { Console.SetOut(o); }
                Assert.True(File.Exists(keep), "a file in quarantine is never touched");
                Assert.True(Directory.Exists(root), "a root holding a file stays");
                Assert.True(!Directory.Exists(Path.Combine(root, "Replaced", "20261120-000000")), "the empty sibling is removed");
            }
            finally { string p = Path.GetDirectoryName(root); if (Directory.Exists(p)) Directory.Delete(p, true); }
        }

        public static void CleanupNewMusicFolder_EmptyDeletedFolderUnderArtist_IsRemoved()
        {
            string root = Path.Combine(MakeTempDir(), "NewMusic");
            try
            {
                Directory.CreateDirectory(Path.Combine(root, "Some Artist", "Deleted"));
                var mi = new MusicIntegrator(root, dryRun: false);
                var o = Console.Out;
                try { Console.SetOut(new StringWriter()); mi.CleanupNewMusicFolder(root); } finally { Console.SetOut(o); }
                Assert.True(Directory.Exists(root), "the NewMusic root stays");
                Assert.Equal("0", Directory.GetFileSystemEntries(root).Length.ToString(), "artist folder and its empty Deleted folder are gone");
            }
            finally { string p = Path.GetDirectoryName(root); if (Directory.Exists(p)) Directory.Delete(p, true); }
        }

        public static void TagFixQuarantine_RecycleOn_SendsInstrumentalToBinAndJournalsIt()
        {
            string dir = MakeTempDir();
            try
            {
                string quarantine = Path.Combine(dir, "NewMusic-Quarantine");
                string f = TagFixerTests.CreateSilentMp3Fixture(dir, "Akira The Don - Track (Instrumental).mp3", "Track (Instrumental)", "Album", "Akira The Don");
                using (var s = new Seams())
                {
                    TagFixer.QuarantineToRecycleBin = true;
                    var log = TagFixer.ForTesting(dryRun: false, quarantineRoot: quarantine).ProcessFile(f);
                    Assert.Equal("1", s.Sent.Count.ToString(), "the instrumental is sent to the Recycle Bin");
                    Assert.True(!File.Exists(f), "the instrumental left NewMusic");
                    Assert.True(!Directory.Exists(quarantine), "no quarantine folder is created");
                    Assert.Equal("quarantined instrumental: " + RecycleBin.HeldMarker, log.Detail, "detail records that it went to the bin");
                }
            }
            finally { Directory.Delete(dir, true); }
        }

        public static void TagFixQuarantine_RecycleOnButTooLarge_FallsBackToQuarantineFolder()
        {
            string dir = MakeTempDir();
            try
            {
                string quarantine = Path.Combine(dir, "NewMusic-Quarantine");
                string f = TagFixerTests.CreateSilentMp3Fixture(dir, "Akira The Don - Track (Instrumental).mp3", "Track (Instrumental)", "Album", "Akira The Don");
                using (var s = new Seams(max: p => 1))
                {
                    TagFixer.QuarantineToRecycleBin = true;
                    var log = TagFixer.ForTesting(dryRun: false, quarantineRoot: quarantine).ProcessFile(f);
                    Assert.Equal("0", s.Sent.Count.ToString(), "an oversize file is not sent");
                    Assert.True(File.Exists(Path.Combine(quarantine, "Akira The Don - Track (Instrumental).mp3")), "it is moved to the quarantine folder instead");
                    Assert.True(log.Detail.StartsWith("quarantined instrumental: ", StringComparison.Ordinal) && !log.Detail.EndsWith(RecycleBin.HeldMarker), "detail names the folder path");
                }
            }
            finally { Directory.Delete(dir, true); }
        }

        public static void Revert_QuarantinedToRecycleBin_IsReportedNotGuessed()
        {
            string root = Path.Combine(Path.GetTempPath(), "AudioManagerApply_" + Guid.NewGuid());
            string folder = Path.Combine(root, "NewMusic");
            string quarantine = Path.Combine(root, "NewMusic-Quarantine");
            Directory.CreateDirectory(folder);
            try
            {
                string path = TagFixerTests.CreateSilentMp3Fixture(folder, "inst.mp3", "Track (Instrumental)", "Album", "Akira The Don");
                string plan =TagFixPlanJson.Fingerprint(folder, TagFixer.ForFolder(folder, true, quarantine).Scanned);
                using (var s = new Seams())
                {
                    TagFixer.QuarantineToRecycleBin = true;
                    var r = TagFixApplier.Apply(folder, plan, Path.Combine(root, "logs"), Path.Combine(root, "backups"), quarantine, new DateTime(2026, 11, 10, 12, 0, 0));
                    Assert.True(r.Quarantined == 1 && !File.Exists(path), "instrumental went to the bin");
                    var rev = TagFixApplier.Revert(r.JournalPath, folder, quarantine);
                    Assert.True(rev.Reverted == 0 && rev.Failed == 1, "a file in the Recycle Bin is reported, not reverted");
                    Assert.True(rev.Notes.Exists(n => n.Contains("Recycle Bin")), "the note tells the user to restore it from the bin");
                }
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }
    }
}
