using AudioManager.Code.Modules;
using System;
using System.IO;
using System.Xml;

namespace AudioManager
{
    /// <summary>
    /// TrackXML error paths: a missing required element must throw XmlException naming the element and file;
    /// a missing file or unwritable location must throw an IO-family exception; malformed XML stays XmlException.
    /// </summary>
    internal static class TrackXMLErrorTests
    {
        private static string TempXmlPath() =>
            Path.Combine(Path.GetTempPath(), $"am_xmlerr_{Guid.NewGuid():N}.xml");

        private static TrackTag BlankTag() =>
            new TrackTag("", "", "", "", "", "", "", "", "", "", "", "");

        private const string XmlMissingCompilation =
            "<Track><Title>T</Title><Artists>A</Artists><Album>B</Album><Year>2020</Year>" +
            "<TrackNumber>1</TrackNumber><Genres>Pop</Genres><Length>00:03:00</Length>" +
            "<AlbumCover><Count>1</Count><Width>500</Width><Height>500</Height></AlbumCover></Track>";

        public static void TrackXML_ReadMissingElement_MessageNamesElementAndPath()
        {
            string path = TempXmlPath();
            string message = null;
            try
            {
                File.WriteAllText(path, XmlMissingCompilation);
                try
                {
                    TrackXML.Read(path, BlankTag());
                }
                catch (XmlException ex)
                {
                    message = ex.Message;
                }
                Assert.True(message != null, "missing Compilation element should throw XmlException");
                Assert.True(message.Contains("Compilation"), "message should name the missing element");
                Assert.True(message.Contains(path), "message should name the file path");
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }

        public static void TrackXML_ReadMissingFile_ThrowsIOException()
        {
            string path = TempXmlPath();
            bool ioFailure = false;
            string other = null;
            try
            {
                TrackXML.Read(path, BlankTag());
            }
            catch (IOException)
            {
                ioFailure = true;
            }
            catch (Exception ex)
            {
                other = ex.GetType().Name;
            }
            Assert.True(ioFailure, "missing file should throw an IO exception, got " + (other ?? "no exception"));
        }

        public static void TrackXML_WriteToMissingDirectory_ThrowsIOException()
        {
            string dir = Path.Combine(Path.GetTempPath(), "am_xmlerr_nodir_" + Guid.NewGuid().ToString("N"));
            string path = Path.Combine(dir, "track.xml");
            bool ioFailure = false;
            string other = null;
            try
            {
                TrackXML.Write(path, BlankTag());
            }
            catch (IOException)
            {
                ioFailure = true;
            }
            catch (Exception ex)
            {
                other = ex.GetType().Name;
            }
            Assert.True(ioFailure, "write into a missing directory should throw an IO exception, got " + (other ?? "no exception"));
        }

        public static void TrackXML_ReadMalformedXml_StillThrowsXmlException()
        {
            string path = TempXmlPath();
            string other = null;
            bool xmlFailure = false;
            try
            {
                File.WriteAllText(path, "<Track><Title>unclosed");
                try
                {
                    TrackXML.Read(path, BlankTag());
                }
                catch (XmlException)
                {
                    xmlFailure = true;
                }
                catch (Exception ex)
                {
                    other = ex.GetType().Name;
                }
                Assert.True(xmlFailure, "malformed XML should throw XmlException, got " + (other ?? "no exception"));
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }
    }
}
