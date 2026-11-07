using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AudioManager
{
    /// <summary>
    /// Tests for the tagfix --dry-run --json-output plan (data/logs/tagfix-plan.json). The pure builder
    /// is tested with synthetic FixLog rows; the folder-level tests run a real dry run against a temp
    /// NewMusic-shaped fixture folder and prove the dry run never moves, renames or rewrites a file.
    /// </summary>
    internal static class TagFixPlanJsonTests
    {
        private static readonly DateTime FixedNow = new DateTime(2026, 11, 7, 9, 30, 0);

        private static TagFixer.TagState Tags(string title, string album, string artists, string genre, bool compilation) =>
            new TagFixer.TagState { Title = title, Album = album, Artists = artists, Genre = genre, Compilation = compilation };

        private static TagFixer.FixLog Pending(string folder, string original, string newName)
        {
            return new TagFixer.FixLog
            {
                OriginalFilename = original,
                Filename = newName,
                SourcePath = Path.Combine(folder, original),
                Status = "would-fix",
                OldTags = Tags("Song Title (Explicit)", "Album", "Primary Artist", "Hip Hop", false),
                NewTags = Tags("Song Title", "Album", "Primary Artist", "Hip Hop", true),
            };
        }

        public static void Build_EmptyFolder_WritesSchemaAndZeroCounts()
        {
            string json = TagFixPlanJson.Build("C:\\NewMusic", new List<TagFixer.ScannedFile>(), new List<TagFixer.FixLog>(), FixedNow);
            Assert.True(json.Contains("\"schemaVersion\": 1"), "schema version present");
            Assert.True(json.Contains("\"generatedAt\": \"2026-11-07T09:30:00\""), "generatedAt is ISO local time");
            Assert.True(json.Contains("\"pending\": 0"), "pending 0");
            Assert.True(json.Contains("\"renames\": 0"), "renames 0");
            Assert.True(json.Contains("\"quarantines\": 0"), "quarantines 0");
            Assert.True(json.Contains("\"needsManual\": 0"), "needsManual count 0");
            Assert.True(json.Contains("\"files\": []"), "empty files array");
            Assert.True(json.Contains("\"needsManual\": []"), "empty needsManual list");
        }

        public static void Build_RenameWithTagChange_ListsOldAndNewValues()
        {
            var logs = new List<TagFixer.FixLog> { Pending("C:\\NewMusic", "messy.mp3", "Primary Artist - Song Title.mp3") };
            string json = TagFixPlanJson.Build("C:\\NewMusic", new List<TagFixer.ScannedFile>(), logs, FixedNow);
            Assert.True(json.Contains("\"pending\": 1"), "one pending file");
            Assert.True(json.Contains("\"renames\": 1"), "one rename");
            Assert.True(json.Contains("\"file\": \"messy.mp3\""), "file is the path relative to NewMusic");
            Assert.True(json.Contains("\"oldName\": \"messy.mp3\""), "old name recorded");
            Assert.True(json.Contains("\"newName\": \"Primary Artist - Song Title.mp3\""), "new name recorded");
            Assert.True(json.Contains("\"title\": \"Song Title (Explicit)\""), "old title recorded");
            Assert.True(json.Contains("\"title\": \"Song Title\""), "new title recorded");
            Assert.True(json.Contains("\"compilation\": true"), "compilation flag recorded");
            Assert.True(json.Contains("\"wouldQuarantine\": false"), "not a quarantine");
        }

        public static void Build_QuarantineRow_CountsQuarantineAndNoRename()
        {
            var logs = new List<TagFixer.FixLog>
            {
                new TagFixer.FixLog
                {
                    OriginalFilename = "Akira The Don - Track (Instrumental).mp3",
                    SourcePath = "C:\\NewMusic\\Akira The Don - Track (Instrumental).mp3",
                    Status = "skipped",
                    Detail = "would quarantine instrumental",
                    WouldQuarantine = true,
                    OldTags = Tags("Track (Instrumental)", "Album", "Akira The Don", "Musivation", false),
                    NewTags = Tags("Track (Instrumental)", "Album", "Akira The Don", "Musivation", false),
                }
            };
            string json = TagFixPlanJson.Build("C:\\NewMusic", new List<TagFixer.ScannedFile>(), logs, FixedNow);
            Assert.True(json.Contains("\"pending\": 1"), "quarantine counts as pending");
            Assert.True(json.Contains("\"quarantines\": 1"), "one quarantine");
            Assert.True(json.Contains("\"renames\": 0"), "a quarantine is not a rename");
            Assert.True(json.Contains("\"wouldQuarantine\": true"), "row flagged wouldQuarantine");
        }

        public static void Build_ErroredFile_IsNeedsManualAndNotPending()
        {
            var logs = new List<TagFixer.FixLog>
            {
                new TagFixer.FixLog
                {
                    OriginalFilename = "broken.mp3",
                    SourcePath = "C:\\NewMusic\\broken.mp3",
                    Status = "error",
                    Detail = "File corrupt",
                }
            };
            string json = TagFixPlanJson.Build("C:\\NewMusic", new List<TagFixer.ScannedFile>(), logs, FixedNow);
            Assert.True(json.Contains("\"pending\": 0"), "an errored file is not pending");
            Assert.True(json.Contains("\"needsManual\": 1"), "errored file needs manual");
            Assert.True(json.Contains("\"file\": \"broken.mp3\""), "needsManual lists the file");
            Assert.True(json.Contains("\"reason\": \"File corrupt\""), "needsManual carries the error detail");
        }

        public static void Build_BlockedRename_IsPendingAndNeedsManual()
        {
            var row = Pending("C:\\NewMusic", "messy.mp3", "messy.mp3");
            row.NeedsManual = true;
            row.ManualReason = "rename target already exists";
            string json = TagFixPlanJson.Build("C:\\NewMusic", new List<TagFixer.ScannedFile>(), new List<TagFixer.FixLog> { row }, FixedNow);
            Assert.True(json.Contains("\"pending\": 1"), "tag change still pending");
            Assert.True(json.Contains("\"renames\": 0"), "blocked rename is not counted as a rename");
            Assert.True(json.Contains("\"needsManual\": 1"), "blocked rename needs manual");
            Assert.True(json.Contains("\"reason\": \"rename target already exists\""), "reason carried");
        }

        public static void Build_CleanFile_IsNotListed()
        {
            var clean = new TagFixer.FixLog
            {
                OriginalFilename = "ok.mp3",
                SourcePath = "C:\\NewMusic\\ok.mp3",
                Status = "skipped",
                Detail = "no fixes needed",
            };
            string json = TagFixPlanJson.Build("C:\\NewMusic", new List<TagFixer.ScannedFile>(), new List<TagFixer.FixLog> { clean }, FixedNow);
            Assert.True(json.Contains("\"files\": []"), "a file with no fixes is not in files");
            Assert.True(json.Contains("\"pending\": 0"), "no fixes means nothing pending");
        }

        public static void Fingerprint_SameContentGivesSameValue()
        {
            var scanned = new List<TagFixer.ScannedFile>
            {
                new TagFixer.ScannedFile { Path = "C:\\NewMusic\\a.mp3", Size = 100, MtimeUtc = FixedNow },
            };
            string a = TagFixPlanJson.Fingerprint("C:\\NewMusic", scanned);
            string b = TagFixPlanJson.Fingerprint("C:\\NewMusic", scanned);
            Assert.Equal(a, b, "same files, sizes and mtimes must fingerprint the same");
            Assert.True(a.Length == 64, "sha256 hex is 64 characters");
        }

        public static void Fingerprint_SizeOrMtimeChangeGivesDifferentValue()
        {
            var baseline = new List<TagFixer.ScannedFile>
            {
                new TagFixer.ScannedFile { Path = "C:\\NewMusic\\a.mp3", Size = 100, MtimeUtc = FixedNow },
            };
            var bigger = new List<TagFixer.ScannedFile>
            {
                new TagFixer.ScannedFile { Path = "C:\\NewMusic\\a.mp3", Size = 101, MtimeUtc = FixedNow },
            };
            var touched = new List<TagFixer.ScannedFile>
            {
                new TagFixer.ScannedFile { Path = "C:\\NewMusic\\a.mp3", Size = 100, MtimeUtc = FixedNow.AddMinutes(1) },
            };
            string original = TagFixPlanJson.Fingerprint("C:\\NewMusic", baseline);
            Assert.True(original != TagFixPlanJson.Fingerprint("C:\\NewMusic", bigger), "size change must change fingerprint");
            Assert.True(original != TagFixPlanJson.Fingerprint("C:\\NewMusic", touched), "mtime change must change fingerprint");
        }

        public static void DryRunFolder_ReportsRenameAndLeavesFileUntouched()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "AudioManagerTest_" + Guid.NewGuid());
            Directory.CreateDirectory(tempDir);
            try
            {
                string fixturePath = TagFixerTests.CreateSilentMp3Fixture(tempDir, "messy filename.mp3",
                    "Song Title (feat. Featured Artist)", "Album Name (Deluxe Edition)", "Primary Artist");
                long sizeBefore = new FileInfo(fixturePath).Length;
                DateTime mtimeBefore = new FileInfo(fixturePath).LastWriteTimeUtc;

                var tf = TagFixer.ScanFolderForTesting(tempDir, dryRun: true);
                string json = TagFixPlanJson.Build(tempDir, tf.Scanned, tf.Results, FixedNow);

                Assert.True(File.Exists(fixturePath), "dry run must not rename the file");
                Assert.True(new FileInfo(fixturePath).Length == sizeBefore, "dry run must not change the file size");
                Assert.True(new FileInfo(fixturePath).LastWriteTimeUtc == mtimeBefore, "dry run must not change the mtime");
                using (TagLib.File still = TagLib.File.Create(fixturePath))
                {
                    Assert.Equal("Song Title (feat. Featured Artist)", still.Tag.Title, "dry run must not rewrite the title");
                }
                Assert.True(json.Contains("\"pending\": 1"), "one pending file in the plan");
                Assert.True(json.Contains("\"renames\": 1"), "one rename in the plan");
                // The filename uses the full artist field, so the extracted featured artist is in the name too.
                Assert.True(json.Contains("\"newName\": \"Primary Artist; Featured Artist - Song Title.mp3\""), "planned new name");
                Assert.True(json.Contains("\"title\": \"Song Title\""), "planned new title");
                Assert.True(json.Contains("\"album\": \"Album Name\""), "planned new album");
                Assert.True(json.Contains("\"artists\": \"Primary Artist; Featured Artist\""), "planned artists include the featured artist");
            }
            finally
            {
                Directory.Delete(tempDir, true);
            }
        }

        public static void DryRunFolder_InstrumentalIsPlannedAsQuarantineNotMoved()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "AudioManagerTest_" + Guid.NewGuid());
            Directory.CreateDirectory(tempDir);
            try
            {
                string fixturePath = TagFixerTests.CreateSilentMp3Fixture(tempDir, "instrumental.mp3",
                    "Track (Instrumental)", "Album", "Akira The Don");

                var tf = TagFixer.ScanFolderForTesting(tempDir, dryRun: true, quarantineRoot: Path.Combine(tempDir, "Quarantine"));
                string json = TagFixPlanJson.Build(tempDir, tf.Scanned, tf.Results, FixedNow);

                Assert.True(File.Exists(fixturePath), "dry run must not move the instrumental");
                Assert.True(!Directory.Exists(Path.Combine(tempDir, "Quarantine")), "dry run must not create the quarantine folder");
                Assert.True(json.Contains("\"quarantines\": 1"), "one planned quarantine");
                Assert.True(json.Contains("\"wouldQuarantine\": true"), "row flagged wouldQuarantine");
            }
            finally
            {
                Directory.Delete(tempDir, true);
            }
        }
    }
}
