using System.Collections.Generic;
using System.Linq;
using AudioManager.Code.Modules;

namespace AudioManager
{
    /// <summary>
    /// GUI-91: warn-only library checks (padded text, Unknown cover dims) are reported in an additive libWarnings list.
    /// They never change TotalHits (libIssues), HasBlockingHits, IsClean or any existing JSON key.
    /// </summary>
    internal static class LibWarningsTests
    {
        private const string Dur = "00:03:00.0000000";

        private static TrackTag Tag(string title, string artists, string album, string genre = "Pop", string trackNo = "1", string w = "1200", string h = "1200")
        {
            return new TrackTag("\\Artists\\Alpha\\Singles\\Alpha - Song.xml", title, artists, album, "2020", trackNo, genre, Dur, "1", "True", w, h);
        }

        private static List<string> WarnKinds(LibChecker c) { return c.Warnings.Select(x => x.Kind).ToList(); }

        public static void PaddedText_WarnsAndLeavesIssueCountIdentical()
        {
            var control = new LibChecker(new List<TrackTag> { Tag("Song", "Alpha", "Real Album") });
            var padded = new LibChecker(new List<TrackTag> { Tag("Song", "Alpha", "Real Album ") });
            Assert.True(WarnKinds(padded).Contains("padded_text"), "padded Album warns as padded_text");
            Assert.Equal(control.TotalHits.ToString(), padded.TotalHits.ToString(), "TotalHits (libIssues) unchanged by a warning");
            Assert.Equal(control.HasBlockingHits.ToString(), padded.HasBlockingHits.ToString(), "blocking result unchanged");
            Assert.Equal(control.Hits.Count.ToString(), padded.Hits.Count.ToString(), "hit list unchanged");
            Assert.True(padded.Hits.All(x => x.Kind != "padded_text"), "a warn id never enters Hits");
        }

        public static void DoubleSpaceAndPaddedTitleAndArtists_Warn()
        {
            var c = new LibChecker(new List<TrackTag> { Tag("So  ng", " Alpha", "Album") });
            Assert.True(c.Warnings.Count(x => x.Kind == "padded_text") >= 2, "Title double space and Artists padding both warn");
        }

        public static void UnknownCoverDims_WarnAndDoNotCount()
        {
            var control = new LibChecker(new List<TrackTag> { Tag("Song", "Alpha", "Album") });
            var unk = new LibChecker(new List<TrackTag> { Tag("Song", "Alpha", "Album", w: "Unknown", h: "Unknown") });
            Assert.True(WarnKinds(unk).Contains("unknown_cover_dims"), "Unknown cover dims warn");
            Assert.Equal(control.TotalHits.ToString(), unk.TotalHits.ToString(), "TotalHits unchanged");
            Assert.Equal(control.HasBlockingHits.ToString(), unk.HasBlockingHits.ToString(), "blocking unchanged");
        }

        public static void CleanTrack_HasNoWarnings()
        {
            var c = new LibChecker(new List<TrackTag> { Tag("Song", "Alpha", "Album") });
            Assert.Equal("0", c.Warnings.Count.ToString(), "a clean track has no warnings");
            Assert.Equal("0", c.WarningCount.ToString(), "WarningCount matches");
        }

        public static void GenreMissingAndTrackNumberZero_NeverWarn()
        {
            var c = new LibChecker(new List<TrackTag> { Tag("Song", "Alpha", "Album", genre: "Missing", trackNo: "0") });
            Assert.Equal("0", c.Warnings.Count.ToString(), "blank or Missing Genres and TrackNumber 0 never warn");
        }

        public static void AcceptRules_StillOnlyUnwantedText()
        {
            Assert.True(TagFixExceptions.ValidAcceptRules.Contains("unwanted_text"), "unwanted_text acceptable");
            foreach (string r in new[] { "padded_text", "unknown_cover_dims", "missing_tag", "bad_field", "bad_cover" })
                Assert.True(!TagFixExceptions.ValidAcceptRules.Contains(r), r + " is not acceptable");
        }

        public static void GateJson_AddsLibWarnings_KeepsExistingKeys()
        {
            var hit = new LibChecker.LibCheckerHit { Kind = "padded_text", Path = "a/b.xml", Detail = "Album has leading or trailing whitespace" };
            string json = GateResultJson.Build(Program.PreIntegrationGateResult.Passed, new List<string>(), 3, new System.DateTime(2026, 11, 10), new List<LibChecker.LibCheckerHit> { hit });
            Assert.True(json.Contains("\"libCheckerIssues\": 3"), "libCheckerIssues unchanged");
            Assert.True(json.Contains("\"libWarnings\": 1"), "libWarnings count");
            Assert.True(json.Contains("\"kind\": \"padded_text\""), "hit carries the rule id");
            string none = GateResultJson.Build(Program.PreIntegrationGateResult.Passed, new List<string>(), 0, new System.DateTime(2026, 11, 10));
            Assert.True(none.Contains("\"libWarnings\": 0") && none.Contains("\"libWarningHits\": []"), "absent warnings give 0 and []");
        }

        public static void MirrorJson_AddsLibWarnings_LibraryKeyUnchanged()
        {
            var r = new MirrorGateResult();
            r.LibraryWarnings.Add(new MirrorLibraryHit { Kind = "unknown_cover_dims", Path = "x.xml", Detail = "d" });
            string json = MirrorGate.ToJson(r);
            Assert.True(json.Contains("\"library\": []"), "library key unchanged");
            Assert.True(json.Contains("\"libWarnings\": [{\"kind\": \"unknown_cover_dims\""), "libWarnings listed, got " + json);
            Assert.True(r.Ok, "a warning never makes the result not ok");
        }
    }
}
