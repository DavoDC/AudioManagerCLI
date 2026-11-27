using AudioManager.Code.Modules;
using ShapeFlags = AudioManager.Code.Modules.TrackXML.ShapeFlags;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AudioManager
{
    /// <summary>
    /// Tests for analysis --as-of (StatsAsOf): resolving an AudioMirror commit, reading the XMLs at that commit
    /// and writing the stats document under data/history. Git is injected as a fake, so no test touches the
    /// real AudioMirror repo, its working tree or the real data/history folder.
    /// </summary>
    internal static class StatsAsOfTests
    {
        private const string FullSha = "56bdf59a0123456789abcdef0123456789abcdef";
        private const string OtherSha = "1a2b3c4d0123456789abcdef0123456789abcdef";

        private static string NewTempDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), "AsOfTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static AudioMirrorCommitter.GitRunResult Ok(string stdout) =>
            new AudioMirrorCommitter.GitRunResult { ExitCode = 0, Stdout = stdout, Stderr = "" };

        private static AudioMirrorCommitter.GitRunResult Fail(string stderr) =>
            new AudioMirrorCommitter.GitRunResult { ExitCode = 128, Stdout = "", Stderr = stderr };

        private static string CaptureConsole(Action action)
        {
            var capture = new StringWriter();
            TextWriter original = Console.Out;
            Console.SetOut(capture);
            try { action(); }
            finally { Console.SetOut(original); }
            return capture.ToString();
        }

        private static TrackTag Tag(string relPath, string title) =>
            new TrackTag(relPath, title, "Artist A", "Album", "2020", "1", "Rock", "00:03:00.0000000", "1", "False", "1000", "1000");

        private static List<TrackTag> Sample() => new List<TrackTag>
        {
            Tag("\\Artists\\Artist A\\Singles\\1.xml", "Song 1"),
            Tag("\\Artists\\Artist A\\Singles\\2.xml", "Song 2"),
        };

        // ---- spec parsing ----

        public static void IsDateSpec_AcceptsIsoDateOnly()
        {
            Assert.True(StatsAsOf.IsDateSpec("2025-12-31"), "ISO date accepted");
            Assert.True(!StatsAsOf.IsDateSpec("2025-13-01"), "month 13 rejected");
            Assert.True(!StatsAsOf.IsDateSpec("20251231"), "no separators rejected");
            Assert.True(!StatsAsOf.IsDateSpec("31-12-2025"), "day-first rejected");
            Assert.True(!StatsAsOf.IsDateSpec(""), "empty rejected");
        }

        public static void IsShaSpec_AcceptsHexOfSevenToFortyChars()
        {
            Assert.True(StatsAsOf.IsShaSpec("56bdf59a"), "8 hex chars accepted");
            Assert.True(StatsAsOf.IsShaSpec(FullSha), "40 hex chars accepted");
            Assert.True(!StatsAsOf.IsShaSpec("56bdf5"), "6 chars rejected");
            Assert.True(!StatsAsOf.IsShaSpec("56bdf59g"), "non-hex rejected");
            Assert.True(!StatsAsOf.IsShaSpec("--all"), "flag-shaped value rejected");
        }

        // ---- resolution ----

        public static void TryResolve_Date_UsesLastCommitBefore2359()
        {
            var calls = new List<string>();
            Func<string, AudioMirrorCommitter.GitRunResult> git = args =>
            {
                calls.Add(args);
                if (args.StartsWith("rev-list", StringComparison.Ordinal)) return Ok(FullSha + "\n");
                return Ok("2025-12-30\t" + FullSha + "\tTidy up\n");
            };
            bool ok = StatsAsOf.TryResolve("2025-12-31", git, out StatsAsOf.ResolvedCommit commit, out string error);
            Assert.True(ok, "date resolves");
            Assert.True(calls[0].Contains("--before=\"2025-12-31 23:59:59\""), "rev-list bounded at 23:59:59 on the date");
            Assert.True(calls[0].EndsWith(" HEAD", StringComparison.Ordinal), "rev-list names HEAD (git needs a commit argument)");
            Assert.Equal(FullSha, commit.FullSha, "full sha from rev-list");
            Assert.Equal("56bdf59a", commit.Sha8, "sha8 is first eight chars");
            Assert.Equal("2025-12-30", commit.Date, "commit date is the commit's own date");
            Assert.Equal("Tidy up", commit.Subject, "subject carried through");
        }

        public static void TryResolve_Date_NoCommitIsClearError()
        {
            Func<string, AudioMirrorCommitter.GitRunResult> git = args => Ok("");
            bool ok = StatsAsOf.TryResolve("2000-01-01", git, out _, out string error);
            Assert.True(!ok, "no commit on or before the date fails");
            Assert.True(error.Contains("No AudioMirror commit on or before 2000-01-01"), "error names the date");
        }

        public static void TryResolve_Sha_UsesFullShaFromGit()
        {
            Func<string, AudioMirrorCommitter.GitRunResult> git = args =>
                args.Contains("show") ? Ok("2025-12-31\t" + FullSha + "\tRelease\n") : Ok("");
            bool ok = StatsAsOf.TryResolve("56bdf59a", git, out StatsAsOf.ResolvedCommit commit, out string error);
            Assert.True(ok, "known short sha resolves");
            Assert.Equal(FullSha, commit.FullSha, "full sha returned, not the short input");
            Assert.Equal("2025-12-31", commit.Date, "date from the commit");
        }

        public static void TryResolve_UnknownSha_IsClearError()
        {
            Func<string, AudioMirrorCommitter.GitRunResult> git = args => Fail("fatal: bad object");
            bool ok = StatsAsOf.TryResolve("deadbeef", git, out _, out string error);
            Assert.True(!ok, "unknown sha fails");
            Assert.True(error.Contains("Unknown AudioMirror commit"), "error says the commit is unknown");
        }

        public static void TryResolve_BadSpec_NeverCallsGit()
        {
            bool called = false;
            Func<string, AudioMirrorCommitter.GitRunResult> git = args => { called = true; return Ok(""); };
            bool ok = StatsAsOf.TryResolve("yesterday", git, out _, out string error);
            Assert.True(!ok, "non-date, non-sha rejected");
            Assert.True(error.Contains("YYYY-MM-DD"), "error shows the accepted forms");
            Assert.True(!called, "git not run for a bad spec");
        }

        // ---- output and list ----

        public static void OutputPath_NamedByCommitDateAndSha8()
        {
            var c = new StatsAsOf.ResolvedCommit { FullSha = FullSha, Sha8 = "56bdf59a", Date = "2025-12-30" };
            string path = StatsAsOf.OutputPath(@"C:\x\data\history", c);
            Assert.Equal(Path.Combine(@"C:\x\data\history", "stats-2025-12-30-56bdf59a.json"), path, "file name");
        }

        public static void ParseCommitLog_SplitsDateShaAndKeepsSubject()
        {
            string stdout = "2025-12-31\t56bdf59a\tRelease: 5379 tracks\n2025-12-30\t1a2b3c4d\tFix: colon: in subject\n";
            List<StatsAsOf.CommitRow> rows = StatsAsOf.ParseCommitLog(stdout);
            Assert.Equal("2", rows.Count.ToString(), "two rows");
            Assert.Equal("2025-12-31|56bdf59a|Release: 5379 tracks", rows[0].Date + "|" + rows[0].Sha8 + "|" + rows[0].Subject, "first row");
            Assert.Equal("Fix: colon: in subject", rows[1].Subject, "subject with colons kept whole");
        }

        // ---- reading XMLs at a commit ----

        public static void LoadTags_ReadsEveryXmlUnderRootAndIgnoresOtherFiles()
        {
            string root = Path.Combine(NewTempDir(), "AUDIO_MIRROR");
            string singles = Path.Combine(root, "Artists", "Artist A", "Singles");
            Directory.CreateDirectory(singles);
            TrackXML.Write(Path.Combine(singles, "1.xml"), Tag("", "Song 1"));
            TrackXML.Write(Path.Combine(singles, "2.xml"), Tag("", "Song 2"));
            File.WriteAllText(Path.Combine(singles, "notes.txt"), "not a track");

            List<TrackTag> tags = StatsAsOf.LoadTags(root);
            Assert.Equal("2", tags.Count.ToString(), "two XMLs, the txt is ignored");
            Assert.True(tags.All(t => t.RelPath.StartsWith("\\Artists\\Artist A\\Singles\\", StringComparison.Ordinal)),
                "RelPath is relative to the extracted mirror root");
            Assert.True(tags.Any(t => t.Title == "Song 2"), "fields read from the XML");
        }

        public static void LoadTags_UnreadableXml_FailsNamingTheFile()
        {
            string root = Path.Combine(NewTempDir(), "AUDIO_MIRROR");
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "broken.xml"), "<Track><Title>");
            bool threw = false;
            string message = "";
            try { StatsAsOf.LoadTags(root); }
            catch (InvalidOperationException ex) { threw = true; message = ex.Message; }
            Assert.True(threw, "broken XML aborts the run rather than silently skipping a track");
            Assert.True(message.Contains("broken.xml"), "message names the file");
        }

        // ---- extraction path budget (XDocument fails past MAX_PATH 260) ----

        // Longest XML path under AUDIO_MIRROR across the sampled history (measured at 56bdf59a with git ls-tree -r).
        private const int LongestXmlRelativePath = 194;

        public static void NewExtractDir_LongestRealXmlStaysUnderMaxPath()
        {
            string extracted = Path.Combine(StatsAsOf.NewExtractDir(), Constants.MirrorFolderName);
            int projected = extracted.Length + 1 + LongestXmlRelativePath;
            Assert.True(projected < 260,
                $"extracted root ({extracted.Length}) plus the longest XML path must stay under MAX_PATH 260 (got {projected})");
        }

        // ---- older XML shapes (seen at AudioMirror 56bdf59a: no AlbumCover block, no HasLyrics) ----

        private const string PreCoverXml =
            "<Track><Title>Song 1</Title><Artists>Artist A</Artists><Album>Album</Album><Year>2020</Year>" +
            "<TrackNumber>1</TrackNumber><Genres>Rock</Genres><Length>00:03:00.0000000</Length>" +
            "<AlbumCoverCount>1</AlbumCoverCount><Compilation>False</Compilation></Track>";

        private const string CoverNoLyricsXml =
            "<Track><Title>Song 2</Title><Artists>Artist A</Artists><Album>Album</Album><Year>2020</Year>" +
            "<TrackNumber>2</TrackNumber><Genres>Rock</Genres><Length>00:03:00.0000000</Length>" +
            "<AlbumCover><Count>1</Count><Width>1400</Width><Height>1400</Height></AlbumCover>" +
            "<Compilation>False</Compilation></Track>";

        private const string FullXml =
            "<Track><Title>Song 3</Title><Artists>Artist A</Artists><Album>Album</Album><Year>2021</Year>" +
            "<TrackNumber>3</TrackNumber><Genres>Rock</Genres><Length>00:03:00.0000000</Length>" +
            "<AlbumCover><Count>1</Count><Width>1000</Width><Height>1000</Height></AlbumCover>" +
            "<Compilation>False</Compilation><HasLyrics>True</HasLyrics></Track>";

        private static string WriteXml(string dir, string name, string xml)
        {
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, name);
            File.WriteAllText(path, xml);
            return path;
        }

        public static void ReadLenient_PreCoverXml_CoverAndLyricsUnknownCountKept()
        {
            string path = WriteXml(NewTempDir(), "1.xml", PreCoverXml);
            TrackTag t = Tag("", "");
            ShapeFlags shape = TrackXML.ReadLenient(path, t);
            Assert.True(!shape.CoverKnown, "no AlbumCover block means cover unknown");
            Assert.True(!shape.LyricsKnown, "no HasLyrics element means lyrics unknown");
            Assert.Equal("Song 1", t.Title, "title read");
            Assert.Equal("1", t.AlbumCoverCount, "count taken from the root AlbumCoverCount");
            Assert.True(string.IsNullOrEmpty(t.CoverWidth) && string.IsNullOrEmpty(t.CoverHeight),
                "dimensions unknown (empty), not zero");
            Assert.True(t.HasLyrics == null, "HasLyrics stays null (unknown)");
        }

        public static void ReadLenient_CoverBlockWithoutLyrics_CoverKnownLyricsUnknown()
        {
            string path = WriteXml(NewTempDir(), "2.xml", CoverNoLyricsXml);
            TrackTag t = Tag("", "");
            ShapeFlags shape = TrackXML.ReadLenient(path, t);
            Assert.True(shape.CoverKnown, "AlbumCover block present");
            Assert.True(!shape.LyricsKnown, "HasLyrics absent");
            Assert.Equal("1400", t.CoverWidth, "width read from the block");
            Assert.True(t.HasLyrics == null, "HasLyrics null");
        }

        public static void ReadLenient_FullXml_BothKnown()
        {
            string path = WriteXml(NewTempDir(), "3.xml", FullXml);
            TrackTag t = Tag("", "");
            ShapeFlags shape = TrackXML.ReadLenient(path, t);
            Assert.True(shape.CoverKnown && shape.LyricsKnown, "current schema: both known");
            Assert.Equal("True", t.HasLyrics, "HasLyrics read");
        }

        public static void ReadLenient_AbsentCoreField_ReadsAsEmptyNotAnError()
        {
            string xml = PreCoverXml.Replace("<Genres>Rock</Genres>", "");
            string path = WriteXml(NewTempDir(), "4.xml", xml);
            TrackTag t = Tag("", "");
            TrackXML.ReadLenient(path, t);
            Assert.Equal("", t.Genres, "absent Genres reads as empty");
        }

        public static void LoadTags_MixedSchemas_FlagsFalseForWholeDocument()
        {
            string root = Path.Combine(NewTempDir(), "AUDIO_MIRROR");
            WriteXml(root, "old.xml", PreCoverXml);
            WriteXml(root, "new.xml", FullXml);
            bool coverKnown, lyricsKnown;
            List<TrackTag> tags = StatsAsOf.LoadTags(root, out coverKnown, out lyricsKnown);
            Assert.Equal("2", tags.Count.ToString(), "old and new XML both load");
            Assert.True(!coverKnown, "one old file means the cover flag is false for the commit");
            Assert.True(!lyricsKnown, "one old file means the lyrics flag is false for the commit");
        }

        public static void LoadTags_CurrentSchemaOnly_FlagsTrue()
        {
            string root = Path.Combine(NewTempDir(), "AUDIO_MIRROR");
            WriteXml(root, "new.xml", FullXml);
            bool coverKnown, lyricsKnown;
            StatsAsOf.LoadTags(root, out coverKnown, out lyricsKnown);
            Assert.True(coverKnown && lyricsKnown, "every XML carries both blocks");
        }

        public static void LoadTags_AllXmlsHaveFileBytes_SizeKnownAndTotalReal()
        {
            string root = Path.Combine(NewTempDir(), "AUDIO_MIRROR");
            WriteXml(root, "a.xml", FullXml.Replace("</Track>", "<FileBytes>1000</FileBytes></Track>"));
            WriteXml(root, "b.xml", FullXml.Replace("</Track>", "<FileBytes>2500</FileBytes></Track>"));
            bool c, l;
            List<TrackTag> tags = StatsAsOf.LoadTags(root, out c, out l, out long total, out bool sizeKnown);
            Assert.True(sizeKnown, "every XML has FileBytes");
            Assert.True(total == 3500L, "total is the sum");
            string json = StatsJson.Build(tags, total, null, "2025-12-30", FullSha, true, true, sizeKnown);
            Assert.True(json.Contains("\"sizeKnown\": true"), "sizeKnown true in the document");
            Assert.True(json.Contains("\"totalLibraryBytes\": 3500"), "real total in the document");
        }

        public static void LoadTags_OneXmlLacksFileBytes_SizeUnknown()
        {
            string root = Path.Combine(NewTempDir(), "AUDIO_MIRROR");
            WriteXml(root, "a.xml", FullXml.Replace("</Track>", "<FileBytes>1000</FileBytes></Track>"));
            WriteXml(root, "b.xml", FullXml);
            bool c, l;
            StatsAsOf.LoadTags(root, out c, out l, out long total, out bool sizeKnown);
            Assert.True(!sizeKnown, "one XML without FileBytes makes size unknown");
            string json = StatsJson.Build(Sample(), 0, null, "2025-12-30", FullSha);
            Assert.True(json.Contains("\"sizeKnown\": false"), "default stays false");
        }

        public static void Build_AsOf_EmitsCoverAndLyricsKnownFlags()
        {
            string json = StatsJson.Build(Sample(), 0, null, "2025-12-30", FullSha, false, true);
            Assert.True(json.Contains("\"coverKnown\": false"), "coverKnown false when the commit predates AlbumCover");
            Assert.True(json.Contains("\"lyricsKnown\": true"), "lyricsKnown true when HasLyrics exists");
        }

        public static void Build_LiveRun_HasNoCoverOrLyricsKnownFields()
        {
            string json = StatsJson.Build(Sample(), 0);
            Assert.True(!json.Contains("\"coverKnown\""), "live document unchanged: no coverKnown");
            Assert.True(!json.Contains("\"lyricsKnown\""), "live document unchanged: no lyricsKnown");
        }

        // ---- StatsJson document fields ----

        public static void Build_AsOf_AddsAsOfAndSizeKnownFalse()
        {
            string json = StatsJson.Build(Sample(), 0, null, "2025-12-30", FullSha);
            Assert.True(json.Contains("\"asOf\": {\"date\": \"2025-12-30\", \"commit\": \"" + FullSha + "\"}"), "asOf block with date and commit");
            Assert.True(json.Contains("\"sizeKnown\": false"), "sizeKnown false in as-of documents");
            Assert.True(json.Contains("\"schemaVersion\": 1"), "additive fields keep schemaVersion 1");
        }

        public static void Build_AsOf_SizeIsZeroAndNotFaked()
        {
            string json = StatsJson.Build(Sample(), 0, null, "2025-12-30", FullSha);
            Assert.True(json.Contains("\"totalLibraryBytes\": 0"), "total bytes zero, not estimated");
            Assert.True(json.Contains("\"avgFileBytes\": 0"), "average file size zero, not estimated");
        }

        public static void Build_LiveRun_HasNoAsOfFields()
        {
            string json = StatsJson.Build(Sample(), 0);
            Assert.True(!json.Contains("\"asOf\""), "live document has no asOf block");
            Assert.True(!json.Contains("\"sizeKnown\""), "live document unchanged: no sizeKnown");
        }

        // ---- entry point ----

        public static void TryRun_NoAsOfFlag_IsNotHandled()
        {
            bool handled = StatsAsOf.TryRun(new[] { "analysis", "--json-output" },
                args => throw new InvalidOperationException("git must not run"), NewTempDir(),
                (sha, dir) => throw new InvalidOperationException("extract must not run"), out int code);
            Assert.True(!handled, "no --as-of or --as-of-list: normal flow continues");
        }

        public static void TryRun_AsOfWithoutValue_IsErrorBeforeGit()
        {
            int code = -1;
            bool handled = false;
            CaptureConsole(() => handled = StatsAsOf.TryRun(new[] { "analysis", "--as-of" },
                args => throw new InvalidOperationException("git must not run"), NewTempDir(),
                (sha, dir) => throw new InvalidOperationException("extract must not run"), out code));
            Assert.True(handled, "handled");
            Assert.Equal("1", code.ToString(), "exit 1");
        }

        public static void TryRun_NonAnalysisMode_IsRefused()
        {
            int code = -1;
            CaptureConsole(() => StatsAsOf.TryRun(new[] { "integrate", "--as-of", "2025-12-31" },
                args => throw new InvalidOperationException("git must not run"), NewTempDir(),
                (sha, dir) => throw new InvalidOperationException("extract must not run"), out code));
            Assert.Equal("1", code.ToString(), "integrate with --as-of is refused");
        }

        public static void TryRun_RegenFlagWithAsOf_IsRefusedBeforeAnyWork()
        {
            int code = -1;
            string output = CaptureConsole(() => StatsAsOf.TryRun(new[] { "analysis", "--as-of", "2025-12-31", "--force-regen" },
                args => throw new InvalidOperationException("git must not run"), NewTempDir(),
                (sha, dir) => throw new InvalidOperationException("extract must not run"), out code));
            Assert.Equal("1", code.ToString(), "exit 1");
            Assert.True(output.Contains("--force-regen"), "names the refused flag");
        }

        public static void TryRun_AsOfAndList_TogetherAreRefused()
        {
            int code = -1;
            CaptureConsole(() => StatsAsOf.TryRun(new[] { "analysis", "--as-of", "2025-12-31", "--as-of-list" },
                args => throw new InvalidOperationException("git must not run"), NewTempDir(),
                (sha, dir) => throw new InvalidOperationException("extract must not run"), out code));
            Assert.Equal("1", code.ToString(), "exit 1");
        }

        public static void TryRun_AsOfList_PrintsCommitsWithoutWriting()
        {
            string history = NewTempDir();
            int code = -1;
            string output = CaptureConsole(() => StatsAsOf.TryRun(new[] { "analysis", "--as-of-list" },
                args =>
                {
                    Assert.True(args.StartsWith("log", StringComparison.Ordinal), "list is a read-only git log");
                    return Ok("2025-12-31\t56bdf59a\tRelease\n2025-12-30\t1a2b3c4d\tFix\n");
                }, history,
                (sha, dir) => throw new InvalidOperationException("extract must not run for a list"), out code));
            Assert.Equal("0", code.ToString(), "exit 0");
            Assert.True(output.Contains("2025-12-31\t56bdf59a\tRelease"), "first commit line printed");
            Assert.Equal("0", Directory.GetFiles(history).Length.ToString(), "list writes no file");
        }

        public static void TryRun_AsOfDate_WritesStatsFileNamedByCommit()
        {
            string history = NewTempDir();
            int code = -1;
            CaptureConsole(() => StatsAsOf.TryRun(new[] { "analysis", "--as-of", "2025-12-31", "--json-output" },
                args => args.StartsWith("rev-list", StringComparison.Ordinal)
                    ? Ok(FullSha + "\n")
                    : Ok("2025-12-30\t" + FullSha + "\tRelease\n"),
                history,
                (sha, dir) =>
                {
                    Assert.Equal(FullSha, sha, "extract receives the full sha");
                    string mirror = Path.Combine(dir, "AUDIO_MIRROR");
                    string singles = Path.Combine(mirror, "Artists", "Artist A", "Singles");
                    Directory.CreateDirectory(singles);
                    TrackXML.Write(Path.Combine(singles, "1.xml"), Tag("", "Song 1"));
                    TrackXML.Write(Path.Combine(singles, "2.xml"), Tag("", "Song 2"));
                    return mirror;
                }, out code));
            Assert.Equal("0", code.ToString(), "exit 0");
            string expected = Path.Combine(history, "stats-2025-12-30-56bdf59a.json");
            Assert.True(File.Exists(expected), "file written under history, named by commit date and sha8");
            string json = File.ReadAllText(expected);
            Assert.True(json.Contains("\"trackCount\": 2"), "counts from the XMLs at that commit");
            Assert.True(json.Contains("\"sizeKnown\": false"), "as-of document flagged");
            Assert.True(json.Contains("\"commit\": \"" + FullSha + "\""), "full commit recorded");
        }

        public static void TryRun_AsOfDate_SecondCommitGetsItsOwnFile()
        {
            string history = NewTempDir();
            int code = -1;
            Func<string, AudioMirrorCommitter.GitRunResult> git = args =>
                args.StartsWith("rev-list", StringComparison.Ordinal) ? Ok(OtherSha + "\n") : Ok("2025-02-22\t" + OtherSha + "\tFirst\n");
            CaptureConsole(() => StatsAsOf.TryRun(new[] { "analysis", "--as-of", "2025-02-22" }, git, history,
                (sha, dir) => { string m = Path.Combine(dir, "AUDIO_MIRROR"); Directory.CreateDirectory(m); return m; }, out code));
            Assert.Equal("0", code.ToString(), "empty mirror still produces a document");
            Assert.True(File.Exists(Path.Combine(history, "stats-2025-02-22-1a2b3c4d.json")), "file named for the second commit");
        }
    }
}
