using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AudioManager
{
    /// <summary>
    /// Manifest-chosen artist spellings: the object manifest form, the chosen spelling reaching TagFixer tags
    /// (including keep=artist files), the folder following the choice, and a non-matching key being ignored.
    /// </summary>
    internal static class ArtistSpellingChoicesTests
    {
        private const string ObjectJson =
            "{\"tracks\":[{\"filename\":\"Charli xcx - 360.mp3\",\"artist\":\"Charli xcx\",\"title\":\"360\"}]," +
            "\"artistSpellings\":[{\"key\":\"Charli xcx\",\"spelling\":\"Charli XCX\"}]}";

        private static Dictionary<string, string> CharliChoice() =>
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Charli xcx"] = "Charli XCX" };

        public static void Parse_ObjectForm_ReadsTracksAndSpellingChoice()
        {
            var m = IntegrationManifest.Parse(ObjectJson, out string error);
            Assert.True(error == null, "object form parses without error");
            Assert.True(m != null, "object form returns a manifest");
            Assert.Equal("1", m.Entries.Count.ToString(), "one track read from tracks");
            Assert.Equal("Charli XCX", m.ArtistSpellings["Charli xcx"], "chosen spelling read from artistSpellings");
            Assert.Equal("0", m.SpellingWarnings.Count.ToString(), "a valid choice produces no warning");
        }

        public static void Parse_BareArray_HasNoSpellingChoices()
        {
            var m = IntegrationManifest.Parse("[{\"filename\":\"Charli xcx - 360.mp3\",\"artist\":\"Charli xcx\",\"title\":\"360\"}]", out _);
            Assert.Equal("1", m.Entries.Count.ToString(), "bare array still reads its entries");
            Assert.Equal("0", m.ArtistSpellings.Count.ToString(), "bare array carries no spelling choices");
        }

        public static void Parse_MismatchedKey_IsIgnoredWithWarning()
        {
            string json = "{\"tracks\":[{\"filename\":\"a.mp3\",\"artist\":\"A\",\"title\":\"B\"}]," +
                "\"artistSpellings\":[{\"key\":\"Charli xcx\",\"spelling\":\"Someone Else\"}]}";
            var m = IntegrationManifest.Parse(json, out _);
            Assert.Equal("0", m.ArtistSpellings.Count.ToString(), "a spelling whose key does not match is not applied");
            Assert.Equal("1", m.SpellingWarnings.Count.ToString(), "a mismatched spelling is reported as a warning");
        }

        public static void PreviewFields_AppliesChosenSpelling()
        {
            var fixer = TagFixer.ForTesting(dryRun: true, artistSpellingChoices: CharliChoice());
            var preview = fixer.PreviewFields(Path.Combine(Path.GetTempPath(), "AudioManagerTest_choice", "Charli xcx - 360.mp3"),
                "360", "Charli xcx", "Album", "Pop");
            Assert.Equal("Charli XCX", preview.artists, "preview shows the chosen spelling");
        }

        public static void PreviewFields_KeepArtist_IsOverriddenByChosenSpelling()
        {
            string dir = Path.Combine(Path.GetTempPath(), "AudioManagerTest_" + Guid.NewGuid());
            Directory.CreateDirectory(dir);
            try
            {
                string exc = Path.Combine(dir, "tagfix-exceptions.xml");
                File.WriteAllText(exc, "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<TagFixExceptions>\n" +
                    "<Exception file=\"Charli xcx - 360.mp3\" keep=\"artist\" />\n</TagFixExceptions>\n");
                var fixer = TagFixer.ForTesting(dryRun: true, exceptionsPath: exc, artistSpellingChoices: CharliChoice());
                var preview = fixer.PreviewFields(Path.Combine(dir, "Charli xcx - 360.mp3"), "360", "Charli xcx", "Album", "Pop");
                Assert.Equal("Charli XCX", preview.artists, "keep=artist still takes the chosen spelling");
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch (Exception) { /* temp cleanup only */ }
            }
        }

        public static void ProcessFile_WritesChosenSpellingToArtistTag()
        {
            string dir = Path.Combine(Path.GetTempPath(), "AudioManagerTest_" + Guid.NewGuid());
            Directory.CreateDirectory(dir);
            try
            {
                TagFixerTests.CreateSilentMp3Fixture(dir, "Charli xcx - 360.mp3", "360", "Album", "Charli xcx");
                TagFixer.ScanFolderForTesting(dir, dryRun: false, artistSpellingChoices: CharliChoice());
                string[] mp3s = Directory.GetFiles(dir, "*.mp3");
                Assert.Equal("1", mp3s.Length.ToString(), "one mp3 after the real run");
                using (var tf = TagLib.File.Create(mp3s[0]))
                    Assert.Equal("Charli XCX", tf.Tag.FirstPerformer ?? "", "artist tag written with the chosen spelling");
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch (Exception) { /* temp cleanup only */ }
            }
        }

        public static void FolderSpellingFor_ChosenSpellingWinsOverFrequency()
        {
            // The frequency pick (fallback) says "Charli xcx"; the manifest choice must win for the folder.
            Assert.Equal("Charli XCX", MusicIntegrator.FolderSpellingFor("Charli xcx", CharliChoice(), "Charli xcx"),
                "chosen spelling names the folder");
            Assert.Equal("Charli xcx", MusicIntegrator.FolderSpellingFor("Charli xcx", null, "Charli xcx"),
                "without a choice the frequency pick stands");
        }
    }
}
