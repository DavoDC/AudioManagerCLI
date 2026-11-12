using AudioManager.Code.Modules;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AudioManager
{
    /// <summary>
    /// Routing decisions for each track: the destination folder and filename, the route category and summary
    /// shown in dry-run output, and the destination-blocked check. Reads run state live through delegates
    /// (library path, scan-ahead result) and counts through LibraryCounter, so it never holds a stale copy.
    /// MusicIntegrator forwards to these members.
    /// </summary>
    internal sealed class DestinationRouter
    {
        private readonly Func<string> _libraryPathSource;
        private readonly Func<ScanAheadResult> _scanSource;
        private readonly LibraryCounter _counter;

        /// <summary>The library path and scan-ahead result are read live on every use, never copied.</summary>
        internal DestinationRouter(Func<string> libraryPath, Func<ScanAheadResult> scan, LibraryCounter counter)
        {
            _libraryPathSource = libraryPath;
            _scanSource = scan;
            _counter = counter;
        }

        private string _libraryPath => _libraryPathSource();
        private Dictionary<string, int> _scanAheadBatchCounts => _scanSource().BatchCounts;
        private Dictionary<string, string> _scanAheadSpellings => _scanSource().Spellings;
        private Dictionary<string, int> _scanAheadMiscCounts => _scanSource().MiscCounts;
        private Dictionary<string, int> _scanAheadSourcesCounts => _scanSource().SourcesCounts;

        /// <summary>The routed filename for a track: "Artists - Title.mp3", both parts sanitised.</summary>
        internal static string BuildDestFilename(Track track)
        {
            return Reflector.SanitiseFilename(track.Artists) + " - " + Reflector.SanitiseFilename(track.Title) + ".mp3";
        }

        /// <summary>
        /// Destination check shared by real and dry runs. Blocked when a file already sits at destPath
        /// (unless it is the library copy being replaced) or when plannedDests already holds destPath.
        /// A null libraryPathBeingReplaced means no exemption; a null plannedDests skips the batch check.
        /// plannedDests must be OrdinalIgnoreCase for case-insensitive batch matching.
        /// </summary>
        internal static bool IsDestBlocked(string destPath, string libraryPathBeingReplaced, HashSet<string> plannedDests)
        {
            bool exempt = libraryPathBeingReplaced != null && destPath.Equals(libraryPathBeingReplaced, StringComparison.OrdinalIgnoreCase);
            if (File.Exists(destPath) && !exempt) return true;
            return plannedDests != null && plannedDests.Contains(destPath);
        }

        /// <summary>
        /// Extracts a display category name from a full destination directory path.
        /// Used to build the route distribution summary at the top of a dry-run routing section.
        /// </summary>
        internal static string GetRouteCategory(string destDir) => GetRouteCategory(destDir, Constants.AudioFolderPath);

        /// <summary>Same as GetRouteCategory(string), with the library root passed in.</summary>
        internal static string GetRouteCategory(string destDir, string libraryRoot)
        {
            if (string.IsNullOrEmpty(destDir)) return "Unknown";
            string rel = destDir.StartsWith(libraryRoot, StringComparison.OrdinalIgnoreCase)
                ? destDir.Substring(libraryRoot.Length).TrimStart('\\', '/')
                : destDir;
            string first = rel.Split(new[] { '\\', '/' }, 2)[0];
            return first.Equals("Miscellaneous Songs", StringComparison.OrdinalIgnoreCase) ? "Misc" : first;
        }

        /// <summary>
        /// Derives a short routing summary from a relative destination path.
        /// Strips the top-level category folder and filename; formats as "A / B / C".
        /// E.g. "Artists\Dizzy Wright\Singles\Track.mp3" -> "Dizzy Wright / Singles"
        /// </summary>
        internal string GetRouteSummary(string relativeDest)
        {
            string dirPath = Path.GetDirectoryName(relativeDest) ?? "";
            if (string.IsNullOrEmpty(dirPath))
                return relativeDest;
            var parts = dirPath.Split(new[] { Path.DirectorySeparatorChar, '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
                return relativeDest;
            if (parts.Length == 1)
                return parts[0].Equals("Miscellaneous Songs", StringComparison.OrdinalIgnoreCase) ? "Misc" : parts[0];
            return string.Join(" / ", parts.Skip(1));
        }

        /// <summary>
        /// Sanitises a tag value (e.g. Album, Artist) for use as a Windows filesystem folder name.
        /// Tag values can contain characters illegal in paths (e.g. ? in "WHAT IF?").
        /// Must be called on any tag value used in Path.Combine - never on display strings.
        /// </summary>
        internal static string SanitiseFolderName(string component)
        {
            return Reflector.SanitiseFilename(component ?? "");
        }

        /// <summary>
        /// Determines the destination directory for a track based on its metadata.
        /// </summary>
        /// <param name="track">The track to route.</param>
        /// <param name="newArtistFolders">Scan-ahead result: artists getting new folders this batch.</param>
        /// <param name="compilationAlbums">Scan-ahead result: album names with 3+ distinct primary artists in the batch.</param>
        /// <param name="reason">Output: human-readable reason for the proposed destination.</param>
        /// <param name="isNewFolder">Output: true if routing creates a new Artists/ folder (scan-ahead promoted artist).</param>
        /// <returns>The full destination directory path.</returns>
        internal string GetDestDir(Track track, HashSet<string> newArtistFolders, HashSet<string> compilationAlbums, out string reason, out bool isNewFolder)
        {
            // Special case: Akira The Don (check by artist name, not genre, since genre may not be set in dry-run)
            string primaryArtist = track.PrimaryArtist;
            if (primaryArtist.IndexOf("Akira The Don", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                // Extract sampled person from secondary artist (after first semicolon)
                var artists = Track.ProcessProperty(track.Artists).ToList();
                if (artists.Count > 1)
                {
                    string sampledPerson = artists[1]; // Second artist is the sampled person

                    // Use existing library folder casing if one already exists (e.g. "Scott Adams" not "Scott adams")
                    string peopleParent = Path.Combine(_libraryPath, Constants.MusivDir, "Akira The Don", "People");
                    if (Directory.Exists(peopleParent))
                    {
                        foreach (var dir in Directory.GetDirectories(peopleParent))
                        {
                            string dirName = Path.GetFileName(dir);
                            if (dirName.Equals(sampledPerson, StringComparison.OrdinalIgnoreCase))
                            {
                                sampledPerson = dirName; // match library casing
                                break;
                            }
                        }
                    }

                    // Count songs for this sampled person (3+ gets own People/ folder, <3 goes to ATD Singles)
                    int personSongCount = _counter.CountAkiraTheDonPersonSongs(sampledPerson, _libraryPath);
                    if (RulesEngine.EarnsPeopleFolder(personSongCount))
                    {
                        string peopleFolder = Path.Combine(peopleParent, SanitiseFolderName(sampledPerson));

                        // Within People/{person}/, apply same album-vs-singles rule as Artists/ routing
                        if (!track.Album.Equals(Constants.MissingTag) && !track.Album.Equals(primaryArtist, StringComparison.OrdinalIgnoreCase))
                        {
                            int albumCount = _counter.CountAkiraTheDonPersonAlbumSongs(sampledPerson, track.Album, _libraryPath);
                            if (RulesEngine.UsesAlbumSubfolder(albumCount))
                            {
                                reason = $"{albumCount} songs from album '{track.Album}'";
                                isNewFolder = false;
                                return Path.Combine(peopleFolder, SanitiseFolderName(track.Album));
                            }
                            else
                            {
                                reason = albumCount == 0
                                    ? $"{personSongCount} song(s) from {sampledPerson}, no songs from this album"
                                    : $"{personSongCount} song(s) from {sampledPerson}, only {albumCount} from this album";
                                isNewFolder = false;
                                return Path.Combine(peopleFolder, Constants.SinglesDir);
                            }
                        }
                        else
                        {
                            reason = $"{personSongCount} song(s) from {sampledPerson}, no distinct album";
                            isNewFolder = false;
                            return Path.Combine(peopleFolder, Constants.SinglesDir);
                        }
                    }
                    else
                    {
                        reason = $"{personSongCount} song(s) from {sampledPerson}, below People threshold";
                        isNewFolder = false;
                        return Path.Combine(_libraryPath, Constants.MusivDir, "Akira The Don", Constants.SinglesDir);
                    }
                }
                else
                {
                    // No sampled person listed, use Singles
                    reason = "No sampled person listed";
                    isNewFolder = false;
                    return Path.Combine(_libraryPath, Constants.MusivDir, "Akira The Don", "Singles");
                }
            }

            // Genres-based rules (after artist checks)
            if (!track.Genres.Equals(Constants.MissingTag) && track.Genres.IndexOf(Constants.MusivDir, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                reason = "Genre is Musivation";
                isNewFolder = false;
                return Path.Combine(_libraryPath, Constants.MusivDir);
            }

            if (!track.Genres.Equals(Constants.MissingTag) && track.Genres.IndexOf(Constants.MotivDir, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                reason = "Genre is Motivation";
                isNewFolder = false;
                return Path.Combine(_libraryPath, Constants.MotivDir);
            }

            string primaryArtist2 = track.PrimaryArtist;
            string artistsRoot = Path.Combine(_libraryPath, Constants.ArtistsDir);
            string wantedName = SanitiseFolderName(primaryArtist2);
            string onDiskName = MusicIntegrator.FindDirNameIgnoreCase(artistsRoot, wantedName);
            bool scanAheadNewFolder = newArtistFolders.Contains(primaryArtist2);
            // The tag decides the folder name (David 2026-11-11): the batch's most frequent tag spelling (this track's own
            // tag when scan-ahead has none). An existing folder differing only by case is renamed to it at integrate time.
            string canonName = (_scanAheadSpellings != null && _scanAheadSpellings.TryGetValue(primaryArtist2, out string canon))
                ? SanitiseFolderName(canon) : wantedName;
            string folderName = canonName;
            string renameNote = "";
            if (onDiskName != null && !onDiskName.Equals(canonName, StringComparison.Ordinal) && onDiskName.Equals(canonName, StringComparison.OrdinalIgnoreCase))
                renameNote = $" [folder '{onDiskName}' will be renamed to '{canonName}' (case only)]";
            else if (onDiskName != null)
                folderName = onDiskName;
            string artistFolder = Path.Combine(artistsRoot, folderName);

            // Artist folder exists OR scan-ahead says this artist needs a new one
            bool existingArtistFolder = onDiskName != null;
            bool routeToArtists = existingArtistFolder || scanAheadNewFolder;
            if (routeToArtists)
            {
                string scanNote = scanAheadNewFolder ? " [new via scan-ahead]" : "";
                scanNote += renameNote;
                // This track's own tag spells the artist differently from the folder (a minority spelling in the batch):
                // list it for review; the tag is never edited silently.
                if (!folderName.Equals(wantedName, StringComparison.Ordinal))
                    scanNote += $" [review: tag '{primaryArtist2}' but folder '{folderName}'; tag not edited, fix the tag]";

                if (!track.Album.Equals(Constants.MissingTag))
                {
                    // Count songs from this album (holistic: library + batch combined).
                    // Self-titled albums (album == artist name) are treated as real albums; the
                    // count determines routing just like any other album.
                    int albumCount = _counter.CountAlbumSongs(primaryArtist2, track.Album);
                    if (RulesEngine.UsesAlbumSubfolder(albumCount))
                    {
                        reason = $"Artist folder{scanNote}; {albumCount} songs from album -> album subfolder";
                        isNewFolder = scanAheadNewFolder;
                        // Use actual on-disk folder name (may have a version suffix like "Life After Death (2014 Remastered Edition)")
                        string actualAlbumFolder = _counter.FindAlbumFolder(artistFolder, track.Album)
                                                  ?? Path.Combine(artistFolder, SanitiseFolderName(track.Album));
                        return actualAlbumFolder;
                    }
                    else
                    {
                        reason = albumCount == 0
                            ? $"Artist folder{scanNote}; no songs from album -> Singles/"
                            : $"Artist folder{scanNote}; only {albumCount} {(albumCount == 1 ? "song" : "songs")} from album -> Singles/";
                        isNewFolder = scanAheadNewFolder;
                        return Path.Combine(artistFolder, "Singles");
                    }
                }
                else
                {
                    reason = $"Artist folder{scanNote}; no album tag -> Singles/";
                    isNewFolder = scanAheadNewFolder;
                    return Path.Combine(artistFolder, "Singles");
                }
            }

            // Compilation album: 3+ distinct primary artists on same album in batch -> Compilations/{album}/
            if (compilationAlbums != null &&
                !string.IsNullOrEmpty(track.Album) &&
                !track.Album.Equals(Constants.MissingTag, StringComparison.OrdinalIgnoreCase) &&
                compilationAlbums.Contains(track.Album))
            {
                reason = $"Compilation album (3+ distinct artists in batch); no artist folder -> Compilations/{track.Album}/";
                isNewFolder = false;
                return Path.Combine(_libraryPath, Constants.CompilationsDir, SanitiseFolderName(track.Album));
            }

            int scanBatch = (_scanAheadBatchCounts != null && _scanAheadBatchCounts.TryGetValue(primaryArtist2, out int sb2)) ? sb2 : 0;
            int scanMisc = (_scanAheadMiscCounts != null && _scanAheadMiscCounts.TryGetValue(primaryArtist2, out int sm)) ? sm : 0;
            int scanSources = (_scanAheadSourcesCounts != null && _scanAheadSourcesCounts.TryGetValue(primaryArtist2, out int ss)) ? ss : 0;
            int scanTotal = scanBatch + scanMisc + scanSources;
            reason = scanTotal > 0
                ? $"no artist folder; {scanBatch} in batch + {scanMisc + scanSources} in library = {scanTotal} total, below threshold 3 -> Misc"
                : "No artist folder found in library";
            isNewFolder = false;
            return Path.Combine(_libraryPath, Constants.MiscDir);
        }
    }
}
