using AudioManager.Code.Modules;
using System;
using System.IO;
using System.Xml;

namespace AudioManager
{
    /// <summary>
    /// Tests for the TrackXML round-trip: write-then-read must be lossless.
    /// This verifies the AudioMirror XML format contract between Reflector (writer) and Parser/TrackTag (reader).
    /// </summary>
    internal static class TrackXMLTests
    {
        private static string TempXmlPath() =>
            Path.Combine(Path.GetTempPath(), $"am_xml_{Guid.NewGuid():N}.xml");

        private static TrackTag SampleTag() =>
            new TrackTag("Artists/X/test.xml", "Song Title", "Artist Name",
                         "Album Name", "2020", "3", "Hip-Hop", "00:03:30.0000000",
                         "1", "True", "500", "499");

        private static TrackTag BlankTag() =>
            new TrackTag("", "", "", "", "", "", "", "", "", "", "", "");

        public static void TrackXML_RoundTrip_AllFieldsPreserved()
        {
            string path = TempXmlPath();
            try
            {
                var tag = SampleTag();
                TrackXML.Write(path, tag);          // write
                var loaded = BlankTag();
                TrackXML.Read(path, loaded);        // read back

                Assert.Equal("Song Title",         loaded.Title,           "Title");
                Assert.Equal("Artist Name",        loaded.Artists,         "Artists");
                Assert.Equal("Album Name",         loaded.Album,           "Album");
                Assert.Equal("2020",               loaded.Year,            "Year");
                Assert.Equal("3",                  loaded.TrackNumber,     "TrackNumber");
                Assert.Equal("Hip-Hop",            loaded.Genres,          "Genres");
                Assert.Equal("00:03:30.0000000",   loaded.Length,          "Length");
                Assert.Equal("1",                  loaded.AlbumCoverCount, "AlbumCoverCount");
                Assert.Equal("True",               loaded.Compilation,     "Compilation");
                Assert.Equal("500",                loaded.CoverWidth,      "CoverWidth");
                Assert.Equal("499",                loaded.CoverHeight,     "CoverHeight");
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }

        public static void TrackXML_RoundTrip_SpecialCharsInFields_Preserved()
        {
            // XDocument encodes &, <, > on write and decodes on read - must be lossless
            string path = TempXmlPath();
            try
            {
                var tag = new TrackTag("Artists/X/test.xml", "Track & Roll",
                    "Artist <Name>", "Album \"Quoted\"", "2020", "1", "Pop",
                    "00:03:00.0000000", "1", "True", "500", "500");
                TrackXML.Write(path, tag);
                var loaded = BlankTag();
                TrackXML.Read(path, loaded);

                Assert.Equal("Track & Roll",       loaded.Title,   "ampersand in title preserved");
                Assert.Equal("Artist <Name>",      loaded.Artists, "angle bracket in artists preserved");
                Assert.Equal("Album \"Quoted\"",   loaded.Album,   "quotes in album preserved");
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }

        public static void TrackXML_ReadMissingElement_Throws()
        {
            // Strict schema enforcement: all elements required. Missing elements throw XmlException.
            string path = TempXmlPath();
            try
            {
                File.WriteAllText(path, "<Track><Title>Song A</Title><Artists>Artist A</Artists></Track>");
                var loaded = BlankTag();
                try
                {
                    TrackXML.Read(path, loaded);
                    Assert.True(false, "Expected XmlException for missing AlbumCover element");
                }
                catch (XmlException)
                {
                    // Expected - missing required element throws
                }
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }

        public static void TrackXML_RoundTrip_HasLyricsTrueAndFalse_Preserved()
        {
            // HasLyrics is presence-only ("True"/"False"); both values must survive write then read
            string path = TempXmlPath();
            try
            {
                var withLyrics = SampleTag();
                withLyrics.HasLyrics = "True";
                TrackXML.Write(path, withLyrics);
                var loaded = BlankTag();
                TrackXML.Read(path, loaded);
                Assert.Equal("True", loaded.HasLyrics, "HasLyrics True round trip");

                var noLyrics = SampleTag();
                noLyrics.HasLyrics = "False";
                TrackXML.Write(path, noLyrics);
                loaded = BlankTag();
                TrackXML.Read(path, loaded);
                Assert.Equal("False", loaded.HasLyrics, "HasLyrics False round trip");
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }

        public static void TrackXML_ReadWithoutHasLyrics_SucceedsWithNull()
        {
            // An XML written before HasLyrics existed must stay readable; the field is optional
            string path = TempXmlPath();
            try
            {
                File.WriteAllText(path,
                    "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" +
                    "<Track>\n" +
                    "  <Title>Song A</Title>\n" +
                    "  <Artists>Artist A</Artists>\n" +
                    "  <Album>Album A</Album>\n" +
                    "  <Year>2020</Year>\n" +
                    "  <TrackNumber>1</TrackNumber>\n" +
                    "  <Genres>Pop</Genres>\n" +
                    "  <Length>00:03:00.0000000</Length>\n" +
                    "  <AlbumCover>\n" +
                    "    <Count>1</Count>\n" +
                    "    <Width>500</Width>\n" +
                    "    <Height>500</Height>\n" +
                    "  </AlbumCover>\n" +
                    "  <Compilation>False</Compilation>\n" +
                    "</Track>");
                var loaded = BlankTag();
                TrackXML.Read(path, loaded);
                Assert.Equal("Song A", loaded.Title, "old XML still reads Title");
                Assert.True(loaded.HasLyrics == null, "absent HasLyrics element reads as null (unknown)");
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }

        public static void TrackXML_ReadWithoutDateAddedOrFileBytes_SucceedsWithNull()
        {
            string path = TempXmlPath();
            try
            {
                TrackXML.Write(path, SampleTag());
                var loaded = BlankTag();
                TrackXML.Read(path, loaded);
                Assert.True(loaded.DateAdded == null, "absent DateAdded reads as null");
                Assert.True(loaded.FileBytes == null, "absent FileBytes reads as null");
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }

        public static void TrackXML_Write_OmitsDateAddedAndFileBytesWhenNull()
        {
            string path = TempXmlPath();
            try
            {
                TrackXML.Write(path, SampleTag());
                string text = File.ReadAllText(path);
                Assert.True(!text.Contains("DateAdded"), "no DateAdded element when null");
                Assert.True(!text.Contains("FileBytes"), "no FileBytes element when null");
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }

        public static void TrackXML_RoundTrip_DateAddedAndFileBytes_Preserved()
        {
            string path = TempXmlPath();
            try
            {
                var tag = SampleTag();
                tag.DateAdded = "2026-03-04";
                tag.FileBytes = 7654321L;
                TrackXML.Write(path, tag);
                string text = File.ReadAllText(path);
                Assert.True(text.Contains("<DateAdded>2026-03-04</DateAdded>"), "DateAdded written when set");
                Assert.True(text.Contains("<FileBytes>7654321</FileBytes>"), "FileBytes written when set");
                var loaded = BlankTag();
                TrackXML.Read(path, loaded);
                Assert.Equal("2026-03-04", loaded.DateAdded, "DateAdded round trip");
                Assert.True(loaded.FileBytes == 7654321L, "FileBytes round trip");
                var lenient = BlankTag();
                TrackXML.ReadLenient(path, lenient);
                Assert.Equal("2026-03-04", lenient.DateAdded, "DateAdded lenient read");
                Assert.True(lenient.FileBytes == 7654321L, "FileBytes lenient read");
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }
    }
}
