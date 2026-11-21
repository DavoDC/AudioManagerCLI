namespace AudioManager
{
    /// <summary>A stale file name (artists added in Mp3tag) must count as a fix on its own (David, 2026-11-21).</summary>
    internal static class FilenameRenameTests
    {
        public static void FilenameNeedsRename_ArtistAddedInTags_True()
        {
            Assert.True(TagFixer.FilenameNeedsRename("Akira The Don - PROUD OF YOU.mp3", "Akira The Don; Fred Rogers", "PROUD OF YOU"), "stale name not flagged");
        }

        public static void FilenameNeedsRename_NameMatches_False()
        {
            Assert.True(!TagFixer.FilenameNeedsRename("Akira The Don; Fred Rogers - PROUD OF YOU.mp3", "Akira The Don; Fred Rogers", "PROUD OF YOU"), "matching name flagged");
        }

        public static void FilenameNeedsRename_EmptyParts_False()
        {
            Assert.True(!TagFixer.FilenameNeedsRename("x.mp3", "", "T"), "empty artist flagged");
        }
    }
}
