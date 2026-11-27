using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Xml.Linq;

namespace AudioManager.Code.Modules
{
    /// <summary>
    /// In-memory hand-off of each track's first-seen DateAdded from Reflector to the Parser. Reflector replaces an XML
    /// with a path-only stub (regen: a fresh staging folder, incremental: a refreshed track), so the old XML's DateAdded
    /// is read first and recorded here, keyed by the XML path relative to the mirror root. TrackTag consults it when it
    /// rebuilds the XML from the stub. No file is written; a process that never ran Reflector simply finds nothing.
    /// </summary>
    internal static class FirstSeenDates
    {
        private static readonly object Gate = new object();
        private static string root = "";
        private static Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Today as the stored form, yyyy-MM-dd.</summary>
        internal static string Today() => DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        /// <summary>Forget everything and set the mirror root the keys are relative to (called when a Reflector build starts).</summary>
        internal static void Reset(string mirrorRoot)
        {
            lock (Gate)
            {
                root = Normalise(mirrorRoot);
                map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
        }

        /// <summary>Reads DateAdded from an existing XML (null when the file, the element or a valid value is missing) and records it.</summary>
        internal static void RecordFrom(string oldXmlPath, string relativeKey)
        {
            string date = ReadDateAdded(oldXmlPath);
            if (date == null) return;
            lock (Gate) map[relativeKey] = date;
        }

        /// <summary>The recorded DateAdded for a mirror XML path, or null when none was recorded.</summary>
        internal static string Lookup(string mirrorFilePath)
        {
            lock (Gate)
            {
                if (map.Count == 0 || root.Length == 0) return null;
                string full = Normalise(mirrorFilePath);
                if (!full.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase)) return null;
                return map.TryGetValue(full.Substring(root.Length + 1), out string date) ? date : null;
            }
        }

        /// <summary>Key for an XML path under a mirror root, or null when it is not under it.</summary>
        internal static string KeyFor(string mirrorRoot, string xmlPath)
        {
            string r = Normalise(mirrorRoot);
            string full = Normalise(xmlPath);
            return full.StartsWith(r + "\\", StringComparison.OrdinalIgnoreCase) ? full.Substring(r.Length + 1) : null;
        }

        private static string Normalise(string path) =>
            Path.GetFullPath(path).TrimEnd('\\', '/').Replace('/', '\\');

        private static string ReadDateAdded(string xmlPath)
        {
            try
            {
                if (!File.Exists(xmlPath)) return null;
                XElement el = XDocument.Load(xmlPath).Root?.Element("DateAdded");
                string v = el?.Value;
                return DateTime.TryParseExact(v, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _) ? v : null;
            }
            catch (Exception)
            {
                return null; // a stub (plain path text) or unreadable file holds no date
            }
        }
    }
}
