namespace AudioManager
{
    /// <summary>
    /// Pins the exact console line formats a client parses. If one of these fails, the
    /// client-side parsers must change in the same commit.
    /// </summary>
    internal static class MusicIntegratorConsoleFormatTests
    {
        public static void AutoLine_IsArtistsDashTitleThenAlbumSuffix()
        {
            Assert.Equal("[AUTO] A; B - Song [Album]", MusicIntegrator.FormatAutoLine("A; B", "Song", " [Album]"), "moved line format");
            Assert.Equal("[AUTO] A - Song", MusicIntegrator.FormatAutoLine("A", "Song", ""), "no album suffix");
        }

        public static void SkipLine_IsArtistsDashTitleThenReason()
        {
            Assert.Equal("[SKIP] A - Song: already exists at destination", MusicIntegrator.FormatSkipLine("A", "Song"), "skip line format");
        }

        public static void ExecutionJsonLine_CarriesMarkerAndPath()
        {
            Assert.Equal("\n  EXECUTION JSON: C:/data/logs/execution-20260101-000000.json",
                MusicIntegrator.FormatExecutionJsonLine("C:/data/logs/execution-20260101-000000.json"), "marker line format");
        }
    }
}
