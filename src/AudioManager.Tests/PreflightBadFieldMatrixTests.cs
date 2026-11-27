using System;
using System.IO;
using AudioManager.Code.Modules;

namespace AudioManager
{
    /// <summary>
    /// One real-mode PreflightMirrorGate case per bad field, plus three cases that must pass (missing Genre, TrackNumber 0,
    /// a 15 second track). A blocked case asserts the gate returns false, prints a [BLOCK] line for that file that names the
    /// field, and writes nothing (same bytes, same timestamp, same file count). Temp roots only; the real library is never touched.
    /// The fixture helper is copied from MusicIntegratorCrossPassTests.cs (which is not edited).
    /// </summary>
    internal static class PreflightBadFieldMatrixTests
    {
        private static string TempRoot()
        {
            return Path.Combine(Path.GetTempPath(), "AmBadField_" + Guid.NewGuid().ToString("N").Substring(0, 8));
        }

        private static IntegrationRoots RootsUnder(string root)
        {
            return new IntegrationRoots(
                Path.Combine(root, "NewMusic"),
                Path.Combine(root, "Audio"),
                Path.Combine(root, "AudioMirror", "AUDIO_MIRROR"),
                Path.Combine(root, "logs"));
        }

        // Runs the real-mode pre-flight with Console.Out captured. Returns the gate result and the printed output.
        private static bool RunRealGate(IntegrationRoots roots, out string output)
        {
            var originalOut = Console.Out;
            var captured = new StringWriter();
            bool clear;
            Console.SetOut(captured);
            try
            {
                var integrator = new MusicIntegrator(roots.Library, roots);
                clear = integrator.PreflightMirrorGate(roots.NewMusic, roots.Mirror);
            }
            finally
            {
                Console.SetOut(originalOut);
            }
            output = captured.ToString();
            return clear;
        }

        // A [BLOCK] line for this file that names the field (case-insensitive), e.g. "[BLOCK] a.mp3: bad_field: Album is missing or blank".
        private static bool BlockLineNamesField(string output, string file, string field)
        {
            foreach (string raw in output.Split('\n'))
            {
                string line = raw.TrimEnd('\r');
                if (!line.StartsWith("[BLOCK] " + file + ":", StringComparison.Ordinal)) continue;
                if (line.IndexOf(field, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        // Shared body for the blocked cases: refused, the [BLOCK] line names the field, and nothing is written.
        private static void AssertBlockedNamingField(string field, Func<string, string> makeFixture)
        {
            string root = TempRoot();
            try
            {
                var roots = RootsUnder(root);
                Directory.CreateDirectory(roots.NewMusic);
                string file = makeFixture(roots.NewMusic);
                string fileName = Path.GetFileName(file);
                DateTime stamp = File.GetLastWriteTimeUtc(file);

                string output;
                bool clear = RunRealGate(roots, out output);

                Assert.True(!clear, field + ": the real pre-flight must refuse the file. Output was: " + output);
                Assert.True(BlockLineNamesField(output, fileName, field),
                    field + ": the [BLOCK] line for " + fileName + " must name the field. Output was: " + output);
                Assert.True(File.Exists(file), field + ": the refused file is not renamed or moved");
                Assert.True(File.GetLastWriteTimeUtc(file) == stamp, field + ": the refused file is not rewritten");
                Assert.True(Directory.GetFiles(roots.NewMusic, "*", SearchOption.AllDirectories).Length == 1,
                    field + ": nothing is added to NewMusic");
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }

        // Shared body for the pass cases: the real pre-flight clears the batch and prints no [BLOCK] line.
        private static void AssertPasses(string label, Func<string, string> makeFixture)
        {
            string root = TempRoot();
            try
            {
                var roots = RootsUnder(root);
                Directory.CreateDirectory(roots.NewMusic);
                makeFixture(roots.NewMusic);

                string output;
                bool clear = RunRealGate(roots, out output);

                Assert.True(clear, label + ": must not block the pre-flight. Output was: " + output);
                Assert.True(output.IndexOf("[BLOCK]", StringComparison.Ordinal) < 0,
                    label + ": no [BLOCK] line may print. Output was: " + output);
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }

        // Adds tag edits on top of the shared silent MP3 fixture (TagFixerTests.CreateSilentMp3Fixture).
        private static string Edited(string dir, string name, string title, string album, string artist, uint year, int frames, Action<TagLib.File> tweak)
        {
            string path = TagFixerTests.CreateSilentMp3Fixture(dir, name, title, album, artist, year, frames);
            if (tweak != null)
            {
                TagLib.File tagFile = TagLib.File.Create(path);
                tweak(tagFile);
                tagFile.Save();
            }
            return path;
        }

        // Case 1: blank Title (the fixture writes an empty title). TagFixer reports empty title (rule empty_title).
        public static void BadField_BlankTitle_BlocksAndNamesTitle()
        {
            AssertBlockedNamingField("Title", dir =>
                TagFixerTests.CreateSilentMp3Fixture(dir, "blank-title.mp3", "", "Album Name", "Primary Artist", 2020));
        }

        // Case 2: blank Artists (an empty performer). TagFixer reports empty artists (rule empty_artists).
        public static void BadField_BlankArtists_BlocksAndNamesArtists()
        {
            AssertBlockedNamingField("Artists", dir =>
                TagFixerTests.CreateSilentMp3Fixture(dir, "blank-artist.mp3", "Song Title", "Album Name", "", 2020));
        }

        // Case 3: blank Album. FieldRules Album rule (bad_field) blocks.
        public static void BadField_BlankAlbum_BlocksAndNamesAlbum()
        {
            AssertBlockedNamingField("Album", dir =>
                TagFixerTests.CreateSilentMp3Fixture(dir, "blank-album.mp3", "Song Title", "", "Primary Artist", 2020));
        }

        // Case 4: missing Year (no Year tag written). TagFixer reports missing_tag "Missing tag: Year".
        public static void BadField_MissingYear_BlocksAndNamesYear()
        {
            AssertBlockedNamingField("Year", dir =>
                TagFixerTests.CreateSilentMp3Fixture(dir, "no-year.mp3", "Song Title", "Album Name", "Primary Artist"));
        }

        // Case 5: invalid Year 201. TagLib holds the value; YearRule stores an implausible year as Missing, so
        // TagFixer reports missing_tag "Missing tag: Year". No fallback to 0 or 99999 was needed.
        public static void BadField_InvalidYear201_BlocksAndNamesYear()
        {
            AssertBlockedNamingField("Year", dir =>
                TagFixerTests.CreateSilentMp3Fixture(dir, "year-201.mp3", "Song Title", "Album Name", "Primary Artist", 201));
        }

        // Case 6: unknown cover dimensions. A JPEG-typed picture whose bytes do not decode: CoverCheck reports
        // "Cover cannot be decoded" (bad_cover) and the cover size reads Unknown.
        public static void BadField_UnknownCoverDimensions_BlocksAndNamesCover()
        {
            AssertBlockedNamingField("Cover", dir =>
                Edited(dir, "bad-cover.mp3", "Song Title", "Album Name", "Primary Artist", 2020, 1000, f =>
                {
                    f.Tag.Pictures = new TagLib.IPicture[]
                    {
                        new TagLib.Picture
                        {
                            MimeType = "image/jpeg",
                            Type = TagLib.PictureType.FrontCover,
                            Data = new TagLib.ByteVector(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 })
                        }
                    };
                }));
        }

        // Case 7: unwanted text in the title. "Edit" is in Constants.UnwantedInfo; the residual check reports unwanted_text.
        public static void BadField_UnwantedTextInTitle_BlocksAndNamesTitle()
        {
            AssertBlockedNamingField("title", dir =>
                TagFixerTests.CreateSilentMp3Fixture(dir, "unwanted.mp3", "Song Title Edit", "Album Name", "Primary Artist", 2020));
        }

        // Case 8: unparseable Length is NOT reachable through the real pre-flight. TagFixer builds Length from TagLib's
        // duration with a fixed hh:mm:ss.fffffff format (TagFixer.FormatLength), so the value always parses. No test is written.

        // PASS: missing Genre. The fixture writes no genre; every other field is set and the batch clears.
        public static void BadField_MissingGenre_DoesNotBlock()
        {
            AssertPasses("missing Genre", dir =>
                TagFixerTests.CreateSilentMp3Fixture(dir, "no-genre.mp3", "Song Title", "Album Name", "Primary Artist", 2020));
        }

        // PASS: TrackNumber 0 (TagLib's unset value for singles). Explicitly set to 0 so the case is not accidental.
        public static void BadField_TrackNumberZero_DoesNotBlock()
        {
            AssertPasses("TrackNumber 0", dir =>
                Edited(dir, "track-zero.mp3", "Song Title", "Album Name", "Primary Artist", 2020, 1000, f => f.Tag.Track = 0));
        }

        // PASS (warn only): a 15 second track. 574 frames of 1152 samples at 44100 Hz is about 14.99 s, under the 20 s floor.
        public static void BadField_FifteenSecondTrack_WarnsOnlyAndDoesNotBlock()
        {
            AssertPasses("15 second track", dir =>
                TagFixerTests.CreateSilentMp3Fixture(dir, "short.mp3", "Song Title", "Album Name", "Primary Artist", 2020, 574));
        }
    }
}
