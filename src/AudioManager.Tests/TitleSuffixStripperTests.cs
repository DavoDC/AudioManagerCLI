using System;

namespace AudioManager
{
    /// <summary>
    /// Tests for TagFixer.TitleSuffixStripper.Suggest: the suggested title for a needsManual unwanted_text
    /// row whose flagged word is in the title. Pure function, no file I/O. Only the LAST (...) or [...] group
    /// is considered, and only when it contains the flagged word.
    /// </summary>
    internal static class TitleSuffixStripperTests
    {
        public static void Suggest_MultilingualVersionInParens_StripsGroup()
        {
            Assert.Equal("Chammak Challo", TagFixer.TitleSuffixStripper.Suggest("Chammak Challo (Multilingual Version)", "version"), "parenthesised group removed");
        }

        public static void Suggest_OriginalScoreInSquareBrackets_StripsGroup()
        {
            Assert.Equal("Flower In A Hurricane", TagFixer.TitleSuffixStripper.Suggest("Flower In A Hurricane [From GTAVI_ Original Score]", "original"), "square-bracket group removed");
        }

        public static void Suggest_AlternateVersionInParens_StripsGroup()
        {
            Assert.Equal("Girl on LSD", TagFixer.TitleSuffixStripper.Suggest("Girl on LSD (Alternate Version)", "version"), "alternate version removed");
        }

        public static void Suggest_GroupWithExtraText_StripsWholeGroup()
        {
            Assert.Equal("The Best Of Everything", TagFixer.TitleSuffixStripper.Suggest("The Best Of Everything (Alternate Version _ Extra Verse)", "version"), "whole group removed, not just the word");
        }

        public static void Suggest_FeatGroupWhenFeatNotFlagged_IsEmpty()
        {
            Assert.Equal("", TagFixer.TitleSuffixStripper.Suggest("Song (feat. X)", "version"), "feat group does not contain the flagged word");
        }

        public static void Suggest_LiveGroupWhenLiveNotUnwanted_IsEmpty()
        {
            Assert.Equal("", TagFixer.TitleSuffixStripper.Suggest("Song (Live)", "version"), "live is not the flagged word");
        }

        public static void Suggest_LiveGroupWhenLiveIsFlagged_StripsGroup()
        {
            Assert.Equal("Song", TagFixer.TitleSuffixStripper.Suggest("Song (Live)", "live"), "live flagged so its group goes");
        }

        public static void Suggest_FlaggedWordNotInTrailingGroup_IsEmpty()
        {
            Assert.Equal("", TagFixer.TitleSuffixStripper.Suggest("Version Song", "version"), "no trailing group at all");
        }

        public static void Suggest_LeadingGroupIsNotStripped_IsEmpty()
        {
            Assert.Equal("", TagFixer.TitleSuffixStripper.Suggest("(Alternate Version) Song", "version"), "only trailing groups count");
        }

        public static void Suggest_OnlyLastGroupConsidered_IsEmpty()
        {
            Assert.Equal("", TagFixer.TitleSuffixStripper.Suggest("Song (Alternate Version) (Live)", "version"), "the trailing group is (Live), which lacks the word");
        }

        public static void Suggest_FlaggedWordMatchesCaseInsensitively()
        {
            Assert.Equal("Girl on LSD", TagFixer.TitleSuffixStripper.Suggest("Girl on LSD (ALTERNATE VERSION)", "Version"), "case-insensitive match");
        }

        public static void Suggest_GroupThatWouldLeaveNoTitle_IsEmpty()
        {
            Assert.Equal("", TagFixer.TitleSuffixStripper.Suggest("(Version)", "version"), "an empty suggested title is not a suggestion");
        }

        public static void Suggest_SurroundingWhitespaceTrimmed()
        {
            Assert.Equal("Girl on LSD", TagFixer.TitleSuffixStripper.Suggest("Girl on LSD   (Alternate Version)  ", "version"), "whitespace trimmed");
        }

        public static void Suggest_NullOrEmptyTitle_IsEmpty()
        {
            Assert.Equal("", TagFixer.TitleSuffixStripper.Suggest(null, "version"), "null title");
            Assert.Equal("", TagFixer.TitleSuffixStripper.Suggest("", "version"), "empty title");
        }
    }
}
