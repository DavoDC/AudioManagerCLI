using AudioManager.Code.Modules;
using System;
using System.Collections.Generic;
using System.IO;

namespace AudioManager
{
    /// <summary>
    /// The batch scan-ahead for the pre-scan: reads the batch tags, counts the existing Misc and Sources songs from the
    /// mirror XML, works out which artists earn a new Artists/ folder, detects compilation albums and records the result
    /// for the later readers. The `_scan` value is read and published back live through the delegates, so publish order is unchanged.
    /// MusicIntegrator forwards to these members.
    /// </summary>
    internal sealed class BatchScanner
    {
        private readonly Func<IntegrationRoots> _rootsSource;
        private readonly Func<ScanAheadResult> _getScan;
        private readonly Action<ScanAheadResult> _setScan;
        private readonly Func<Dictionary<string, string>> _spellingChoicesSource;
        private readonly Func<Dictionary<string, List<string>>> _miscSource;

        /// <summary>
        /// Roots and the spelling and Misc dictionaries are read live on every use, never copied. The scan-ahead result is
        /// read and written back through the getter and setter, so RunScanAhead publishes it in the same order as before.
        /// </summary>
        internal BatchScanner(Func<IntegrationRoots> roots, Func<ScanAheadResult> getScan, Action<ScanAheadResult> setScan, Func<Dictionary<string, string>> spellingChoices, Func<Dictionary<string, List<string>>> miscMigrationCandidates)
        {
            _rootsSource = roots;
            _getScan = getScan;
            _setScan = setScan;
            _spellingChoicesSource = spellingChoices;
            _miscSource = miscMigrationCandidates;
        }

        private IntegrationRoots _roots => _rootsSource();
        private ScanAheadResult _scan { get => _getScan(); set => _setScan(value); }
        private Dictionary<string, string> _artistSpellingChoices => _spellingChoicesSource();
        private Dictionary<string, List<string>> _miscMigrationCandidates => _miscSource();

        /// <summary>
        /// Pre-scans the batch to find artists that will hit the 3-song threshold.
        /// Checks existing Misc songs in AudioMirror XML + batch count.
        /// Returns a set of primary artist names that need a new Artists/ folder.
        /// Prints a preview of what will happen for those artists.
        /// </summary>
        internal HashSet<string> RunScanAhead(string[] batchFiles)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Console.Write($"\nScan-ahead: reading {batchFiles.Length} file(s)...");

            // Count artists in the incoming batch; also map album -> distinct primary artists for compilation detection.
            // Cache raw tag data here so PreScanFiles avoids a second TagLib read per file.
            var batchCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var spellings = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var batchAlbumCounts = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
            var albumArtists = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            var spellingPairs = new List<(string TrackPath, string ArtistTag)>();
            var tagCache = new Dictionary<string, (string artists, string title, string album, string genres, uint year)>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in batchFiles)
            {
                try
                {
                    using (TagLib.File tf = TagLib.File.Create(f))
                    {
                        string artists = tf.Tag.JoinedPerformers;
                        // Cache all fields PreScanFiles will need, so it can skip its own TagLib.File.Create
                        tagCache[f] = (artists, tf.Tag.Title, tf.Tag.Album, tf.Tag.JoinedGenres, tf.Tag.Year);
                        if (string.IsNullOrEmpty(artists)) continue;
                        // Normalize via TagFixer so artist-name variants (e.g. "JAŸ-Z" via U+0178)
                        // are resolved to their canonical form ("Jay-Z") before being used as dict keys.
                        // Without this, batchAlbumCounts stores "JAŸ-Z" but CountAlbumSongs queries
                        // "Jay-Z" (tag-fixed), and OrdinalIgnoreCase can't bridge the Unicode gap.
                        var fixedArtists = TagFixer.ExtractAndFixArtists(tf.Tag.Title ?? "", artists);
                        string primary = fixedArtists.Count > 0 ? fixedArtists[0].Trim() : Track.ProcessProperty(artists)[0].Trim();
                        if (string.IsNullOrEmpty(primary)) continue;
                        batchCounts[primary] = batchCounts.ContainsKey(primary) ? batchCounts[primary] + 1 : 1;
                        if (!spellings.TryGetValue(primary, out var spellList)) spellings[primary] = spellList = new List<string>();
                        spellList.Add(primary);
                        spellingPairs.Add((MusicIntegrator.RelToNewMusic(f, _roots.NewMusic) ?? Path.GetFileName(f), primary));
                        string album = tf.Tag.Album;
                        if (!string.IsNullOrEmpty(album) && !album.Equals(Constants.MissingTag, StringComparison.OrdinalIgnoreCase))
                        {
                            // Normalize album to match how PreScanFiles cleans tags before routing.
                            // Without this, "The Blueprint (Explicit Version)" and "The Blueprint" are
                            // counted under different keys, causing CountAlbumSongs to return 0 for the batch.
                            album = RulesEngine.NormalizeAlbum(album);
                            if (!string.IsNullOrEmpty(album))
                            {
                                if (!albumArtists.ContainsKey(album))
                                    albumArtists[album] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                                albumArtists[album].Add(primary);
                                if (!batchAlbumCounts.ContainsKey(primary))
                                    batchAlbumCounts[primary] = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                                var byAlbum = batchAlbumCounts[primary];
                                byAlbum[album] = byAlbum.ContainsKey(album) ? byAlbum[album] + 1 : 1;
                            }
                        }
                    }
                }
                catch { /* skip unreadable files - no cache entry, PreScanFiles falls back to TagLib */ }
            }
            _scan = _scan.WithBatchTagCache(tagCache);

            // Count existing Misc songs by artist from AudioMirror XML
            var miscCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var miscFilesByArtist = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            string mirrorMiscPath = Path.GetFullPath(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, _roots.Mirror, Constants.MiscDir));
            if (Directory.Exists(mirrorMiscPath))
            {
                var miscXmlFiles = Directory.GetFiles(mirrorMiscPath, "*.xml");
                Console.Write($" checking Misc ({miscXmlFiles.Length})...");
                foreach (var xmlFile in miscXmlFiles)
                {
                    try
                    {
                        var xmlDoc = new System.Xml.XmlDocument();
                        xmlDoc.Load(xmlFile);
                        var artistsEl = xmlDoc.SelectSingleNode("//Artists");
                        if (artistsEl == null) continue;
                        string primary = Track.ProcessProperty(artistsEl.InnerText)[0].Trim();
                        if (string.IsNullOrEmpty(primary)) continue;
                        miscCounts[primary] = miscCounts.ContainsKey(primary) ? miscCounts[primary] + 1 : 1;
                        // Misc songs migrate into the new folder with their tags untouched, so their spelling votes too.
                        if (!spellings.TryGetValue(primary, out var miscSpell)) spellings[primary] = miscSpell = new List<string>();
                        miscSpell.Add(primary);
                        spellingPairs.Add((Path.GetFileName(xmlFile), primary));
                        if (!miscFilesByArtist.ContainsKey(primary))
                            miscFilesByArtist[primary] = new List<string>();
                        miscFilesByArtist[primary].Add(xmlFile);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"\n  [WARN] Skipped malformed XML '{Path.GetFileName(xmlFile)}': {ex.Message}");
                    }
                }
            }

            // Count existing Sources/ songs by artist (Films, Shows, Anime) from AudioMirror XML.
            // Sources/ tracks count toward the 3-song threshold but are NOT migrated - they stay
            // in Sources/ regardless of whether the artist gets a new Artists/ folder.
            var sourcesCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            string mirrorSourcesPath = Path.Combine(_roots.Mirror, Constants.SourcesDir);
            if (Directory.Exists(mirrorSourcesPath))
            {
                var sourcesXmlFiles = Directory.GetFiles(mirrorSourcesPath, "*.xml", SearchOption.AllDirectories);
                Console.Write($" checking Sources ({sourcesXmlFiles.Length})...");
                foreach (var xmlFile in sourcesXmlFiles)
                {
                    try
                    {
                        var xmlDoc = new System.Xml.XmlDocument();
                        xmlDoc.Load(xmlFile);
                        var artistsEl = xmlDoc.SelectSingleNode("//Artists");
                        if (artistsEl == null) continue;
                        string primary = Track.ProcessProperty(artistsEl.InnerText)[0].Trim();
                        if (string.IsNullOrEmpty(primary)) continue;
                        sourcesCounts[primary] = sourcesCounts.ContainsKey(primary) ? sourcesCounts[primary] + 1 : 1;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"\n  [WARN] Skipped malformed XML '{Path.GetFileName(xmlFile)}': {ex.Message}");
                    }
                }
            }
            Console.WriteLine(" done.");

            // Find artists that will hit 3+ threshold and don't already have an Artists/ folder
            var previewLines = new List<string>();
            foreach (var kvp in batchCounts)
            {
                string artist = kvp.Key;
                int batchCount = kvp.Value;
                int miscCount = miscCounts.ContainsKey(artist) ? miscCounts[artist] : 0;
                int sourcesCount = sourcesCounts.ContainsKey(artist) ? sourcesCounts[artist] : 0;
                int total = batchCount + miscCount + sourcesCount;

                string artistFolder = Path.Combine(_roots.Library, Constants.ArtistsDir, MusicIntegrator.SanitiseFolderName(artist));
                string musivArtistFolder = Path.Combine(_roots.Library, Constants.MusivDir, MusicIntegrator.SanitiseFolderName(artist));
                bool hasExistingFolder = Directory.Exists(artistFolder) || Directory.Exists(musivArtistFolder);
                if (RulesEngine.EarnsArtistFolder(total) && !hasExistingFolder)
                {
                    result.Add(artist);
                    string note;
                    if (miscCount > 0 && sourcesCount > 0)
                        note = $"{batchCount} in batch + {miscCount} in Misc + {sourcesCount} in Sources = {total} total -> new Artists/{artist}/";
                    else if (miscCount > 0)
                        note = $"{batchCount} in batch + {miscCount} in Misc = {total} total -> new Artists/{artist}/";
                    else if (sourcesCount > 0)
                        note = $"{batchCount} in batch + {sourcesCount} in Sources = {total} total -> new Artists/{artist}/";
                    else
                        note = $"{batchCount} in batch = {total} total -> new Artists/{artist}/";
                    if (miscCount > 0)
                        note += $" ({miscCount} existing Misc song(s) will be auto-migrated)";
                    previewLines.Add($"  - {note}");
                }
            }

            if (result.Count > 0)
            {
                Console.WriteLine($"\nScan-ahead: {result.Count} artist(s) will hit 3-song threshold:");
                foreach (var line in previewLines) Console.WriteLine(line);
                Console.WriteLine();
            }

            // Detect compilation albums: 3+ distinct primary artists on same album in this batch
            var compilationAlbums = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in albumArtists)
            {
                if (RulesEngine.IsCompilation(kvp.Value.Count))
                    compilationAlbums.Add(kvp.Key);
            }
            _scan = _scan.WithCompilationAlbums(compilationAlbums);
            if (compilationAlbums.Count > 0)
            {
                Console.WriteLine($"Scan-ahead: {compilationAlbums.Count} compilation album(s) detected:");
                foreach (var album in compilationAlbums)
                    Console.WriteLine($"  - '{album}' ({albumArtists[album].Count} distinct artists) -> Compilations/{album}/");
                Console.WriteLine();
            }

            // Track Misc XMLs for promoted artists so RunMiscMigration can move them post-routing
            foreach (var artist in result)
            {
                if (miscFilesByArtist.TryGetValue(artist, out var xmlPaths) && xmlPaths.Count > 0)
                    _miscMigrationCandidates[artist] = xmlPaths;
            }

            // Store counts so GetDestDir can build a full routing reason for Misc fallback
            var scanSpellings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _scan = _scan.WithBatchCountsAndSpellings(batchCounts, scanSpellings);
            foreach (var kv in spellings)
            {
                scanSpellings[kv.Key] = FolderSpellingFor(kv.Key, _artistSpellingChoices, ChooseCanonicalSpelling(kv.Value));
                var distinct = new HashSet<string>(kv.Value, StringComparer.Ordinal);
                if (distinct.Count > 1)
                    Console.WriteLine($"  [review] '{kv.Key}' is spelled {distinct.Count} ways in the batch and Misc ({string.Join(", ", distinct)}); folder uses '{scanSpellings[kv.Key]}', tracks tagged otherwise are listed in their routing reason. Tags are not edited.");
            }
            var spellingCandidates = ArtistSpellingCandidates.BuildSpellingCandidates(spellingPairs);
            _scan = _scan.WithFinalCounts(spellingCandidates, batchAlbumCounts, miscCounts, sourcesCounts);

            return result;
        }

        /// <summary>
        /// Folder spelling for an artist: the manifest-chosen spelling when one exists for its key, otherwise the
        /// frequency pick (fallback). A chosen spelling always wins, so the folder follows the tags it was given.
        /// </summary>
        internal static string FolderSpellingFor(string spellingKey, IDictionary<string, string> choices, string fallback)
        {
            if (choices != null && spellingKey != null &&
                choices.TryGetValue(ArtistSpellingCandidates.SpellingKey(spellingKey), out string chosen))
                return chosen;
            return fallback;
        }

        /// <summary>Most frequent spelling among the tags (ordinal compare); a tie picks the ordinal-first spelling.</summary>
        internal static string ChooseCanonicalSpelling(IEnumerable<string> tags)
        {
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (string t in tags) counts[t] = counts.ContainsKey(t) ? counts[t] + 1 : 1;
            string best = null;
            foreach (var kv in counts)
                if (best == null || kv.Value > counts[best] || (kv.Value == counts[best] && string.CompareOrdinal(kv.Key, best) < 0))
                    best = kv.Key;
            return best;
        }
    }
}
