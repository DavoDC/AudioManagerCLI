using System.Collections.Generic;

namespace AudioManager
{
    /// <summary>Primary-first rule: a listed artist becomes the folder artist (David, 2026-11-21).</summary>
    internal static class PrimaryFirstTests
    {
        public static void MovePrimaryFirst_YsKanyeTy_PutsKanyeFirst()
        {
            var r = TagFixer.MovePrimaryFirst(new List<string> { "YS", "Kanye West", "Ty Dolla Sign" }, new List<string> { "Kanye West" });
            Assert.True(string.Join("|", r) == "Kanye West|YS|Ty Dolla Sign", "got " + string.Join("|", r));
        }

        public static void MovePrimaryFirst_NotCredited_Unchanged()
        {
            var r = TagFixer.MovePrimaryFirst(new List<string> { "Drake", "Future" }, new List<string> { "Kanye West" });
            Assert.True(string.Join("|", r) == "Drake|Future", "got " + string.Join("|", r));
        }

        public static void MovePrimaryFirst_AlreadyFirst_Unchanged()
        {
            var r = TagFixer.MovePrimaryFirst(new List<string> { "Kanye West", "YS" }, new List<string> { "Kanye West" });
            Assert.True(string.Join("|", r) == "Kanye West|YS", "got " + string.Join("|", r));
        }
    }
}
