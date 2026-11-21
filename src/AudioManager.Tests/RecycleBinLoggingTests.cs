using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AudioManager
{
    /// <summary>
    /// Duplicate D delete logging and oversize refusal, plus timestamped copies of the fixed-name result JSONs.
    /// Every test swaps RecycleBin seams for fakes so no real file reaches the Recycle Bin.
    /// </summary>
    internal static class RecycleBinLoggingTests
    {
        public static void Send_FileLargerThanBinLimit_RefusesWithTooLargeAndNeverSends()
        {
            string f = Path.Combine(Path.GetTempPath(), "am_rbbig_" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".mp3");
            File.WriteAllBytes(f, new byte[100]);
            var origSender = RecycleBin.Sender; var origCheck = RecycleBin.BinCheck; var origMax = RecycleBin.MaxBinBytes;
            try
            {
                bool sent = false;
                RecycleBin.Sender = p => sent = true;
                RecycleBin.BinCheck = p => true;
                RecycleBin.MaxBinBytes = p => 50;
                bool threw = false;
                try { RecycleBin.Send(f); } catch (RecycleBinTooLargeException) { threw = true; }
                Assert.True(threw, "oversize file must throw RecycleBinTooLargeException");
                Assert.True(!sent, "oversize file must never reach the sender");
                Assert.True(File.Exists(f), "oversize file must stay");
            }
            finally { RecycleBin.Sender = origSender; RecycleBin.BinCheck = origCheck; RecycleBin.MaxBinBytes = origMax; File.Delete(f); }
        }

        public static void Send_FileWithinLimit_Sends()
        {
            string f = Path.Combine(Path.GetTempPath(), "am_rbok_" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".mp3");
            File.WriteAllBytes(f, new byte[10]);
            var origSender = RecycleBin.Sender; var origCheck = RecycleBin.BinCheck; var origMax = RecycleBin.MaxBinBytes;
            try
            {
                bool sent = false;
                RecycleBin.Sender = p => sent = true;
                RecycleBin.BinCheck = p => true;
                RecycleBin.MaxBinBytes = p => 50;
                RecycleBin.Send(f);
                Assert.True(sent, "file within the limit is sent");
            }
            finally { RecycleBin.Sender = origSender; RecycleBin.BinCheck = origCheck; RecycleBin.MaxBinBytes = origMax; File.Delete(f); }
        }

        public static void DuplicateD_TooLargeForBin_KeepsFileWithClearStatus()
        {
            string lib = RoutingFixtures.CreateLibraryFixture();
            string nm = Path.Combine(Path.GetTempPath(), "AudioManagerTest_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(nm);
            string src = Path.Combine(nm, "Big Artist - Big Song.mp3");
            var origSender = RecycleBin.Sender; var origCheck = RecycleBin.BinCheck; var origMax = RecycleBin.MaxBinBytes;
            var origOut = Console.Out;
            try
            {
                var sw = new StringWriter();
                Console.SetOut(sw);
                bool sent = false;
                RecycleBin.Sender = p => sent = true;
                RecycleBin.BinCheck = p => true;
                RecycleBin.MaxBinBytes = p => 50;
                var sf = MusicIntegratorDeleteGuardTests.MakeDeleteDuplicate(src, "Big Artist", "Big Song",
                    MusicIntegratorDeleteGuardTests.WriteLibraryCopy(lib, "Big Artist - Big Song.mp3"));
                File.WriteAllBytes(src, new byte[100]);
                var logEntries = new List<MusicIntegrator.LogEntry>();
                int skipped = 0;
                var integrator = new MusicIntegrator(lib, dryRun: false);
                integrator.ExecuteDuplicateDecisions(new List<MusicIntegrator.ScannedFile> { sf }, new HashSet<string>(), logEntries, ref skipped);
                Console.SetOut(origOut);
                Assert.True(!sent, "oversize file never sent");
                Assert.True(File.Exists(src), "oversize file kept");
                Assert.Equal("error", sf.LogEntry.Status, "oversize refusal is an error status");
                Assert.True(sf.LogEntry.Detail.Contains("too large for Recycle Bin"), "clear detail, got: " + sf.LogEntry.Detail);
                string o = sw.ToString();
                Assert.True(o.Contains(src) && o.Contains("100"), "log carries full path and size: " + o);
            }
            finally
            {
                Console.SetOut(origOut);
                RecycleBin.Sender = origSender; RecycleBin.BinCheck = origCheck; RecycleBin.MaxBinBytes = origMax;
                RoutingFixtures.Cleanup(lib);
                if (Directory.Exists(nm)) Directory.Delete(nm, true);
            }
        }

        public static void DuplicateD_Success_LogsPathSizeAndRecycled()
        {
            string lib = RoutingFixtures.CreateLibraryFixture();
            string nm = Path.Combine(Path.GetTempPath(), "AudioManagerTest_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(nm);
            string src = Path.Combine(nm, "Ok Artist - Ok Song.mp3");
            var origSender = RecycleBin.Sender; var origCheck = RecycleBin.BinCheck; var origMax = RecycleBin.MaxBinBytes;
            var origOut = Console.Out;
            try
            {
                var sw = new StringWriter();
                Console.SetOut(sw);
                RecycleBin.Sender = p => { };
                RecycleBin.BinCheck = p => true;
                RecycleBin.MaxBinBytes = p => 1000;
                var sf = MusicIntegratorDeleteGuardTests.MakeDeleteDuplicate(src, "Ok Artist", "Ok Song",
                    MusicIntegratorDeleteGuardTests.WriteLibraryCopy(lib, "Ok Artist - Ok Song.mp3"));
                File.WriteAllBytes(src, new byte[77]);
                var logEntries = new List<MusicIntegrator.LogEntry>();
                int skipped = 0;
                new MusicIntegrator(lib, dryRun: false).ExecuteDuplicateDecisions(new List<MusicIntegrator.ScannedFile> { sf }, new HashSet<string>(), logEntries, ref skipped);
                Console.SetOut(origOut);
                string o = sw.ToString();
                Assert.True(o.Contains("[RECYCLE]") && o.Contains(src) && o.Contains("77 bytes") && o.Contains("recycled"), "success log line: " + o);
            }
            finally
            {
                Console.SetOut(origOut);
                RecycleBin.Sender = origSender; RecycleBin.BinCheck = origCheck; RecycleBin.MaxBinBytes = origMax;
                RoutingFixtures.Cleanup(lib);
                if (Directory.Exists(nm)) Directory.Delete(nm, true);
            }
        }

        public static void GateJson_Write_AlsoWritesTimestampedCopyBesideFixedName()
        {
            string d = Path.Combine(Path.GetTempPath(), "am_gatets_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                string fixedPath = GateResultJson.WriteTo(d, "{\"a\":1}", new DateTime(2026, 11, 21, 10, 11, 12, 345));
                Assert.True(File.Exists(fixedPath) && Path.GetFileName(fixedPath) == "integrate-gate.json", "fixed name kept");
                string stamped = Path.Combine(d, "integrate-gate-20261121-101112345.json");
                Assert.True(File.Exists(stamped), "timestamped copy written");
                Assert.Equal(File.ReadAllText(fixedPath), File.ReadAllText(stamped), "copy matches fixed file");
            }
            finally { if (Directory.Exists(d)) Directory.Delete(d, true); }
        }

        public static void MirrorResult_Write_AlsoWritesTimestampedCopyBesideFixedName()
        {
            string d = Path.Combine(Path.GetTempPath(), "am_mirts_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                string p = Program.WriteMirrorResult(d, new Program.PostIntegrationMirrorResult { Status = "ok" });
                Assert.Equal("mirror-result.json", Path.GetFileName(p), "fixed name kept");
                var copies = Directory.GetFiles(d, "mirror-result-*.json");
                Assert.Equal("1", copies.Length.ToString(), "one timestamped copy");
                Assert.Equal(File.ReadAllText(p), File.ReadAllText(copies[0]), "copy matches fixed file");
            }
            finally { if (Directory.Exists(d)) Directory.Delete(d, true); }
        }
    }
}
