using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AudioManager
{
    /// <summary>
    /// analysis --backfill-date-added: DateAdded for XML that lacks it, taken from the AudioMirror history. Git is a fake
    /// that returns canned output, and every XML lives in a temp folder with fake names, so no test touches the real
    /// AudioMirror repo or mirror.
    /// </summary>
    internal static class DateAddedBackfillTests
    {
        private const string Mirror = "AUDIO_MIRROR";

        private sealed class FakeGit
        {
            public string LogOutput = "";
            public string StatusOutput = "";
            public int LogCalls;
            public int StatusCalls;
            public int OtherCalls;

            public AudioMirrorCommitter.GitRunResult Run(string args)
            {
                if (args.Contains(" log ")) { LogCalls++; return Ok(LogOutput); }
                if (args.Contains(" status ")) { StatusCalls++; return Ok(StatusOutput); }
                OtherCalls++;
                return new AudioMirrorCommitter.GitRunResult { ExitCode = 128, Stdout = "", Stderr = "unexpected git call: " + args };
            }
        }

        private static AudioMirrorCommitter.GitRunResult Ok(string stdout) =>
            new AudioMirrorCommitter.GitRunResult { ExitCode = 0, Stdout = stdout, Stderr = "" };

        private static string NewTempRoot()
        {
            string root = Path.Combine(Path.GetTempPath(), "DateAddedTests_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(Path.Combine(root, Mirror));
            return root;
        }

        private static string MirrorOf(string root) => Path.Combine(root, Mirror);

        private static string Xml(string dateAdded)
        {
            string date = dateAdded == null ? "" : "  <DateAdded>" + dateAdded + "</DateAdded>\n";
            return "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<Track>\n"
                + "  <Title>Fake Title</Title>\n  <Artists>Fake Artist</Artists>\n  <Album>Fake Album</Album>\n"
                + "  <Year>2000</Year>\n  <TrackNumber>1</TrackNumber>\n  <Genres>Fake</Genres>\n"
                + "  <Length>00:01:00.0000000</Length>\n"
                + "  <AlbumCover>\n    <Count>0</Count>\n    <Width>0</Width>\n    <Height>0</Height>\n  </AlbumCover>\n"
                + "  <Compilation>False</Compilation>\n" + date + "</Track>\n";
        }

        /// <summary>Writes a fake XML at a mirror-relative path (backslashes); dateAdded null means the element is absent.</summary>
        private static void Put(string root, string rel, string dateAdded)
        {
            string full = Path.Combine(MirrorOf(root), rel);
            Directory.CreateDirectory(Path.GetDirectoryName(full));
            File.WriteAllText(full, Xml(dateAdded));
        }

        private static string Read(string root, string rel) => File.ReadAllText(Path.Combine(MirrorOf(root), rel));

        private static string CaptureConsole(Action action)
        {
            var capture = new StringWriter();
            TextWriter original = Console.Out;
            Console.SetOut(capture);
            try { action(); }
            finally { Console.SetOut(original); }
            return capture.ToString();
        }

        private static string Log(params string[] lines) => string.Join("\n", lines) + "\n";

        private static int YearCount(DateAddedBackfill.Report r, int year) =>
            r.PerYear.TryGetValue(year, out int n) ? n : -1;

        /// <summary>The recorded date for a key, or "(missing)" so a missing key fails as an assertion, not an exception.</summary>
        private static string Get(Dictionary<string, string> dates, string key) =>
            dates.TryGetValue(key, out string d) ? d : "(missing)";

        // ---- first-add dates from one bulk git log ----

        public static void FirstAddDates_UsesTheCommitThatFirstAddedTheFile()
        {
            string log = Log(
                "COMMIT 2020-01-05", "", "A\tAUDIO_MIRROR/Artists/A/Album/1.xml", "",
                "COMMIT 2021-02-02", "", "A\tAUDIO_MIRROR/Artists/A/Album/2.xml", "",
                "COMMIT 2022-03-03", "", "A\tAUDIO_MIRROR/Artists/A/Album/1.xml", "");
            Dictionary<string, string> dates = DateAddedBackfill.FirstAddDates(log);
            Assert.Equal("2020-01-05", Get(dates, "Artists\\A\\Album\\1.xml"), "first add wins over a later re-add");
            Assert.Equal("2021-02-02", Get(dates, "Artists\\A\\Album\\2.xml"), "second file keeps its own add date");
        }

        public static void FirstAddDates_RenameInheritsTheOldFilesDate()
        {
            string log = Log(
                "COMMIT 2019-03-03", "", "A\tAUDIO_MIRROR/Old/Name.xml", "",
                "COMMIT 2022-04-04", "", "R100\tAUDIO_MIRROR/Old/Name.xml\tAUDIO_MIRROR/New/Name.xml", "");
            Dictionary<string, string> dates = DateAddedBackfill.FirstAddDates(log);
            Assert.Equal("2019-03-03", Get(dates, "New\\Name.xml"), "renamed file inherits the old file's first-add date");
            Assert.Equal("2019-03-03", Get(dates, "Old\\Name.xml"), "old path keeps its date");
        }

        public static void FirstAddDates_RenameFromBeforeTheMirrorFolderInheritsTheDate()
        {
            // Older history kept XML at the repo root (no AUDIO_MIRROR/ prefix); a later rename moved it under the folder.
            string log = Log(
                "COMMIT 2019-01-01", "", "A\tArtists/A/Album/1.xml", "",
                "COMMIT 2022-04-04", "", "R100\tArtists/A/Album/1.xml\tAUDIO_MIRROR/Artists/A/Album/1.xml", "");
            Dictionary<string, string> dates = DateAddedBackfill.FirstAddDates(log);
            Assert.Equal("2019-01-01", Get(dates, "Artists\\A\\Album\\1.xml"), "rename from a path outside the folder keeps the first-add date");
        }

        public static void FirstAddDates_FirstCommitFilesGetThatCommitsDate()
        {
            string log = Log(
                "COMMIT 2018-01-01", "", "A\tAUDIO_MIRROR/Artists/A/Album/1.xml", "A\tAUDIO_MIRROR/Artists/A/Album/2.xml", "");
            Dictionary<string, string> dates = DateAddedBackfill.FirstAddDates(log);
            Assert.Equal("2018-01-01", Get(dates, "Artists\\A\\Album\\1.xml"), "file in the first commit gets that date");
            Assert.Equal("2018-01-01", Get(dates, "Artists\\A\\Album\\2.xml"), "second file in the first commit gets that date");
        }

        public static void FirstAddDates_IgnoresNonXmlAndOtherFolders()
        {
            string log = Log("COMMIT 2020-01-05", "", "A\tAUDIO_MIRROR/readme.txt", "A\tOtherFolder/1.xml", "");
            Assert.Equal("0", DateAddedBackfill.FirstAddDates(log).Count.ToString(), "only AUDIO_MIRROR XML files are keyed");
        }

        // ---- scan counts ----

        public static void Scan_ExistingDateAddedIsNeverCountedAsFill()
        {
            string root = NewTempRoot();
            Put(root, "Artists\\A\\Album\\1.xml", "2001-01-01");
            var dates = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { { "Artists\\A\\Album\\1.xml", "2020-01-05" } };
            DateAddedBackfill.Report r = DateAddedBackfill.Scan(MirrorOf(root), dates);
            Assert.Equal("1", r.AlreadySet.ToString(), "existing DateAdded counts as already set");
            Assert.Equal("0", r.ToFill.ToString(), "existing DateAdded is not a fill");
        }

        public static void Scan_PathWithNoHistoryCountsUnresolved()
        {
            string root = NewTempRoot();
            Put(root, "Artists\\A\\Album\\3.xml", null);
            DateAddedBackfill.Report r = DateAddedBackfill.Scan(MirrorOf(root), new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
            Assert.Equal("1", r.Unresolved.ToString(), "no history means unresolved");
            Assert.Equal("0", r.ToFill.ToString(), "unresolved is never filled");
        }

        public static void Scan_ReportsEarliestLatestAndPerYear()
        {
            string root = NewTempRoot();
            Put(root, "a.xml", null);
            Put(root, "b.xml", null);
            Put(root, "c.xml", null);
            var dates = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "a.xml", "2020-06-01" }, { "b.xml", "2020-01-02" }, { "c.xml", "2023-12-31" },
            };
            DateAddedBackfill.Report r = DateAddedBackfill.Scan(MirrorOf(root), dates);
            Assert.Equal("3", r.ToFill.ToString(), "three files to fill");
            Assert.Equal("2020-01-02", r.Earliest, "earliest fill date");
            Assert.Equal("2023-12-31", r.Latest, "latest fill date");
            Assert.Equal("2", YearCount(r, 2020).ToString(), "two fills dated 2020");
            Assert.Equal("1", YearCount(r, 2023).ToString(), "one fill dated 2023");
        }

        // ---- best guess: earliest of existing, git, mp3 ----

        public static void Earliest_PicksTheEarliestValidDateAndIgnoresJunk()
        {
            Assert.Equal("2019-05-05", DateAddedBackfill.Earliest("2026-11-28", null, "2019-05-05", "garbage", "2021-01-01"), "earliest valid wins");
            Assert.True(DateAddedBackfill.Earliest(null, "", "x") == null, "no valid candidate gives null");
        }

        public static void Scan_TodayStampIsReplacedByAnEarlierGitDate()
        {
            string root = NewTempRoot();
            Put(root, "a.xml", "2026-11-28");
            var dates = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { { "a.xml", "2023-03-10" } };
            DateAddedBackfill.Report r = DateAddedBackfill.Scan(MirrorOf(root), dates);
            Assert.Equal("1", r.ToFill.ToString(), "a wrong later stamp is refilled");
            Assert.Equal("2023-03-10", Get(r.FillDates, "a.xml"), "with the earlier git date");
        }

        public static void Scan_EarlierMp3CreationBeatsLaterGitDate_AndNeverMovesDatesLater()
        {
            string root = NewTempRoot();
            Put(root, "a.xml", null);
            Put(root, "b.xml", "2020-01-01");
            var dates = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { { "a.xml", "2023-03-10" }, { "b.xml", "2023-03-10" } };
            var saved = DateAddedBackfill.Mp3Created;
            DateAddedBackfill.Mp3Created = k => new DateTime(2018, 2, 3);
            try
            {
                DateAddedBackfill.Report r = DateAddedBackfill.Scan(MirrorOf(root), dates);
                Assert.Equal("2018-02-03", Get(r.FillDates, "a.xml"), "mp3 creation earlier than git wins");
                Assert.Equal("2018-02-03", Get(r.FillDates, "b.xml"), "an existing later date is lowered to the earlier source");
            }
            finally { DateAddedBackfill.Mp3Created = saved; }
        }

        // ---- TryRun: dry run and apply ----

        public static void TryRun_WithoutTheFlag_IsNotHandledAndRunsNoGit()
        {
            var git = new FakeGit();
            bool handled = DateAddedBackfill.TryRun(new[] { "analysis", "--as-of", "2025-01-01" }, git.Run, MirrorOf(NewTempRoot()), out int code);
            Assert.True(!handled, "not handled without --backfill-date-added");
            Assert.Equal("0", (git.LogCalls + git.StatusCalls + git.OtherCalls).ToString(), "no git call");
        }

        public static void DryRun_WritesNothing()
        {
            string root = NewTempRoot();
            Put(root, "Artists\\A\\Album\\1.xml", null);
            Put(root, "Artists\\A\\Album\\2.xml", "2001-01-01");
            Put(root, "Artists\\A\\Album\\3.xml", null);
            var git = new FakeGit
            {
                LogOutput = Log("COMMIT 2020-01-05", "", "A\tAUDIO_MIRROR/Artists/A/Album/1.xml", "A\tAUDIO_MIRROR/Artists/A/Album/2.xml", ""),
            };
            string before1 = Read(root, "Artists\\A\\Album\\1.xml");
            string before2 = Read(root, "Artists\\A\\Album\\2.xml");
            int files = Directory.GetFiles(MirrorOf(root), "*", SearchOption.AllDirectories).Length;

            int code = -1;
            string output = CaptureConsole(() => DateAddedBackfill.TryRun(new[] { "analysis", "--backfill-date-added" }, git.Run, MirrorOf(root), out code));

            Assert.Equal("0", code.ToString(), "dry run exits 0");
            Assert.Equal(before1, Read(root, "Artists\\A\\Album\\1.xml"), "dry run leaves the unset file byte-identical");
            Assert.Equal(before2, Read(root, "Artists\\A\\Album\\2.xml"), "dry run leaves the set file byte-identical");
            Assert.Equal(files.ToString(), Directory.GetFiles(MirrorOf(root), "*", SearchOption.AllDirectories).Length.ToString(), "dry run creates no files");
            Assert.True(output.Contains("To fill: 1"), "dry run reports the fill count");
            Assert.True(output.Contains("Already set: 1"), "dry run reports the already-set count");
            Assert.True(output.Contains("Unresolved"), "dry run reports unresolved");
            Assert.True(output.Contains("Nothing was written"), "dry run says it wrote nothing");
        }

        public static void Log_IsOneBulkCallForAllFiles()
        {
            string root = NewTempRoot();
            for (int i = 0; i < 5; i++) Put(root, "Artists\\A\\Album\\" + i + ".xml", null);
            var git = new FakeGit { LogOutput = Log("COMMIT 2020-01-05", "", "A\tAUDIO_MIRROR/Artists/A/Album/0.xml", "") };
            CaptureConsole(() => DateAddedBackfill.TryRun(new[] { "analysis", "--backfill-date-added" }, git.Run, MirrorOf(root), out _));
            Assert.Equal("1", git.LogCalls.ToString(), "exactly one git log call, never one per file");
            Assert.Equal("0", git.OtherCalls.ToString(), "no other unexpected git call in dry run");
        }

        public static void Apply_WritesDateAddedAndSecondApplyChangesNothing()
        {
            string root = NewTempRoot();
            Put(root, "Artists\\A\\Album\\1.xml", null);
            Put(root, "Artists\\A\\Album\\2.xml", "2001-01-01");
            Put(root, "Artists\\A\\Album\\3.xml", null);
            var git = new FakeGit
            {
                LogOutput = Log("COMMIT 2020-01-05", "", "A\tAUDIO_MIRROR/Artists/A/Album/1.xml", "A\tAUDIO_MIRROR/Artists/A/Album/2.xml", ""),
            };
            string keep2 = Read(root, "Artists\\A\\Album\\2.xml");

            int code = -1;
            CaptureConsole(() => DateAddedBackfill.TryRun(new[] { "analysis", "--backfill-date-added", "--apply" }, git.Run, MirrorOf(root), out code));
            Assert.Equal("0", code.ToString(), "apply exits 0");
            Assert.True(Read(root, "Artists\\A\\Album\\1.xml").Contains("<DateAdded>2020-01-05</DateAdded>"), "apply writes the commit date");
            Assert.Equal(keep2, Read(root, "Artists\\A\\Album\\2.xml"), "apply never touches an existing DateAdded");
            string after1 = Read(root, "Artists\\A\\Album\\1.xml");
            string after3 = Read(root, "Artists\\A\\Album\\3.xml");
            Assert.True(!after3.Contains("DateAdded"), "unresolved file stays without DateAdded");

            string second = CaptureConsole(() => DateAddedBackfill.TryRun(new[] { "analysis", "--backfill-date-added", "--apply" }, git.Run, MirrorOf(root), out code));
            Assert.True(second.Contains("To fill: 0"), "second apply has nothing to fill");
            Assert.Equal(after1, Read(root, "Artists\\A\\Album\\1.xml"), "second apply changes nothing");
        }

        public static void Apply_WarnsWithTheCountOfModifiedMirrorXmls()
        {
            string root = NewTempRoot();
            Put(root, "Artists\\A\\Album\\1.xml", null);
            var git = new FakeGit
            {
                LogOutput = Log("COMMIT 2020-01-05", "", "A\tAUDIO_MIRROR/Artists/A/Album/1.xml", ""),
                StatusOutput = " M AUDIO_MIRROR/Artists/A/Album/1.xml\n?? AUDIO_MIRROR/Artists/A/Album/2.xml\n M AUDIO_MIRROR/readme.txt\n",
            };
            string output = CaptureConsole(() => DateAddedBackfill.TryRun(new[] { "analysis", "--backfill-date-added", "--apply" }, git.Run, MirrorOf(root), out _));
            Assert.Equal("1", git.StatusCalls.ToString(), "apply reads git status once");
            Assert.True(output.Contains("WARNING") && output.Contains("2 AUDIO_MIRROR XML"), "apply warns with the count of modified mirror XML files (non-XML ignored)");
        }
    }
}
