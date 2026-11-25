using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace AudioManager
{
    /// <summary>
    /// Tests for the lyrics plan apply and revert (LyricsPlanApplier). Every test works on a temp library
    /// folder created here and deleted in finally; the real library is never read or written.
    /// </summary>
    internal static class LyricsPlanApplierTests
    {
        private static readonly DateTime Now = new DateTime(2026, 11, 25, 12, 0, 0);

        private class Env
        {
            public string Root, Library, Logs, Backups, PlanPath;
        }

        private static Env NewEnv()
        {
            string root = Path.Combine(Path.GetTempPath(), "AudioManagerLyrics_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            var e = new Env
            {
                Root = root,
                Library = Path.Combine(root, "Library"),
                Logs = Path.Combine(root, "logs"),
                Backups = Path.Combine(root, "backups-lyrics"),
                PlanPath = Path.Combine(root, "plan.json"),
            };
            Directory.CreateDirectory(Path.Combine(e.Library, "Artist"));
            return e;
        }

        private static void Cleanup(Env e) { try { Directory.Delete(e.Root, true); } catch (IOException) { } }

        private static string Song(Env e, string name, string uslt = null)
        {
            string path = TagFixerTests.CreateSilentMp3Fixture(Path.Combine(e.Library, "Artist"), name, "Title", "Album", "Artist");
            if (uslt != null) SetUslt(path, uslt);
            return path;
        }

        private static void SetUslt(string path, string text)
        {
            using (var f = TagLib.File.Create(path))
            {
                var id3 = (TagLib.Id3v2.Tag)f.GetTag(TagLib.TagTypes.Id3v2, true);
                foreach (var fr in id3.GetFrames<TagLib.Id3v2.UnsynchronisedLyricsFrame>().ToList()) id3.RemoveFrame(fr);
                id3.AddFrame(new TagLib.Id3v2.UnsynchronisedLyricsFrame("", "eng", TagLib.StringType.UTF16) { Text = text });
                f.Save();
            }
        }

        private static string ReadUslt(string path)
        {
            using (var f = TagLib.File.Create(path))
            {
                var id3 = (TagLib.Id3v2.Tag)f.GetTag(TagLib.TagTypes.Id3v2, false);
                if (id3 == null) return null;
                var fr = id3.GetFrames<TagLib.Id3v2.UnsynchronisedLyricsFrame>().FirstOrDefault();
                return fr == null ? null : fr.Text;
            }
        }

        private class Row
        {
            public string Path, Action = "write", Lyrics = "La la la";
            public long? Size; public string Mtime; public string Source = "lrclib";
        }

        // Row for an existing file with its real size and mtime, so it is fresh unless the test changes it.
        private static Row Fresh(Env e, string path, string action = "write", string lyrics = "La la la")
        {
            string rel = path.Substring(e.Library.Length + 1).Replace('\\', '/');
            return new Row
            {
                Path = rel, Action = action, Lyrics = lyrics,
                Size = new FileInfo(path).Length,
                Mtime = File.GetLastWriteTime(path).ToString("s", CultureInfo.InvariantCulture),
            };
        }

        private static void WritePlan(Env e, int version, params Row[] rows)
        {
            var sb = new StringBuilder();
            sb.Append("{\"version\":" + version + ",\"created\":\"2026-11-25T10:00:00\",\"tracks\":[");
            for (int i = 0; i < rows.Length; i++)
            {
                var r = rows[i];
                if (i > 0) sb.Append(",");
                sb.Append("{\"path\":" + JsonText.Quote(r.Path) + ",\"size\":" + (r.Size ?? 0) + ",\"mtime\":" + JsonText.Quote(r.Mtime)
                    + ",\"verdict\":\"single\",\"source\":" + JsonText.Quote(r.Source) + ",\"action\":" + JsonText.Quote(r.Action)
                    + ",\"lyrics\":" + JsonText.Quote(r.Lyrics) + ",\"evidence\":{\"ignored\":[1,2]}}");
            }
            sb.Append("]}");
            File.WriteAllText(e.PlanPath, sb.ToString(), new UTF8Encoding(false));
        }

        private static LyricsPlanApplier.ApplyResult Run(Env e, bool apply) =>
            LyricsPlanApplier.Apply(e.PlanPath, e.Library, e.Logs, e.Backups, Now, apply);

        public static void Apply_EmptyUslt_WritesLyricsJournalsAndBacksUp()
        {
            var e = NewEnv();
            try
            {
                string song = Song(e, "a.mp3");
                WritePlan(e, 1, Fresh(e, song, "write", "Line one\nLine two"));
                var r = Run(e, true);
                Assert.Equal("applied", r.Status, "status");
                Assert.Equal("1", r.Written.ToString(), "written");
                Assert.Equal("Line one\nLine two", ReadUslt(song), "USLT now holds the planned lyrics");
                Assert.True(File.Exists(r.JournalPath), "journal written");
                Assert.True(File.ReadAllText(r.JournalPath).Contains("\"status\": \"done\""), "journal entry marked done");
                Assert.True(Directory.GetFiles(e.Backups, "a.mp3", SearchOption.AllDirectories).Length == 1, "byte backup copied");
                Assert.True(Directory.GetFiles(e.Logs, "lyrics-result*.json").Length > 0, "result JSON written");
                Assert.True(!File.Exists(Path.Combine(e.Logs, LyricsPlanApplier.LockFileName)), "lock released");
            }
            finally { Cleanup(e); }
        }

        public static void Apply_StaleMtime_SkipsAndCountsNotFails()
        {
            var e = NewEnv();
            try
            {
                string song = Song(e, "a.mp3");
                var row = Fresh(e, song);
                row.Mtime = File.GetLastWriteTime(song).AddHours(-1).ToString("s", CultureInfo.InvariantCulture);
                WritePlan(e, 1, row);
                var r = Run(e, true);
                Assert.Equal("1", r.SkippedStale.ToString(), "stale counted");
                Assert.Equal("0", r.Written.ToString(), "nothing written");
                Assert.Equal("0", r.Failed.ToString(), "stale is not a failure");
                Assert.True(ReadUslt(song) == null, "file untouched");
            }
            finally { Cleanup(e); }
        }

        public static void Apply_StaleSize_Skips()
        {
            var e = NewEnv();
            try
            {
                string song = Song(e, "a.mp3");
                var row = Fresh(e, song);
                row.Size += 1;
                WritePlan(e, 1, row);
                var r = Run(e, true);
                Assert.Equal("1", r.SkippedStale.ToString(), "size mismatch is stale");
                Assert.True(ReadUslt(song) == null, "file untouched");
            }
            finally { Cleanup(e); }
        }

        public static void Apply_KeepAndSkipRows_AreNeverTouched()
        {
            var e = NewEnv();
            try
            {
                string keep = Song(e, "keep.mp3"), skip = Song(e, "skip.mp3");
                byte[] keepBefore = File.ReadAllBytes(keep), skipBefore = File.ReadAllBytes(skip);
                WritePlan(e, 1, Fresh(e, keep, "keep"), Fresh(e, skip, "skip"));
                var r = Run(e, true);
                Assert.Equal("2", r.SkippedOther.ToString(), "keep and skip counted as skipped_other");
                Assert.Equal("0", r.Written.ToString(), "nothing written");
                Assert.Equal(Convert.ToBase64String(keepBefore), Convert.ToBase64String(File.ReadAllBytes(keep)), "keep row byte-identical");
                Assert.Equal(Convert.ToBase64String(skipBefore), Convert.ToBase64String(File.ReadAllBytes(skip)), "skip row byte-identical");
            }
            finally { Cleanup(e); }
        }

        public static void Apply_DryRun_ChangesNothingAndWritesNoFiles()
        {
            var e = NewEnv();
            try
            {
                string song = Song(e, "a.mp3");
                byte[] before = File.ReadAllBytes(song);
                WritePlan(e, 1, Fresh(e, song));
                var r = Run(e, false);
                Assert.Equal("dry_run", r.Status, "status");
                Assert.Equal("1", r.WouldWrite.ToString(), "would write counted");
                Assert.Equal("0", r.Written.ToString(), "written stays 0");
                Assert.Equal(Convert.ToBase64String(before), Convert.ToBase64String(File.ReadAllBytes(song)), "file byte-identical");
                Assert.True(!Directory.Exists(e.Logs), "no logs folder created");
                Assert.True(!Directory.Exists(e.Backups), "no backup folder created");
            }
            finally { Cleanup(e); }
        }

        public static void Apply_ExistingUslt_CountsHasLyricsAndKeepsIt()
        {
            var e = NewEnv();
            try
            {
                string song = Song(e, "a.mp3", "Original words");
                WritePlan(e, 1, Fresh(e, song, "write", "Other words"));
                var r = Run(e, true);
                Assert.Equal("1", r.HasLyrics.ToString(), "has_lyrics counted");
                Assert.Equal("0", r.Written.ToString(), "not overwritten");
                Assert.Equal("Original words", ReadUslt(song), "existing USLT kept");
            }
            finally { Cleanup(e); }
        }

        public static void Apply_UnknownVersion_RefusesAndChangesNothing()
        {
            var e = NewEnv();
            try
            {
                string song = Song(e, "a.mp3");
                byte[] before = File.ReadAllBytes(song);
                WritePlan(e, 2, Fresh(e, song));
                var r = Run(e, true);
                Assert.Equal("bad_plan", r.Status, "version 2 refused");
                Assert.Equal("0", r.Written.ToString(), "nothing written");
                Assert.Equal(Convert.ToBase64String(before), Convert.ToBase64String(File.ReadAllBytes(song)), "file byte-identical");
            }
            finally { Cleanup(e); }
        }

        public static void Apply_PathOutsideLibrary_IsSkippedNotWritten()
        {
            var e = NewEnv();
            try
            {
                string outside = TagFixerTests.CreateSilentMp3Fixture(e.Root, "outside.mp3", "T", "A", "X");
                byte[] before = File.ReadAllBytes(outside);
                var row = Fresh(e, Song(e, "a.mp3"));
                row.Path = "../outside.mp3";
                WritePlan(e, 1, row);
                var r = Run(e, true);
                Assert.Equal("1", r.SkippedOther.ToString(), "escaping path skipped");
                Assert.Equal(Convert.ToBase64String(before), Convert.ToBase64String(File.ReadAllBytes(outside)), "outside file byte-identical");
            }
            finally { Cleanup(e); }
        }

        public static void Apply_LockHeld_RefusesWithoutChanges()
        {
            var e = NewEnv();
            try
            {
                string song = Song(e, "a.mp3");
                WritePlan(e, 1, Fresh(e, song));
                using (var held = TagFixApplier.TryAcquireLock(e.Logs, LyricsPlanApplier.LockFileName))
                {
                    var r = Run(e, true);
                    Assert.Equal("apply_locked", r.Status, "second apply refused");
                    Assert.True(ReadUslt(song) == null, "file untouched");
                }
            }
            finally { Cleanup(e); }
        }

        public static void Flag_WithoutApply_IsDryRunAndWithApplyWrites()
        {
            var e = NewEnv();
            try
            {
                string song = Song(e, "a.mp3");
                WritePlan(e, 1, Fresh(e, song));
                int code;
                Assert.True(!Program.TryRunLyricsPlan(new[] { "tagfix" }, e.Library, e.Logs, e.Backups, Now, out code), "no lyrics flag: not handled");
                Assert.True(Program.TryRunLyricsPlan(new[] { "--apply-lyrics-plan", e.PlanPath }, e.Library, e.Logs, e.Backups, Now, out code), "handled");
                Assert.Equal("0", code.ToString(), "dry run exits 0");
                Assert.True(ReadUslt(song) == null, "default is dry run, nothing written");
                Program.TryRunLyricsPlan(new[] { "--apply-lyrics-plan", e.PlanPath, "--apply" }, e.Library, e.Logs, e.Backups, Now, out code);
                Assert.Equal("La la la", ReadUslt(song), "--apply writes");
                Program.TryRunLyricsPlan(new[] { "--apply-lyrics-plan" }, e.Library, e.Logs, e.Backups, Now, out code);
                Assert.Equal("1", code.ToString(), "missing plan path exits 1");
            }
            finally { Cleanup(e); }
        }

        public static void Revert_AfterApply_RestoresEmptyUslt()
        {
            var e = NewEnv();
            try
            {
                string song = Song(e, "a.mp3");
                WritePlan(e, 1, Fresh(e, song));
                var r = Run(e, true);
                Assert.Equal("La la la", ReadUslt(song), "applied");
                var rev = LyricsPlanApplier.Revert(r.JournalPath, e.Library);
                Assert.Equal("1", rev.Reverted.ToString(), "reverted");
                Assert.True(string.IsNullOrEmpty(ReadUslt(song)), "USLT empty again");
                Assert.True(File.ReadAllText(r.JournalPath).Contains("\"status\": \"reverted\""), "journal marked reverted");
            }
            finally { Cleanup(e); }
        }

        public static void Revert_RestoresWhitespaceOnlyOldValue()
        {
            var e = NewEnv();
            try
            {
                string song = Song(e, "a.mp3", "  ");
                WritePlan(e, 1, Fresh(e, song));
                var r = Run(e, true);
                Assert.Equal("1", r.Written.ToString(), "whitespace-only USLT counts as empty");
                LyricsPlanApplier.Revert(r.JournalPath, e.Library);
                Assert.Equal("  ", ReadUslt(song), "old value restored exactly");
            }
            finally { Cleanup(e); }
        }

        public static void Revert_ValueEditedSince_LeavesFileAlone()
        {
            var e = NewEnv();
            try
            {
                string song = Song(e, "a.mp3");
                WritePlan(e, 1, Fresh(e, song));
                var r = Run(e, true);
                SetUslt(song, "Hand edited");
                var rev = LyricsPlanApplier.Revert(r.JournalPath, e.Library);
                Assert.Equal("1", rev.SkippedModified.ToString(), "counted as modified");
                Assert.Equal("0", rev.Reverted.ToString(), "not reverted");
                Assert.Equal("Hand edited", ReadUslt(song), "edit kept");
            }
            finally { Cleanup(e); }
        }
    }
}
