using AudioManager.Code.Modules;
using System;

namespace AudioManager
{
    // BL-319: FieldRules is the one per-field metadata validator. These tests pin which problems BLOCK a write or a
    // commit and which only WARN. Year is delegated to YearRule (BL-316) and is tested there too.
    internal static class FieldRulesTests
    {
        private static bool Blocks(string field, string value)
        {
            FieldProblem p = FieldRules.Check(field, value);
            return p != null && p.Severity == FieldSeverity.Block;
        }

        private static bool Warns(string field, string value)
        {
            FieldProblem p = FieldRules.Check(field, value);
            return p != null && p.Severity == FieldSeverity.Warn;
        }

        private static bool Passes(string field, string value)
        {
            return FieldRules.Check(field, value) == null;
        }

        public static void FieldRules_BlankOrMissing_BlocksTextFields()
        {
            foreach (string field in new[] { "Title", "Artists", "Album" })
            {
                Assert.True(Blocks(field, ""), field + ": empty blocks");
                Assert.True(Blocks(field, "   "), field + ": spaces only block");
                Assert.True(Blocks(field, "Missing"), field + ": Missing blocks");
                Assert.True(Blocks(field, null), field + ": null blocks");
            }
        }

        public static void FieldRules_WhitespaceControlAndReplacement_BlockTextFields()
        {
            foreach (string field in new[] { "Title", "Artists", "Album" })
            {
                Assert.True(Blocks(field, " Song"), field + ": leading space blocks");
                Assert.True(Blocks(field, "Song "), field + ": trailing space blocks");
                Assert.True(Blocks(field, "Song  Two"), field + ": double space blocks");
                Assert.True(Blocks(field, "Song\u0001"), field + ": control character blocks");
                Assert.True(Blocks(field, "Song\tTwo"), field + ": tab (control character) blocks");
                Assert.True(Blocks(field, "Son�g"), field + ": U+FFFD blocks");
                Assert.True(Passes(field, "Song (Live) - AC/DC"), field + ": clean text passes");
            }
        }

        public static void FieldRules_Genres_BlankOrMissing_Blocks()
        {
            Assert.True(Blocks("Genres", ""), "empty genre blocks");
            Assert.True(Blocks("Genres", "Missing"), "Missing genre blocks");
            Assert.True(Passes("Genres", "Rock; Pop"), "a genre passes");
        }

        public static void FieldRules_TrackNumber_DigitsRequired_ZeroAndAbove99Warn()
        {
            Assert.True(Passes("TrackNumber", "1"), "1 passes");
            Assert.True(Passes("TrackNumber", "99"), "99 passes");
            Assert.True(Warns("TrackNumber", "0"), "0 warns (466 library files are grandfathered)");
            Assert.True(Warns("TrackNumber", "100"), "100 warns");
            Assert.True(Blocks("TrackNumber", ""), "blank blocks");
            Assert.True(Blocks("TrackNumber", "1a"), "non-digit blocks");
            Assert.True(Blocks("TrackNumber", "-1"), "minus sign blocks");
        }

        public static void FieldRules_Length_ShapeAndBounds()
        {
            Assert.True(Passes("Length", "00:03:02.6250000"), "hh:mm:ss.fraction passes");
            Assert.True(Passes("Length", "00:03:02"), "hh:mm:ss without a fraction passes");
            Assert.True(Passes("Length", "00:00:20"), "exactly 20 seconds passes");
            Assert.True(Passes("Length", "00:30:00"), "exactly 30 minutes passes");
            Assert.True(Blocks("Length", "200"), "bare seconds is not hh:mm:ss");
            Assert.True(Blocks("Length", ""), "blank blocks");
            Assert.True(Blocks("Length", "00:00:19.9999999"), "under 20 seconds blocks");
            Assert.True(Warns("Length", "00:30:00.0000001"), "over 30 minutes warns");
            Assert.True(Warns("Length", "01:12:00"), "an hour long warns (Motivation folder is legitimate)");
        }

        public static void FieldRules_Cover_UnknownBlocks_NonSquareWarns()
        {
            Assert.True(Blocks("CoverWidth", "Unknown"), "Unknown width blocks");
            Assert.True(Blocks("CoverHeight", "Unknown"), "Unknown height blocks");
            Assert.True(Passes("CoverWidth", "1200"), "a pixel width passes");
            Assert.True(FieldRules.CheckCoverShape("1200", "1200") == null, "square cover passes");
            FieldProblem nonSquare = FieldRules.CheckCoverShape("1200", "1000");
            Assert.True(nonSquare != null && nonSquare.Severity == FieldSeverity.Warn, "nonsquare cover warns");
            Assert.True(FieldRules.CheckCoverShape("Unknown", "1000") == null, "Unknown is left to the Unknown block rule");
        }

        public static void FieldRules_Year_DelegatesToYearRule()
        {
            Assert.True(Passes("Year", "2020"), "a plausible year passes");
            Assert.True(Passes("Year", "Missing"), "Missing year is allowed, as MirrorGate always allowed it");
            Assert.True(Blocks("Year", "201"), "an implausible year blocks");
            Assert.True(Blocks("Year", ""), "a blank year blocks");
        }

        public static void FieldRules_Problem_NullWhenOkAndReasonWhenNot()
        {
            Assert.True(FieldRules.Problem("Title", "Song") == null, "an ok value gives null");
            string reason = FieldRules.Problem("Title", "Missing");
            Assert.True(!string.IsNullOrEmpty(reason), "a bad value gives a reason");
            Assert.True(FieldRules.Problem("Length", "00:00:05").Contains("20 seconds"), "the Length reason names the bound");
        }

        public static void FieldRules_UnknownField_Throws()
        {
            bool threw = false;
            try
            {
                FieldRules.Check("Titel", "Song");
            }
            catch (ArgumentException)
            {
                threw = true;
            }
            Assert.True(threw, "a misspelt field name throws instead of passing silently");
        }
    }
}
