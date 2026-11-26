using System;
using System.Collections.Generic;
using System.IO;
using System.Web.Script.Serialization;

namespace AudioManager
{
    /// <summary>
    /// The kept lyrics status list: tracks that were checked by hand and have no lyrics to find
    /// (status "none_found") or are instrumental ("instrumental"). Machine data in config/lyrics-status.json
    /// (gitignored; config/lyrics-status.example.json is the tracked copy). Read only by StatsJson.
    ///
    /// Tolerant by design: a missing or malformed file is an empty list, never an exception.
    /// Keys are library-relative MP3 paths; see NormaliseKey for the matching rules.
    /// </summary>
    internal static class LyricsStatus
    {
        public const string NoneFound = "none_found";
        public const string Instrumental = "instrumental";

        private static readonly IReadOnlyDictionary<string, string> Empty = new Dictionary<string, string>();

        /// <summary>Production path of the kept list (the real file, gitignored).</summary>
        public static string ConfigPath => Path.Combine(Constants.RepoRoot, "config", "lyrics-status.json");

        /// <summary>Loads the kept list from a file. Missing file = empty; unreadable or malformed = empty plus one [WARN] line.</summary>
        public static IReadOnlyDictionary<string, string> LoadFile(string path)
        {
            if (!File.Exists(path)) return Empty;
            try
            {
                return Parse(File.ReadAllText(path));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  [WARN] Could not read lyrics status '{path}': {ex.Message}");
                return Empty;
            }
        }

        /// <summary>
        /// Parses the kept-list JSON text. Returns normalised key -> status for rows whose status is
        /// "none_found" or "instrumental"; other rows are skipped. Malformed text or a wrong shape returns
        /// an empty list and prints one [WARN] line. Never throws.
        /// </summary>
        public static IReadOnlyDictionary<string, string> Parse(string json)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                var root = serializer.DeserializeObject(json) as Dictionary<string, object>;
                var tracks = root != null && root.ContainsKey("tracks") ? root["tracks"] as Dictionary<string, object> : null;
                if (tracks == null)
                {
                    Console.WriteLine("  [WARN] lyrics-status.json is not {\"tracks\": {...}}; ignoring it");
                    return Empty;
                }

                foreach (var pair in tracks)
                {
                    var row = pair.Value as Dictionary<string, object>;
                    string status = row != null && row.ContainsKey("status") ? row["status"] as string : null;
                    if (status == NoneFound || status == Instrumental)
                        result[NormaliseKey(pair.Key)] = status;
                }
                return result;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  [WARN] lyrics-status.json is malformed; ignoring it: {ex.Message}");
                return Empty;
            }
        }

        /// <summary>
        /// Matching key for a library path: backslashes become forward slashes, a leading slash is dropped,
        /// a ".xml" mirror name becomes ".mp3" (the library file), and the result is lowercase.
        /// Applied to both the kept-list keys and the track RelPath, so either form matches.
        /// </summary>
        public static string NormaliseKey(string path)
        {
            if (string.IsNullOrEmpty(path)) return string.Empty;
            string key = path.Replace('\\', '/').TrimStart('/');
            if (key.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                key = key.Substring(0, key.Length - 4) + ".mp3";
            return key.ToLowerInvariant();
        }
    }
}
