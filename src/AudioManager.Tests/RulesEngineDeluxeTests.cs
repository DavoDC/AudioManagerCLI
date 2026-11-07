namespace AudioManager
{
    /// <summary>
    /// Word-boundary and empty-base rules for RulesEngine.IsDeluxeVersionOf.
    /// </summary>
    internal static class RulesEngineDeluxeTests
    {
        public static void Deluxe_LongerTitleSharingPrefix_IsNotDeluxe()
        {
            Assert.True(!RulesEngine.IsDeluxeVersionOf("Abbey Roadshow Deluxe", "Abbey"),
                "'Abbey Roadshow' is a different album, not a deluxe of 'Abbey'");
        }

        public static void Deluxe_ParenthesisedEdition_IsDeluxeOfBase()
        {
            Assert.True(RulesEngine.IsDeluxeVersionOf("Abbey Road (Deluxe Edition)", "Abbey Road"),
                "parenthesised deluxe edition is deluxe of base");
        }

        public static void Deluxe_SpaceSeparatedKeyword_IsDeluxeOfBase()
        {
            Assert.True(RulesEngine.IsDeluxeVersionOf("Abbey Road Deluxe", "Abbey Road"),
                "space-separated deluxe keyword is still deluxe of base");
        }

        public static void Deluxe_BaseTrimsToEmpty_ReturnsFalse()
        {
            Assert.True(!RulesEngine.IsDeluxeVersionOf("- Deluxe", "-"),
                "a base that trims to nothing must not match everything");
        }
    }
}
