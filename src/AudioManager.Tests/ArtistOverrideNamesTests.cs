using System.Collections.Generic;

namespace AudioManager
{
    /// <summary>David-named canonical spellings in config/artist-name-overrides.xml (2026-11-11).</summary>
    internal static class ArtistOverrideNamesTests
    {
        public static void ExtractAndFixArtists_TjHickeyVariant_BecomesTJHickey()
        {
            TagFixer.ResetArtistOverridesCache();
            List<string> result = TagFixer.ExtractAndFixArtists("Some Song", "Tj Hickey");
            Assert.True(result.Count == 1 && result[0] == "TJ Hickey", "Expected [TJ Hickey], got [" + string.Join("|", result) + "]");
        }

        public static void ExtractAndFixArtists_CharliXcxVariant_BecomesCharliXCX()
        {
            TagFixer.ResetArtistOverridesCache();
            List<string> result = TagFixer.ExtractAndFixArtists("Some Song", "Charli xcx");
            Assert.True(result.Count == 1 && result[0] == "Charli XCX", "Expected [Charli XCX], got [" + string.Join("|", result) + "]");
        }
    }
}
