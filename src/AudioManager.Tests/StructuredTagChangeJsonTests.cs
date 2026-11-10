using System;
using System.Collections.Generic;

namespace AudioManager
{
    /// <summary>
    /// GUI-54: every JSON the GUI reads carries structured before/after tag values per track.
    /// Covers the artCount field in the tagfix plan (TagFixPlanJson) and the per-entry tagFix object in
    /// the routing JSON (MusicIntegrator.BuildJson). Pure string assertions, no filesystem.
    /// </summary>
    internal static class StructuredTagChangeJsonTests
    {
        private static readonly DateTime FixedNow = new DateTime(2026, 11, 10, 9, 0, 0);

        private static TagFixer.TagState Tags(string title, int artCount) =>
            new TagFixer.TagState { Title = title, Album = "Album", Artists = "Artist", Genre = "Genre", Compilation = false, ArtCount = artCount };

        public static void TagFixPlan_ArtCount_EmittedInOldAndNewTags()
        {
            var log = new TagFixer.FixLog
            {
                OriginalFilename = "x.mp3",
                Filename = "A.mp3",
                SourcePath = "C:\\NewMusic\\x.mp3",
                Status = "would-fix",
                OldTags = Tags("A (Live)", 1),
                NewTags = Tags("A", 1),
            };
            string json = TagFixPlanJson.Build("C:\\NewMusic", new List<TagFixer.ScannedFile>(), new List<TagFixer.FixLog> { log }, FixedNow);
            Assert.True(json.Contains("\"artCount\": 1"), "artCount emitted in tagfix plan tag objects");
            int count = 0;
            int idx = 0;
            while ((idx = json.IndexOf("\"artCount\": 1", idx, StringComparison.Ordinal)) >= 0) { count++; idx++; }
            Assert.True(count == 2, "artCount appears once in oldTags and once in newTags");
        }

        public static void BuildJson_TagFix_EmitsStructuredOldAndNewTags()
        {
            var entry = new MusicIntegrator.LogEntry
            {
                Filename = "A.mp3",
                Status = "would-move",
                TagFix = new TagFixer.FixLog
                {
                    OriginalFilename = "x.mp3",
                    Filename = "A.mp3",
                    Status = "would-fix",
                    OldTags = Tags("A (Live)", 1),
                    NewTags = Tags("A", 1),
                },
            };
            string json = MusicIntegrator.BuildJson(new List<MusicIntegrator.LogEntry> { entry });
            Assert.True(json.Contains("\"tagFix\": {"), "tagFix object emitted");
            Assert.True(json.Contains("\"oldName\": \"x.mp3\""), "tagFix oldName is the original filename");
            Assert.True(json.Contains("\"newName\": \"A.mp3\""), "tagFix newName is the new filename");
            Assert.True(json.Contains("\"oldTags\""), "tagFix oldTags present");
            Assert.True(json.Contains("\"newTags\""), "tagFix newTags present");
            Assert.True(json.Contains("\"title\": \"A (Live)\""), "old title carried");
            Assert.True(json.Contains("\"title\": \"A\""), "new title carried");
        }

        public static void BuildJson_NoTagFix_EmitsNullTagFix()
        {
            var entry = new MusicIntegrator.LogEntry { Filename = "B.mp3", Status = "would-move" };
            string json = MusicIntegrator.BuildJson(new List<MusicIntegrator.LogEntry> { entry });
            Assert.True(json.Contains("\"tagFix\": null"), "tagFix is null when no tag fix applies");
        }

        public static void ProcessFile_FilenameOnlyRename_IsRegisteredInLogsByFile()
        {
            string tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AudioManagerTest_" + Guid.NewGuid());
            System.IO.Directory.CreateDirectory(tempDir);
            try
            {
                // Clean tags, messy filename: the only change is the rename.
                string path = TagFixerTests.CreateSilentMp3Fixture(tempDir, "messy filename.mp3", "Song Title", "Album Name", "Primary Artist");
                var fixer = TagFixer.ForTesting(dryRun: true);
                var log = fixer.ProcessFile(path);
                Assert.True(fixer.LogsByFile.ContainsKey("messy filename.mp3"), "A filename-only rename must reach LogsByFile (GUI-54)");
                Assert.True(fixer.LogsByFile["messy filename.mp3"].OldTags != null && log.Filename == "Primary Artist - Song Title.mp3", "Rename planned: " + log.Filename);
            }
            finally { System.IO.Directory.Delete(tempDir, true); }
        }
    }
}
