using System;
using System.Collections.Generic;
using System.Linq;

namespace AudioManager
{
    /// <summary>Artists whose tracks carry differing spellings are found by one pure grouping function.</summary>
    internal static class ArtistSpellingCandidatesTests
    {
        public static void Build_CharliXcxSpellings_OneCandidateSuggestsOrdinalFirst()
        {
            var pairs = new List<(string TrackPath, string ArtistTag)>
            {
                ("a1.mp3", "Charli xcx"), ("a2.mp3", "Charli xcx"),
                ("b1.mp3", "Charli XCX"), ("b2.mp3", "Charli XCX"),
            };

            var result = ArtistSpellingCandidates.BuildSpellingCandidates(pairs);

            Assert.Equal("1", result.Count.ToString(), "one artist with two spellings gives one candidate");
            Assert.Equal("Charli XCX", result[0].Suggested, "a tie on count picks the ordinal-first spelling");
            Assert.Equal("Charli XCX", result[0].Spellings[0].Spelling, "spellings sorted by count desc then ordinal");
            Assert.Equal("Charli xcx", result[0].Spellings[1].Spelling, "the other spelling is second");
        }

        public static void Build_SingleSpelling_ReturnsNoCandidate()
        {
            var pairs = new List<(string TrackPath, string ArtistTag)>
            {
                ("a1.mp3", "Charli XCX"), ("a2.mp3", "Charli XCX"),
            };

            var result = ArtistSpellingCandidates.BuildSpellingCandidates(pairs);

            Assert.Equal("0", result.Count.ToString(), "one spelling is not a candidate");
        }

        public static void Build_RegisteredMarkSpelling_GroupsWithPlainName()
        {
            var pairs = new List<(string TrackPath, string ArtistTag)>
            {
                ("a1.mp3", "BNYX®"), ("a2.mp3", "BNYX"),
            };

            var result = ArtistSpellingCandidates.BuildSpellingCandidates(pairs);

            Assert.Equal("1", result.Count.ToString(), "BNYX® and BNYX are one artist after the folder sanitiser");
            Assert.Equal("2", result[0].Spellings.Count.ToString(), "both spellings are listed");
        }

        public static void Build_TrackListsAndCountsAreCorrect()
        {
            var pairs = new List<(string TrackPath, string ArtistTag)>
            {
                ("a1.mp3", "Charli xcx"), ("a2.mp3", "Charli xcx"), ("a3.mp3", "Charli xcx"),
                ("b1.mp3", "Charli XCX"),
            };

            var result = ArtistSpellingCandidates.BuildSpellingCandidates(pairs);
            var lower = result[0].Spellings.Single(s => s.Spelling == "Charli xcx");
            var upper = result[0].Spellings.Single(s => s.Spelling == "Charli XCX");

            Assert.Equal("3", lower.Count.ToString(), "count is the number of tracks with that spelling");
            Assert.Equal("a1.mp3|a2.mp3|a3.mp3", string.Join("|", lower.Tracks), "tracks listed in input order");
            Assert.Equal("1", upper.Count.ToString(), "minority spelling count");
            Assert.Equal("b1.mp3", string.Join("|", upper.Tracks), "minority spelling tracks");
            Assert.Equal("Charli xcx", result[0].Spellings[0].Spelling, "most frequent spelling first");
        }

        public static void Build_EmptyAndNullTags_AreSkipped()
        {
            var pairs = new List<(string TrackPath, string ArtistTag)>
            {
                ("a1.mp3", null), ("a2.mp3", ""), ("a3.mp3", "Charli XCX"), ("a4.mp3", "Charli xcx"),
            };

            var result = ArtistSpellingCandidates.BuildSpellingCandidates(pairs);

            Assert.Equal("1", result.Count.ToString(), "blank tags never form a candidate");
            Assert.Equal("2", result[0].Spellings.Count.ToString(), "only the two real spellings remain");
        }

        public static void PlanJson_ArtistSpellingCandidates_EmitsKeySuggestedAndSpellings()
        {
            var pairs = new List<(string TrackPath, string ArtistTag)>
            {
                ("Charli xcx - Track A.mp3", "Charli xcx"), ("Charli xcx - Track B.mp3", "Charli xcx"),
                ("Charli XCX - Track C.mp3", "Charli XCX"),
            };
            var candidates = ArtistSpellingCandidates.BuildSpellingCandidates(pairs);

            string json = MusicIntegrator.BuildJson(new List<MusicIntegrator.LogEntry>(), artistSpellingCandidates: candidates);

            Assert.True(json.Contains("\"artistSpellingCandidates\": ["), "top-level artistSpellingCandidates array is written");
            Assert.True(json.Contains("\"key\": \"Charli XCX\""), "key is the ordinal-first dedupe key");
            Assert.True(json.Contains("\"suggested\": \"Charli xcx\""), "suggested is the most frequent spelling");
            Assert.True(json.Contains("{\"spelling\": \"Charli xcx\", \"tracks\": [\"Charli xcx - Track A.mp3\", \"Charli xcx - Track B.mp3\"], \"count\": 2}"),
                "each spelling lists its tracks and count");
        }

        public static void PlanJson_NoSpellingCandidates_WritesEmptyArray()
        {
            string json = MusicIntegrator.BuildJson(new List<MusicIntegrator.LogEntry>());

            Assert.True(json.Contains("\"artistSpellingCandidates\": []"), "the array is always present, empty when there are no candidates");
        }
    }
}
