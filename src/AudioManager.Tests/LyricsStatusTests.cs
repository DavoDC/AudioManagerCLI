using AudioManager.Code.Modules;
using System.Collections.Generic;
using System.IO;

namespace AudioManager
{
    /// <summary>
    /// Tests for the kept lyrics status list (config/lyrics-status.json): loader tolerance and key normalisation.
    /// Coverage counting over the list is tested in StatsJsonTests.
    /// </summary>
    internal static class LyricsStatusTests
    {
        private const string Sample =
            "{\"version\":1,\"tracks\":{" +
            "\"Artists/Artist A/Singles/Song 1.mp3\":{\"status\":\"none_found\",\"checked\":\"2026-11-01\"}," +
            "\"Artists\\\\Artist B\\\\Singles\\\\Song 2.mp3\":{\"status\":\"instrumental\",\"checked\":\"2026-11-02\"}" +
            "}}";

        public static void Parse_ReadsBothStatusesAndNormalisesSlashes()
        {
            IReadOnlyDictionary<string, string> kept = LyricsStatus.Parse(Sample);
            Assert.Equal("2", kept.Count.ToString(), "two kept rows");
            Assert.Equal("none_found", kept[LyricsStatus.NormaliseKey("\\Artists\\Artist A\\Singles\\Song 1.xml")], "none_found row found via mirror xml path");
            Assert.Equal("instrumental", kept[LyricsStatus.NormaliseKey("Artists/Artist B/Singles/Song 2.mp3")], "instrumental row found via forward-slash path");
        }

        public static void Parse_MalformedJson_ReturnsEmptyWithoutThrowing()
        {
            IReadOnlyDictionary<string, string> kept = LyricsStatus.Parse("{\"version\":1,\"tracks\":{");
            Assert.Equal("0", kept.Count.ToString(), "malformed JSON gives an empty list");
        }

        public static void Parse_WrongShape_ReturnsEmptyWithoutThrowing()
        {
            IReadOnlyDictionary<string, string> kept = LyricsStatus.Parse("{\"version\":1,\"tracks\":[1,2]}");
            Assert.Equal("0", kept.Count.ToString(), "tracks that is not an object gives an empty list");
        }

        public static void Parse_SkipsRowsWithUnknownStatus()
        {
            string json = "{\"version\":1,\"tracks\":{" +
                          "\"Artists/A/x.mp3\":{\"status\":\"bogus\",\"checked\":\"2026-11-01\"}," +
                          "\"Artists/A/y.mp3\":{\"status\":\"none_found\",\"checked\":\"2026-11-01\"}}}";
            IReadOnlyDictionary<string, string> kept = LyricsStatus.Parse(json);
            Assert.Equal("1", kept.Count.ToString(), "only the valid status row is kept");
        }

        public static void LoadFile_MissingFile_ReturnsEmpty()
        {
            string path = Path.Combine(Path.GetTempPath(), "no-such-lyrics-status-" + System.Guid.NewGuid().ToString("N") + ".json");
            IReadOnlyDictionary<string, string> kept = LyricsStatus.LoadFile(path);
            Assert.Equal("0", kept.Count.ToString(), "missing file gives an empty list");
        }

        public static void LoadFile_ReadsRealFileContents()
        {
            string dir = Path.Combine(Path.GetTempPath(), "lyrics-status-test-" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                string path = Path.Combine(dir, "lyrics-status.json");
                File.WriteAllText(path, Sample);
                IReadOnlyDictionary<string, string> kept = LyricsStatus.LoadFile(path);
                Assert.Equal("2", kept.Count.ToString(), "file with two rows loads two rows");
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        public static void NormaliseKey_SlashesAndCaseDoNotMatter()
        {
            string a = LyricsStatus.NormaliseKey("\\Artists\\Artist A\\Singles\\Song 1.xml");
            string b = LyricsStatus.NormaliseKey("artists/ARTIST a/singles/song 1.MP3");
            Assert.Equal(a, b, "backslash .xml and forward-slash .mp3 forms share one key");
            Assert.Equal("artists/artist a/singles/song 1.mp3", a, "key is lowercase forward-slash mp3 with no leading slash");
        }
    }
}
