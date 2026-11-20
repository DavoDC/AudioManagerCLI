using System;
using System.Collections.Generic;
using System.Linq;

namespace AudioManager
{
    internal static class TagFixerTests
    {
        // ---- RemoveParentheticals ----

        public static void RemoveParentheticals_StripsFeatArtist()
        {
            string result = TagFixer.RemoveParentheticals("Song Title (feat. Some Artist)");
            Assert.Equal("Song Title", result, "feat. parenthetical should be stripped");
        }

        public static void RemoveParentheticals_StripsFtArtist()
        {
            string result = TagFixer.RemoveParentheticals("Song Title (ft. Some Artist)");
            Assert.Equal("Song Title", result, "ft. parenthetical should be stripped");
        }

        public static void RemoveParentheticals_StripsExplicit()
        {
            string result = TagFixer.RemoveParentheticals("Song Title (Explicit)");
            Assert.Equal("Song Title", result, "(Explicit) should be stripped");
        }

        public static void RemoveParentheticals_StripsAlbumVersion()
        {
            string result = TagFixer.RemoveParentheticals("Song Title (Album Version)");
            Assert.Equal("Song Title", result, "(Album Version) should be stripped");
        }

        public static void RemoveParentheticals_LeavesPlainTitle()
        {
            string result = TagFixer.RemoveParentheticals("Plain Song Title");
            Assert.Equal("Plain Song Title", result, "Plain title should be unchanged");
        }

        // ---- StripAlbumSuffixes ----

        public static void StripAlbumSuffixes_StripsDeluxeEdition()
        {
            string result = TagFixer.StripAlbumSuffixes("Album Name (Deluxe Edition)");
            Assert.Equal("Album Name", result, "(Deluxe Edition) should be stripped");
        }

        public static void StripAlbumSuffixes_StripsRemastered()
        {
            string result = TagFixer.StripAlbumSuffixes("Album Name (Remastered)");
            Assert.Equal("Album Name", result, "(Remastered) should be stripped");
        }

        public static void StripAlbumSuffixes_StripsYearSuffix()
        {
            string result = TagFixer.StripAlbumSuffixes("Album Name (2019)");
            Assert.Equal("Album Name", result, "Year suffix should be stripped");
        }

        public static void StripAlbumSuffixes_LeavesPlainAlbum()
        {
            string result = TagFixer.StripAlbumSuffixes("Plain Album Name");
            Assert.Equal("Plain Album Name", result, "Plain album name should be unchanged");
        }

        // ---- ExtractAndFixArtists ----

        public static void ExtractAndFixArtists_ExtractsFeaturedArtist()
        {
            List<string> result = TagFixer.ExtractAndFixArtists("Song (feat. Featured Artist)", "Primary Artist");
            Assert.True(
                result.Any(a => a.Equals("Featured Artist", StringComparison.OrdinalIgnoreCase)),
                "Featured artist should be extracted from (feat. X) parenthetical");
        }

        // ---- BL-65: trailing credit in a title ----
        public static void StripTrailingCredit_BareFeaturingAtEnd()
        {
            Assert.True(TagFixer.StripTrailingCredit("360 featuring robyn & yung lean") == "360", "bare featuring credit removed");
            Assert.True(TagFixer.StripTrailingCredit("Song ft. Some Artist") == "Song", "bare ft. credit removed");
            Assert.True(TagFixer.StripTrailingCredit("Song feat. Some Artist") == "Song", "bare feat. credit removed");
            Assert.True(TagFixer.StripTrailingCredit("Song (featuring Some Artist)") == "Song", "parenthesised featuring removed");
        }

        public static void StripTrailingCredit_LeavesRealTitles()
        {
            foreach (string t in new[] { "Dancing With Myself", "Stay With Me", "Ft. Lauderdale Blues", "Song (Live)", "Featuring Nobody Yet", "Song (Remix)" })
                Assert.True(TagFixer.StripTrailingCredit(t) == t, "unchanged: " + t);
        }

        public static void ExtractAndFixArtists_BareFeaturingMovesNamesToArtists()
        {
            List<string> result = TagFixer.ExtractAndFixArtists("360 featuring robyn & yung lean", "bladee");
            Assert.True(result.SequenceEqual(new[] { "bladee", "robyn", "yung lean" }), "names split once, not recased: " + string.Join("|", result));
            List<string> dedup = TagFixer.ExtractAndFixArtists("360 featuring robyn & yung lean", "Charli xcx; Robyn; Yung Lean");
            Assert.True(dedup.Count == 3, "no duplicates when the artist already lists the names");
        }

        public static void StripTrailingCredit_CurlyApostropheKept()
        {
            Assert.True(TagFixer.StripTrailingCredit(TagFixer.RemoveParentheticals("Because I’m Me (ft. Camp Lo)")) == "Because I’m Me", "curly apostrophe stays in the title");
        }

        public static void ExtractAndFixArtists_SplitsAmpersandInFeat()
        {
            // Perry Como class of bug: "(feat. Alpha & Beta)" must produce Alpha and Beta separately
            List<string> result = TagFixer.ExtractAndFixArtists("Song (feat. Artist Alpha & Artist Beta)", "Primary Artist");
            Assert.True(
                result.Any(a => a.Equals("Artist Alpha", StringComparison.OrdinalIgnoreCase)),
                "First artist in '& ' split should be in list");
            Assert.True(
                result.Any(a => a.Equals("Artist Beta", StringComparison.OrdinalIgnoreCase)),
                "Second artist in '& ' split should be in list");
            Assert.True(
                !result.Any(a => a.Equals("Artist Alpha & Artist Beta", StringComparison.OrdinalIgnoreCase)),
                "Compound '& ' form must NOT appear as a single artist entry");
        }

        public static void ExtractAndFixArtists_NoDuplicates()
        {
            // Artist already in the field should not be added again via feat. extraction
            List<string> result = TagFixer.ExtractAndFixArtists("Song (feat. Existing Artist)", "Primary Artist;Existing Artist");
            int count = result.Count(a => a.Equals("Existing Artist", StringComparison.OrdinalIgnoreCase));
            Assert.True(count == 1, $"Existing artist should appear exactly once, appeared {count} times");
        }

        public static void ExtractAndFixArtists_SkipsOfBandNameClarification()
        {
            // "Patrick Stump of Fall Out Boy" when Fall Out Boy already present - "of BandName" is a descriptor
            List<string> result = TagFixer.ExtractAndFixArtists(
                "Song (feat. Macklemore & Patrick Stump of Fall Out Boy)",
                "Primary Artist;Fall Out Boy");
            Assert.True(
                !result.Any(a => a.IndexOf("of Fall Out Boy", StringComparison.OrdinalIgnoreCase) >= 0),
                "'X of BandName' should be skipped when BandName is already in artist list");
        }

        // ---- ShouldFixGenre ----

        public static void ShouldFixGenre_MusivationArtistMissingGenre()
        {
            bool result = TagFixer.ShouldFixGenre("Akira The Don", "Hip-Hop");
            Assert.True(result, "Akira The Don without Musivation genre should need genre fix");
        }

        public static void ShouldFixGenre_MusivationArtistAlreadyHasGenre()
        {
            bool result = TagFixer.ShouldFixGenre("Akira The Don", "Musivation");
            Assert.True(!result, "Akira The Don already having Musivation genre should not need fix");
        }

        public static void ShouldFixGenre_NormalArtistNoChange()
        {
            bool result = TagFixer.ShouldFixGenre("Some Artist", "Hip-Hop");
            Assert.True(!result, "Normal artist with non-Motivation genre should not need fix");
        }

        // ---- DetermineGenre ----

        public static void DetermineGenre_MusivationForAkiraTheDon()
        {
            string result = TagFixer.DetermineGenre("Akira The Don");
            Assert.Equal("Musivation", result, "Akira The Don should get Musivation genre");
        }

        public static void DetermineGenre_MusivationForLootBryonSmith()
        {
            string result = TagFixer.DetermineGenre("Loot Bryon Smith");
            Assert.Equal("Musivation", result, "Loot Bryon Smith should get Musivation genre");
        }

        public static void DetermineGenre_MotivationForOtherArtist()
        {
            string result = TagFixer.DetermineGenre("Generic Motivation Speaker");
            Assert.Equal("Motivation", result, "Non-Musivation artist should get Motivation genre");
        }

        // ---- RemoveParentheticals - additional patterns ----

        public static void RemoveParentheticals_StripsWithArtist()
        {
            string result = TagFixer.RemoveParentheticals("Song Title (with Guest Artist)");
            Assert.Equal("Song Title", result, "(with X) should be stripped");
        }

        public static void RemoveParentheticals_StripsSquareBracketFeat()
        {
            string result = TagFixer.RemoveParentheticals("Song Title [feat. Featured Artist]");
            Assert.Equal("Song Title", result, "[feat. X] square bracket form should be stripped");
        }

        public static void RemoveParentheticals_StripsEdit()
        {
            string result = TagFixer.RemoveParentheticals("Song Title (Edit)");
            Assert.Equal("Song Title", result, "(Edit) should be stripped");
        }

        public static void RemoveParentheticals_StripsRadioEdit()
        {
            string result = TagFixer.RemoveParentheticals("Song Title (Radio Edit)");
            Assert.Equal("Song Title", result, "(Radio Edit) should be stripped");
        }

        public static void RemoveParentheticals_StripsSingleVersion()
        {
            string result = TagFixer.RemoveParentheticals("Song Title (Single Version)");
            Assert.Equal("Song Title", result, "(Single Version) should be stripped");
        }

        public static void RemoveParentheticals_MultipleParentheticals_BothStripped()
        {
            string result = TagFixer.RemoveParentheticals("Song Title (feat. Artist) (Explicit)");
            Assert.Equal("Song Title", result, "both parentheticals should be stripped");
        }

        // ---- StripAlbumSuffixes - additional patterns ----

        public static void StripAlbumSuffixes_Strips2011Remaster()
        {
            string result = TagFixer.StripAlbumSuffixes("Album Name (2011 Remaster)");
            Assert.Equal("Album Name", result, "(2011 Remaster) should be stripped");
        }

        public static void StripAlbumSuffixes_StripsInternationalVersion()
        {
            string result = TagFixer.StripAlbumSuffixes("Album Name (International Version)");
            Assert.Equal("Album Name", result, "(International Version) should be stripped");
        }

        public static void StripAlbumSuffixes_StripsDeluxeWithoutEdition()
        {
            string result = TagFixer.StripAlbumSuffixes("Album Name (Deluxe)");
            Assert.Equal("Album Name", result, "(Deluxe) without Edition suffix should be stripped");
        }

        public static void StripAlbumSuffixes_StripsBonusTrack()
        {
            string result = TagFixer.StripAlbumSuffixes("Album Name (Bonus Track)");
            Assert.Equal("Album Name", result, "(Bonus Track) should be stripped");
        }

        // ---- ShouldFixGenre - additional cases ----

        public static void ShouldFixGenre_LootBryonSmith_MissingMusivation_NeedsFix()
        {
            bool result = TagFixer.ShouldFixGenre("Loot Bryon Smith", "Hip-Hop");
            Assert.True(result, "Loot Bryon Smith without Musivation genre should need fix");
        }

        public static void ShouldFixGenre_MotivationalGenre_NeedsNormalization()
        {
            // "Motivational" contains "Motivation" but is not exactly "Motivation"
            bool result = TagFixer.ShouldFixGenre("Some Speaker", "Motivational");
            Assert.True(result, "Non-exact Motivation variant (Motivational) should need normalization");
        }

        // ---- ExtractAndFixArtists - additional patterns ----

        public static void ExtractAndFixArtists_WithPattern_ExtractsFeaturedArtist()
        {
            var result = TagFixer.ExtractAndFixArtists("Song (with Guest Artist)", "Primary Artist");
            Assert.True(
                result.Contains("Guest Artist") ||
                result.Exists(a => a.Equals("Guest Artist", System.StringComparison.OrdinalIgnoreCase)),
                "(with X) pattern should extract the featured artist");
        }

        public static void ExtractAndFixArtists_SquareBracketFeat_ExtractsFeaturedArtist()
        {
            var result = TagFixer.ExtractAndFixArtists("Song [feat. Featured Artist]", "Primary Artist");
            Assert.True(
                result.Exists(a => a.Equals("Featured Artist", System.StringComparison.OrdinalIgnoreCase)),
                "[feat. X] square bracket form should extract the featured artist");
        }

        public static void ExtractAndFixArtists_NeverRecasesArtistNames()
        {
            // BL-61: the source tag is the truth. Recasing turned PARTYNEXTDOOR into Partynextdoor.
            foreach (string name in new[] { "PARTYNEXTDOOR", "RZA", "MF DOOM", "SZA", "RaisedbyWolvvs", "lowercase artist name" })
            {
                List<string> result = TagFixer.ExtractAndFixArtists("Song Title", name);
                Assert.True(result.Count == 1 && result[0] == name, $"'{name}' must be kept exactly, got '{string.Join("|", result)}'");
            }
        }

        public static void RemoveParentheticals_KeepsRemixInTitle()
        {
            // (Remix) names a different recording; stripping it merged Ne-Yo's remix into the original.
            Assert.Equal("Because Of You (Remix)", TagFixer.RemoveParentheticals("Because Of You (Remix)"), "remix marker kept");
            Assert.Equal("Because Of You (Remix)", TagFixer.RemoveParentheticals("Because Of You (Remix) (feat. X)"), "feat still stripped, remix kept");
        }

        public static void ExtractAndFixArtists_FeatWithAnd_SplitsOnceNeverCombined()
        {
            // BL-60: 'feat. Kim Petras and Jay Park' added a combined 'Kim Petras And Jay Park' extra entry.
            List<string> result = TagFixer.ExtractAndFixArtists("bladee (feat. Kim Petras and Jay Park)", "bladee; Kim Petras; Jay Park");
            Assert.Equal("bladee|Kim Petras|Jay Park", string.Join("|", result), "each name once, source casing");
        }

        public static void ExtractAndFixArtists_CommaJoinedExistingEntryDroppedWhenSplitNamesPresent()
        {
            // BL-60 Justice case: 'Tame Impala, SZA, King Gizzard' comma-joined AND split entries.
            List<string> result = TagFixer.ExtractAndFixArtists("Flower In A Hurricane",
                "Justice; Tame Impala; SZA; King Gizzard; Tame Impala, SZA, King Gizzard");
            Assert.Equal("Justice|Tame Impala|SZA|King Gizzard", string.Join("|", result), "combined duplicate dropped");
        }

        public static void ExtractAndFixArtists_BandNameWithAndThe_NotSplit()
        {
            // Real dry-run finding (SZA / Justice): "King Gizzard & The Lizard Wizard" became two artists.
            List<string> a = TagFixer.ExtractAndFixArtists("Flower In A Hurricane (feat. SZA, King Gizzard & The Lizard Wizard)",
                "Justice; Tame Impala");
            Assert.Equal("Justice|Tame Impala|SZA|King Gizzard & The Lizard Wizard", string.Join("|", a), "feat band name kept whole");
            List<string> b = TagFixer.ExtractAndFixArtists("Song",
                "Justice; SZA; King Gizzard & The Lizard Wizard");
            Assert.Equal("Justice|SZA|King Gizzard & The Lizard Wizard", string.Join("|", b), "existing band entry kept whole");
        }

        // ---- Artist name variant override (Unicode diacritics -> canonical) ----

        public static void ExtractAndFixArtists_VariantArtistName_NormalizesToCanonical()
        {
            // JAŸ-Z (Ÿ = U+0178) is a Unicode-stylized variant of Jay-Z used by some providers.
            // The overrides config maps it to canonical "Jay-Z" via the variant attribute.
            List<string> result = TagFixer.ExtractAndFixArtists("Song Title", "JAŸ-Z");
            Assert.True(
                result.Any(a => a.Equals("Jay-Z", StringComparison.OrdinalIgnoreCase)),
                "Variant artist name JAŸ-Z should normalize to canonical Jay-Z via override");
            Assert.True(
                !result.Any(a => a.IndexOf("Ÿ", StringComparison.Ordinal) >= 0 || a.IndexOf("ÿ", StringComparison.Ordinal) >= 0),
                "No diaeresis form (Ÿ or ÿ) should appear in output - variant must resolve to canonical");
        }

        public static void ExtractAndFixArtists_VariantArtistAndFeatCanonical_NoDuplicate()
        {
            // Real-world case: "The Notorious B.I.G.; Angela Winbush; JAŸ-Z" tag
            // + title "(feat. Jay-Z & Angela Winbush)" -> must NOT produce Jay-Z;Jay-Z
            // (JAŸ-Z in tag normalizes to Jay-Z via variant override, dedup removes the feat. Jay-Z copy)
            List<string> result = TagFixer.ExtractAndFixArtists(
                "I Love The Dough (feat. Jay-Z & Angela Winbush)",
                "The Notorious B.I.G.;Angela Winbush;JAŸ-Z");
            int jayzCount = result.Count(a => a.Equals("Jay-Z", StringComparison.OrdinalIgnoreCase));
            Assert.True(jayzCount == 1,
                $"Jay-Z should appear exactly once (got {jayzCount}): variant JAŸ-Z in tag + Jay-Z in feat must deduplicate");
        }

        // ---- Artist apostrophe variants dedupe (first spelling kept) ----

        public static void ExtractAndFixArtists_ApostropheVariantFeat_DroppedWhenTagHasNoApostrophe()
        {
            // Real batch: title feat "Lil' Wayne" + tag "Lil Wayne" produced two spellings of one artist.
            List<string> result = TagFixer.ExtractAndFixArtists("Official Girl (feat. Lil' Wayne)", "Cassie; Lil Wayne");
            Assert.Equal("Cassie|Lil Wayne", string.Join("|", result), "later apostrophe variant dropped, first spelling kept");
        }

        public static void ExtractAndFixArtists_ApostropheVariantTag_FirstSpellingKept()
        {
            // Tag already has "Lil' Wayne"; title feat "Lil Wayne" must not add a second entry.
            List<string> result = TagFixer.ExtractAndFixArtists("Official Girl (feat. Lil Wayne)", "Cassie; Lil' Wayne");
            Assert.Equal("Cassie|Lil' Wayne", string.Join("|", result), "existing apostrophe spelling kept as read");
        }

        public static void ExtractAndFixArtists_CurlyApostropheTitle_DedupesAgainstStraightTag()
        {
            // Curly right single quote U+2019 in title, straight apostrophe in tags.
            List<string> result = TagFixer.ExtractAndFixArtists("Official Girl (feat. Lil’ Wayne)", "Cassie; Lil' Wayne");
            Assert.Equal("Cassie|Lil' Wayne", string.Join("|", result), "curly and straight apostrophe are one artist");
        }

        public static void ExtractAndFixArtists_DistinctArtistNames_NotMergedByApostropheKey()
        {
            // Regression: names that differ by more than the apostrophe stay separate.
            List<string> result = TagFixer.ExtractAndFixArtists("Official Girl (feat. Drake)", "Cassie; Lil Wayne");
            Assert.Equal("Cassie|Lil Wayne|Drake", string.Join("|", result), "distinct artists untouched");
        }

        // ---- Null/empty input guards ----

        public static void RemoveParentheticals_EmptyString_ReturnsEmpty()
        {
            string result = TagFixer.RemoveParentheticals("");
            Assert.Equal("", result, "empty input should return empty (null guard)");
        }

        public static void StripAlbumSuffixes_EmptyString_ReturnsEmpty()
        {
            string result = TagFixer.StripAlbumSuffixes("");
            Assert.Equal("", result, "empty input should return empty (null guard)");
        }

        public static void ShouldFixGenre_EmptyArtists_ReturnsFalse()
        {
            bool result = TagFixer.ShouldFixGenre("", "Hip-Hop");
            Assert.True(!result, "empty artists string should not need genre fix");
        }

        public static void DetermineGenre_EmptyArtists_ReturnsEmptyString()
        {
            string result = TagFixer.DetermineGenre("");
            Assert.Equal("", result, "empty artists string should return empty genre");
        }

        // ---- StripAlbumSuffixes - version-suffix normalization (B.I.G. bug 2026-06-28) ----

        public static void StripAlbumSuffixes_StripsYearRemasteredEdition()
        {
            // Root case: "Life After Death (2014 Remastered Edition)" was not stripped before this fix.
            // The remaster pattern stopped at Remaster(ed) without the trailing Edition word.
            string result = TagFixer.StripAlbumSuffixes("Life After Death (2014 Remastered Edition)");
            Assert.Equal("Life After Death", result, "(YYYY Remastered Edition) should be stripped");
        }

        public static void StripAlbumSuffixes_StripsRemasteredEdition()
        {
            string result = TagFixer.StripAlbumSuffixes("Album Name (Remastered Edition)");
            Assert.Equal("Album Name", result, "(Remastered Edition) without year should be stripped");
        }

        public static void StripAlbumSuffixes_StripsYearRemasterEdition()
        {
            string result = TagFixer.StripAlbumSuffixes("Album Name (2011 Remaster Edition)");
            Assert.Equal("Album Name", result, "(YYYY Remaster Edition) should be stripped");
        }

        public static void StripAlbumSuffixes_StripsMono()
        {
            string result = TagFixer.StripAlbumSuffixes("Album Name (Mono)");
            Assert.Equal("Album Name", result, "(Mono) should be stripped");
        }

        public static void StripAlbumSuffixes_StripsStereo()
        {
            string result = TagFixer.StripAlbumSuffixes("Album Name (Stereo)");
            Assert.Equal("Album Name", result, "(Stereo) should be stripped");
        }

        // ---- Intentionally NOT stripped (documents design boundaries) ----

        public static void RemoveParentheticals_LivePattern_NotStripped()
        {
            // (Live) is not in the strip patterns - live performances keep their label
            string result = TagFixer.RemoveParentheticals("Song Title (Live)");
            Assert.Equal("Song Title (Live)", result, "(Live) should NOT be stripped - not in strip patterns");
        }

        public static void RemoveParentheticals_AcousticPattern_NotStripped()
        {
            // (Acoustic) is also not in the strip patterns
            string result = TagFixer.RemoveParentheticals("Song Title (Acoustic)");
            Assert.Equal("Song Title (Acoustic)", result, "(Acoustic) should NOT be stripped - not in strip patterns");
        }

        public static void StripAlbumSuffixes_LivePattern_NotStripped()
        {
            // Album "(Live at Wembley)" should not be stripped - only specific edition/remaster patterns are removed
            string result = TagFixer.StripAlbumSuffixes("Album Name (Live at Wembley)");
            Assert.Equal("Album Name (Live at Wembley)", result, "(Live at ...) should NOT be stripped from album names");
        }

        // ---- ExtractAndFixArtists - casing bugs ----

        public static void ExtractAndFixArtists_MixedCaseAbbreviation_Preserved()
        {
            // Bug fix: "PJ Simas".ToLower() = "pj simas" -> ToTitleCase = "Pj Simas" (wrong).
            // Mixed-case names (upper + lower) must be preserved without ToLower() normalization.
            List<string> result = TagFixer.ExtractAndFixArtists("Ocean Drop", "PJ Simas");
            Assert.True(
                result.Any(a => a.Equals("PJ Simas", StringComparison.Ordinal)),
                "Mixed-case abbreviation 'PJ Simas' must be preserved as-is (not lowercased to 'Pj Simas')");
        }

        public static void ExtractAndFixArtists_AllCapsAbbreviation_InOverrides_Preserved()
        {
            // "XV" is all-caps and in the artist-name-overrides.xml config.
            // The override lookup must fire before the ToLower+ToTitleCase normalization destroys it.
            // Note: this test only passes if the overrides config file exists and contains "XV".
            List<string> result = TagFixer.ExtractAndFixArtists("Mirror's Edge", "XV");
            Assert.True(
                result.Any(a => a.Equals("XV", StringComparison.Ordinal)),
                "All-caps override 'XV' must be preserved via artist-name-overrides.xml (not normalized to 'Xv')");
        }

        public static void ExtractAndFixArtists_AllCapsFullName_KeptAsSourceTag()
        {
            // BL-61 supersedes the old title-casing rule: an all-caps name is the artist's own styling.
            List<string> result = TagFixer.ExtractAndFixArtists("Some Track", "NOT IN OVERRIDES");
            Assert.True(result.Count == 1 && result[0] == "NOT IN OVERRIDES", "all-caps name kept as is");
        }

        // ---- ProcessFile (full pipeline: real MP3 file I/O, not pure functions) ----

        /// <summary>
        /// Writes a minimal silent MPEG1 Layer III file (128kbps/44100Hz, no CRC) with the given
        /// starting tags. No binary fixture is committed to the repo - the frame is synthesized
        /// so TagLib# recognizes the file and can read/write ID3v2 tags on it.
        /// </summary>
        internal static string CreateSilentMp3Fixture(string directory, string filename, string title, string album, string artist)
        {
            string path = System.IO.Path.Combine(directory, filename);
            const int frameLength = 417; // floor(144 * 128000 / 44100), no padding
            byte[] frame = new byte[frameLength];
            frame[0] = 0xFF; frame[1] = 0xFB; frame[2] = 0x90; frame[3] = 0x64;
            using (var fs = new System.IO.FileStream(path, System.IO.FileMode.Create, System.IO.FileAccess.Write))
            {
                for (int i = 0; i < 20; i++) fs.Write(frame, 0, frame.Length);
            }

            TagLib.File tagFile = TagLib.File.Create(path);
            tagFile.Tag.Title = title;
            tagFile.Tag.Album = album;
            tagFile.Tag.Performers = new[] { artist };
            var id3 = (TagLib.Id3v2.Tag)tagFile.GetTag(TagLib.TagTypes.Id3v2, true);
            id3.IsCompilation = false;
            tagFile.Save();
            return path;
        }

        public static void ProcessFile_MessyTags_WritesCleanedId3TagsToDisk()
        {
            string tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AudioManagerTest_" + Guid.NewGuid());
            System.IO.Directory.CreateDirectory(tempDir);
            try
            {
                string fixturePath = CreateSilentMp3Fixture(tempDir, "fixture.mp3",
                    "Song Title (feat. Featured Artist)", "Album Name (Deluxe Edition)", "Primary Artist");

                var fixer = TagFixer.ForTesting(dryRun: false);
                var log = fixer.ProcessFile(fixturePath);

                string resultPath = System.IO.Path.Combine(tempDir, log.Filename);
                TagLib.File resultFile = TagLib.File.Create(resultPath);
                Assert.Equal("Song Title", resultFile.Tag.Title, "Title should have feat. parenthetical stripped");
                Assert.Equal("Album Name", resultFile.Tag.Album, "Album should have Deluxe Edition suffix stripped");
                Assert.True(
                    resultFile.Tag.JoinedPerformers.IndexOf("Featured Artist", StringComparison.OrdinalIgnoreCase) >= 0,
                    "Featured artist extracted from title should appear in Performers");
            }
            finally
            {
                System.IO.Directory.Delete(tempDir, true);
            }
        }

        public static void ProcessFile_MessyTags_RenamesFileToArtistDashTitle()
        {
            string tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AudioManagerTest_" + Guid.NewGuid());
            System.IO.Directory.CreateDirectory(tempDir);
            try
            {
                string fixturePath = CreateSilentMp3Fixture(tempDir, "messy filename.mp3",
                    "Song Title", "Album Name", "Primary Artist");

                var fixer = TagFixer.ForTesting(dryRun: false);
                var log = fixer.ProcessFile(fixturePath);

                Assert.Equal("Primary Artist - Song Title.mp3", log.Filename, "File should be renamed to 'Artist - Title.mp3' convention");
                Assert.True(!System.IO.File.Exists(fixturePath), "Original messy-named file should no longer exist after rename");
                string resultPath = System.IO.Path.Combine(tempDir, log.Filename);
                Assert.True(System.IO.File.Exists(resultPath), "Renamed file should exist on disk");
            }
            finally
            {
                System.IO.Directory.Delete(tempDir, true);
            }
        }

        // Pins the rename ordering: the TagLib handle must be released before File.Move, so the file
        // lands at the new path with the new tags readable there and the old path is gone.
        public static void ProcessFile_TagsAndNameChange_RenamedFileReadableAtNewPath()
        {
            string tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AudioManagerTest_" + Guid.NewGuid());
            System.IO.Directory.CreateDirectory(tempDir);
            try
            {
                string fixturePath = CreateSilentMp3Fixture(tempDir, "messy filename.mp3",
                    "Song Title (Explicit)", "Album Name", "Primary Artist");

                var fixer = TagFixer.ForTesting(dryRun: false);
                var log = fixer.ProcessFile(fixturePath);

                Assert.Equal("fixed", log.Status, "tags needing a fix and a name change must end with status fixed");
                string newPath = System.IO.Path.Combine(tempDir, "Primary Artist - Song Title.mp3");
                Assert.True(System.IO.File.Exists(newPath), "file must be renamed to the new path");
                Assert.True(!System.IO.File.Exists(fixturePath), "old path must be gone after rename");
                using (TagLib.File renamed = TagLib.File.Create(newPath))
                {
                    Assert.Equal("Song Title", renamed.Tag.Title, "new tags must be readable at the new path");
                }
            }
            finally
            {
                System.IO.Directory.Delete(tempDir, true);
            }
        }

        // A rename that throws (destination name already taken by a directory, so File.Move fails on any OS)
        // must keep the fixed status, keep the original name, and not throw out of ProcessFile.
        public static void ProcessFile_RenameFails_KeepsFixedStatusAndOriginalName()
        {
            string tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AudioManagerTest_" + Guid.NewGuid());
            System.IO.Directory.CreateDirectory(tempDir);
            try
            {
                string fixturePath = CreateSilentMp3Fixture(tempDir, "messy filename.mp3",
                    "Song Title", "Album Name", "Primary Artist");
                System.IO.Directory.CreateDirectory(System.IO.Path.Combine(tempDir, "Primary Artist - Song Title.mp3"));

                var fixer = TagFixer.ForTesting(dryRun: false);
                var log = fixer.ProcessFile(fixturePath);

                Assert.Equal("fixed", log.Status, "a failed rename must not mark the file as an error");
                Assert.True(System.IO.File.Exists(fixturePath), "file must stay at its original path when the rename fails");
                Assert.Equal("messy filename.mp3", log.Filename, "filename must report the original name when the rename failed");
                Assert.True(log.Detail != null && log.Detail.StartsWith("rename failed", StringComparison.Ordinal), "detail must note the rename failed");
            }
            finally
            {
                System.IO.Directory.Delete(tempDir, true);
            }
        }

        // A fix whose target name is already held by a different file must skip the rename and report the original
        // name, so the tag changes are not attributed to the file that owns the target name. Both files stay put.
        public static void ProcessFile_RenameBlockedByDifferentFile_KeepsOriginalNameAndBothFiles()
        {
            string tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AudioManagerTest_" + Guid.NewGuid());
            System.IO.Directory.CreateDirectory(tempDir);
            try
            {
                string blockerPath = System.IO.Path.Combine(tempDir, "Primary Artist - Song Title.mp3");
                byte[] blockerBytes = System.Text.Encoding.ASCII.GetBytes("different file holding the target name");
                System.IO.File.WriteAllBytes(blockerPath, blockerBytes);

                string fixturePath = CreateSilentMp3Fixture(tempDir, "messy filename.mp3",
                    "Song Title", "Album Name", "Primary Artist");

                var fixer = TagFixer.ForTesting(dryRun: false);
                var log = fixer.ProcessFile(fixturePath);

                Assert.Equal("fixed", log.Status, "a blocked rename must not mark the file as an error");
                Assert.Equal("messy filename.mp3", log.Filename, "a blocked rename must report the original name, not the name owned by another file");
                Assert.True(System.IO.File.Exists(fixturePath), "file must stay at its original path when another file holds the target name");
                Assert.True(blockerBytes.SequenceEqual(System.IO.File.ReadAllBytes(blockerPath)), "the blocking file must keep its original bytes");
            }
            finally
            {
                System.IO.Directory.Delete(tempDir, true);
            }
        }

        // A file already at its target name must keep reporting that name after a tag-only fix (no rename happens).
        public static void ProcessFile_AlreadyCorrectlyNamed_ReportsOwnNameAfterTagFix()
        {
            string tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AudioManagerTest_" + Guid.NewGuid());
            System.IO.Directory.CreateDirectory(tempDir);
            try
            {
                string fixturePath = CreateSilentMp3Fixture(tempDir, "Primary Artist - Song Title.mp3",
                    "Song Title (Explicit)", "Album Name", "Primary Artist");

                var fixer = TagFixer.ForTesting(dryRun: false);
                var log = fixer.ProcessFile(fixturePath);

                Assert.Equal("fixed", log.Status, "a title needing a parenthetical strip must be fixed");
                Assert.Equal("Primary Artist - Song Title.mp3", log.Filename, "a file already at its target name must report that name");
                Assert.True(System.IO.File.Exists(fixturePath), "file must stay at its path when no rename is needed");
            }
            finally
            {
                System.IO.Directory.Delete(tempDir, true);
            }
        }

        public static void ProcessFile_CleanTags_SkipsWithNoChanges()
        {
            string tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AudioManagerTest_" + Guid.NewGuid());
            System.IO.Directory.CreateDirectory(tempDir);
            try
            {
                string fixturePath = CreateSilentMp3Fixture(tempDir, "Primary Artist - Clean Title.mp3",
                    "Clean Title", "Clean Album", "Primary Artist");

                var fixer = TagFixer.ForTesting(dryRun: false);
                fixer.ProcessFile(fixturePath); // first pass only normalizes TCMP, filename unchanged
                var secondLog = fixer.ProcessFile(fixturePath);

                Assert.Equal("skipped", secondLog.Status, "Second pass over already-clean, TCMP-set file should skip with no changes");
            }
            finally
            {
                System.IO.Directory.Delete(tempDir, true);
            }
        }

        public static void ProcessFile_TwoPerformerArray_NoArtistRewriteOrArtistRename()
        {
            string tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AudioManagerTest_" + Guid.NewGuid());
            System.IO.Directory.CreateDirectory(tempDir);
            try
            {
                string fixturePath = CreateSilentMp3Fixture(tempDir, "Artist One; Artist Two - Clean Title.mp3",
                    "Clean Title", "Clean Album", "Artist One");
                var seed = TagLib.File.Create(fixturePath);
                seed.Tag.Performers = new[] { "Artist One", "Artist Two" };
                seed.Save();
                Assert.Equal("Artist One; Artist Two", TagLib.File.Create(fixturePath).Tag.JoinedPerformers, "fixture precondition: TagLib# joins with '; '");

                var fixer = TagFixer.ForTesting(dryRun: false);
                var log = fixer.ProcessFile(fixturePath);

                Assert.True(!log.Changes.Any(c => c.StartsWith("Artists:")), "separator spacing alone must not log an Artists change");
                Assert.Equal("Artist One; Artist Two - Clean Title.mp3", log.Filename, "file must not be renamed over separator spacing");
                Assert.True(System.IO.File.Exists(fixturePath), "original path must still exist");
                var after = TagLib.File.Create(fixturePath);
                Assert.Equal("2", after.Tag.Performers.Length.ToString(), "Performers array must stay two entries");
            }
            finally
            {
                System.IO.Directory.Delete(tempDir, true);
            }
        }

        // Pins the quarantine path: the instrumental must leave its original location after ProcessFile, even
        // though TagLib opened the file for tag reads (the handle must be released before File.Move).
        public static void ProcessFile_AkiraInstrumental_QuarantinesFileWhileTagsOpen()
        {
            string tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AudioManagerTest_" + Guid.NewGuid());
            System.IO.Directory.CreateDirectory(tempDir);
            try
            {
                string fixturePath = CreateSilentMp3Fixture(tempDir, "Akira The Don - Track (Instrumental).mp3",
                    "Track (Instrumental)", "Album", "Akira The Don");

                var fixer = TagFixer.ForTesting(dryRun: false, quarantineRoot: System.IO.Path.Combine(tempDir, "NewMusic-Quarantine"));
                var log = fixer.ProcessFile(fixturePath);

                Assert.Equal("skipped", log.Status, "instrumental should take the quarantine branch");
                Assert.True(!System.IO.File.Exists(fixturePath), "instrumental must leave its original location, not stay locked in place");
            }
            finally
            {
                System.IO.Directory.Delete(tempDir, true);
            }
        }

        public static void ProcessFile_AkiraInstrumental_LiveRunMovesFileToQuarantine()
        {
            string tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AudioManagerTest_" + Guid.NewGuid());
            System.IO.Directory.CreateDirectory(tempDir);
            try
            {
                string quarantineDir = System.IO.Path.Combine(tempDir, "NewMusic-Quarantine");
                string fixturePath = CreateSilentMp3Fixture(tempDir, "Akira The Don - Track (Instrumental).mp3",
                    "Track (Instrumental)", "Album", "Akira The Don");

                var fixer = TagFixer.ForTesting(dryRun: false, quarantineRoot: quarantineDir);
                var log = fixer.ProcessFile(fixturePath);

                Assert.Equal("skipped", log.Status, "instrumental should take the quarantine branch");
                Assert.True(!System.IO.File.Exists(fixturePath), "instrumental must leave its original location");
                string movedPath = System.IO.Path.Combine(quarantineDir, "Akira The Don - Track (Instrumental).mp3");
                Assert.True(System.IO.File.Exists(movedPath), "instrumental must be moved into the quarantine folder");
                Assert.True(log.Detail.StartsWith("quarantined instrumental: ", StringComparison.Ordinal), "detail must record the quarantine destination");
            }
            finally
            {
                System.IO.Directory.Delete(tempDir, true);
            }
        }

        public static void ProcessFile_AkiraInstrumental_NameCollisionKeepsBothFiles()
        {
            string tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AudioManagerTest_" + Guid.NewGuid());
            System.IO.Directory.CreateDirectory(tempDir);
            try
            {
                string quarantineDir = System.IO.Path.Combine(tempDir, "NewMusic-Quarantine");
                System.IO.Directory.CreateDirectory(quarantineDir);
                string occupantPath = System.IO.Path.Combine(quarantineDir, "Akira The Don - Track (Instrumental).mp3");
                byte[] occupantBytes = System.Text.Encoding.ASCII.GetBytes("earlier quarantined file");
                System.IO.File.WriteAllBytes(occupantPath, occupantBytes);

                string fixturePath = CreateSilentMp3Fixture(tempDir, "Akira The Don - Track (Instrumental).mp3",
                    "Track (Instrumental)", "Album", "Akira The Don");
                byte[] incomingBytes = System.IO.File.ReadAllBytes(fixturePath);

                var fixer = TagFixer.ForTesting(dryRun: false, quarantineRoot: quarantineDir);
                var log = fixer.ProcessFile(fixturePath);

                Assert.Equal("skipped", log.Status, "collision must still take the quarantine branch");
                Assert.True(!System.IO.File.Exists(fixturePath), "incoming instrumental must leave its original location");
                Assert.True(occupantBytes.SequenceEqual(System.IO.File.ReadAllBytes(occupantPath)), "existing quarantined file must keep its original bytes");
                string[] others = System.IO.Directory.GetFiles(quarantineDir).Where(f => f != occupantPath).ToArray();
                Assert.Equal("1", others.Length.ToString(), "incoming instrumental must land under a new, non-clashing name");
                Assert.True(incomingBytes.SequenceEqual(System.IO.File.ReadAllBytes(others[0])), "moved file must keep the incoming bytes");
            }
            finally
            {
                System.IO.Directory.Delete(tempDir, true);
            }
        }

        // The collision suffix is a timestamp. Under a culture whose default calendar is not Gregorian (Thai Buddhist
        // years, +543) a culture-sensitive format would stamp the wrong year, so the name must match the invariant date.
        public static void ProcessFile_AkiraInstrumental_CollisionStampIsInvariantUnderThaiCulture()
        {
            string tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AudioManagerTest_" + Guid.NewGuid());
            System.IO.Directory.CreateDirectory(tempDir);
            System.Globalization.CultureInfo previous = System.Threading.Thread.CurrentThread.CurrentCulture;
            try
            {
                string quarantineDir = System.IO.Path.Combine(tempDir, "NewMusic-Quarantine");
                System.IO.Directory.CreateDirectory(quarantineDir);
                string occupantPath = System.IO.Path.Combine(quarantineDir, "Akira The Don - Track (Instrumental).mp3");
                System.IO.File.WriteAllBytes(occupantPath, System.Text.Encoding.ASCII.GetBytes("earlier quarantined file"));

                string fixturePath = CreateSilentMp3Fixture(tempDir, "Akira The Don - Track (Instrumental).mp3",
                    "Track (Instrumental)", "Album", "Akira The Don");

                var fixer = TagFixer.ForTesting(dryRun: false, quarantineRoot: quarantineDir);
                System.Threading.Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("th-TH");
                string expectedDate = DateTime.Now.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture);
                string thaiYearText = DateTime.Now.ToString("yyyy");
                var log = fixer.ProcessFile(fixturePath);
                System.Threading.Thread.CurrentThread.CurrentCulture = previous;

                Assert.True(thaiYearText != expectedDate.Substring(0, 4), "precondition: th-TH must format the year differently from invariant");
                Assert.Equal("skipped", log.Status, "collision must still take the quarantine branch");
                string[] others = System.IO.Directory.GetFiles(quarantineDir).Where(f => f != occupantPath).ToArray();
                Assert.Equal("1", others.Length.ToString(), "incoming instrumental must land under a new, non-clashing name");
                string stampedPrefix = "Akira The Don - Track (Instrumental) (" + expectedDate + "-";
                Assert.True(System.IO.Path.GetFileName(others[0]).StartsWith(stampedPrefix, StringComparison.Ordinal),
                    "collision stamp must use the invariant Gregorian date under th-TH");
            }
            finally
            {
                System.Threading.Thread.CurrentThread.CurrentCulture = previous;
                System.IO.Directory.Delete(tempDir, true);
            }
        }

        public static void ProcessFile_AkiraInstrumental_DryRunTouchesNothing()
        {
            string tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AudioManagerTest_" + Guid.NewGuid());
            System.IO.Directory.CreateDirectory(tempDir);
            try
            {
                string quarantineDir = System.IO.Path.Combine(tempDir, "NewMusic-Quarantine");
                string fixturePath = CreateSilentMp3Fixture(tempDir, "Akira The Don - Track (Instrumental).mp3",
                    "Track (Instrumental)", "Album", "Akira The Don");
                byte[] before = System.IO.File.ReadAllBytes(fixturePath);

                var fixer = TagFixer.ForTesting(dryRun: true, quarantineRoot: quarantineDir);
                var log = fixer.ProcessFile(fixturePath);

                Assert.Equal("skipped", log.Status, "dry run should report the instrumental as skipped");
                Assert.True(System.IO.File.Exists(fixturePath), "dry run must leave the instrumental in place");
                Assert.True(before.SequenceEqual(System.IO.File.ReadAllBytes(fixturePath)), "dry run must not change the file bytes");
                Assert.True(!System.IO.Directory.Exists(quarantineDir), "dry run must not create the quarantine folder");
            }
            finally
            {
                System.IO.Directory.Delete(tempDir, true);
            }
        }

        // ENG-83: a tag still carrying unwanted text or a missing field after the fix is a needsManual row with a rule id, unless Accepted.
        private static TagFixer.FixLog ResidualRun(string title, string album, string acceptXml, out string name)
        {
            string tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AudioManagerTest_" + Guid.NewGuid());
            System.IO.Directory.CreateDirectory(tempDir);
            try
            {
                name = "Band - Song.mp3";
                string fixturePath = CreateSilentMp3Fixture(tempDir, name, title, album, "Band");
                string exPath = System.IO.Path.Combine(tempDir, "ex.xml");
                System.IO.File.WriteAllText(exPath, "<Exceptions>" + acceptXml + "</Exceptions>");
                var fixer = TagFixer.ForTesting(dryRun: true, quarantineRoot: System.IO.Path.Combine(tempDir, "q"), exceptionsPath: exPath);
                return fixer.ProcessFile(fixturePath);
            }
            finally { System.IO.Directory.Delete(tempDir, true); }
        }

        public static void ProcessFile_ResidualUnwantedText_IsNeedsManualWithRule()
        {
            var log = ResidualRun("Song", "Greatest Original Hits", "", out _);
            Assert.True(log.NeedsManual, "unwanted text left in album needs a person");
            Assert.Equal("unwanted_text", log.ManualRule, "rule id");
            Assert.Equal("", log.SuggestedTitle, "flagged word is in the album, so no title suggestion");
        }

        public static void ProcessFile_ResidualUnwantedTextInTitle_SuggestsTitleWithoutSuffix()
        {
            var log = ResidualRun("Chammak Challo (Multilingual Version)", "Album", "", out _);
            Assert.True(log.NeedsManual, "unwanted text left in the title needs a person");
            Assert.Equal("unwanted_text", log.ManualRule, "rule id");
            Assert.Equal("Chammak Challo", log.SuggestedTitle, "suggested title drops the trailing group");
        }

        public static void ProcessFile_ResidualUnwantedTextInAlbum_SuggestsAlbumWithoutSuffix()
        {
            // "(... Version)" and "(Original)" are removed by the built-in album cleanup before the residual check,
            // so only a group that survives it (square brackets here) can be a residual album.
            var log = ResidualRun("Song", "Flower In A Hurricane [From GTAVI_ Original Score]", "", out _);
            Assert.True(log.NeedsManual, "unwanted text left in the album needs a person");
            Assert.Equal("unwanted_text", log.ManualRule, "rule id");
            Assert.Equal("Flower In A Hurricane", log.SuggestedAlbum, "suggested album drops the trailing group");
            Assert.Equal("Flower In A Hurricane [From GTAVI_ Original Score]", log.OldAlbum, "old album is the current album tag");
            Assert.Equal("", log.SuggestedTitle, "a clean title gets no title suggestion");
        }

        public static void ProcessFile_ResidualUnwantedTextInAlbum_NoTrailingGroup_NoAlbumSuggestion()
        {
            var log = ResidualRun("Song", "Version Album", "", out _);
            Assert.True(log.NeedsManual, "premise: the album still carries unwanted text");
            Assert.Equal("", log.SuggestedAlbum, "no trailing group, so no album suggestion");
        }

        public static void ProcessFile_ResidualMissingAlbum_IsNotNeedsManual()
        {
            var log = ResidualRun("Song", "", "", out _);
            Assert.True(!log.NeedsManual, "missing album is reported by projectedHits, not tagfix");
        }

        public static void ProcessFile_AcceptedUnwantedText_IsNotNeedsManual()
        {
            var log = ResidualRun("Song", "Greatest Original Hits", "<Accept file=\"Band - Song.mp3\" rule=\"unwanted_text\" />", out _);
            Assert.True(!log.NeedsManual, "accepted unwanted_text is skipped");
            var log2 = ResidualRun("Song", "Greatest Original Hits", "<Accept file=\"Band - Song.mp3\" rule=\"missing_tag\" />", out _);
            Assert.True(log2.NeedsManual, "accepting a different rule leaves unwanted_text");
        }

        // ENG-83 follow-up: several fixtures in one folder through one dry-run fixer, so an Accept row on one file can be checked against its neighbours.
        // Calls ProcessFile per file, the same call ScanAndProcess makes, because ScanFolderForTesting takes no exceptionsPath.
        private static List<TagFixer.FixLog> ResidualRunFolder(string[] names, string[] titles, string[] albums, string acceptXml)
        {
            string tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AudioManagerTest_" + Guid.NewGuid());
            System.IO.Directory.CreateDirectory(tempDir);
            try
            {
                var paths = new List<string>();
                for (int i = 0; i < names.Length; i++)
                    paths.Add(CreateSilentMp3Fixture(tempDir, names[i], titles[i], albums[i], "Band"));
                string exPath = System.IO.Path.Combine(tempDir, "ex.xml");
                System.IO.File.WriteAllText(exPath, "<Exceptions>" + acceptXml + "</Exceptions>");
                var fixer = TagFixer.ForTesting(dryRun: true, quarantineRoot: System.IO.Path.Combine(tempDir, "q"), exceptionsPath: exPath);
                var logs = new List<TagFixer.FixLog>();
                foreach (string path in paths) logs.Add(fixer.ProcessFile(path));
                return logs;
            }
            finally { System.IO.Directory.Delete(tempDir, true); }
        }

        // Every field the dry-run plan reports for one file, as one comparable string.
        private static string ResidualSignature(TagFixer.FixLog log)
        {
            TagFixer.TagState t = log.NewTags;
            string tags = t == null ? "null" : t.Title + "|" + t.Album + "|" + t.Artists + "|" + t.Genre + "|" + t.Compilation + "|" + t.ArtCount;
            return log.Status + "|" + log.Filename + "|" + log.NeedsManual + "|" + log.ManualRule + "|" + log.ManualReason + "|" + tags;
        }

        public static void ProcessFile_AcceptedUnwantedText_LeavesNeighbourRowsUnchanged()
        {
            string[] names = { "Band - Song A.mp3", "Band - Song B.mp3", "Band - Song C.mp3" };
            string[] titles = { "Song A", "Song B", "Song C" };
            string[] albums = { "Greatest Original Hits", "Plain Album One", "Plain Album Two" };
            var withoutAccept = ResidualRunFolder(names, titles, albums, "");
            var withAccept = ResidualRunFolder(names, titles, albums, "<Accept file=\"Band - Song A.mp3\" rule=\"unwanted_text\" />");
            Assert.True(withoutAccept[0].NeedsManual, "without an Accept row the unwanted-text file needs a person");
            Assert.True(!withAccept[0].NeedsManual, "with an Accept row the accepted file no longer needs a person");
            for (int i = 1; i < names.Length; i++)
                Assert.Equal(ResidualSignature(withoutAccept[i]), ResidualSignature(withAccept[i]), "neighbour row " + names[i]);
        }

        // ---- apply_title: an accepted title-suffix strip (Accept row, rule apply_title) ----

        private static readonly string[] ApplyTitleTitles =
        {
            "Chammak Challo (Multilingual Version)",
            "Flower In A Hurricane [From GTAVI_ Original Score]",
            "Girl on LSD (Alternate Version)",
            "The Best Of Everything (Alternate Version _ Extra Verse)",
        };

        private static readonly string[] ApplyTitleStripped =
        {
            "Chammak Challo",
            "Flower In A Hurricane",
            "Girl on LSD",
            "The Best Of Everything",
        };

        /// <summary>Runs fn with the AudioMirror title lookup replaced, so a test never reads the real mirror.</summary>
        internal static T WithMirrorStub<T>(Func<string, string, bool> mirror, Func<T> fn)
        {
            var saved = TagFixer.MirrorHasTitle;
            TagFixer.MirrorHasTitle = mirror;
            try { return fn(); }
            finally { TagFixer.MirrorHasTitle = saved; }
        }

        private static string NewApplyTitleDir()
        {
            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AudioManagerTest_" + Guid.NewGuid());
            System.IO.Directory.CreateDirectory(dir);
            return dir;
        }

        private static string WriteApplyTitleExceptions(string dir, string acceptXml)
        {
            string exPath = System.IO.Path.Combine(dir, "ex.xml");
            System.IO.File.WriteAllText(exPath, "<TagFixExceptions>" + acceptXml + "</TagFixExceptions>");
            return exPath;
        }

        private static TagFixer.FixLog ApplyTitleDryRun(string dir, string title, string acceptXml)
        {
            CreateSilentMp3Fixture(dir, "old.mp3", title, "Album", "Band");
            string exPath = WriteApplyTitleExceptions(dir, acceptXml);
            var fixer = TagFixer.ForTesting(dryRun: true, quarantineRoot: System.IO.Path.Combine(dir, "q"), exceptionsPath: exPath);
            return WithMirrorStub((a, t) => false, () => fixer.ProcessFile(System.IO.Path.Combine(dir, "old.mp3")));
        }

        public static void ApplyTitle_DryRun_WithoutAccept_SuggestsStrippedTitleOnly()
        {
            string dir = NewApplyTitleDir();
            try
            {
                for (int i = 0; i < ApplyTitleTitles.Length; i++)
                {
                    var log = ApplyTitleDryRun(dir, ApplyTitleTitles[i], "");
                    Assert.True(log.NeedsManual, "premise: the title still carries unwanted text: " + ApplyTitleTitles[i]);
                    Assert.Equal(ApplyTitleStripped[i], log.SuggestedTitle, "suggestion for " + ApplyTitleTitles[i]);
                    System.IO.File.Delete(System.IO.Path.Combine(dir, "old.mp3"));
                }
            }
            finally { System.IO.Directory.Delete(dir, true); }
        }

        public static void ApplyTitle_DryRun_WithAccept_PlansStrippedTitleAndRename()
        {
            for (int i = 0; i < ApplyTitleTitles.Length; i++)
            {
                string dir = NewApplyTitleDir();
                try
                {
                    var log = ApplyTitleDryRun(dir, ApplyTitleTitles[i], "<Accept file=\"old.mp3\" rule=\"apply_title\" />");
                    Assert.Equal("would-fix", log.Status, "accepted suggestion is a planned change: " + ApplyTitleTitles[i]);
                    Assert.True(!log.NeedsManual, "accepted suggestion no longer needs a person");
                    Assert.Equal(ApplyTitleStripped[i], log.NewTags.Title, "planned title is the stripped suggestion");
                    Assert.Equal("Band - " + ApplyTitleStripped[i] + ".mp3", log.Filename, "planned file name matches the stripped title");
                }
                finally { System.IO.Directory.Delete(dir, true); }
            }
        }

        public static void ApplyTitle_RealRun_SetsStrippedTitleAndRenamesFile()
        {
            for (int i = 0; i < ApplyTitleTitles.Length; i++)
            {
                string dir = NewApplyTitleDir();
                try
                {
                    string path = CreateSilentMp3Fixture(dir, "old.mp3", ApplyTitleTitles[i], "Album", "Band");
                    string exPath = WriteApplyTitleExceptions(dir, "<Accept file=\"old.mp3\" rule=\"apply_title\" />");
                    var fixer = TagFixer.ForTesting(dryRun: false, quarantineRoot: System.IO.Path.Combine(dir, "q"), exceptionsPath: exPath);
                    var log = WithMirrorStub((a, t) => false, () => fixer.ProcessFile(path));

                    Assert.Equal("fixed", log.Status, "real run applies the accepted title: " + ApplyTitleTitles[i]);
                    string renamed = System.IO.Path.Combine(dir, "Band - " + ApplyTitleStripped[i] + ".mp3");
                    Assert.True(System.IO.File.Exists(renamed), "file renamed to the stripped title");
                    Assert.True(!System.IO.File.Exists(path), "old file name is gone");
                    using (var f = TagLib.File.Create(renamed)) Assert.Equal(ApplyTitleStripped[i], f.Tag.Title, "title tag holds the stripped title");
                }
                finally { System.IO.Directory.Delete(dir, true); }
            }
        }

        // Survives the built-in album cleanup, so it reaches the residual check and the apply_title album strip.
        private const string AlbumResidual = "Flower In A Hurricane [From GTAVI_ Original Score]";

        private static TagFixer.FixLog ApplyAlbumDryRun(string dir, string album, string acceptXml)
        {
            CreateSilentMp3Fixture(dir, "old.mp3", "Girl Song", album, "Band");
            string exPath = WriteApplyTitleExceptions(dir, acceptXml);
            var fixer = TagFixer.ForTesting(dryRun: true, quarantineRoot: System.IO.Path.Combine(dir, "q"), exceptionsPath: exPath);
            return WithMirrorStub((a, t) => false, () => fixer.ProcessFile(System.IO.Path.Combine(dir, "old.mp3")));
        }

        public static void ApplyTitle_AlbumDryRun_WithAccept_PlansStrippedAlbum()
        {
            string dir = NewApplyTitleDir();
            try
            {
                var log = ApplyAlbumDryRun(dir, AlbumResidual, "<Accept file=\"old.mp3\" rule=\"apply_title\" />");
                Assert.Equal("would-fix", log.Status, "accepted album strip is a planned change");
                Assert.True(!log.NeedsManual, "accepted album strip no longer needs a person");
                Assert.Equal("Flower In A Hurricane", log.NewTags.Album, "planned album is the stripped suggestion");
                Assert.Equal("Girl Song", log.NewTags.Title, "title is untouched when only the album is flagged");
                Assert.True(log.Changes.Contains("Album: \"" + AlbumResidual + "\"  -> \"Flower In A Hurricane\" (accepted suggestion)"), "change line for the album");
            }
            finally { System.IO.Directory.Delete(dir, true); }
        }

        public static void ApplyTitle_AlbumDryRun_WithoutAccept_SuggestsAlbumOnly()
        {
            string dir = NewApplyTitleDir();
            try
            {
                var log = ApplyAlbumDryRun(dir, AlbumResidual, "");
                Assert.True(log.NeedsManual, "premise: the album still carries unwanted text");
                Assert.Equal("Flower In A Hurricane", log.SuggestedAlbum, "album suggestion offered without accept");
                Assert.Equal("", log.SuggestedTitle, "no title suggestion for a clean title");
            }
            finally { System.IO.Directory.Delete(dir, true); }
        }

        public static void ApplyTitle_AlbumRealRun_SetsStrippedAlbum()
        {
            string dir = NewApplyTitleDir();
            try
            {
                string path = CreateSilentMp3Fixture(dir, "old.mp3", "Girl Song", AlbumResidual, "Band");
                string exPath = WriteApplyTitleExceptions(dir, "<Accept file=\"old.mp3\" rule=\"apply_title\" />");
                var fixer = TagFixer.ForTesting(dryRun: false, quarantineRoot: System.IO.Path.Combine(dir, "q"), exceptionsPath: exPath);
                var log = WithMirrorStub((a, t) => false, () => fixer.ProcessFile(path));
                Assert.Equal("fixed", log.Status, "real run applies the accepted album strip");
                // The real run also renames the file to "Artist - Title.mp3", so find the one file left in the folder.
                string[] mp3s = System.IO.Directory.GetFiles(dir, "*.mp3");
                Assert.Equal("1", mp3s.Length.ToString(), "one file left in the folder");
                using (var f = TagLib.File.Create(mp3s[0])) Assert.Equal("Flower In A Hurricane", f.Tag.Album, "album tag holds the stripped album");
            }
            finally { System.IO.Directory.Delete(dir, true); }
        }

        public static void ApplyTitle_SiblingHasStrippedTitle_RefusesAndLeavesFileUntouched()
        {
            string dir = NewApplyTitleDir();
            try
            {
                CreateSilentMp3Fixture(dir, "Band - Girl on LSD.mp3", "Girl on LSD", "Album", "Band");
                string path = CreateSilentMp3Fixture(dir, "old.mp3", "Girl on LSD (Alternate Version)", "Album", "Band");
                byte[] before = System.IO.File.ReadAllBytes(path);
                string exPath = WriteApplyTitleExceptions(dir, "<Accept file=\"old.mp3\" rule=\"apply_title\" />");

                var dry = TagFixer.ForTesting(dryRun: true, quarantineRoot: System.IO.Path.Combine(dir, "q"), exceptionsPath: exPath);
                var dryLog = WithMirrorStub((a, t) => false, () => dry.ProcessFile(path));
                Assert.True(dryLog.NeedsManual, "dry run flags the collision");
                Assert.True(dryLog.ManualReason.EndsWith(" (would duplicate)", StringComparison.Ordinal), "dry run reason carries the would-duplicate suffix");
                Assert.Equal("", dryLog.SuggestedTitle, "a colliding suggestion is not offered");

                var real = TagFixer.ForTesting(dryRun: false, quarantineRoot: System.IO.Path.Combine(dir, "q"), exceptionsPath: exPath);
                var realLog = WithMirrorStub((a, t) => false, () => real.ProcessFile(path));
                Assert.True(realLog.NeedsManual, "real run refuses the collision");
                Assert.Equal("would duplicate: Girl on LSD", realLog.ManualReason, "real run reason");
                Assert.True(System.IO.File.Exists(path), "refused file keeps its name");
                Assert.Equal(Convert.ToBase64String(before), Convert.ToBase64String(System.IO.File.ReadAllBytes(path)), "refused file bytes unchanged");
            }
            finally { System.IO.Directory.Delete(dir, true); }
        }

        public static void ApplyTitle_MirrorHasStrippedTitle_Refuses()
        {
            string dir = NewApplyTitleDir();
            try
            {
                string path = CreateSilentMp3Fixture(dir, "old.mp3", "Girl on LSD (Alternate Version)", "Album", "Band");
                string exPath = WriteApplyTitleExceptions(dir, "<Accept file=\"old.mp3\" rule=\"apply_title\" />");
                var fixer = TagFixer.ForTesting(dryRun: true, quarantineRoot: System.IO.Path.Combine(dir, "q"), exceptionsPath: exPath);
                var log = WithMirrorStub((a, t) => a == "Band" && t == "Girl on LSD", () => fixer.ProcessFile(path));
                Assert.True(log.NeedsManual, "a stripped title already in the library mirror is a collision");
                Assert.True(log.ManualReason.EndsWith(" (would duplicate)", StringComparison.Ordinal), "dry run reason carries the would-duplicate suffix");
            }
            finally { System.IO.Directory.Delete(dir, true); }
        }

        public static void ApplyTitle_TwoFilesStripToSameTitle_SecondIsRefused()
        {
            string dir = NewApplyTitleDir();
            try
            {
                string a = CreateSilentMp3Fixture(dir, "a.mp3", "Girl on LSD (Alternate Version)", "Album", "Band");
                string b = CreateSilentMp3Fixture(dir, "b.mp3", "Girl on LSD (Alternate Version)", "Album", "Band");
                string exPath = WriteApplyTitleExceptions(dir,
                    "<Accept file=\"a.mp3\" rule=\"apply_title\" /><Accept file=\"b.mp3\" rule=\"apply_title\" />");
                var fixer = TagFixer.ForTesting(dryRun: true, quarantineRoot: System.IO.Path.Combine(dir, "q"), exceptionsPath: exPath);
                var first = WithMirrorStub((x, y) => false, () => fixer.ProcessFile(a));
                var second = WithMirrorStub((x, y) => false, () => fixer.ProcessFile(b));
                Assert.Equal("would-fix", first.Status, "first file takes the stripped title");
                Assert.True(second.NeedsManual, "second file would land on the same name in this run");
                Assert.True(second.ManualReason.EndsWith(" (would duplicate)", StringComparison.Ordinal), "second reason carries the would-duplicate suffix");
            }
            finally { System.IO.Directory.Delete(dir, true); }
        }

        // BL-58: the public constructor (the real Tag Fix path) scans the real NewMusic inbox, so tests cannot run it.
        // Every constructor must still start from the same default quarantine root, so pin that default here.
        public static void Constructor_DefaultQuarantineRoot_IsConstantsQuarantinePath()
        {
            var fixer = TagFixer.ForTesting(dryRun: false);
            var field = typeof(TagFixer).GetField("quarantineRoot", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Assert.True(field != null, "TagFixer must keep a quarantineRoot field");
            Assert.Equal(Constants.QuarantinePath, (string)field.GetValue(fixer), "default quarantine root must be Constants.QuarantinePath");
        }

        public static void ProcessFile_NonAkiraInstrumental_NeverQuarantined()
        {
            string tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AudioManagerTest_" + Guid.NewGuid());
            System.IO.Directory.CreateDirectory(tempDir);
            try
            {
                string quarantineDir = System.IO.Path.Combine(tempDir, "NewMusic-Quarantine");
                string fixturePath = CreateSilentMp3Fixture(tempDir, "Other Artist - Track (Instrumental).mp3",
                    "Track (Instrumental)", "Album", "Other Artist");

                var fixer = TagFixer.ForTesting(dryRun: false, quarantineRoot: quarantineDir);
                var log = fixer.ProcessFile(fixturePath);

                Assert.True(System.IO.File.Exists(fixturePath), "non-Akira instrumental must stay in place");
                Assert.True(!System.IO.Directory.Exists(quarantineDir) || System.IO.Directory.GetFiles(quarantineDir).Length == 0,
                    "non-Akira instrumental must never be moved to quarantine");
                Assert.True(log.Detail == null || !log.Detail.StartsWith("quarantined", StringComparison.Ordinal), "non-Akira log must not record a quarantine");
            }
            finally
            {
                System.IO.Directory.Delete(tempDir, true);
            }
        }

        public static void ProcessFile_SetsCompilationFlagOnSave()
        {
            string tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AudioManagerTest_" + Guid.NewGuid());
            System.IO.Directory.CreateDirectory(tempDir);
            try
            {
                string fixturePath = CreateSilentMp3Fixture(tempDir, "Clean Artist - Clean Title.mp3",
                    "Clean Title", "Clean Album", "Clean Artist");

                var fixer = TagFixer.ForTesting(dryRun: false);
                var log = fixer.ProcessFile(fixturePath);

                string resultPath = System.IO.Path.Combine(tempDir, log.Filename);
                TagLib.File resultFile = TagLib.File.Create(resultPath);
                var id3 = (TagLib.Id3v2.Tag)resultFile.GetTag(TagLib.TagTypes.Id3v2, true);
                Assert.True(id3.IsCompilation, "TCMP flag should be set to true after ProcessFile writes tags");
            }
            finally
            {
                System.IO.Directory.Delete(tempDir, true);
            }
        }

        // A successful fix must delete its .tagfix.bak backup, so no backup is left next to the MP3.
        public static void ProcessFile_SuccessfulFix_LeavesNoTagfixBackup()
        {
            string tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AudioManagerTest_" + Guid.NewGuid());
            System.IO.Directory.CreateDirectory(tempDir);
            try
            {
                string fixturePath = CreateSilentMp3Fixture(tempDir, "messy filename.mp3",
                    "Song Title (Explicit)", "Album Name", "Primary Artist");

                var fixer = TagFixer.ForTesting(dryRun: false);
                var log = fixer.ProcessFile(fixturePath);

                Assert.Equal("fixed", log.Status, "fixture must take the real write path");
                Assert.True(!System.IO.File.Exists(fixturePath + ".tagfix.bak"), "backup must be deleted after a successful save");
                Assert.Equal("0", System.IO.Directory.GetFiles(tempDir, "*.tagfix.bak").Length.ToString(), "no .tagfix.bak may remain in the folder");
            }
            finally
            {
                System.IO.Directory.Delete(tempDir, true);
            }
        }

        // A stale .tagfix.bak left by an earlier crashed run must not block the fix, and must not survive it.
        public static void ProcessFile_StaleBackupFromCrash_RunSucceedsAndLeavesNone()
        {
            string tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AudioManagerTest_" + Guid.NewGuid());
            System.IO.Directory.CreateDirectory(tempDir);
            try
            {
                string fixturePath = CreateSilentMp3Fixture(tempDir, "messy filename.mp3",
                    "Song Title (Explicit)", "Album Name", "Primary Artist");
                string staleBackup = fixturePath + ".tagfix.bak";
                System.IO.File.WriteAllBytes(staleBackup, System.Text.Encoding.ASCII.GetBytes("stale backup from a crashed run"));

                var fixer = TagFixer.ForTesting(dryRun: false);
                var log = fixer.ProcessFile(fixturePath);

                Assert.Equal("fixed", log.Status, "a stale backup from an earlier run must not block the fix");
                Assert.Equal("0", System.IO.Directory.GetFiles(tempDir, "*.tagfix.bak").Length.ToString(), "stale backup must be replaced and then removed");
            }
            finally
            {
                System.IO.Directory.Delete(tempDir, true);
            }
        }

        // A save that throws after half-writing the file must put the original bytes back, report the
        // exception message as the error, and leave no backup. The seam releases the handle and writes junk
        // first, so the test only passes if RestoreFromBackup actually runs.
        public static void ProcessFile_SaveFails_RestoresOriginalFileAndReportsError()
        {
            string tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AudioManagerTest_" + Guid.NewGuid());
            System.IO.Directory.CreateDirectory(tempDir);
            var originalSaver = TagFixer.Saver;
            try
            {
                string fixturePath = CreateSilentMp3Fixture(tempDir, "messy filename.mp3",
                    "Song Title (Explicit)", "Album Name", "Primary Artist");
                byte[] originalBytes = System.IO.File.ReadAllBytes(fixturePath);
                byte[] junk = System.Text.Encoding.ASCII.GetBytes("half written tag junk");
                TagFixer.Saver = f =>
                {
                    f.Dispose();
                    System.IO.File.WriteAllBytes(fixturePath, junk);
                    throw new System.IO.IOException("simulated save failure");
                };

                var fixer = TagFixer.ForTesting(dryRun: false);
                var log = fixer.ProcessFile(fixturePath);

                Assert.Equal("error", log.Status, "a failed save must report error");
                Assert.True(log.Detail != null && log.Detail.Contains("simulated save failure"), "detail must carry the save exception message");
                Assert.True(System.IO.File.Exists(fixturePath), "original file must still exist at its source path");
                byte[] restoredBytes = System.IO.File.ReadAllBytes(fixturePath);
                Assert.True(restoredBytes.SequenceEqual(originalBytes), "source file must hold its original bytes after the restore");
                Assert.Equal("0", System.IO.Directory.GetFiles(tempDir, "*.tagfix.bak").Length.ToString(), "no .tagfix.bak may remain after a successful restore");
            }
            finally
            {
                TagFixer.Saver = originalSaver;
                System.IO.Directory.Delete(tempDir, true);
            }
        }

        // When the save fails and the restore also fails, the backup is the only intact copy and must stay on disk.
        public static void ProcessFile_SaveAndRestoreFail_KeepsBackupAndReportsRestoreFailed()
        {
            string tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AudioManagerTest_" + Guid.NewGuid());
            System.IO.Directory.CreateDirectory(tempDir);
            var originalSaver = TagFixer.Saver;
            var originalRestorer = TagFixer.Restorer;
            string backupPath = null;
            try
            {
                string fixturePath = CreateSilentMp3Fixture(tempDir, "messy filename.mp3",
                    "Song Title (Explicit)", "Album Name", "Primary Artist");
                backupPath = fixturePath + ".tagfix.bak";
                TagFixer.Saver = f => throw new System.IO.IOException("simulated save failure");
                TagFixer.Restorer = (bak, src) => throw new System.IO.IOException("simulated restore failure");

                var fixer = TagFixer.ForTesting(dryRun: false);
                var log = fixer.ProcessFile(fixturePath);

                Assert.Equal("error", log.Status, "a save that cannot be restored must report error");
                Assert.True(log.Detail != null && log.Detail.StartsWith("restore failed", StringComparison.Ordinal), "detail must start with 'restore failed'");
                Assert.True(System.IO.File.Exists(backupPath), "backup must be kept on disk when the restore fails");
            }
            finally
            {
                TagFixer.Saver = originalSaver;
                TagFixer.Restorer = originalRestorer;
                if (backupPath != null && System.IO.File.Exists(backupPath)) System.IO.File.Delete(backupPath);
                System.IO.Directory.Delete(tempDir, true);
            }
        }
    }
}
