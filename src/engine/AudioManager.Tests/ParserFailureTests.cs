using AudioManager.Code.Modules;
using System;
using System.IO;

namespace AudioManager
{
    /// <summary>
    /// Parser failure reporting, extension and README matching, and deterministic ordering.
    /// Temp mirror folders are named AUDIO_MIRROR so TrackTag accepts their paths.
    /// </summary>
    internal static class ParserFailureTests
    {
        private static string TempBase() =>
            Path.Combine(Path.GetTempPath(), $"am_pf_{Guid.NewGuid():N}");

        private static string TempCache(string baseDir) =>
            Path.Combine(baseDir, "cache.txt");

        private static void WriteGoodXml(string dir, string filename, string title)
        {
            var tag = new TrackTag($"\\{title}.xml", title, "Artist A", "Album", "2020", "1",
                                   "Hip-Hop", "00:03:00.0000000", "1", "True", "500", "500");
            TrackXML.Write(Path.Combine(dir, filename), tag);
        }

        public static void Parser_MissingMirrorFolder_ThrowsDirectoryNotFoundException()
        {
            string baseDir = TempBase();
            try
            {
                Directory.CreateDirectory(baseDir);
                bool threw = false;
                try { new Parser(Path.Combine(baseDir, Constants.MirrorFolderName), TempCache(baseDir)); }
                catch (DirectoryNotFoundException) { threw = true; }
                Assert.True(threw, "a missing mirror folder should throw DirectoryNotFoundException");
            }
            finally
            {
                if (Directory.Exists(baseDir)) Directory.Delete(baseDir, recursive: true);
            }
        }

        public static void Parser_MalformedXml_ThrowsInvalidDataExceptionListingEveryFailedPath()
        {
            string baseDir = TempBase();
            string mirror = Path.Combine(baseDir, Constants.MirrorFolderName);
            try
            {
                Directory.CreateDirectory(mirror);
                WriteGoodXml(mirror, "good.xml", "Good Song");
                File.WriteAllText(Path.Combine(mirror, "bad-a.xml"), "not xml at all");
                File.WriteAllText(Path.Combine(mirror, "bad-b.xml"), "also not xml");

                string message = null;
                try { new Parser(mirror, TempCache(baseDir)); }
                catch (InvalidDataException ex) { message = ex.Message; }

                Assert.True(message != null, "malformed XML should throw InvalidDataException");
                Assert.True(message.Contains("bad-a.xml"), "message should list the first failed path");
                Assert.True(message.Contains("bad-b.xml"), "message should list the second failed path");
                Assert.True(!message.Contains("good.xml"), "message should not list the good file");
            }
            finally
            {
                if (Directory.Exists(baseDir)) Directory.Delete(baseDir, recursive: true);
            }
        }

        public static void Parser_LowercaseReadmeMd_IsSkippedNotRejected()
        {
            string baseDir = TempBase();
            string mirror = Path.Combine(baseDir, Constants.MirrorFolderName);
            try
            {
                Directory.CreateDirectory(mirror);
                WriteGoodXml(mirror, "good.xml", "Good Song");
                File.WriteAllText(Path.Combine(mirror, "readme.md"), "# notes\n");

                var parser = new Parser(mirror, TempCache(baseDir));
                Assert.Equal("1", parser.audioTags.Count.ToString(), "readme.md in any case should be skipped");
            }
            finally
            {
                if (Directory.Exists(baseDir)) Directory.Delete(baseDir, recursive: true);
            }
        }

        public static void Parser_UppercaseExtensionXml_IsParsed()
        {
            string baseDir = TempBase();
            string mirror = Path.Combine(baseDir, Constants.MirrorFolderName);
            try
            {
                Directory.CreateDirectory(mirror);
                WriteGoodXml(mirror, "TRACK.XML", "Upper Song");

                var parser = new Parser(mirror, TempCache(baseDir));
                Assert.Equal("1", parser.audioTags.Count.ToString(), ".XML extension should be treated as XML");
            }
            finally
            {
                if (Directory.Exists(baseDir)) Directory.Delete(baseDir, recursive: true);
            }
        }

        public static void Parser_ResultSortedByRelPathOrdinal()
        {
            string baseDir = TempBase();
            string mirror = Path.Combine(baseDir, Constants.MirrorFolderName);
            try
            {
                Directory.CreateDirectory(mirror);
                WriteGoodXml(mirror, "c.xml", "Charlie");
                WriteGoodXml(mirror, "a.xml", "Alpha");
                WriteGoodXml(mirror, "b.xml", "Bravo");

                var parser = new Parser(mirror, TempCache(baseDir));
                Assert.Equal("3", parser.audioTags.Count.ToString(), "three tracks parsed");
                for (int i = 1; i < parser.audioTags.Count; i++)
                {
                    bool ordered = string.CompareOrdinal(parser.audioTags[i - 1].RelPath, parser.audioTags[i].RelPath) <= 0;
                    Assert.True(ordered, $"RelPath order broken at index {i}");
                }
            }
            finally
            {
                if (Directory.Exists(baseDir)) Directory.Delete(baseDir, recursive: true);
            }
        }
    }
}
