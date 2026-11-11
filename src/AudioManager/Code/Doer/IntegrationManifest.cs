using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace AudioManager
{
    /// <summary>
    /// Manifest for selective integration (integrate --manifest &lt;accepted.json&gt;).
    /// A client's review queue writes a JSON array of accepted tracks:
    ///   [ {"filename": "...", "artist": "...", "title": "..."}, ... ]
    /// and the integrator moves ONLY matching NewMusic files; everything else is
    /// left untouched in NewMusic (no moves, no deletes).
    ///
    /// Matching is by filename OR by the canonical TagFixer rename
    /// "{SanitiseFilename(artist)} - {SanitiseFilename(title)}.mp3". The second
    /// candidate is what makes manifests survive TagFixer: dry-run JSON records
    /// the pre-rename on-disk name, but a real run renames files before the
    /// integrator lists them - the canonical name matches either way.
    /// </summary>
    internal class IntegrationManifest
    {
        internal class Entry
        {
            public string Filename;
            // Optional path of the file relative to NewMusic. When present the entry matches ONLY that
            // exact file, so two same-named files in different folders are accepted or declined
            // independently. Absent = match by filename in any folder (old manifests).
            public string SourceRel;
            public string Artist;
            public string Title;
            // Review-stage duplicate resolution ("D"/"L"/"K"), if the user changed it away
            // from the exe's own recommendation. Null/absent means "unresolved" - the exe falls
            // back to its recommendation exactly as it always has.
            public string DupResolution;
        }

        internal List<Entry> Entries = new List<Entry>();

        // Manifest-chosen artist spellings (object form only), keyed by ArtistSpellingCandidates.SpellingKey.
        internal readonly Dictionary<string, string> ArtistSpellings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        internal readonly List<string> SpellingWarnings = new List<string>();

        private HashSet<string> candidateNames;
        private readonly Dictionary<string, Entry> candidateToEntry =
            new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Entry> relToEntry =
            new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Entry> relCanonical =
            new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<Entry> matchedEntries = new HashSet<Entry>();

        /// <summary>
        /// Loads and validates a manifest file. Returns null (with error set) on
        /// unreadable/invalid/empty input - callers must treat that as a hard
        /// failure, never as "integrate everything".
        /// </summary>
        internal static IntegrationManifest Load(string path, out string error)
        {
            error = null;
            string text;
            try
            {
                text = File.ReadAllText(path, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                error = $"could not read manifest '{path}': {ex.Message}";
                return null;
            }

            var manifest = Parse(text, out error);
            if (manifest == null) return null;
            if (manifest.Entries.Count == 0)
            {
                error = "manifest contains no entries - refusing to integrate nothing";
                return null;
            }
            return manifest;
        }

        /// <summary>
        /// Parses the JSON text. Two shapes: the bare array of flat string-valued objects (no spelling choices),
        /// or {"tracks": [...], "artistSpellings": [{"key": "...", "spelling": "..."}]}.
        /// </summary>
        internal static IntegrationManifest Parse(string text, out string error)
        {
            error = null;
            var manifest = new IntegrationManifest();
            if (string.IsNullOrWhiteSpace(text))
            {
                error = "manifest is empty";
                return null;
            }

            int i = 0;
            SkipWhitespace(text, ref i);
            if (i < text.Length && text[i] == '{') return ParseObjectForm(text, out error);
            if (i >= text.Length || text[i] != '[')
            {
                error = "manifest must be a JSON array or object";
                return null;
            }
            i++; // past [

            while (true)
            {
                SkipWhitespace(text, ref i);
                if (i >= text.Length) { error = "unterminated manifest array"; return null; }
                if (text[i] == ']') break;
                if (text[i] == ',') { i++; continue; }
                if (text[i] != '{') { error = $"unexpected character '{text[i]}' at position {i}"; return null; }

                var fields = ParseFlatObject(text, ref i, out error);
                if (fields == null) return null;

                var entry = new Entry();
                fields.TryGetValue("filename", out entry.Filename);
                fields.TryGetValue("sourceRel", out entry.SourceRel);
                fields.TryGetValue("artist", out entry.Artist);
                fields.TryGetValue("title", out entry.Title);
                fields.TryGetValue("dupResolution", out entry.DupResolution);
                if (string.IsNullOrWhiteSpace(entry.Filename) && string.IsNullOrWhiteSpace(entry.SourceRel) &&
                    (string.IsNullOrWhiteSpace(entry.Artist) || string.IsNullOrWhiteSpace(entry.Title)))
                {
                    error = "manifest entry needs a filename, or an artist and title";
                    return null;
                }
                manifest.Entries.Add(entry);
            }

            manifest.BuildCandidates();
            return manifest;
        }

        /// <summary>True if a NewMusic filename is accepted by this manifest.</summary>
        internal bool Matches(string filename, string sourceRel = null)
        {
            var entry = FindEntry(filename, null, null, sourceRel);
            if (entry == null) return false;
            matchedEntries.Add(entry);
            return true;
        }

        private static string NormRel(string rel) => rel?.Replace('/', '\\').Trim('\\');

        /// <summary>
        /// Entry for a scanned file. An entry with a sourceRel matches only that exact relative path (or,
        /// after a Tag Fix rename, the canonical artist - title name in the SAME folder); an entry without
        /// one matches by filename in any folder, as old manifests always did.
        /// </summary>
        private Entry FindEntry(string filename, string artist, string title, string sourceRel)
        {
            string rel = NormRel(sourceRel);
            if (!string.IsNullOrEmpty(rel) && relToEntry.TryGetValue(rel, out var byRel)) return byRel;

            Entry entry = null;
            if (!string.IsNullOrEmpty(filename)) candidateToEntry.TryGetValue(filename, out entry);
            if (entry == null && !string.IsNullOrEmpty(artist) && !string.IsNullOrEmpty(title))
            {
                string sanArtist = Reflector.SanitiseFilename(artist);
                string sanTitle = Reflector.SanitiseFilename(title);
                if (!string.IsNullOrEmpty(sanArtist) && !string.IsNullOrEmpty(sanTitle))
                    candidateToEntry.TryGetValue($"{sanArtist} - {sanTitle}.mp3", out entry);
            }
            if (entry == null && !string.IsNullOrEmpty(rel))
            {
                // Same-folder canonical rename of a sourceRel entry (Tag Fix renamed the file in place).
                string dir = Path.GetDirectoryName(rel) ?? "";
                string name = Path.GetFileName(rel);
                if (relCanonical.TryGetValue(dir + "\\" + name, out var byCanon)) return byCanon;
            }
            return entry;
        }

        /// <summary>Entries that never matched any scanned file (stale scan warning).</summary>
        internal List<Entry> UnmatchedEntries() =>
            Entries.Where(e => !matchedEntries.Contains(e)).ToList();

        /// <summary>
        /// Looks up a review-resolved duplicate decision ('D'/'L'/'K') for a scanned file, tried by
        /// exact filename first (same candidate set Matches() uses), then falling back to the
        /// canonical TagFixer-rename form built from artist+title (so it survives a real-run
        /// rename same as filename matching already does). Returns '\0' when unresolved or when
        /// the value isn't one of D/L/K - callers must treat that as "no override", never as an
        /// implicit decision.
        /// </summary>
        internal char GetDupResolution(string filename, string artist, string title, string sourceRel = null)
        {
            Entry entry = FindEntry(filename, artist, title, sourceRel);

            if (entry == null || string.IsNullOrEmpty(entry.DupResolution)) return '\0';
            char c = char.ToUpperInvariant(entry.DupResolution[0]);
            return (c == 'D' || c == 'L' || c == 'K') ? c : '\0';
        }

        private void BuildCandidates()
        {
            candidateNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in Entries)
            {
                if (!string.IsNullOrWhiteSpace(e.SourceRel))
                {
                    // Path-keyed entry: matches only its own file, never a same-named file elsewhere.
                    string rel = NormRel(e.SourceRel);
                    if (!relToEntry.ContainsKey(rel)) relToEntry[rel] = e;
                    if (!string.IsNullOrWhiteSpace(e.Artist) && !string.IsNullOrWhiteSpace(e.Title))
                    {
                        string a = Reflector.SanitiseFilename(e.Artist), t = Reflector.SanitiseFilename(e.Title);
                        if (!string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(t))
                        {
                            string key = (Path.GetDirectoryName(rel) ?? "") + "\\" + $"{a} - {t}.mp3";
                            if (!relCanonical.ContainsKey(key)) relCanonical[key] = e;
                        }
                    }
                    continue;
                }
                if (!string.IsNullOrWhiteSpace(e.Filename))
                {
                    candidateNames.Add(e.Filename);
                    if (!candidateToEntry.ContainsKey(e.Filename))
                        candidateToEntry[e.Filename] = e;
                }
                if (!string.IsNullOrWhiteSpace(e.Artist) && !string.IsNullOrWhiteSpace(e.Title))
                {
                    // Same canonical name TagFixer renames to (see TagFixer rename block)
                    string sanArtist = Reflector.SanitiseFilename(e.Artist);
                    string sanTitle = Reflector.SanitiseFilename(e.Title);
                    if (!string.IsNullOrEmpty(sanArtist) && !string.IsNullOrEmpty(sanTitle))
                    {
                        string canonical = $"{sanArtist} - {sanTitle}.mp3";
                        candidateNames.Add(canonical);
                        if (!candidateToEntry.ContainsKey(canonical))
                            candidateToEntry[canonical] = e;
                    }
                }
            }
        }

        /// <summary>
        /// Object form. The tracks array is parsed by the bare-array rules; each artistSpellings choice is kept only when
        /// its spelling's SpellingKey equals its key (OrdinalIgnoreCase). Other choices are dropped with a warning.
        /// </summary>
        private static IntegrationManifest ParseObjectForm(string text, out string error)
        {
            error = null;
            var manifest = new IntegrationManifest();
            string tracksText = null, spellingsText = null;
            int i = 0;
            SkipWhitespace(text, ref i);
            i++; // past {
            while (true)
            {
                SkipWhitespace(text, ref i);
                if (i >= text.Length) { error = "unterminated manifest object"; return null; }
                if (text[i] == '}') break;
                if (text[i] == ',') { i++; continue; }
                if (text[i] != '"') { error = $"expected key string at position {i}"; return null; }

                string key = ParseString(text, ref i, out error);
                if (key == null) return null;
                SkipWhitespace(text, ref i);
                if (i >= text.Length || text[i] != ':') { error = $"expected ':' at position {i}"; return null; }
                i++;
                SkipWhitespace(text, ref i);

                int start = i;
                SkipValue(text, ref i);
                string value = text.Substring(start, i - start);
                if (key.Equals("tracks", StringComparison.OrdinalIgnoreCase)) tracksText = value;
                else if (key.Equals("artistSpellings", StringComparison.OrdinalIgnoreCase)) spellingsText = value;
            }

            if (tracksText == null || tracksText.Length == 0 || tracksText[0] != '[')
            {
                error = "manifest object needs a \"tracks\" array";
                return null;
            }
            var tracks = Parse(tracksText, out error);
            if (tracks == null) return null;
            manifest.Entries.AddRange(tracks.Entries);

            if (spellingsText != null && !ParseSpellingArray(spellingsText, manifest, out error)) return null;
            manifest.BuildCandidates();
            return manifest;
        }

        private static bool ParseSpellingArray(string text, IntegrationManifest manifest, out string error)
        {
            error = null;
            if (text.Length == 0 || text[0] != '[') { error = "artistSpellings must be a JSON array"; return false; }
            int i = 1;
            while (true)
            {
                SkipWhitespace(text, ref i);
                if (i >= text.Length) { error = "unterminated artistSpellings array"; return false; }
                if (text[i] == ']') return true;
                if (text[i] == ',') { i++; continue; }
                if (text[i] != '{') { error = $"unexpected character '{text[i]}' in artistSpellings"; return false; }

                var fields = ParseFlatObject(text, ref i, out error);
                if (fields == null) return false;
                fields.TryGetValue("key", out string key);
                fields.TryGetValue("spelling", out string spelling);
                if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(spelling))
                {
                    error = "artistSpellings entry needs a key and a spelling";
                    return false;
                }
                if (!string.Equals(ArtistSpellingCandidates.SpellingKey(spelling), key, StringComparison.OrdinalIgnoreCase))
                {
                    manifest.SpellingWarnings.Add($"artistSpellings '{key}' -> '{spelling}' ignored: the spelling does not match its artist key");
                    continue;
                }
                if (manifest.ArtistSpellings.ContainsKey(key))
                {
                    manifest.SpellingWarnings.Add($"artistSpellings '{key}' is listed twice; the first choice is kept");
                    continue;
                }
                manifest.ArtistSpellings[key] = spelling;
            }
        }

        /// <summary>Skips one JSON value (string, nested array/object, or bare token) starting at i.</summary>
        private static void SkipValue(string s, ref int i)
        {
            if (i < s.Length && s[i] == '"') { ParseString(s, ref i, out _); return; }
            if (i < s.Length && (s[i] == '[' || s[i] == '{'))
            {
                int depth = 0;
                while (i < s.Length)
                {
                    char c = s[i];
                    if (c == '"') { ParseString(s, ref i, out _); continue; }
                    if (c == '[' || c == '{') depth++;
                    else if (c == ']' || c == '}')
                    {
                        depth--;
                        if (depth == 0) { i++; return; }
                    }
                    i++;
                }
                return;
            }
            while (i < s.Length && s[i] != ',' && s[i] != '}') i++;
        }

        // ------------------------------------------------ minimal JSON pieces

        private static void SkipWhitespace(string s, ref int i)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        }

        /// <summary>Parses {"key": "value", ...} (string values only) starting at '{'.</summary>
        private static Dictionary<string, string> ParseFlatObject(string s, ref int i, out string error)
        {
            error = null;
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            i++; // past {
            while (true)
            {
                SkipWhitespace(s, ref i);
                if (i >= s.Length) { error = "unterminated manifest object"; return null; }
                if (s[i] == '}') { i++; return result; }
                if (s[i] == ',') { i++; continue; }
                if (s[i] != '"') { error = $"expected key string at position {i}"; return null; }

                string key = ParseString(s, ref i, out error);
                if (key == null) return null;
                SkipWhitespace(s, ref i);
                if (i >= s.Length || s[i] != ':') { error = $"expected ':' at position {i}"; return null; }
                i++;
                SkipWhitespace(s, ref i);
                if (i < s.Length && s[i] == '"')
                {
                    string value = ParseString(s, ref i, out error);
                    if (value == null) return null;
                    result[key] = value;
                }
                else
                {
                    // tolerate non-string values (null/true/numbers) by skipping the token
                    while (i < s.Length && s[i] != ',' && s[i] != '}') i++;
                }
            }
        }

        /// <summary>Parses a JSON string starting at '"', handling standard escapes.</summary>
        private static string ParseString(string s, ref int i, out string error)
        {
            error = null;
            var sb = new StringBuilder();
            i++; // past opening quote
            while (i < s.Length)
            {
                char c = s[i];
                if (c == '"') { i++; return sb.ToString(); }
                if (c == '\\' && i + 1 < s.Length)
                {
                    char n = s[i + 1];
                    switch (n)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'u':
                            if (i + 5 < s.Length &&
                                int.TryParse(s.Substring(i + 2, 4),
                                    System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out int code))
                            {
                                sb.Append((char)code);
                                i += 4;
                            }
                            break;
                        default: sb.Append(n); break;
                    }
                    i += 2;
                    continue;
                }
                sb.Append(c);
                i++;
            }
            error = "unterminated string in manifest";
            return null;
        }
    }
}
