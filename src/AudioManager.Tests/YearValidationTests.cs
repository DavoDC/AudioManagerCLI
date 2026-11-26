using AudioManager.Code.Modules;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace AudioManager
{
    // BL-316: a mistyped Year (201) must never reach the AudioMirror XML. YearRule is the one plausibility
    // rule; these tests pin it, the extraction helper every TrackTag/BatchScanner call site uses, and the
    // LibChecker check that reports a Year that is present but implausible.
    internal static class YearValidationTests
    {
        private static TrackTag TagWithYear(string year)
        {
            return new TrackTag("\\Artists\\Band\\Singles\\Band - Song.xml", "Song", "Band", "Album", year, "1", "Rock",
                "00:03:00.0000000", "1", "True", "1200", "1200");
        }

        private static string RunLibChecker(List<TrackTag> tags, out bool isClean)
        {
            var original = Console.Out;
            var capture = new StringWriter();
            Console.SetOut(capture);
            bool clean;
            try
            {
                clean = new LibChecker(tags).IsClean;
            }
            finally
            {
                Console.SetOut(original);
            }
            isClean = clean;
            return capture.ToString();
        }

        // ---- YearRule.IsValid(string) ----

        public static void YearRule_StringTable_AcceptsOnlyPlausibleFourDigitYears()
        {
            int next = DateTime.Now.Year + 1;
            var cases = new[]
            {
                ("201", false), ("20111", false), ("0", false), ("1899", false), ("1900", true),
                ("2026", true), (next.ToString(CultureInfo.InvariantCulture), true),
                ((next + 1).ToString(CultureInfo.InvariantCulture), false),
                ("abc", false), ("", false), ("Missing", false), (null, false), ("20 2", false),
            };
            foreach (var c in cases)
            {
                Assert.True(YearRule.IsValid(c.Item1) == c.Item2,
                    "YearRule.IsValid(\"" + (c.Item1 ?? "null") + "\") should be " + c.Item2);
            }
        }

        public static void YearRule_IntOverload_MatchesStringRule()
        {
            int next = DateTime.Now.Year + 1;
            Assert.True(!YearRule.IsValid(201), "int 201 is not plausible");
            Assert.True(!YearRule.IsValid(0), "int 0 (no Year tag) is not a valid year");
            Assert.True(!YearRule.IsValid(1899), "int 1899 is below the floor");
            Assert.True(YearRule.IsValid(1900), "int 1900 is the floor");
            Assert.True(YearRule.IsValid(next), "next calendar year is accepted for pre-release tags");
            Assert.True(!YearRule.IsValid(next + 1), "two years ahead is rejected");
        }

        // ---- Extraction helper used at TrackTag, BatchScanner call sites ----

        public static void YearRule_ToStoredYear_CleansImplausibleToMissing()
        {
            Assert.Equal(Constants.MissingTag, YearRule.ToStoredYear(0, "x.mp3"), "0 stays Missing");
            Assert.Equal(Constants.MissingTag, YearRule.ToStoredYear(201, "x.mp3"), "201 becomes Missing");
            Assert.Equal("2020", YearRule.ToStoredYear(2020, "x.mp3"), "a plausible year is stored as text");
        }

        public static void YearRule_OrMissing_KeepsValidAndCleansTheRest()
        {
            Assert.Equal("1995", YearRule.OrMissing("1995"), "valid text kept");
            Assert.Equal(Constants.MissingTag, YearRule.OrMissing("201"), "implausible text becomes Missing");
            Assert.Equal(Constants.MissingTag, YearRule.OrMissing(Constants.MissingTag), "Missing stays Missing");
        }

        // ---- LibChecker reports a present but implausible Year (legacy XML that still holds 201) ----

        public static void LibChecker_ImplausibleYear_IsReportedAsInvalid()
        {
            bool clean;
            string output = RunLibChecker(new List<TrackTag> { TagWithYear("201") }, out clean);
            Assert.True(!clean, "a track with Year 201 must make the library dirty");
            Assert.True(output.Contains("has an invalid Year '201'"), "LibChecker should name the bad Year value");
        }

        public static void LibChecker_PlausibleYear_HasNoInvalidYearHit()
        {
            bool clean;
            string output = RunLibChecker(new List<TrackTag> { TagWithYear("2020") }, out clean);
            Assert.True(!output.Contains("invalid Year"), "a plausible Year must not be reported");
        }
    }
}
