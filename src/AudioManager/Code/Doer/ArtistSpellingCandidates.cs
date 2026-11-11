using System;
using System.Collections.Generic;
using System.Linq;

namespace AudioManager
{
    /// <summary>One artist whose tracks carry two or more distinct spellings, with the suggested folder spelling.</summary>
    internal sealed class ArtistSpellingCandidate
    {
        public string Key;
        public string Suggested;
        public List<SpellingVariant> Spellings;
    }

    /// <summary>One exact tag spelling for an artist, with the tracks that carry it (input order).</summary>
    internal sealed class SpellingVariant
    {
        public string Spelling;
        public List<string> Tracks;
        public int Count;
    }

    /// <summary>Pure grouping of (track, artist tag) pairs into spelling candidates. No file or tag reads.</summary>
    internal static class ArtistSpellingCandidates
    {
        /// <summary>
        /// Dedupe key for one spelling: the same folder sanitiser GetDestDir uses (drops symbols such as the registered mark),
        /// compared case-insensitively by the caller.
        /// </summary>
        internal static string SpellingKey(string spelling)
        {
            return Reflector.SanitiseFilename(spelling ?? "");
        }

        /// <summary>
        /// Groups artist tags by SpellingKey and returns every group with 2+ distinct ordinal spellings.
        /// Suggested comes from MusicIntegrator.ChooseCanonicalSpelling. Output is ordered by Key (ordinal);
        /// spellings by Count descending, then ordinal.
        /// </summary>
        internal static List<ArtistSpellingCandidate> BuildSpellingCandidates(IEnumerable<(string TrackPath, string ArtistTag)> pairs)
        {
            var groups = new Dictionary<string, List<(string Spelling, string TrackPath)>>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in pairs)
            {
                if (string.IsNullOrWhiteSpace(pair.ArtistTag)) continue;
                string key = SpellingKey(pair.ArtistTag);
                if (key.Length == 0) continue;
                if (!groups.TryGetValue(key, out var list)) groups[key] = list = new List<(string, string)>();
                list.Add((pair.ArtistTag, pair.TrackPath));
            }

            var result = new List<ArtistSpellingCandidate>();
            foreach (var group in groups)
            {
                var byOrdinal = new Dictionary<string, List<string>>(StringComparer.Ordinal);
                foreach (var entry in group.Value)
                {
                    if (!byOrdinal.TryGetValue(entry.Spelling, out var tracks)) byOrdinal[entry.Spelling] = tracks = new List<string>();
                    tracks.Add(entry.TrackPath);
                }
                if (byOrdinal.Count < 2) continue;

                var spellings = byOrdinal
                    .Select(kv => new SpellingVariant { Spelling = kv.Key, Tracks = kv.Value, Count = kv.Value.Count })
                    .OrderByDescending(v => v.Count)
                    .ThenBy(v => v.Spelling, StringComparer.Ordinal)
                    .ToList();

                result.Add(new ArtistSpellingCandidate
                {
                    Key = group.Value.Select(e => SpellingKey(e.Spelling)).OrderBy(k => k, StringComparer.Ordinal).First(),
                    Suggested = MusicIntegrator.ChooseCanonicalSpelling(group.Value.Select(e => e.Spelling)),
                    Spellings = spellings,
                });
            }

            return result.OrderBy(c => c.Key, StringComparer.Ordinal).ToList();
        }
    }
}
