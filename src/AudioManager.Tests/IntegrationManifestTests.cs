using System.Linq;

namespace AudioManager
{
    /// <summary>
    /// Tests for the selective-integration manifest (integrate --manifest).
    /// Locks the parse contract a client writes and the two-way filename matching
    /// (raw dry-run name OR canonical TagFixer rename) that makes manifests
    /// survive TagFixer renames between the dry run and the real run.
    /// </summary>
    internal static class IntegrationManifestTests
    {
        private const string SampleJson = @"[
          {""filename"": ""21 Savage - see the real (Explicit).mp3"", ""artist"": ""21 Savage"", ""title"": ""see the real""},
          {""filename"": ""Aurora Bytes - Nightfall.mp3"", ""artist"": ""Aurora Bytes"", ""title"": ""Nightfall""}
        ]";

        public static void Parse_ReadsAllEntries()
        {
            var m = IntegrationManifest.Parse(SampleJson, out string error);
            Assert.True(error == null, $"no parse error, got: {error}");
            Assert.True(m.Entries.Count == 2, "two entries parsed");
            Assert.True(m.Entries[0].Artist == "21 Savage", "artist field mapped");
            Assert.True(m.Entries[1].Title == "Nightfall", "title field mapped");
        }

        private const string PairJson = @"[
          {""filename"": ""Lukis Mac - Intro.mp3"", ""sourceRel"": ""Album A\\Lukis Mac - Intro.mp3"", ""artist"": ""Lukis Mac"", ""title"": ""Intro"", ""dupResolution"": ""K""}
        ]";

        public static void SourceRel_EntryMatchesOnlyItsOwnFolder()
        {
            var m = IntegrationManifest.Parse(PairJson, out string error);
            Assert.True(error == null, $"no parse error, got: {error}");
            Assert.True(m.Matches("Lukis Mac - Intro.mp3", @"Album A\Lukis Mac - Intro.mp3"), "the accepted row matches");
            Assert.True(!m.Matches("Lukis Mac - Intro.mp3", @"Album B\Lukis Mac - Intro.mp3"),
                "a same-named file in another folder must stay declined");
        }

        public static void SourceRel_DupResolutionIsPerFile()
        {
            var m = IntegrationManifest.Parse(PairJson, out _);
            Assert.Equal("K", m.GetDupResolution("Lukis Mac - Intro.mp3", "Lukis Mac", "Intro", @"Album A\Lukis Mac - Intro.mp3").ToString(), "own row resolves");
            Assert.Equal("\0", m.GetDupResolution("Lukis Mac - Intro.mp3", "Lukis Mac", "Intro", @"Album B\Lukis Mac - Intro.mp3").ToString(), "other folder has no resolution");
        }

        // Manifest built from a PRE-apply routing: sourceRel holds the raw name, artist/title the tags.
        private const string PreApplyJson = @"[
          {""filename"": ""raw name 1.mp3"", ""sourceRel"": ""Album A\\raw name 1.mp3"", ""artist"": ""Lukis Mac"", ""title"": ""Intro""}
        ]";

        public static void SourceRel_AfterTagFixRename_MatchesOnlySameFolderCanonicalName()
        {
            var m = IntegrationManifest.Parse(PreApplyJson, out _);
            Assert.True(m.Matches("Lukis Mac - Intro.mp3", @"Album A\Lukis Mac - Intro.mp3"), "renamed file in the same folder still matches");
            Assert.True(!m.Matches("Lukis Mac - Intro.mp3", @"Album B\Lukis Mac - Intro.mp3"), "same new name in another folder must not match");
            Assert.True(!m.Matches("Lukis Mac - Intro.mp3", null), "a file with no path under NewMusic must not match a sourceRel row");
            Assert.True(!m.Matches("Lukis Mac - Intro.mp3"), "name alone must not match a sourceRel row");
        }

        public static void SourceRel_StaleEntry_MatchesNothingAndIsReportedUnmatched()
        {
            var m = IntegrationManifest.Parse(PreApplyJson, out _);
            Assert.True(!m.Matches("Something Else.mp3", @"Album A\Something Else.mp3"), "unrelated file in the same folder stays out");
            Assert.True(!m.Matches("raw name 1.mp3", @"Album B\raw name 1.mp3"), "the old raw name in another folder stays out");
            Assert.True(m.UnmatchedEntries().Count == 1, "the stale row is reported as unmatched, not silently dropped");
            Assert.True(m.GetDupResolution("Lukis Mac - Intro.mp3", "Lukis Mac", "Intro", @"Album B\Lukis Mac - Intro.mp3") == '\0', "no dup resolution leaks across folders");
        }

        public static void FilenameOnlyManifest_StillMatchesAnyFolder()
        {
            var m = IntegrationManifest.Parse(SampleJson, out _);
            Assert.True(m.Matches("Aurora Bytes - Nightfall.mp3", @"Some Folder\Aurora Bytes - Nightfall.mp3"), "old manifests keep matching by name");
        }

        public static void RoutingJson_EmitsSourceRelPerFile()
        {
            var e = new MusicIntegrator.LogEntry { Filename = "Intro.mp3", SourceRel = @"Album A\Intro.mp3", Status = "routed" };
            string json = MusicIntegrator.BuildJson(new System.Collections.Generic.List<MusicIntegrator.LogEntry> { e });
            Assert.True(json.Contains("\"sourceRel\": \"Album A\\\\Intro.mp3\""), "routing JSON carries sourceRel, got: " + json);
        }

        public static void RoutingJson_EmitsOriginalSourceRelBeforeTagFixRename()
        {
            var renamed = new MusicIntegrator.LogEntry { Filename = "Lukis Mac - Intro.mp3", SourceRel = @"Album A\Lukis Mac - Intro.mp3", OriginalSourceRel = @"Album A\1.mp3", Status = "moved" };
            var plain = new MusicIntegrator.LogEntry { Filename = "Intro.mp3", SourceRel = @"Album A\Intro.mp3", Status = "moved" };
            string json = MusicIntegrator.BuildJson(new System.Collections.Generic.List<MusicIntegrator.LogEntry> { renamed, plain }, dryRun: false);
            Assert.True(json.Contains("\"originalSourceRel\": \"Album A\\\\1.mp3\""), "a renamed row carries its pre-rename path, got: " + json);
            Assert.True(json.Contains("\"originalSourceRel\": \"Album A\\\\Intro.mp3\""), "an unrenamed row repeats sourceRel, got: " + json);
        }

        public static void RelToNewMusic_OnlyForFilesUnderNewMusic()
        {
            string inside = System.IO.Path.Combine(Constants.NewMusicPath, "Album A", "x.mp3");
            Assert.Equal(@"Album A\x.mp3", MusicIntegrator.RelToNewMusic(inside), "relative path under NewMusic");
            Assert.True(MusicIntegrator.RelToNewMusic(@"Z:\elsewhere\x.mp3") == null, "outside NewMusic gives null");
        }

        public static void RelToNewMusic_UsesGivenRoot()
        {
            string root = @"C:\fixture\nm";
            Assert.Equal(@"Album A\x.mp3", MusicIntegrator.RelToNewMusic(@"C:\fixture\nm\Album A\x.mp3", root), "relative path under the given root");
            Assert.True(MusicIntegrator.RelToNewMusic(System.IO.Path.Combine(Constants.NewMusicPath, "Album A", "x.mp3"), root) == null, "path under Constants.NewMusicPath is outside the given root");
            Assert.Equal(@"Album A\x.mp3", MusicIntegrator.RelToNewMusic(@"C:\fixture\nm\Album A\x.mp3", @"C:\fixture\nm\"), "trailing slash on the root is tolerated");
        }

        public static void Matches_ByExactFilename()
        {
            var m = IntegrationManifest.Parse(SampleJson, out _);
            Assert.True(m.Matches("21 Savage - see the real (Explicit).mp3"), "raw dry-run filename matches");
            Assert.True(!m.Matches("Some Other - Song.mp3"), "unlisted file does not match");
        }

        public static void Matches_ByCanonicalRenameAfterTagFixer()
        {
            // Real run: TagFixer renamed the file to "{artist} - {title}.mp3" before
            // the integrator listed NewMusic - the manifest must still match it.
            var m = IntegrationManifest.Parse(SampleJson, out _);
            Assert.True(m.Matches("21 Savage - see the real.mp3"), "canonical post-rename name matches");
        }

        public static void Matches_IsCaseInsensitive()
        {
            var m = IntegrationManifest.Parse(SampleJson, out _);
            Assert.True(m.Matches("AURORA BYTES - NIGHTFALL.MP3"), "matching ignores case");
        }

        public static void Parse_HandlesEscapedQuotesAndUnicode()
        {
            var m = IntegrationManifest.Parse(
                @"[{""filename"": ""AC\/DC - T.N.T..mp3"", ""artist"": ""AC\/DC"", ""title"": ""Say \""Hi\"" & Go""}]",
                out string error);
            Assert.True(error == null, $"no parse error, got: {error}");
            Assert.True(m.Entries[0].Title == "Say \"Hi\" & Go", "escapes decoded");
            Assert.True(m.Matches("AC/DC - T.N.T..mp3"), "escaped filename matches");
        }

        public static void Parse_RejectsGarbageAndEmpty()
        {
            Assert.True(IntegrationManifest.Parse("not json", out string e1) == null && e1 != null,
                "garbage rejected with error");
            Assert.True(IntegrationManifest.Parse("", out string e2) == null && e2 != null,
                "empty text rejected with error");
            Assert.True(IntegrationManifest.Parse("[]", out _)?.Entries.Count == 0,
                "empty array parses to zero entries (Load() then refuses it)");
        }

        public static void Parse_RejectsEntryWithNoUsableKeys()
        {
            var m = IntegrationManifest.Parse(@"[{""artist"": ""Solo Artist""}]", out string error);
            Assert.True(m == null && error != null, "entry without filename or artist+title rejected");
        }

        public static void UnmatchedEntries_ReportsStaleScanEntries()
        {
            var m = IntegrationManifest.Parse(SampleJson, out _);
            m.Matches("Aurora Bytes - Nightfall.mp3");
            var unmatched = m.UnmatchedEntries();
            Assert.True(unmatched.Count == 1, "one entry never matched");
            Assert.True(unmatched.Single().Artist == "21 Savage", "the unmatched entry is the 21 Savage one");
        }

        public static void Parse_ToleratesNonStringValues()
        {
            var m = IntegrationManifest.Parse(
                @"[{""filename"": ""A - B.mp3"", ""accepted"": true, ""order"": 3}]", out string error);
            Assert.True(error == null && m.Entries.Count == 1, "non-string values skipped without error");
        }

        // ---------------------------------------------- dupResolution (review-stage override)

        private const string DupJson = @"[
          {""filename"": ""Kendrick Lamar - HUMBLE.mp3"", ""artist"": ""Kendrick Lamar"", ""title"": ""HUMBLE."",
           ""dupResolution"": ""L""}
        ]";

        public static void Parse_ReadsDupResolutionField()
        {
            var m = IntegrationManifest.Parse(DupJson, out string error);
            Assert.True(error == null, $"no parse error, got: {error}");
            Assert.Equal("L", m.Entries[0].DupResolution);
        }

        public static void GetDupResolution_MatchesByExactFilename()
        {
            var m = IntegrationManifest.Parse(DupJson, out _);
            char r = m.GetDupResolution("Kendrick Lamar - HUMBLE.mp3", "Kendrick Lamar", "HUMBLE.");
            Assert.Equal("L", r.ToString());
        }

        public static void GetDupResolution_FallsBackToCanonicalArtistTitleAfterRename()
        {
            // Real run: TagFixer already renamed the file before the integrator listed NewMusic,
            // so the on-disk filename no longer matches the dry-run scan's filename - same
            // survival requirement as Matches_ByCanonicalRenameAfterTagFixer above.
            var m = IntegrationManifest.Parse(DupJson, out _);
            char r = m.GetDupResolution("some-other-on-disk-name.mp3", "Kendrick Lamar", "HUMBLE.");
            Assert.Equal("L", r.ToString());
        }

        public static void GetDupResolution_ReturnsNulCharWhenUnresolved()
        {
            var m = IntegrationManifest.Parse(SampleJson, out _); // entries with no dupResolution field
            char r = m.GetDupResolution("21 Savage - see the real (Explicit).mp3", "21 Savage", "see the real");
            Assert.Equal("\0", r.ToString());
        }

        public static void GetDupResolution_IgnoresGarbageValue()
        {
            var m = IntegrationManifest.Parse(
                @"[{""filename"": ""A - B.mp3"", ""artist"": ""A"", ""title"": ""B"", ""dupResolution"": ""Z""}]",
                out _);
            Assert.Equal("\0", m.GetDupResolution("A - B.mp3", "A", "B").ToString());
        }

        // A DECLINED file that already carries the canonical name must not be matched through
        // the same-folder rename fallback of an accepted raw-named entry.
        private const string DeclinedCanonicalJson = @"[
          {""filename"": ""1.mp3"", ""sourceRel"": ""Album A\\1.mp3"", ""artist"": ""Lukis Mac"", ""title"": ""Intro""}
        ]";

        public static void Preexisting_CanonicalNameNotMatchedByRenameFallback()
        {
            var m = IntegrationManifest.Parse(DeclinedCanonicalJson, out _);
            m.RecordPreexisting(new[] { @"Album A\1.mp3", @"Album A\Lukis Mac - Intro.mp3" });
            Assert.True(!m.Matches("Lukis Mac - Intro.mp3", @"Album A\Lukis Mac - Intro.mp3"),
                "a declined file that existed before Tag Fix must not match via the canonical rename fallback");
        }

        public static void Preexisting_RenamedAcceptedEntryStillMatchesWhenCanonicalWasNew()
        {
            var m = IntegrationManifest.Parse(DeclinedCanonicalJson, out _);
            m.RecordPreexisting(new[] { @"Album A\1.mp3" });
            Assert.True(m.Matches("Lukis Mac - Intro.mp3", @"Album A\Lukis Mac - Intro.mp3"),
                "the accepted file renamed by Tag Fix to a name that did not exist before still matches");
        }

        public static void Preexisting_ExactSourceRelStillMatches()
        {
            var m = IntegrationManifest.Parse(DeclinedCanonicalJson, out _);
            m.RecordPreexisting(new[] { @"Album A\1.mp3", @"Album A\Lukis Mac - Intro.mp3" });
            Assert.True(m.Matches("1.mp3", @"Album A\1.mp3"), "the accepted file still matches by its exact sourceRel");
        }
    }
}
