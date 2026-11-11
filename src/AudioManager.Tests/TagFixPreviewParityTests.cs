using System;
using System.IO;

namespace AudioManager
{
    /// <summary>The dry-run preview of cleaned fields must honour keep= flags and custom rules exactly like the real tag fix.</summary>
    internal static class TagFixPreviewParityTests
    {
        public static void PreviewFields_KeepTitle_LeavesTitleAsRead()
        {
            string dir = Path.Combine(Path.GetTempPath(), "AudioManagerTest_" + Guid.NewGuid());
            Directory.CreateDirectory(dir);
            try
            {
                string ex = Path.Combine(dir, "ex.xml");
                File.WriteAllText(ex, "<TagFixExceptions><Exception file=\"Intro.mp3\" keep=\"title\" /></TagFixExceptions>");
                var fixer = TagFixer.ForTesting(true, exceptionsPath: ex);

                var kept = fixer.PreviewFields(@"C:\NewMusic\Intro.mp3", "Intro (Radio Version)", "Some Artist", "Album (Deluxe)", "Pop");
                var other = fixer.PreviewFields(@"C:\NewMusic\Other.mp3", "Intro (Radio Version)", "Some Artist", "Album (Deluxe)", "Pop");

                Assert.Equal("Intro (Radio Version)", kept.title, "keep=title: the preview must not strip the parenthetical");
                Assert.Equal("Intro", other.title, "without keep the parenthetical is stripped");
            }
            finally { Directory.Delete(dir, true); }
        }

        public static void PreviewFields_CustomRule_IsAppliedToThePreview()
        {
            string dir = Path.Combine(Path.GetTempPath(), "AudioManagerTest_" + Guid.NewGuid());
            Directory.CreateDirectory(dir);
            try
            {
                string rules = Path.Combine(dir, "rules.xml");
                File.WriteAllText(rules, "<TagFixCustomRules><Rule id=\"r1\" field=\"title\" match=\"contains\" value=\"Skit\" action=\"set-value\" replacement=\"Replaced\" /></TagFixCustomRules>");
                var fixer = TagFixer.ForTesting(true, customRulesPath: rules);

                var r = fixer.PreviewFields(@"C:\NewMusic\a.mp3", "Skit Song", "Some Artist", "Missing", "Pop");

                Assert.Equal("Replaced", r.title, "a custom rule changes the previewed title like the real run");
            }
            finally { Directory.Delete(dir, true); }
        }
    }
}
