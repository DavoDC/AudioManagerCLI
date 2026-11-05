using System;
using System.IO;
using System.Xml;
using System.Xml.Linq;

namespace AudioManager.Code.Modules
{
    internal static class TrackXML
    {
        internal static void Read(string path, TrackTag t)
        {
            // IO failures (missing file, locked file) propagate as IO exceptions; only XML problems are XmlException
            XDocument doc;
            try
            {
                doc = XDocument.Load(path);
            }
            catch (XmlException ex)
            {
                throw new XmlException($"Error reading XML: {path}", ex);
            }

            XElement root = doc.Root ?? throw new XmlException($"Missing root element in XML: {path}");
            XElement cover = RequiredElement(root, "AlbumCover", path);
            t.Title          = RequiredElement(root, "Title", path).Value;
            t.Artists        = RequiredElement(root, "Artists", path).Value;
            t.Album          = RequiredElement(root, "Album", path).Value;
            t.Year           = RequiredElement(root, "Year", path).Value;
            t.TrackNumber    = RequiredElement(root, "TrackNumber", path).Value;
            t.Genres         = RequiredElement(root, "Genres", path).Value;
            t.Length         = RequiredElement(root, "Length", path).Value;
            t.AlbumCoverCount = RequiredElement(cover, "Count", path).Value;
            t.CoverWidth     = RequiredElement(cover, "Width", path).Value;
            t.CoverHeight    = RequiredElement(cover, "Height", path).Value;
            t.Compilation    = RequiredElement(root, "Compilation", path).Value;
        }

        private static XElement RequiredElement(XElement parent, string name, string path)
        {
            XElement element = parent.Element(name);
            if (element == null)
            {
                throw new XmlException($"Missing required element '{name}' under '{parent.Name.LocalName}' in XML: {path}");
            }
            return element;
        }

        internal static void Write(string path, TrackTag t)
        {
            string tmpPath = path + ".tmp";
            try
            {
                var doc = new XDocument(
                    new XDeclaration("1.0", "utf-8", null),
                    new XElement("Track",
                        new XElement("Title",       t.Title),
                        new XElement("Artists",     t.Artists),
                        new XElement("Album",       t.Album),
                        new XElement("Year",        t.Year),
                        new XElement("TrackNumber", t.TrackNumber),
                        new XElement("Genres",      t.Genres),
                        new XElement("Length",      t.Length),
                        new XElement("AlbumCover",
                            new XElement("Count",  t.AlbumCoverCount),
                            new XElement("Width",  t.CoverWidth),
                            new XElement("Height", t.CoverHeight)),
                        new XElement("Compilation", t.Compilation)));

                var settings = new XmlWriterSettings
                {
                    Indent = true,
                    NewLineChars = "\n",
                    NewLineHandling = NewLineHandling.Replace,
                    Encoding = new System.Text.UTF8Encoding(true)
                };
                using (var writer = XmlWriter.Create(tmpPath, settings))
                    doc.Save(writer);

                // Atomic promotion: replace existing file or move into place for first write
                if (File.Exists(path))
                    File.Replace(tmpPath, path, null);
                else
                    File.Move(tmpPath, path);
            }
            catch (Exception ex)
            {
                DeleteTempFile(tmpPath);
                if (ex is XmlException)
                {
                    throw new XmlException($"Error writing XML: {path}", ex);
                }
                throw; // IO failures keep their IO-family type
            }
        }

        private static void DeleteTempFile(string tmpPath)
        {
            try
            {
                if (File.Exists(tmpPath)) File.Delete(tmpPath);
            }
            catch (Exception ex)
            {
                Console.WriteLine($" - [!] Could not delete temp file {tmpPath}: {ex.Message}");
            }
        }
    }
}
