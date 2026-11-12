using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Xml;
using AudioManager.Code.Modules;

namespace AudioManager
{
    /// <summary>
    /// Reads the AudioMirror index: duplicate lookup keys, mirror-to-library path mapping and album compilation checks.
    /// MusicIntegrator forwards to these.
    /// </summary>
    internal sealed class MirrorIndex
    {
        private readonly IntegrationRoots _roots;

        internal MirrorIndex(IntegrationRoots roots) { _roots = roots; }

        /// <summary>
        /// Loads artist-aliases.xml: maps old names to canonical names for duplicate detection.
        /// Returns empty dict if the file is missing or unreadable.
        /// </summary>
        internal static Dictionary<string, string> LoadArtistAliases(string path = null)
        {
            string filePath = path ?? Constants.ArtistAliasesPath;
            var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!File.Exists(filePath)) return aliases;
            try
            {
                var doc = new XmlDocument();
                doc.Load(filePath);
                foreach (XmlNode node in doc.SelectNodes("//Alias"))
                {
                    string from = node.Attributes?["from"]?.Value;
                    string to = node.Attributes?["to"]?.Value;
                    if (!string.IsNullOrEmpty(from) && !string.IsNullOrEmpty(to))
                        aliases[from] = to;
                }
            }
            catch (Exception ex)
            {
                // Duplicate detection degrades gracefully (keeps any aliases parsed before the failure), but say why.
                Console.WriteLine($"  [WARN] Could not read artist aliases '{filePath}': {ex.Message}");
            }
            return aliases;
        }

        /// <summary>
        /// Returns all mirror index keys for an artist+title pair, including alias expansions.
        /// Handles both directions: old-name entries also get a canonical key, and canonical
        /// entries also get old-name keys. This ensures duplicate detection works regardless
        /// of which name the library was indexed under.
        /// </summary>
        internal static IEnumerable<string> GetAliasExpandedKeys(
            string primaryArtist, string title,
            Dictionary<string, string> aliases,
            Dictionary<string, List<string>> reverseAliases)
        {
            string normTitle = NormaliseTitleForDedup(title).ToLowerInvariant();
            string baseKey = primaryArtist.ToLowerInvariant() + "\0" + normTitle;
            yield return baseKey;

            // If this artist is an old name (e.g. "Kanye West"), also yield the canonical key ("ye\0...")
            if (aliases != null && aliases.TryGetValue(primaryArtist, out string canonical))
                yield return canonical.ToLowerInvariant() + "\0" + normTitle;

            // If this artist is a canonical name (e.g. "Ye"), also yield old-name keys ("kanye west\0...")
            if (reverseAliases != null && reverseAliases.TryGetValue(primaryArtist, out List<string> oldNames))
                foreach (var oldName in oldNames)
                    yield return oldName.ToLowerInvariant() + "\0" + normTitle;
        }

        /// <summary>
        /// Normalises a title for duplicate detection by stripping featured-artist parentheticals.
        /// Library tags may contain "(feat. X)" that TagFixer hasn't cleaned; incoming batch files
        /// have already been cleaned. Without normalisation "God's Plan (feat. XYZ)" in the library
        /// won't match "God's Plan" from the batch even though they're the same song.
        /// Only strips feat./ft./featuring - does NOT strip (Remix), (Edit), (Version) which are
        /// genuinely different tracks.
        /// </summary>
        internal static string NormaliseTitleForDedup(string title)
        {
            if (string.IsNullOrEmpty(title)) return title;
            // Strip "(feat. X)", "(ft. X)", "(featuring X)" - case-insensitive, greedy on content
            return System.Text.RegularExpressions.Regex.Replace(
                title,
                @"\s*\((feat\.?|ft\.?|featuring)\s[^)]*\)",
                "",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();
        }

        /// <summary>
        /// Builds an in-memory index of all AudioMirror XMLs: normalised "primary\0title" -> xmlPath.
        /// Called once before PreScanFiles so duplicate detection is O(1) per file instead of O(mirror_size).
        /// Also indexes alias-expanded keys so artist-rename scenarios (Ye/Kanye West) are caught.
        /// </summary>
        internal static void AddIndexKey(ConcurrentDictionary<string, string> index, string key, string xmlFile)
        {
            // Deterministic under Parallel.ForEach: the smallest path wins, whatever order threads arrive in.
            index.AddOrUpdate(key, xmlFile,
                (k, existing) => string.Compare(xmlFile, existing, StringComparison.OrdinalIgnoreCase) < 0 ? xmlFile : existing);
        }

        internal Dictionary<string, string> BuildMirrorIndex()
        {
            if (!Directory.Exists(_roots.Mirror)) return new Dictionary<string, string>(StringComparer.Ordinal);

            var xmlFiles = Directory.GetFiles(_roots.Mirror, "*.xml", SearchOption.AllDirectories);
            Console.Write($"\nIndexing AudioMirror for duplicates ({xmlFiles.Length} tracks)...");

            // Load aliases once - read-only during parallel phase, safe to share
            var aliases = LoadArtistAliases();
            var reverseAliases = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in aliases)
            {
                if (!reverseAliases.ContainsKey(kv.Value))
                    reverseAliases[kv.Value] = new List<string>();
                reverseAliases[kv.Value].Add(kv.Key);
            }

            // Parallel reads: XmlDocument is created locally per iteration, aliases are read-only
            var concurrentIndex = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
            Parallel.ForEach(xmlFiles, xmlFile =>
            {
                try
                {
                    var xmlDoc = new System.Xml.XmlDocument();
                    xmlDoc.Load(xmlFile);
                    var artistsEl = xmlDoc.SelectSingleNode("//Artists");
                    var titleEl = xmlDoc.SelectSingleNode("//Title");
                    if (artistsEl == null || titleEl == null) return;
                    string primary = Track.ProcessProperty(artistsEl.InnerText)[0].Trim();
                    string title = titleEl.InnerText.Trim();
                    if (string.IsNullOrEmpty(primary) || string.IsNullOrEmpty(title)) return;

                    foreach (var key in GetAliasExpandedKeys(primary, title, aliases, reverseAliases))
                        AddIndexKey(concurrentIndex, key, xmlFile);
                }
                catch (Exception ex)
                {
                    // Best-effort index: the malformed file is left out of duplicate detection, so say so.
                    Console.WriteLine($"\n  [WARN] Skipped malformed XML '{Path.GetFileName(xmlFile)}': {ex.Message}");
                }
            });

            Console.WriteLine(" done.");
            return new Dictionary<string, string>(concurrentIndex, StringComparer.Ordinal);
        }

        /// <summary>
        /// Looks up an existing track in the pre-built mirror index by primary artist + title (case-insensitive).
        /// Returns the matching XML path, or null if not found.
        /// </summary>
        internal string FindDuplicateInMirror(Track track, Dictionary<string, string> mirrorIndex)
        {
            string primaryArtist = track.PrimaryArtist;
            if (string.IsNullOrEmpty(primaryArtist) || primaryArtist.Equals(Constants.MissingTag)) return null;
            string title = track.Title;
            if (string.IsNullOrEmpty(title) || title.Equals(Constants.MissingTag)) return null;
            string normTitle = NormaliseTitleForDedup(title).ToLowerInvariant();
            string key = primaryArtist.ToLowerInvariant() + "\0" + normTitle;
            return mirrorIndex.TryGetValue(key, out string xmlPath) ? xmlPath : null;
        }

        /// <summary>
        /// Derives the actual library file path from an AudioMirror XML path.
        /// AudioMirror: C:\...\AudioMirror\AUDIO_MIRROR\Artist\Album\Track.xml
        /// Library:     <home>\Audio\Artist\Album\Track.mp3
        /// </summary>
        internal string DeriveLibraryPathFromMirrorPath(string mirrorXmlPath)
        {
            try
            {
                string mirrorBaseFolder = _roots.Mirror;
                if (!mirrorXmlPath.StartsWith(mirrorBaseFolder, StringComparison.OrdinalIgnoreCase))
                    return mirrorXmlPath;

                string relativePath = mirrorXmlPath.Substring(mirrorBaseFolder.Length).TrimStart(Path.DirectorySeparatorChar);

                if (relativePath.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                    relativePath = relativePath.Substring(0, relativePath.Length - 4) + ".mp3";

                return Path.Combine(_roots.Library, relativePath);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  [WARN] Could not derive library path from '{mirrorXmlPath}': {ex.Message}");
                return mirrorXmlPath;
            }
        }

        /// <summary>
        /// Reads Track and Album fields from an AudioMirror XML file for display purposes.
        /// Falls back to null on any failure.
        /// </summary>
        internal void ReadMirrorTrackInfo(string xmlPath, out string trackDisplay, out string album)
        {
            trackDisplay = null;
            album = null;
            try
            {
                var xmlDoc = new System.Xml.XmlDocument();
                xmlDoc.Load(xmlPath);
                string title = xmlDoc.SelectSingleNode("//Title")?.InnerText?.Trim();
                string artists = xmlDoc.SelectSingleNode("//Artists")?.InnerText?.Trim();
                string albumStr = xmlDoc.SelectSingleNode("//Album")?.InnerText?.Trim();
                if (!string.IsNullOrEmpty(title) && !string.IsNullOrEmpty(artists))
                    trackDisplay = $"{Track.ProcessProperty(artists)[0].Trim()} - {title}";
                if (!string.IsNullOrEmpty(albumStr))
                    album = albumStr;
            }
            catch { /* fall through to null */ }
        }

        /// <summary>
        /// Detects whether the album containing the given XML mirror file is a compilation,
        /// by reading all XML files in the same folder and collecting every distinct artist
        /// across all positions (not just primary). Returns true if 3 or more distinct artists
        /// are found, which catches both traditional compilations (many primary artists) and
        /// ATD-style albums (one primary artist featuring many different people).
        /// </summary>
        internal bool IsAlbumFolderCompilation(string xmlPath)
        {
            try
            {
                string albumFolder = Path.GetDirectoryName(xmlPath);
                if (string.IsNullOrEmpty(albumFolder) || !Directory.Exists(albumFolder))
                    return false;

                var xmlFiles = Directory.GetFiles(albumFolder, "*.xml");
                if (xmlFiles.Length < 3)
                    return false;

                var distinctArtists = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var f in xmlFiles)
                {
                    try
                    {
                        var doc = new XmlDocument();
                        doc.Load(f);
                        var artistsEl = doc.SelectSingleNode("//Artists");
                        if (artistsEl == null) continue;
                        foreach (string artist in Track.ProcessProperty(artistsEl.InnerText))
                        {
                            string trimmed = artist.Trim();
                            if (!string.IsNullOrEmpty(trimmed))
                                distinctArtists.Add(trimmed);
                        }
                    }
                    catch (Exception ex)
                    {
                        // Compilation check only: an unreadable sibling is left out of the distinct-artist count, so say so.
                        Console.WriteLine($"  [WARN] Skipped malformed XML '{Path.GetFileName(f)}': {ex.Message}");
                    }
                }

                return RulesEngine.IsCompilation(distinctArtists.Count);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  [WARN] Compilation check skipped for '{xmlPath}': {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Derives the AudioMirror RelPath from a full mirror XML path.
        /// E.g. "C:\...\AUDIO_MIRROR\Artists\X\Y.xml" -> "\Artists\X\Y.xml"
        /// The leading backslash is required by LibChecker's path-index logic.
        /// </summary>
        internal static string GetMirrorRelPath(string mirrorXmlPath)
        {
            if (string.IsNullOrEmpty(mirrorXmlPath)) return string.Empty;
            int pos = mirrorXmlPath.LastIndexOf(Constants.MirrorFolderName, StringComparison.OrdinalIgnoreCase);
            if (pos < 0) return "\\" + Path.GetFileName(mirrorXmlPath);
            return mirrorXmlPath.Substring(pos + Constants.MirrorFolderName.Length);
        }
    }
}
