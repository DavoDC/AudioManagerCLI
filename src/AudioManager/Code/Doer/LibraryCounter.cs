using AudioManager.Code.Modules;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AudioManager
{
    /// <summary>
    /// Counts library and batch songs for routing: album totals, Singles album tags and Akira The Don People folders.
    /// Owns the library-side count caches, so pre-flight and the real pass share them. MusicIntegrator forwards to these.
    /// </summary>
    internal sealed class LibraryCounter
    {
        private readonly Func<IntegrationRoots> _rootsSource;
        private readonly Func<string> _libraryPathSource;
        private readonly Func<Dictionary<string, Dictionary<string, int>>> _batchAlbumCountsSource;

        // Library-side album song count cache: "artist\0album" -> count. Avoids O(N) Directory.GetFiles
        // calls when the same artist+album appears multiple times in a batch (e.g. 6 Blueprint tracks).
        private Dictionary<string, int> _albumLibraryCountCache = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        // Library Singles album tags: artist -> normalised album -> song count. Each Singles folder is read once per artist.
        private Dictionary<string, Dictionary<string, int>> _librarySinglesAlbumCache = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>The owner's roots, library path and batch counts are read live on every use, never copied.</summary>
        internal LibraryCounter(Func<IntegrationRoots> roots, Func<string> libraryPath, Func<Dictionary<string, Dictionary<string, int>>> batchAlbumCounts)
        {
            _rootsSource = roots;
            _libraryPathSource = libraryPath;
            _batchAlbumCountsSource = batchAlbumCounts;
        }

        private IntegrationRoots _roots => _rootsSource();
        private string _libraryPath => _libraryPathSource();
        private Dictionary<string, Dictionary<string, int>> _scanAheadBatchAlbumCounts => _batchAlbumCountsSource();

        /// <summary>
        /// Counts the audio entries directly inside the library album folder of a duplicate.
        /// Returns 0 when the folder is missing or unreadable, which makes the size rule stand down.
        /// </summary>
        internal static int CountLibraryAlbumTracks(string libraryFilePath)
        {
            try
            {
                string albumFolder = Path.GetDirectoryName(libraryFilePath);
                if (string.IsNullOrEmpty(albumFolder) || !Directory.Exists(albumFolder))
                    return 0;
                return Directory.GetFiles(albumFolder, "*.mp3", SearchOption.TopDirectoryOnly).Length;
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>Forwards to <see cref="RulesEngine.IsDeluxeVersionOf"/> (the single definition, INT-3).</summary>
        internal static bool IsDeluxeVersionOf(string candidate, string baseAlbum) =>
            RulesEngine.IsDeluxeVersionOf(candidate, baseAlbum);

        /// <summary>
        /// Finds the best matching album folder in an artist directory.
        /// Tries exact match first; if not found, strips version suffixes from each existing subfolder
        /// and returns the first that matches cleanAlbum (case-insensitive).
        /// Returns null if no match found.
        /// </summary>
        internal string FindAlbumFolder(string artistFolder, string cleanAlbum)
        {
            string exactPath = Path.Combine(artistFolder, MusicIntegrator.SanitiseFolderName(cleanAlbum));
            if (Directory.Exists(exactPath)) return exactPath;
            if (!Directory.Exists(artistFolder)) return null;
            try
            {
                foreach (var dir in Directory.GetDirectories(artistFolder))
                {
                    string folderName = Path.GetFileName(dir);
                    string stripped = RulesEngine.NormalizeAlbum(folderName);
                    if (string.Equals(stripped, cleanAlbum, StringComparison.OrdinalIgnoreCase))
                        return dir;
                }
            }
            catch { /* filesystem errors: fall through to null */ }
            return null;
        }

        /// <summary>
        /// Count total songs from an album (library + new batch combined).
        /// Returns the count of existing library songs + all songs from this album in the current files array.
        /// </summary>
        internal int CountAlbumSongs(string artist, string album)
        {
            // Library-side: memoize per (artist, album) to avoid repeated Directory.GetFiles
            // when the same album appears multiple times in a batch (e.g. 6 Blueprint songs -> 6 routing calls)
            string cacheKey = artist + "\0" + album;
            if (!_albumLibraryCountCache.TryGetValue(cacheKey, out int libCount))
            {
                string artistFolder = Path.Combine(_libraryPath, Constants.ArtistsDir, MusicIntegrator.SanitiseFolderName(artist));
                string albumFolder = FindAlbumFolder(artistFolder, album);
                libCount = albumFolder != null
                    ? Directory.GetFiles(albumFolder, "*.mp3", SearchOption.AllDirectories).Length
                    : 0;
                // Album Subfolder Rule: library songs of this album in Singles/ count too.
                // Skipped when the album folder IS Singles/, which is already counted above.
                if (!string.Equals(albumFolder, Path.Combine(artistFolder, "Singles"), StringComparison.OrdinalIgnoreCase))
                    libCount += CountLibrarySinglesOfAlbum(artist, artistFolder, album);
                _albumLibraryCountCache[cacheKey] = libCount;
            }

            // Batch-side: use scan-ahead data built by RunScanAhead (O(1) dict lookup, no TagLib re-reads)
            int batchCount = 0;
            if (_scanAheadBatchAlbumCounts != null &&
                _scanAheadBatchAlbumCounts.TryGetValue(artist, out var byAlbum))
                byAlbum.TryGetValue(album, out batchCount);

            return libCount + batchCount;
        }

        /// <summary>
        /// Counts library songs in Artists/{artist}/Singles/ whose album tag (normalised) equals album.
        /// Each artist's Singles folder is read once and cached. Unreadable files are skipped.
        /// </summary>
        internal int CountLibrarySinglesOfAlbum(string artist, string artistFolder, string album)
        {
            if (!_librarySinglesAlbumCache.TryGetValue(artist, out var byAlbum))
            {
                byAlbum = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                string singlesFolder = Path.Combine(artistFolder, "Singles");
                try
                {
                    if (Directory.Exists(singlesFolder))
                    {
                        foreach (string file in Directory.GetFiles(singlesFolder, "*.mp3"))
                        {
                            string fileAlbum;
                            try
                            {
                                using (TagLib.File tf = TagLib.File.Create(file))
                                    fileAlbum = tf.Tag.Album;
                            }
                            catch { continue; }
                            if (string.IsNullOrEmpty(fileAlbum)) continue;
                            string key = RulesEngine.NormalizeAlbum(fileAlbum);
                            byAlbum[key] = byAlbum.TryGetValue(key, out int n) ? n + 1 : 1;
                        }
                    }
                }
                catch { /* filesystem errors: count what was read */ }
                _librarySinglesAlbumCache[artist] = byAlbum;
            }
            return byAlbum.TryGetValue(RulesEngine.NormalizeAlbum(album), out int count) ? count : 0;
        }

        /// <summary>
        /// Count songs for a sampled person in Akira The Don catalog (library + new batch combined).
        /// Used to determine if person gets their own People/{person}/ folder (3+ songs) or Singles/.
        /// </summary>
        internal int CountAkiraTheDonPersonSongs(string sampledPerson, string libraryPath = null)
        {
            string libPath = libraryPath ?? _roots.Library;
            int count = 0;

            // Count in library (scan Musivation/Akira The Don/People/{person}/)
            string personFolder = Path.Combine(libPath, Constants.MusivDir, "Akira The Don", "People", MusicIntegrator.SanitiseFolderName(sampledPerson));
            if (Directory.Exists(personFolder))
            {
                count += Directory.GetFiles(personFolder, "*.mp3", SearchOption.AllDirectories).Length;
            }

            // Count in library Singles folder
            string singlesFolder = Path.Combine(libPath, Constants.MusivDir, "Akira The Don", "Singles");
            if (Directory.Exists(singlesFolder))
            {
                var singlesFiles = Directory.GetFiles(singlesFolder, "*.mp3", SearchOption.AllDirectories);
                foreach (var filePath in singlesFiles)
                {
                    try
                    {
                        using (TagLib.File tagFile = TagLib.File.Create(filePath))
                        {
                            var tag = tagFile.Tag;
                            string fileArtists = tag.JoinedPerformers ?? "";
                            // Check if this file has sampledPerson as secondary artist
                            var artistList = Track.ProcessProperty(fileArtists).ToList();
                            if (artistList.Count > 1 && artistList[1].Equals(sampledPerson, StringComparison.OrdinalIgnoreCase))
                            {
                                count++;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        // Unreadable files are left out of the count; logged so a short count can be explained.
                        Console.WriteLine($"  [WARN] Skipped unreadable file '{Path.GetFileName(filePath)}': {ex.Message}");
                    }
                }
            }

            // Count in new batch (scan NewMusic for Akira The Don files with matching sampled person)
            if (Directory.Exists(_roots.NewMusic))
            {
                var newMusicFiles = Directory.GetFiles(_roots.NewMusic, "*.mp3", SearchOption.AllDirectories);
                foreach (var filePath in newMusicFiles)
                {
                    try
                    {
                        using (TagLib.File tagFile = TagLib.File.Create(filePath))
                        {
                            var tag = tagFile.Tag;
                            string fileArtists = tag.JoinedPerformers ?? "";

                            // Check if file is Akira The Don + has matching sampled person.
                            // Don't require Musivation genre - genre isn't set yet in dry-run (TagFixer hasn't run).
                            // Artist name check is sufficient for batch holistic counting.
                            if (fileArtists.IndexOf("Akira The Don", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                var artistList = Track.ProcessProperty(fileArtists).ToList();
                                if (artistList.Count > 1 && artistList[1].Equals(sampledPerson, StringComparison.OrdinalIgnoreCase))
                                {
                                    count++;
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        // Unreadable files are left out of the count; logged so a short count can be explained.
                        Console.WriteLine($"  [WARN] Skipped unreadable file '{Path.GetFileName(filePath)}': {ex.Message}");
                    }
                }
            }

            return count;
        }

        /// <summary>
        /// Count songs from a specific album for a sampled person in People/{sampledPerson}/{album}/ (library + batch).
        /// Mirrors CountAlbumSongs but for the ATD People/ folder structure.
        /// Used to decide: People/{person}/{album}/ if 2+ songs, else People/{person}/Singles/.
        /// </summary>
        internal int CountAkiraTheDonPersonAlbumSongs(string sampledPerson, string album, string libraryPath = null)
        {
            string libPath = libraryPath ?? _roots.Library;
            int count = 0;

            // Count in library: Musivation/Akira The Don/People/{sampledPerson}/{album}/
            string albumFolder = Path.Combine(
                libPath, Constants.MusivDir, "Akira The Don", "People", MusicIntegrator.SanitiseFolderName(sampledPerson), MusicIntegrator.SanitiseFolderName(album));
            if (Directory.Exists(albumFolder))
            {
                count += Directory.GetFiles(albumFolder, "*.mp3", SearchOption.AllDirectories).Length;
            }

            // Count in new batch: ATD songs with matching sampled person + same album
            if (Directory.Exists(_roots.NewMusic))
            {
                foreach (var filePath in Directory.GetFiles(_roots.NewMusic, "*.mp3", SearchOption.AllDirectories))
                {
                    try
                    {
                        using (TagLib.File tagFile = TagLib.File.Create(filePath))
                        {
                            var tag = tagFile.Tag;
                            string fileArtists = tag.JoinedPerformers ?? "";
                            string fileAlbum = tag.Album ?? "";

                            if (fileArtists.IndexOf("Akira The Don", StringComparison.OrdinalIgnoreCase) >= 0 &&
                                fileAlbum.Equals(album, StringComparison.OrdinalIgnoreCase))
                            {
                                var artistList = Track.ProcessProperty(fileArtists).ToList();
                                if (artistList.Count > 1 && artistList[1].Equals(sampledPerson, StringComparison.OrdinalIgnoreCase))
                                    count++;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        // Unreadable files are left out of the count; logged so a short count can be explained.
                        Console.WriteLine($"  [WARN] Skipped unreadable file '{Path.GetFileName(filePath)}': {ex.Message}");
                    }
                }
            }

            return count;
        }
    }
}
