using AudioManager.Code.Modules;
using System;
using System.IO;

namespace AudioManager
{
    /// <summary>
    /// TrackTag mirror path handling: a path outside the mirror folder must throw, naming the path;
    /// a path inside it must yield the remainder after the marker as RelPath.
    /// </summary>
    internal static class TrackTagPathTests
    {
        public static void TrackTag_PathWithoutMirrorFolderName_ThrowsArgumentExceptionNamingPath()
        {
            string badPath = "C:/elsewhere/Artists/Someone/Singles/song.xml";
            string message = null;
            try
            {
                new TrackTag(badPath);
            }
            catch (ArgumentException ex)
            {
                message = ex.Message;
            }
            Assert.True(message != null, "path without the mirror folder name should throw ArgumentException");
            Assert.True(message.Contains(badPath), "exception message should name the offending path");
        }

        public static void TrackTag_PathInsideMirror_RelPathIsRemainderAfterMarker()
        {
            string root = Path.Combine(Path.GetTempPath(), "am_trackpath_" + Guid.NewGuid().ToString("N"));
            string mirrorDir = Path.Combine(root, Constants.MirrorFolderName, "Artists", "X", "Singles");
            string filePath = Path.Combine(mirrorDir, "song.xml");
            try
            {
                Directory.CreateDirectory(mirrorDir);
                TrackXML.Write(filePath, new TrackTag("Artists/X/Singles/song.xml", "Song", "Artist X", "Album",
                                                      "2020", "1", "Hip-Hop", "00:03:00.0000000", "1", "False", "500", "500"));

                var tag = new TrackTag(filePath);

                Assert.Equal("\\Artists\\X\\Singles\\song.xml", tag.RelPath, "RelPath should be the remainder after AUDIO_MIRROR");
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        }
    }
}
