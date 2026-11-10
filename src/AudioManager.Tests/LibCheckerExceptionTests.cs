using AudioManager.Code.Modules;
using System;
using System.Collections.Generic;
using System.IO;

namespace AudioManager
{
    /// <summary>
    /// Tests that LibChecker's IsExceptionToRules mechanism actually suppresses warnings
    /// for whitelisted tracks. Previously untested - exceptions loading was hardcoded to
    /// Constants.LibCheckerExceptionsPath with no injection point.
    /// </summary>
    internal static class LibCheckerExceptionTests
    {
        private static string TempXmlPath() =>
            Path.Combine(Path.GetTempPath(), $"am_except_{Guid.NewGuid():N}.xml");

        // Tag that would be dirty without exceptions: Title contains "feat." pattern
        private static TrackTag FeatTag() =>
            new TrackTag("\\Artists\\Known Artist\\Singles\\Known Artist - Song (feat. Someone).xml",
                "Song (feat. Someone)", "Known Artist", "Test Album", "2020", "1",
                "Hip-Hop", "00:03:00.0000000", "1", "True", "1200", "1200");

        public static void LibChecker_ExceptionWhitelists_UnwantedFeat_IsClean()
        {
            // Without exception: "feat." in title+filename -> dirty
            // With exception matching Title+Artists: -> clean
            string exPath = TempXmlPath();
            try
            {
                File.WriteAllText(exPath, @"<?xml version=""1.0"" encoding=""utf-8""?>
<Exceptions>
  <Exception unwanted=""feat."">
    <Title equals=""Song (feat. Someone)""/>
    <Artists equals=""Known Artist""/>
  </Exception>
</Exceptions>");

                var checker = new LibChecker(new List<TrackTag> { FeatTag() }, exceptionsPath: exPath);
                Assert.True(checker.IsClean,
                    "track whitelisted by exceptions config should be clean despite feat. in title");
            }
            finally { if (File.Exists(exPath)) File.Delete(exPath); }
        }

        private static bool CleanWith(string exceptionRow)
        {
            string exPath = TempXmlPath();
            try
            {
                File.WriteAllText(exPath, "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<Exceptions>\n" + exceptionRow + "\n</Exceptions>");
                return new LibChecker(new List<TrackTag> { FeatTag() }, exceptionsPath: exPath).IsClean;
            }
            finally { if (File.Exists(exPath)) File.Delete(exPath); }
        }

        public static void LibChecker_MalformedExceptionRows_NeverSuppressAnything()
        {
            Assert.True(!CleanWith("<Exception unwanted=\"feat.\"><Title equalz=\"Song (feat. Someone)\"/></Exception>"),
                "a typo attribute (no equals/contains) must not become Contains(empty) and match everything");
            Assert.True(!CleanWith("<Exception unwanted=\"feat.\"><Title equals=\"\"/></Exception>"),
                "an empty equals must not suppress");
            Assert.True(!CleanWith("<Exception unwanted=\"*\"></Exception>"),
                "a wildcard row with no conditions must not match every track");
            Assert.True(!CleanWith("<Exception unwanted=\"feat.\"><Titel equals=\"Song (feat. Someone)\"/></Exception>"),
                "an unknown field name must not make the condition silently true or empty");
            Assert.True(!CleanWith("<Exception><Title equals=\"Song (feat. Someone)\"/></Exception>"),
                "a row with no unwanted attribute must not suppress");
        }

        public static void LibChecker_ExceptionWildcard_SuppressesForMatchingTrack_IsClean()
        {
            // unwanted="*" matches any unwanted string when track conditions match
            string exPath = TempXmlPath();
            try
            {
                File.WriteAllText(exPath, @"<?xml version=""1.0"" encoding=""utf-8""?>
<Exceptions>
  <Exception unwanted=""*"">
    <Artists equals=""Known Artist""/>
    <Title equals=""Song (feat. Someone)""/>
  </Exception>
</Exceptions>");

                var checker = new LibChecker(new List<TrackTag> { FeatTag() }, exceptionsPath: exPath);
                Assert.True(checker.IsClean,
                    "wildcard exception matching on Artists+Title should suppress all warnings for that track");
            }
            finally { if (File.Exists(exPath)) File.Delete(exPath); }
        }

        public static void LibChecker_ExceptionNonMatch_DoeNotSuppressOtherTracks()
        {
            // Exception targets "Different Artist" - should NOT suppress "Known Artist" tag
            string exPath = TempXmlPath();
            try
            {
                File.WriteAllText(exPath, @"<?xml version=""1.0"" encoding=""utf-8""?>
<Exceptions>
  <Exception unwanted=""feat."">
    <Artists equals=""Different Artist""/>
  </Exception>
</Exceptions>");

                var checker = new LibChecker(new List<TrackTag> { FeatTag() }, exceptionsPath: exPath);
                Assert.True(!checker.IsClean,
                    "exception for 'Different Artist' should NOT suppress 'Known Artist' track (still dirty)");
            }
            finally { if (File.Exists(exPath)) File.Delete(exPath); }
        }
    }
}
