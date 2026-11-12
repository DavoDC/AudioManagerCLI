using AudioManager.Code.Modules;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ScannedFile = AudioManager.MusicIntegrator.ScannedFile;

namespace AudioManager
{
    /// <summary>
    /// Projected LibChecker for dry runs: builds the post-integration tag list from the current library and the
    /// planned moves, deletes and Misc migrations, then runs LibChecker on that projection. Reads run state live
    /// through delegates (roots, scan-ahead result, Misc migration candidates) and routes through DestinationRouter,
    /// so it never holds a stale copy. MusicIntegrator forwards to these members.
    /// </summary>
    internal sealed class ProjectedValidator
    {
        private readonly Func<IntegrationRoots> _rootsSource;
        private readonly Func<ScanAheadResult> _scanSource;
        private readonly Func<Dictionary<string, List<string>>> _miscMigrationCandidatesSource;
        private readonly DestinationRouter _router;

        /// <summary>Roots, scan-ahead result and Misc migration candidates are read live on every use, never copied.</summary>
        internal ProjectedValidator(Func<IntegrationRoots> roots, Func<ScanAheadResult> scan, Func<Dictionary<string, List<string>>> miscMigrationCandidates, DestinationRouter router)
        {
            _rootsSource = roots;
            _scanSource = scan;
            _miscMigrationCandidatesSource = miscMigrationCandidates;
            _router = router;
        }

        private IntegrationRoots _roots => _rootsSource();
        private HashSet<string> _compilationAlbums => _scanSource().CompilationAlbums;
        private Dictionary<string, List<string>> _miscMigrationCandidates => _miscMigrationCandidatesSource();

        /// <summary>
        /// Pure function: builds the projected post-integration tag list.
        /// currentTags: current library state. removals: RelPaths to remove. additions: new TrackTags.
        /// Extracted as a static so it is unit-testable without filesystem access.
        /// </summary>
        internal static List<TrackTag> BuildProjection(
            IEnumerable<TrackTag> currentTags,
            IEnumerable<TrackTag> additions,
            ISet<string> removals)
        {
            var projected = currentTags
                .Where(t => !removals.Contains(t.RelPath))
                .ToList();
            projected.AddRange(additions);
            return projected;
        }

        /// <summary>
        /// Loads current library tags, builds the projected post-integration tag list
        /// (models all moves/deletes/misc-migrations), then runs LibChecker on the projection
        /// and prints the result in a dry-run section.
        /// Silently skips if AudioMirror is unavailable.
        /// </summary>
        internal void RunProjectedLibChecker(List<ScannedFile> scannedFiles, HashSet<string> newArtistFolders)
        {
            Console.WriteLine("\n===========================================================================");
            Console.WriteLine("Projected LibChecker (Dry Run)");
            Console.WriteLine("===========================================================================");

            List<TrackTag> currentTags;
            try
            {
                var parser = new Parser(_roots.Mirror);
                currentTags = parser.audioTags;
            }
            catch (Exception ex)
            {
                Console.WriteLine($" - SKIP: could not load current library tags: {ex.Message}");
                return;
            }

            // Removals: L-decided library copies + Misc migration sources
            var removals = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var sf in scannedFiles)
            {
                if (sf.Duplicate != null && sf.Duplicate.Decision == 'L')
                    removals.Add(MusicIntegrator.GetMirrorRelPath(sf.Duplicate.DuplicatePath));
            }

            foreach (var kvp in _miscMigrationCandidates)
            {
                foreach (var xmlPath in kvp.Value)
                    removals.Add(MusicIntegrator.GetMirrorRelPath(xmlPath));
            }

            // Additions: all files routed to the library (not D-decided) + Misc migration destinations
            var additions = new List<TrackTag>();

            foreach (var sf in scannedFiles)
            {
                if (!sf.IsReadable) continue;
                if (sf.Duplicate?.Decision == 'D') continue; // deleted from NewMusic, library unchanged

                Track track = sf.Track;
                if (track.Title.Equals(Constants.MissingTag) || track.Artists.Equals(Constants.MissingTag)) continue;

                string destDir = _router.GetDestDir(track, newArtistFolders, _compilationAlbums, out _, out _);
                string sanitisedArtists = Reflector.SanitiseFilename(track.Artists);
                string sanitisedTitle = Reflector.SanitiseFilename(track.Title);
                string destFilename = sanitisedArtists + " - " + sanitisedTitle + ".xml";

                // RelPath: strip AudioFolderPath prefix, append filename - gives "\Artists\...\file.xml"
                string destRelDir = destDir.StartsWith(_roots.Library, StringComparison.OrdinalIgnoreCase)
                    ? destDir.Substring(_roots.Library.Length)
                    : "\\" + Path.GetFileName(destDir);
                string relPath = destRelDir + Path.DirectorySeparatorChar + destFilename;

                var synthesized = new TrackTag(relPath,
                    track.Title, track.Artists, track.Album,
                    track.Year ?? Constants.MissingTag,
                    track.TrackNumber ?? "0",
                    track.Genres ?? Constants.MissingTag,
                    track.Length ?? "00:00:00.0000000",
                    "1",     // AlbumCoverCount: assume art present
                    "True",  // Compilation: TagFixer sets TCMP=1
                    "0", "0" // CoverWidth/Height: unknown (dimensions check disabled)
                );
                additions.Add(synthesized);
            }

            // Misc migration destinations: synthesize tag at Artists/{artist}/Singles/ location
            foreach (var kvp in _miscMigrationCandidates)
            {
                string artist = kvp.Key;
                string destRelDir = "\\" + Constants.ArtistsDir + "\\" + MusicIntegrator.SanitiseFolderName(artist) + "\\" + Constants.SinglesDir;
                foreach (var xmlPath in kvp.Value)
                {
                    string srcRelPath = MusicIntegrator.GetMirrorRelPath(xmlPath);
                    // Find existing tag to copy metadata from
                    var srcTag = currentTags.FirstOrDefault(
                        t => string.Equals(t.RelPath, srcRelPath, StringComparison.OrdinalIgnoreCase));
                    if (srcTag == null) continue;

                    string filename = Path.GetFileName(xmlPath);
                    string newRelPath = destRelDir + "\\" + filename;
                    var migratedTag = new TrackTag(newRelPath,
                        srcTag.Title, srcTag.Artists, srcTag.Album, srcTag.Year,
                        srcTag.TrackNumber, srcTag.Genres, srcTag.Length,
                        srcTag.AlbumCoverCount, srcTag.Compilation,
                        srcTag.CoverWidth, srcTag.CoverHeight);
                    additions.Add(migratedTag);
                }
            }

            var projected = BuildProjection(currentTags, additions, removals);
            Console.WriteLine($" - Projected library: {currentTags.Count} current, -{removals.Count} removals, +{additions.Count} additions = {projected.Count} projected");
            new LibChecker(projected);
        }
    }
}
