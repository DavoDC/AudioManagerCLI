using AudioManager.Code.Modules;
using System;
using System.IO;

namespace AudioManager
{
    // David 2026-11-27 (decision e): a tagfix Accept row may bypass only the unwanted_text rule. missing_tag, bad_field
    // and bad_cover are never accepted, so an Accept row for them is refused at load and Accepts stays false.
    internal static class WarnRulesETests
    {
        private static TagFixExceptions LoadWith(string acceptRows)
        {
            string dir = Path.Combine(Path.GetTempPath(), "AudioManagerTest_" + Guid.NewGuid());
            Directory.CreateDirectory(dir);
            try
            {
                string path = Path.Combine(dir, "ex.xml");
                File.WriteAllText(path, "<Exceptions>" + acceptRows + "</Exceptions>");
                return TagFixExceptions.Load(path);
            }
            finally { Directory.Delete(dir, true); }
        }

        public static void WarnRulesE_AcceptMissingTag_IsRefused()
        {
            TagFixExceptions set = LoadWith("<Accept file=\"Band - Song.mp3\" rule=\"missing_tag\" />");
            Assert.True(!set.Accepts("Band - Song.mp3", "missing_tag"), "an Accept row cannot bypass missing_tag");
        }

        public static void WarnRulesE_AcceptBadFieldOrBadCover_IsRefused()
        {
            TagFixExceptions set = LoadWith("<Accept file=\"Band - Song.mp3\" rule=\"bad_field\" /><Accept file=\"Band - Song.mp3\" rule=\"bad_cover\" />");
            Assert.True(!set.Accepts("Band - Song.mp3", "bad_field"), "an Accept row cannot bypass bad_field");
            Assert.True(!set.Accepts("Band - Song.mp3", "bad_cover"), "an Accept row cannot bypass bad_cover");
        }

        public static void WarnRulesE_AcceptUnwantedText_StillBypasses()
        {
            TagFixExceptions set = LoadWith("<Accept file=\"Band - Song.mp3\" rule=\"unwanted_text\" />");
            Assert.True(set.Accepts("Band - Song.mp3", "unwanted_text"), "unwanted_text remains acceptable");
        }
    }
}
