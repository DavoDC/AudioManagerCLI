using System;
using System.IO;
using System.Xml;
using System.Xml.Linq;

namespace AudioManager.Code.Modules
{
    internal static class TrackXML
    {
        /// <summary>Which blocks an as-of read found in one XML. False means the file predates that block.</summary>
        internal struct ShapeFlags
        {
            public bool CoverKnown;
            public bool LyricsKnown;
        }

        /// <summary>
        /// Lenient reader for analysis --as-of only (live code keeps the strict Read). Older commits hold XML without
        /// the AlbumCover block (its count sits in a root AlbumCoverCount) or without HasLyrics. Those map to unknown:
        /// cover width and height are "" (the missingData meaning) and HasLyrics is null. Any other absent element reads
        /// as "". Malformed XML still throws. The returned flags say which blocks the file carried.
        /// </summary>
        internal static ShapeFlags ReadLenient(string path, TrackTag t)
        {
            XElement root = LoadDocument(path).Root ?? throw new XmlException($"Missing root element in XML: {path}");
            XElement cover = root.Element("AlbumCover");
            t.Title           = ValueOrEmpty(root, "Title");
            t.Artists         = ValueOrEmpty(root, "Artists");
            t.Album           = ValueOrEmpty(root, "Album");
            t.Year            = ValueOrEmpty(root, "Year");
            t.TrackNumber     = ValueOrEmpty(root, "TrackNumber");
            t.Genres          = ValueOrEmpty(root, "Genres");
            t.Length          = ValueOrEmpty(root, "Length");
            t.Compilation     = ValueOrEmpty(root, "Compilation");
            if (cover != null)
            {
                t.AlbumCoverCount = ValueOrEmpty(cover, "Count");
                t.CoverWidth      = ValueOrEmpty(cover, "Width");
                t.CoverHeight     = ValueOrEmpty(cover, "Height");
            }
            else
            {
                t.AlbumCoverCount = ValueOrEmpty(root, "AlbumCoverCount");
                t.CoverWidth      = "";
                t.CoverHeight     = "";
            }
            XElement hasLyrics = root.Element("HasLyrics");
            t.HasLyrics = hasLyrics == null ? null : hasLyrics.Value;
            return new ShapeFlags { CoverKnown = cover != null, LyricsKnown = hasLyrics != null };
        }

        private static string ValueOrEmpty(XElement parent, string name)
        {
            XElement element = parent.Element(name);
            return element == null ? "" : element.Value;
        }

        private static XDocument LoadDocument(string path)
        {
            // IO failures (missing file, locked file) propagate as IO exceptions; only XML problems are XmlException
            try
            {
                return XDocument.Load(path);
            }
            catch (XmlException ex)
            {
                throw new XmlException($"Error reading XML: {path}", ex);
            }
        }

        internal static void Read(string path, TrackTag t)
        {
            XDocument doc = LoadDocument(path);

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

            // Optional: XML written before HasLyrics existed has no element, so HasLyrics stays null (unknown)
            XElement hasLyrics = root.Element("HasLyrics");
            if (hasLyrics != null) t.HasLyrics = hasLyrics.Value;
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
                var track = new XElement("Track",
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
                    new XElement("Compilation", t.Compilation));
                // HasLyrics is written only when known (null = unknown, element omitted)
                if (t.HasLyrics != null)
                    track.Add(new XElement("HasLyrics", t.HasLyrics));
                var doc = new XDocument(new XDeclaration("1.0", "utf-8", null), track);

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
