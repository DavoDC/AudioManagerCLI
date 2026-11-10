using System;
using System.IO;
using System.Linq;

namespace AudioManager
{
    /// <summary>
    /// Tests for the per-file exceptions file (config/tagfix-exceptions.xml): keep= flags read by TagFixer,
    /// and NotDuplicate rows read by the integrate library-duplicate check.
    /// </summary>
    internal static class TagFixExceptionsTests
    {
        // ---- helpers ----

        private static string NewTempDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), "AudioManagerTest_" + Guid.NewGuid());
            Directory.CreateDirectory(dir);
            return dir;
        }

        /// <summary>Writes an exceptions file containing the given raw rows inside the root element.</summary>
        private static string WriteExceptionsFile(string directory, string rows)
        {
            string path = Path.Combine(directory, "tagfix-exceptions.xml");
            File.WriteAllText(path,
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<TagFixExceptions>\n" + rows + "\n</TagFixExceptions>\n");
            return path;
        }

        /// <summary>Runs an action with Console.Out captured, returning what it printed.</summary>
        private static string CaptureConsole(Action action)
        {
            var capture = new StringWriter();
            TextWriter original = Console.Out;
            Console.SetOut(capture);
            try { action(); }
            finally { Console.SetOut(original); }
            return capture.ToString();
        }

        // ---- optional sourceRel: scope a row to one file when two share a filename ----

        public static void SourceRel_RowAppliesOnlyToItsOwnPath_OtherFolderUnaffected()
        {
            string dir = NewTempDir();
            try
            {
                string path = WriteExceptionsFile(dir,
                    "<Exception file=\"Intro.mp3\" sourceRel=\"Album A\\Intro.mp3\" keep=\"title\" />\n" +
                    "<NotDuplicate file=\"Intro.mp3\" sourceRel=\"Album A\\Intro.mp3\" />");
                TagFixExceptions set = TagFixExceptions.Load(path);

                Assert.True(set.Keeps("Intro.mp3", "title", @"Album A\Intro.mp3"), "keep applies to its own path");
                Assert.True(!set.Keeps("Intro.mp3", "title", @"Album B\Intro.mp3"), "keep must not hit the same-named file elsewhere");
                Assert.True(!set.Keeps("Intro.mp3", "title"), "a path-scoped row must not match a name-only lookup");
                Assert.True(set.IsNotDuplicate("Intro.mp3", @"album a/Intro.mp3"), "NotDuplicate matches its own path, slash and case tolerant");
                Assert.True(!set.IsNotDuplicate("Intro.mp3", @"Album B\Intro.mp3"), "NotDuplicate must not hit the other folder");
                Assert.True(!set.IsNotDuplicate("Intro.mp3"), "a path-scoped NotDuplicate must not match a name-only lookup");
            }
            finally { Directory.Delete(dir, true); }
        }

        public static void SourceRel_AbsentRow_StillMatchesByFilenameInAnyFolder()
        {
            string dir = NewTempDir();
            try
            {
                string path = WriteExceptionsFile(dir,
                    "<Exception file=\"Intro.mp3\" keep=\"album\" />\n<NotDuplicate file=\"Intro.mp3\" />");
                TagFixExceptions set = TagFixExceptions.Load(path);

                Assert.True(set.Keeps("Intro.mp3", "album", @"Album B\Intro.mp3"), "old row keeps matching by name with a path supplied");
                Assert.True(set.IsNotDuplicate("Intro.mp3", @"Album B\Intro.mp3"), "old NotDuplicate row keeps matching by name");
            }
            finally { Directory.Delete(dir, true); }
        }

        // ---- Load: parsing and fail-safe behaviour ----

        public static void Load_ValidFile_ParsesKeepAndNotDuplicateRows()
        {
            string dir = NewTempDir();
            try
            {
                string path = WriteExceptionsFile(dir,
                    "<Exception file=\"Track One.mp3\" keep=\"title|casing\" />\n" +
                    "<Exception file=\"track one.mp3\" keep=\"album\" />\n" +
                    "<NotDuplicate file=\"Track Two.mp3\" />");

                TagFixExceptions set = TagFixExceptions.Load(path);

                Assert.True(set.Keeps("TRACK ONE.MP3", "title"), "keep=title should apply, matching the file case-insensitively");
                Assert.True(set.Keeps("Track One.mp3", "casing"), "keep=casing should apply");
                Assert.True(set.Keeps("Track One.mp3", "album"), "Multiple Exception rows for one file should combine (album)");
                Assert.True(!set.Keeps("Track One.mp3", "artist"), "A keep token that was not listed must not apply");
                Assert.True(set.IsNotDuplicate("track two.mp3"), "NotDuplicate row should mark the file as not a duplicate");
                Assert.True(!set.IsNotDuplicate("Track One.mp3"), "Exception rows must not imply NotDuplicate");
            }
            finally { Directory.Delete(dir, true); }
        }

        public static void Load_MissingFile_ReturnsNoExceptionsWithoutThrowing()
        {
            string missing = Path.Combine(Path.GetTempPath(), "AudioManagerTest_" + Guid.NewGuid(), "no-such-exceptions.xml");

            TagFixExceptions set = null;
            CaptureConsole(() => set = TagFixExceptions.Load(missing));

            Assert.True(set != null, "Load must return a set even when the file is missing");
            Assert.True(!set.Keeps("any.mp3", "title"), "Missing file should keep nothing");
            Assert.True(!set.IsNotDuplicate("any.mp3"), "Missing file should mark nothing as NotDuplicate");
        }

        public static void Load_MalformedFile_ReturnsNoExceptionsAndWarns()
        {
            string dir = NewTempDir();
            try
            {
                string path = Path.Combine(dir, "tagfix-exceptions.xml");
                File.WriteAllText(path, "<TagFixExceptions><Exception file=\"a.mp3\" keep=\"title\"  <<< not xml");

                TagFixExceptions set = null;
                string output = CaptureConsole(() => set = TagFixExceptions.Load(path));

                Assert.True(set != null, "Malformed exceptions file must not throw from Load");
                Assert.True(!set.Keeps("a.mp3", "title"), "Malformed file should behave as no exceptions");
                Assert.True(!set.IsNotDuplicate("a.mp3"), "Malformed file should behave as no NotDuplicate rows");
                Assert.True(output.IndexOf("[WARN]", StringComparison.Ordinal) >= 0, "Malformed exceptions file should print one [WARN] line");
            }
            finally { Directory.Delete(dir, true); }
        }

        public static void CheckFile_MalformedFile_ReturnsErrorText_MissingAndGoodAreNull()
        {
            string dir = NewTempDir();
            try
            {
                Assert.True(TagFixExceptions.CheckFile(Path.Combine(dir, "absent.xml")) == null, "a missing file is fine (no protections defined)");
                string good = WriteExceptionsFile(dir, "<Exception file=\"a.mp3\" keep=\"title\" />");
                Assert.True(TagFixExceptions.CheckFile(good) == null, "a well-formed file is fine");
                string bad = Path.Combine(dir, "bad.xml");
                File.WriteAllText(bad, "<TagFixExceptions><Exception file=\"a.mp3\" keep=\"title\"  <<< not xml");
                string err = TagFixExceptions.CheckFile(bad);
                Assert.True(err != null && err.Contains("tagfix-exceptions") || err != null && err.Contains("bad.xml"), "a malformed file gives an error naming it: " + err);
            }
            finally { Directory.Delete(dir, true); }
        }

        // ---- TagFixer: keep flags change what is written ----

        public static void TagFixer_KeepTitle_KeepsParentheticalTitleUnchanged()
        {
            string dir = NewTempDir();
            try
            {
                string rulesPath = Path.Combine(dir, "no-custom-rules.xml");
                string exceptionsPath = WriteExceptionsFile(dir, "<Exception file=\"fixture.mp3\" keep=\"title\" />");
                string fixturePath = TagFixerTests.CreateSilentMp3Fixture(dir, "fixture.mp3",
                    "Song Title (Radio Edit)", "Album Name", "Primary Artist");

                var fixer = TagFixer.ForTesting(dryRun: false, customRulesPath: rulesPath, exceptionsPath: exceptionsPath);
                var log = fixer.ProcessFile(fixturePath);

                // ProcessFile renames the fixture to "Artist - Title.mp3", so read the file under its new name.
                TagLib.File resultFile = TagLib.File.Create(Path.Combine(dir, log.Filename));
                Assert.Equal("Song Title (Radio Edit)", resultFile.Tag.Title,
                    "keep=title must leave the title tag exactly as read");
            }
            finally { Directory.Delete(dir, true); }
        }

        public static void TagFixer_KeepTitle_IgnoresCustomRuleOnTitle()
        {
            string dir = NewTempDir();
            try
            {
                string rulesPath = Path.Combine(dir, "tagfix-custom-rules.xml");
                File.WriteAllText(rulesPath,
                    "<TagFixCustomRules><Rule id=\"strip-demo\" field=\"title\" match=\"contains\" value=\"[DEMO]\" " +
                    "action=\"regex-replace\" pattern=\"\\s*\\[DEMO\\]\" replacement=\"\" /></TagFixCustomRules>");
                string exceptionsPath = WriteExceptionsFile(dir, "<Exception file=\"fixture.mp3\" keep=\"title\" />");
                string fixturePath = TagFixerTests.CreateSilentMp3Fixture(dir, "fixture.mp3",
                    "Song Title [DEMO]", "Album Name", "Primary Artist");

                var fixer = TagFixer.ForTesting(dryRun: false, customRulesPath: rulesPath, exceptionsPath: exceptionsPath);
                var log = fixer.ProcessFile(fixturePath);

                // ProcessFile renames the fixture to "Artist - Title.mp3", so read the file under its new name.
                TagLib.File resultFile = TagLib.File.Create(Path.Combine(dir, log.Filename));
                Assert.Equal("Song Title [DEMO]", resultFile.Tag.Title,
                    "keep=title must also stop custom rules from changing the title");
            }
            finally { Directory.Delete(dir, true); }
        }

        public static void TagFixer_WithoutException_FeatIsSplitIntoArtists()
        {
            string dir = NewTempDir();
            try
            {
                string rulesPath = Path.Combine(dir, "no-custom-rules.xml");
                string exceptionsPath = Path.Combine(dir, "no-exceptions.xml");
                string fixturePath = TagFixerTests.CreateSilentMp3Fixture(dir, "fixture.mp3",
                    "Song Title (feat. Guest Star)", "Album Name", "Primary Artist");

                var fixer = TagFixer.ForTesting(dryRun: false, customRulesPath: rulesPath, exceptionsPath: exceptionsPath);
                var log = fixer.ProcessFile(fixturePath);

                // ProcessFile renames the fixture to "Artist - Title.mp3", so read the file under its new name.
                TagLib.File resultFile = TagLib.File.Create(Path.Combine(dir, log.Filename));
                Assert.True(resultFile.Tag.JoinedPerformers.IndexOf("Guest Star", StringComparison.Ordinal) >= 0,
                    "Control: without keep=artist the feat. artist is split into the artists tag");
            }
            finally { Directory.Delete(dir, true); }
        }

        public static void TagFixer_KeepArtist_NoFeatSplit()
        {
            string dir = NewTempDir();
            try
            {
                string rulesPath = Path.Combine(dir, "no-custom-rules.xml");
                string exceptionsPath = WriteExceptionsFile(dir, "<Exception file=\"fixture.mp3\" keep=\"artist\" />");
                string fixturePath = TagFixerTests.CreateSilentMp3Fixture(dir, "fixture.mp3",
                    "Song Title (feat. Guest Star)", "Album Name", "Primary Artist");

                var fixer = TagFixer.ForTesting(dryRun: false, customRulesPath: rulesPath, exceptionsPath: exceptionsPath);
                var log = fixer.ProcessFile(fixturePath);

                // ProcessFile renames the fixture to "Artist - Title.mp3", so read the file under its new name.
                TagLib.File resultFile = TagLib.File.Create(Path.Combine(dir, log.Filename));
                Assert.Equal("Primary Artist", resultFile.Tag.JoinedPerformers,
                    "keep=artist must keep the artists tag as read, with no feat. split");
            }
            finally { Directory.Delete(dir, true); }
        }

        public static void TagFixer_KeepAlbum_KeepsAlbumTagUnchanged()
        {
            string dir = NewTempDir();
            try
            {
                string rulesPath = Path.Combine(dir, "no-custom-rules.xml");
                string exceptionsPath = WriteExceptionsFile(dir, "<Exception file=\"fixture.mp3\" keep=\"album\" />");
                string fixturePath = TagFixerTests.CreateSilentMp3Fixture(dir, "fixture.mp3",
                    "Song Title", "Album Name (Deluxe Edition)", "Primary Artist");

                var fixer = TagFixer.ForTesting(dryRun: false, customRulesPath: rulesPath, exceptionsPath: exceptionsPath);
                var log = fixer.ProcessFile(fixturePath);

                // ProcessFile renames the fixture to "Artist - Title.mp3", so read the file under its new name.
                TagLib.File resultFile = TagLib.File.Create(Path.Combine(dir, log.Filename));
                Assert.Equal("Album Name (Deluxe Edition)", resultFile.Tag.Album,
                    "keep=album must keep the album suffix that built-in cleanup would strip");
            }
            finally { Directory.Delete(dir, true); }
        }

        // ---- Built-in casing helper: keep=casing ----

        public static void ExtractAndFixArtists_KeepCasing_SkipsOverrideRecasing()
        {
            // config/artist-name-overrides.xml maps "blink-182" canonical; a source tag in upper case is recased without the keep.
            var recased = TagFixer.ExtractAndFixArtists("Song", "BLINK-182", applyCasing: true);
            var kept = TagFixer.ExtractAndFixArtists("Song", "BLINK-182", applyCasing: false);

            Assert.Equal("blink-182", recased[0], "Control: override casing applies by default");
            Assert.Equal("BLINK-182", kept[0], "keep=casing must leave the artist casing as read");
        }

        // ---- NotDuplicate: skip the library-duplicate check ----

        public static void NotDuplicate_RowSkipsDuplicateFlag_ForThatFileOnly()
        {
            string dir = NewTempDir();
            try
            {
                string path = WriteExceptionsFile(dir, "<NotDuplicate file=\"Keep Me.mp3\" />");

                TagFixExceptions set = TagFixExceptions.Load(path);

                Assert.True(set.IsNotDuplicate("Keep Me.mp3"), "NotDuplicate file must skip the duplicate check");
                Assert.True(!set.IsNotDuplicate("Other.mp3"), "Other files must still be duplicate-checked");
            }
            finally { Directory.Delete(dir, true); }
        }
    }
}
