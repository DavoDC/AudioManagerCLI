using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace AudioManager
{
    /// <summary>
    /// The routing preview (dry-run JSON) is stamped with a NewMusic fingerprint; a real run compares it to the folder
    /// it starts on and the execution record says whether the preview was stale. All tests use throwaway temp folders.
    /// </summary>
    internal static class PreviewStaleTests
    {
        private static string NewTemp()
        {
            string d = Path.Combine(Path.GetTempPath(), "am_previewstale_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(d);
            return d;
        }

        public static void Fingerprint_ChangesOnAddResizeAndTouch_NotOnNonMp3()
        {
            string d = NewTemp();
            try
            {
                string a = Path.Combine(d, "a.mp3");
                File.WriteAllText(a, "one");
                string f0 = MusicIntegrator.NewMusicFingerprint(d);
                Assert.True(f0.Length == 64, "sha256 hex");
                Assert.Equal(f0, MusicIntegrator.NewMusicFingerprint(d), "stable when nothing changed");
                File.WriteAllText(Path.Combine(d, "note.txt"), "x");
                Assert.Equal(f0, MusicIntegrator.NewMusicFingerprint(d), "non-mp3 files do not count");
                Directory.CreateDirectory(Path.Combine(d, "Sub"));
                File.WriteAllText(Path.Combine(d, "Sub", "b.mp3"), "two");
                string f1 = MusicIntegrator.NewMusicFingerprint(d);
                Assert.True(f1 != f0, "an added file changes it");
                File.WriteAllText(a, "one more");
                string f2 = MusicIntegrator.NewMusicFingerprint(d);
                Assert.True(f2 != f1, "a resized file changes it");
                File.SetLastWriteTimeUtc(a, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
                Assert.True(MusicIntegrator.NewMusicFingerprint(d) != f2, "a touched file changes it");
                Assert.True(MusicIntegrator.NewMusicFingerprint(Path.Combine(d, "missing")).Length == 64, "missing folder still gives a value");
            }
            finally { Directory.Delete(d, true); }
        }

        public static void FingerprintJsonLine_MatchesNewMusicFingerprint_SingleLineNoWrites()
        {
            string d = NewTemp();
            try
            {
                File.WriteAllText(Path.Combine(d, "a.mp3"), "one");
                string before = string.Join("|", Directory.GetFileSystemEntries(d, "*", SearchOption.AllDirectories));
                string line = Program.FingerprintJsonLine(d);
                Assert.Equal("{\"newMusicFingerprint\": \"" + MusicIntegrator.NewMusicFingerprint(d) + "\"}", line, "one json line, same algorithm");
                Assert.True(!line.Contains("\n"), "single line");
                Assert.Equal(before, string.Join("|", Directory.GetFileSystemEntries(d, "*", SearchOption.AllDirectories)), "read-only");
            }
            finally { Directory.Delete(d, true); }
        }

        public static void BuildJson_DryRunCarriesFingerprint_ExecutionCarriesStaleFlag()
        {
            var none = new List<MusicIntegrator.LogEntry>();
            string dry = MusicIntegrator.BuildJson(none, null, dryRun: true, newMusicFingerprint: "abc");
            Assert.True(dry.Contains("\"newMusicFingerprint\": \"abc\""), "dry run stamps the fingerprint");
            Assert.True(!dry.Contains("previewStale"), "dry run has no previewStale");

            string same = MusicIntegrator.BuildJson(none, null, dryRun: false, newMusicFingerprint: "abc", previewFingerprint: "abc");
            Assert.True(same.Contains("\"previewStale\": false"), "matching preview is not stale");
            string diff = MusicIntegrator.BuildJson(none, null, dryRun: false, newMusicFingerprint: "abc", previewFingerprint: "zzz");
            Assert.True(diff.Contains("\"previewStale\": true"), "differing preview is stale");
            string noPreview = MusicIntegrator.BuildJson(none, null, dryRun: false, newMusicFingerprint: "abc", previewFingerprint: null);
            Assert.True(noPreview.Contains("\"previewStale\": null"), "no preview on record: previewStale null");
        }

        public static void ReadLatestPreviewFingerprint_PicksNewestRoutingFile_IgnoresExecutionAndGarbage()
        {
            string logs = NewTemp();
            try
            {
                Assert.True(MusicIntegrator.ReadLatestPreviewFingerprint(logs) == null, "no files: null");
                File.WriteAllText(Path.Combine(logs, "routing-20261101-100000.json"), "{ \"newMusicFingerprint\": \"old\" }");
                File.WriteAllText(Path.Combine(logs, "routing-20261102-100000.json"), "{ \"newMusicFingerprint\": \"new\" }");
                File.WriteAllText(Path.Combine(logs, "execution-20261103-100000.json"), "{ \"newMusicFingerprint\": \"exec\" }");
                Assert.Equal("new", MusicIntegrator.ReadLatestPreviewFingerprint(logs), "newest routing file wins");
                File.WriteAllText(Path.Combine(logs, "routing-20261104-100000.json"), "garbage");
                Assert.True(MusicIntegrator.ReadLatestPreviewFingerprint(logs) == null, "newest preview has no stamp: null, never an older one");
            }
            finally { Directory.Delete(logs, true); }
        }
    }
}
