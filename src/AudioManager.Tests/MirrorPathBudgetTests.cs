namespace AudioManager
{
    internal static class MirrorPathBudgetTests
    {
        // The longest XML path in the mirror is 246 chars. Windows MAX_PATH (259) leaves 255 usable
        // for the staged temp file: xml path + staging suffix + ".tmp" (AtomicFile / TrackXML temp suffix).
        public static void PathBudget_StagingTempPathOf246CharXml_Is254OrLess()
        {
            int xmlLength = 246;
            int total = xmlLength + Reflector.StagingSuffix.Length + ".tmp".Length;
            Assert.True(total <= 254, "staging temp path must stay within the MAX_PATH budget (got " + total + ")");
        }
    }
}
