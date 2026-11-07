using AudioManager.Code.Modules;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace AudioManager
{
    /// <summary>
    /// Flat-file cache for parsed TrackTag data. Eliminates XML re-reads when the mirror is unchanged.
    /// Cache validity: compare cache file LastWriteTime against newest XML mtime in the mirror.
    /// If no XML is newer than the cache, the cached data is current and can be used directly.
    /// Each field is escaped (\ to \\, | to \p, CR to \r, LF to \n) so '|' and newlines survive.
    /// V1 files had no escaping; the V2 header makes them a cache miss so they are rebuilt.
    /// </summary>
    internal static class ParseCache
    {
        private const string Header = "PARSE_CACHE_V2";
        private const char Sep = '|';
        private static readonly int FieldCount = Extract(null).Length;

        // Returns fields in cache serialization order. Extract(null) returns an empty 12-element
        // array whose length is the authoritative field count - one edit here updates Save + validation.
        internal static string[] Extract(TrackTag t) => t == null
            ? new string[12]
            : new[] { t.RelPath, t.Title, t.Artists, t.Album,
                      t.Year, t.TrackNumber, t.Genres, t.Length,
                      t.AlbumCoverCount, t.Compilation, t.CoverWidth, t.CoverHeight };

        /// <summary>
        /// Try to load the parse cache. Returns false if cache is missing, stale, or corrupt.
        /// Any filesystem error during the mtime check degrades to a miss (never crashes).
        /// </summary>
        internal static bool TryLoad(string cachePath, string mirrorPath, out List<TrackTag> tags)
        {
            tags = null;
            if (!File.Exists(cachePath)) return false;
            FileInfo[] xmls;
            try
            {
                DateTime cacheTime = new FileInfo(cachePath).LastWriteTime;
                // One enumeration serves both the staleness check and the count below
                xmls = new DirectoryInfo(mirrorPath).GetFiles("*.xml", SearchOption.AllDirectories);
                // IsStale: miss if mirror is empty (Reflector crash mid-regen) or any XML is newer
                if (IsStale(xmls, cacheTime)) return false;
            }
            catch { return false; } // mirror path error -> degrade to miss
            if (!TryDeserialize(cachePath, out tags)) return false;
            // A deleted XML leaves no newer mtime behind; one cached row per XML, so a count change is a miss
            if (xmls.Length == tags.Count) return true;
            tags = null;
            return false;
        }

        /// <summary>
        /// Save the tag list to the cache file. Silently no-ops on I/O failure.
        /// </summary>
        internal static void Save(string cachePath, List<TrackTag> tags)
        {
            try
            {
                string dir = Path.GetDirectoryName(cachePath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                using (var w = new StreamWriter(cachePath, append: false))
                {
                    w.WriteLine(Header);
                    foreach (var t in tags)
                        w.WriteLine(string.Join(Sep.ToString(), Extract(t).Select(Escape)));
                }
            }
            catch { /* cache write failures must not break the pipeline */ }
        }

        /// <summary>
        /// Deserialize a cache file without checking mtimes. Used by tests and TryLoad.
        /// Returns false if the file is missing, has the wrong header, or has malformed rows.
        /// </summary>
        internal static bool TryDeserialize(string cachePath, out List<TrackTag> tags)
        {
            tags = null;
            if (!File.Exists(cachePath)) return false;
            try
            {
                string[] lines = File.ReadAllLines(cachePath);
                if (lines.Length == 0 || lines[0] != Header) return false;

                var result = new List<TrackTag>(lines.Length - 1);
                for (int i = 1; i < lines.Length; i++)
                {
                    if (string.IsNullOrEmpty(lines[i])) continue;
                    string[] parts = lines[i].Split(Sep);
                    if (parts.Length != FieldCount) return false;
                    var f = new string[FieldCount];
                    for (int j = 0; j < FieldCount; j++)
                        if (!TryUnescape(parts[j], out f[j])) return false;
                    result.Add(new TrackTag(f[0], f[1], f[2], f[3],
                                            f[4], f[5], f[6], f[7],
                                            f[8], f[9], f[10], f[11]));
                }
                tags = result;
                return true;
            }
            catch
            {
                tags = null;
                return false;
            }
        }

        /// <summary>
        /// Escapes one field so it contains no raw '|', CR or LF. Backslash is escaped first in
        /// the mapping order, so a field ending in '\' cannot swallow the separator after it.
        /// </summary>
        internal static string Escape(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            var sb = new StringBuilder(s.Length + 8);
            foreach (char c in s)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '|': sb.Append("\\p"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\n': sb.Append("\\n"); break;
                    default: sb.Append(c); break;
                }
            }
            return sb.ToString();
        }

        /// <summary>
        /// Reverses Escape. Returns false on a dangling backslash or an unknown escape code.
        /// </summary>
        internal static bool TryUnescape(string s, out string value)
        {
            if (s.IndexOf('\\') < 0) { value = s; return true; }
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c != '\\') { sb.Append(c); continue; }
                if (i + 1 >= s.Length) { value = null; return false; }
                switch (s[++i])
                {
                    case '\\': sb.Append('\\'); break;
                    case 'p': sb.Append('|'); break;
                    case 'r': sb.Append('\r'); break;
                    case 'n': sb.Append('\n'); break;
                    default: value = null; return false;
                }
            }
            value = sb.ToString();
            return true;
        }

        /// <summary>
        /// Returns true if the mirror is stale relative to the cache timestamp.
        /// Stale means: (a) no XMLs found - mirror is empty or mid-regen after a crash, or
        /// (b) at least one XML has a LastWriteTime newer than the cache.
        /// Uses DirectoryInfo.GetFiles so mtime comes from the directory enumeration, no extra stats.
        /// Exposed internal for tests.
        /// </summary>
        internal static bool IsMirrorStale(string mirrorPath, DateTime cacheTime) =>
            IsStale(new DirectoryInfo(mirrorPath).GetFiles("*.xml", SearchOption.AllDirectories), cacheTime);

        /// <summary>
        /// Staleness decision over an already-enumerated list of mirror XMLs (see IsMirrorStale).
        /// </summary>
        internal static bool IsStale(FileInfo[] xmlFiles, DateTime cacheTime)
        {
            // Empty mirror = Reflector crash or missing - treat as stale so Parser re-populates
            if (!xmlFiles.Any()) return true;
            return xmlFiles.Any(f => f.LastWriteTime > cacheTime);
        }

    }
}
