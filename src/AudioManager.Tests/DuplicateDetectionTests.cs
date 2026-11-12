using System;
using System.Collections.Generic;
using System.IO;
using AudioManager.Code.Modules;

namespace AudioManager
{
    internal static class DuplicateDetectionTests
    {
        // ---- NormaliseTitleForDedup ----

        public static void NormaliseTitle_NoFeat_Unchanged()
        {
            Assert.Equal("God's Plan", MusicIntegrator.NormaliseTitleForDedup("God's Plan"), "title without feat should be unchanged");
        }

        public static void NormaliseTitle_FeatDot_Stripped()
        {
            Assert.Equal("God's Plan", MusicIntegrator.NormaliseTitleForDedup("God's Plan (feat. Drake)"), "feat. parenthetical should be stripped");
        }

        public static void NormaliseTitle_FtDot_Stripped()
        {
            Assert.Equal("God's Plan", MusicIntegrator.NormaliseTitleForDedup("God's Plan (ft. Drake)"), "ft. parenthetical should be stripped");
        }

        public static void NormaliseTitle_Featuring_Stripped()
        {
            Assert.Equal("God's Plan", MusicIntegrator.NormaliseTitleForDedup("God's Plan (featuring Drake)"), "featuring parenthetical should be stripped");
        }

        public static void NormaliseTitle_Remix_Preserved()
        {
            Assert.Equal("God's Plan (Remix)", MusicIntegrator.NormaliseTitleForDedup("God's Plan (Remix)"), "(Remix) should NOT be stripped - different track");
        }

        public static void NormaliseTitle_CaseInsensitive()
        {
            Assert.Equal("SONG", MusicIntegrator.NormaliseTitleForDedup("SONG (FEAT. ARTIST)"), "feat stripping should be case-insensitive");
        }

        public static void NormaliseTitle_Empty_ReturnsEmpty()
        {
            Assert.Equal("", MusicIntegrator.NormaliseTitleForDedup(""), "empty title should return empty");
        }

        // ---- LoadArtistAliases ----

        public static void LoadArtistAliases_MissingFile_ReturnsEmpty()
        {
            var result = MusicIntegrator.LoadArtistAliases("/nonexistent/path/aliases.xml");
            Assert.Equal("0", result.Count.ToString(), "missing file should return empty dict");
        }

        public static void LoadArtistAliases_ValidFile_ParsesOldToCanonical()
        {
            string tmp = Path.GetTempFileName() + ".xml";
            try
            {
                File.WriteAllText(tmp,
                    "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
                    "<ArtistAliases>" +
                    "<Alias from=\"Kanye West\" to=\"Ye\" />" +
                    "<Alias from=\"Jay Z\" to=\"Jay-Z\" />" +
                    "</ArtistAliases>");
                var result = MusicIntegrator.LoadArtistAliases(tmp);
                Assert.Equal("2", result.Count.ToString(), "should parse 2 aliases");
                Assert.Equal("Ye", result["Kanye West"], "Kanye West -> Ye");
                Assert.Equal("Jay-Z", result["Jay Z"], "Jay Z -> Jay-Z");
            }
            finally { if (File.Exists(tmp)) File.Delete(tmp); }
        }

        public static void LoadArtistAliases_CaseInsensitiveKeys()
        {
            string tmp = Path.GetTempFileName() + ".xml";
            try
            {
                File.WriteAllText(tmp,
                    "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
                    "<ArtistAliases><Alias from=\"kanye west\" to=\"Ye\" /></ArtistAliases>");
                var result = MusicIntegrator.LoadArtistAliases(tmp);
                // Lookup by different casing should work
                Assert.True(result.ContainsKey("Kanye West"), "lookup should be case-insensitive");
            }
            finally { if (File.Exists(tmp)) File.Delete(tmp); }
        }

        // ---- GetAliasExpandedKeys ----

        private static Dictionary<string, string> MakeAliases()
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "Kanye West", "Ye" }
            };
        }

        private static Dictionary<string, List<string>> MakeReverseAliases()
        {
            return new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                { "Ye", new List<string> { "Kanye West" } }
            };
        }

        public static void AliasExpand_NoAlias_YieldsOnlyOriginalKey()
        {
            var keys = new List<string>(MusicIntegrator.GetAliasExpandedKeys(
                "Drake", "God's Plan", MakeAliases(), MakeReverseAliases()));
            Assert.Equal("1", keys.Count.ToString(), "artist with no alias should yield exactly 1 key");
            Assert.Equal("drake\0god's plan", keys[0], "key should be artist NUL title lowercased");
        }

        public static void AliasExpand_OldName_AddsCanonicalKey()
        {
            // Library has "Kanye West" -> index built under "kanye west\0dam"
            // FindDuplicateInMirror will also look up "ye\0dam" -> should find it
            var keys = new List<string>(MusicIntegrator.GetAliasExpandedKeys(
                "Kanye West", "DAM", MakeAliases(), MakeReverseAliases()));
            Assert.Equal("2", keys.Count.ToString(), "old name should yield 2 keys (original + canonical)");
            Assert.True(keys.Contains("kanye west\0dam"), "should contain original key");
            Assert.True(keys.Contains("ye\0dam"), "should contain canonical key");
        }

        public static void AliasExpand_CanonicalName_AddsOldNameKey()
        {
            // Library has "Ye" -> index built under "ye\0dam"
            // If new batch has "Kanye West" -> finds "kanye west\0dam" -> should also hit
            var keys = new List<string>(MusicIntegrator.GetAliasExpandedKeys(
                "Ye", "DAM", MakeAliases(), MakeReverseAliases()));
            Assert.Equal("2", keys.Count.ToString(), "canonical name should yield 2 keys (original + old name)");
            Assert.True(keys.Contains("ye\0dam"), "should contain canonical key");
            Assert.True(keys.Contains("kanye west\0dam"), "should contain old-name key");
        }

        // ---- IsDeluxeVersionOf ----

        public static void IsDeluxe_DeluxeEdition_IsDeluxeOfBase()
        {
            Assert.True(MusicIntegrator.IsDeluxeVersionOf("BULLY - DELUXE", "BULLY"), "BULLY - DELUXE is deluxe of BULLY");
        }

        public static void IsDeluxe_ExpandedEdition_IsDeluxeOfBase()
        {
            Assert.True(MusicIntegrator.IsDeluxeVersionOf("Some Album (Expanded Edition)", "Some Album"), "expanded edition is deluxe of base");
        }

        public static void IsDeluxe_Remastered_IsDeluxeOfBase()
        {
            Assert.True(MusicIntegrator.IsDeluxeVersionOf("The Blueprint (Remastered)", "The Blueprint"), "remastered is deluxe of base");
        }

        public static void IsDeluxe_BaseNotDeluxeOfDeluxe()
        {
            Assert.True(!MusicIntegrator.IsDeluxeVersionOf("BULLY", "BULLY - DELUXE"), "base is NOT deluxe of deluxe edition");
        }

        public static void IsDeluxe_TwoBothDeluxe_ReturnsFalse()
        {
            Assert.True(!MusicIntegrator.IsDeluxeVersionOf("BULLY - DELUXE", "BULLY - SUPER DELUXE"), "two deluxe editions - no clear hierarchy");
        }

        public static void IsDeluxe_UnrelatedAlbums_ReturnsFalse()
        {
            Assert.True(!MusicIntegrator.IsDeluxeVersionOf("Some Album DELUXE", "Other Album"), "unrelated albums are not deluxe versions of each other");
        }

        public static void IsDeluxe_CaseInsensitive()
        {
            Assert.True(MusicIntegrator.IsDeluxeVersionOf("bully - deluxe", "BULLY"), "deluxe detection is case-insensitive");
        }

        public static void IsDeluxe_NullOrEmpty_ReturnsFalse()
        {
            Assert.True(!MusicIntegrator.IsDeluxeVersionOf(null, "BULLY"), "null candidate returns false");
            Assert.True(!MusicIntegrator.IsDeluxeVersionOf("BULLY - DELUXE", null), "null base returns false");
            Assert.True(!MusicIntegrator.IsDeluxeVersionOf("", ""), "empty strings return false");
        }

        // ---- Same song on two albums: larger album preferred (MusicLibraryRules "Same Song On Two Albums Rule") ----

        private const string SizeArtist = "Akira The Don";
        private const string SizeTitle = "Don't Stop Asking Questions";

        /// <summary>
        /// Creates a temp library whose library album folder holds libraryTracks .mp3 files.
        /// The duplicate path is a mirror-style XML inside that folder; BuildDupData derives the
        /// library album folder from it. Caller deletes the root with RoutingFixtures.Cleanup.
        /// </summary>
        private static string SizeFixture(string libraryAlbum, int libraryTracks)
        {
            string root = Path.Combine(Path.GetTempPath(), "AudioManagerTest_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            string albumDir = Path.Combine(root, "Musivation", SizeArtist, "People", "Naval Ravikant", libraryAlbum);
            Directory.CreateDirectory(albumDir);
            for (int i = 0; i < libraryTracks; i++)
                File.WriteAllBytes(Path.Combine(albumDir, "track" + (i + 1) + ".mp3"), new byte[0]);
            return root;
        }

        private static string SizeDupPath(string root, string libraryAlbum) =>
            Path.Combine(root, "Musivation", SizeArtist, "People", "Naval Ravikant", libraryAlbum, SizeArtist + " - " + SizeTitle + ".xml");

        /// <summary>Runs BuildDupData for the incoming track against the duplicate path.</summary>
        private static MusicIntegrator.DupData BuildSizeDup(string root, string libraryAlbum, string incomingAlbum, int incomingBatchTracks)
        {
            var batch = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
            batch[SizeArtist] = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { { incomingAlbum, incomingBatchTracks } };
            var integrator = new MusicIntegrator(root, batch);
            var track = new Track { Artists = SizeArtist, Title = SizeTitle, Album = incomingAlbum, Genres = "Missing" };
            return integrator.BuildDupData("C:\\unused\\new.mp3", track, SizeDupPath(root, libraryAlbum));
        }

        public static void SameSongSize_LargerIncomingAlbum_RecommendsL()
        {
            string root = SizeFixture("ME IS A DISEASE", 3);
            try
            {
                var dup = BuildSizeDup(root, "ME IS A DISEASE", "THE REAL CURRENCY OF LIFE", 5);
                Assert.Equal("L", dup.RecommendedKey.ToString(), "5-track incoming album beats 3-track library album: keep new");
                Assert.True(dup.DupReason != null && dup.DupReason.Contains("larger album preferred"), "reason names the size rule");
                Assert.True(dup.DupReason.Contains("5 tracks") && dup.DupReason.Contains("3 tracks"), "reason states both counts");
            }
            finally
            {
                RoutingFixtures.Cleanup(root);
            }
        }

        public static void SameSongSize_SmallerIncomingAlbum_RecommendsD()
        {
            string root = SizeFixture("THE REAL CURRENCY OF LIFE", 5);
            try
            {
                var dup = BuildSizeDup(root, "THE REAL CURRENCY OF LIFE", "ME IS A DISEASE", 3);
                Assert.Equal("D", dup.RecommendedKey.ToString(), "3-track incoming album loses to 5-track library album: keep library");
            }
            finally
            {
                RoutingFixtures.Cleanup(root);
            }
        }

        public static void SameSongSize_TieRecommendsL()
        {
            string root = SizeFixture("ME IS A DISEASE", 3);
            try
            {
                var dup = BuildSizeDup(root, "ME IS A DISEASE", "SOME OTHER ALBUM", 3);
                Assert.Equal("L", dup.RecommendedKey.ToString(), "equal track counts: incoming copy wins");
            }
            finally
            {
                RoutingFixtures.Cleanup(root);
            }
        }

        public static void SameSongSize_UnknownLibraryCount_NoNewRecommendation()
        {
            string root = SizeFixture("ME IS A DISEASE", 0);
            try
            {
                var dup = BuildSizeDup(root, "ME IS A DISEASE", "THE REAL CURRENCY OF LIFE", 5);
                Assert.Equal("\0", dup.RecommendedKey.ToString(), "library album with no audio entries: size rule must not fire");
            }
            finally
            {
                RoutingFixtures.Cleanup(root);
            }
        }

        public static void SameSongSize_UnknownIncomingCount_NoNewRecommendation()
        {
            string root = SizeFixture("ME IS A DISEASE", 3);
            try
            {
                var dup = BuildSizeDup(root, "ME IS A DISEASE", "THE REAL CURRENCY OF LIFE", 0);
                Assert.Equal("\0", dup.RecommendedKey.ToString(), "incoming album with no known tracks: size rule must not fire");
            }
            finally
            {
                RoutingFixtures.Cleanup(root);
            }
        }

        public static void SameSongSize_SameAlbum_StillRecommendsD()
        {
            string root = SizeFixture("THE REAL CURRENCY OF LIFE", 5);
            try
            {
                var dup = BuildSizeDup(root, "THE REAL CURRENCY OF LIFE", "THE REAL CURRENCY OF LIFE", 5);
                Assert.Equal("D", dup.RecommendedKey.ToString(), "same album is still D, not decided by size");
            }
            finally
            {
                RoutingFixtures.Cleanup(root);
            }
        }

        public static void SameSongSize_DeluxeRuleStillWinsOverSize()
        {
            // Incoming deluxe has FEWER tracks than the library album: size alone would say D.
            string root = SizeFixture("DAMN.", 5);
            try
            {
                var dup = BuildSizeDup(root, "DAMN.", "DAMN. (Deluxe)", 2);
                Assert.Equal("L", dup.RecommendedKey.ToString(), "deluxe rule keeps priority over the size rule");
                Assert.True(dup.DupReason != null && dup.DupReason.Contains("deluxe preferred"), "reason is the deluxe reason, not the size reason");
            }
            finally
            {
                RoutingFixtures.Cleanup(root);
            }
        }
    }
}
