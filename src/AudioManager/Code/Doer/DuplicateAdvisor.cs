using AudioManager.Code.Modules;
using System;
using System.IO;
using System.Linq;
using DupData = AudioManager.MusicIntegrator.DupData;
using DupFacts = AudioManager.MusicIntegrator.DupFacts;

namespace AudioManager
{
    /// <summary>
    /// Duplicate advice for the pre-scan: the facts a duplicate is judged on, the one recommendation rule and the
    /// DupData shown to the user. Roots are read live through a delegate, and album counts go through the
    /// LibraryCounter that MusicIntegrator shares, so nothing is held as a stale copy. MusicIntegrator forwards to these members.
    /// </summary>
    internal sealed class DuplicateAdvisor
    {
        private readonly Func<IntegrationRoots> _rootsSource;
        private readonly LibraryCounter _counter;

        /// <summary>Roots are read live on every use, never copied.</summary>
        internal DuplicateAdvisor(Func<IntegrationRoots> roots, LibraryCounter counter)
        {
            _rootsSource = roots;
            _counter = counter;
        }

        private IntegrationRoots _roots => _rootsSource();
        private MirrorIndex _mirror => new MirrorIndex(_roots);

        /// <summary>True when both lengths are known and differ by more than the tolerance (remix, edit, other take).</summary>
        internal static bool LengthsDiffer(double a, double b) =>
            a > 0 && b > 0 && Math.Abs(a - b) > MusicIntegrator.DistinctLengthToleranceSec;

        /// <summary>
        /// The one duplicate recommendation. Order: distinct recording (keep both), richer metadata,
        /// same album, deluxe, single, compilation, then the album-size rule (tie keeps incoming).
        /// </summary>
        internal static void RecommendDuplicate(DupFacts f, out char key, out string reason)
        {
            key = '\0'; reason = "";
            if (LengthsDiffer(f.IncomingLengthSec, f.LibraryLengthSec))
            {
                reason = $"Different recording: incoming is {f.IncomingLengthSec:F0}s, library copy is {f.LibraryLengthSec:F0}s - keep both";
                return;
            }
            if (f.LibraryArtists > 0 && f.IncomingArtists > f.LibraryArtists)
            {
                key = 'L';
                reason = $"Incoming has richer metadata ({f.IncomingArtists} artists vs {f.LibraryArtists} in library) - keep incoming";
                return;
            }
            if (f.SameAlbum)
            {
                key = 'D'; reason = $"Same song from same album ('{f.NewAlbum}') - already in library";
            }
            else if (f.NewIsAlbum && MusicIntegrator.IsDeluxeVersionOf(f.NewAlbum, f.LibraryAlbum))
            {
                key = 'L'; reason = $"New file album '{f.NewAlbum}' is a deluxe/expanded version of library album '{f.LibraryAlbum}' - deluxe preferred";
            }
            else if (f.NewIsAlbum && MusicIntegrator.IsDeluxeVersionOf(f.LibraryAlbum, f.NewAlbum))
            {
                key = 'D'; reason = $"Library album '{f.LibraryAlbum}' is a deluxe/expanded version of new file album '{f.NewAlbum}' - keep library deluxe";
            }
            else if (f.LibraryIsSingle && f.NewIsAlbum)
            {
                key = 'L'; reason = $"Library has single; new file is from album '{f.NewAlbum}' - album preferred";
            }
            else if (f.LibraryIsCompilation && f.NewIsAlbum)
            {
                key = 'L'; reason = $"Library copy is from a compilation; new file is from artist album '{f.NewAlbum}'";
            }
            else if (f.NewIsAlbum && f.IncomingAlbumTracks > 0 && f.LibraryAlbumTracks > 0)
            {
                key = f.IncomingAlbumTracks >= f.LibraryAlbumTracks ? 'L' : 'D';
                string sizeNote = f.IncomingAlbumTracks > f.LibraryAlbumTracks ? "larger album preferred"
                    : f.IncomingAlbumTracks < f.LibraryAlbumTracks ? "library album is larger, keep library"
                    : "same size, incoming copy kept";
                reason = $"New album '{f.NewAlbum}' has {f.IncomingAlbumTracks} tracks, library album '{f.LibraryAlbum}' has {f.LibraryAlbumTracks} tracks - {sizeNote}";
            }
            else
            {
                // GUI-54: a row with no key still explains itself, so JSON and console agree.
                reason = "No rule decides this duplicate - keep both or choose manually";
            }
        }

        /// <summary>Audio length in seconds from the tags, or 0 when unreadable.</summary>
        internal static double ReadLengthSeconds(string path)
        {
            try
            {
                using (TagLib.File f = TagLib.File.Create(path))
                    return f.Properties.Duration.TotalSeconds;
            }
            catch { return 0; }
        }

        /// <summary>Length (seconds) and artist count of a mirror XML; 0 when missing.</summary>
        internal static void ReadMirrorFacts(string xmlPath, out double lengthSec, out int artistCount)
        {
            lengthSec = 0; artistCount = 0;
            try
            {
                var doc = new System.Xml.XmlDocument();
                doc.Load(xmlPath);
                string len = doc.SelectSingleNode("//Length")?.InnerText;
                if (TimeSpan.TryParse(len, System.Globalization.CultureInfo.InvariantCulture, out TimeSpan ts)) lengthSec = ts.TotalSeconds;
                string artists = doc.SelectSingleNode("//Artists")?.InnerText;
                if (!string.IsNullOrEmpty(artists))
                    artistCount = Track.ProcessProperty(artists).Count(x => !string.IsNullOrWhiteSpace(x));
            }
            catch { /* unknown facts stand the new rules down */ }
        }

        /// <summary>
        /// Builds a DupData object for a duplicate found during pre-scan.
        /// Computes all display strings and the [L]/[D] recommendation.
        /// </summary>
        internal DupData BuildDupData(string sourcePath, Track track, string duplicatePath)
        {
            string libraryFilePath = _mirror.DeriveLibraryPathFromMirrorPath(duplicatePath);
            string relLibraryPath = libraryFilePath.StartsWith(_roots.Library, StringComparison.OrdinalIgnoreCase)
                ? libraryFilePath.Substring(_roots.Library.Length).TrimStart('\\', '/')
                : libraryFilePath;
            string relMirrorPath = duplicatePath.StartsWith(_roots.Mirror, StringComparison.OrdinalIgnoreCase)
                ? duplicatePath.Substring(_roots.Mirror.Length).TrimStart('\\', '/')
                : Path.GetFileName(duplicatePath);
            string relNewPath = Path.GetFileName(sourcePath);

            bool libraryIsSingle = relLibraryPath.IndexOf("\\Singles\\", StringComparison.OrdinalIgnoreCase) >= 0
                                || relLibraryPath.StartsWith("Singles\\", StringComparison.OrdinalIgnoreCase);
            bool libraryIsCompilation = relMirrorPath.StartsWith("Compilations\\", StringComparison.OrdinalIgnoreCase)
                                     || relMirrorPath.StartsWith("Compilations/", StringComparison.OrdinalIgnoreCase)
                                     || _mirror.IsAlbumFolderCompilation(duplicatePath);
            bool newIsAlbum = !string.IsNullOrEmpty(track.Album)
                           && !track.Album.Equals(Constants.MissingTag, StringComparison.OrdinalIgnoreCase)
                           && !track.Album.Equals(track.Title, StringComparison.OrdinalIgnoreCase);

            string libraryAlbum = Path.GetFileName(Path.GetDirectoryName(duplicatePath));
            bool sameAlbum = newIsAlbum && libraryAlbum.Equals(track.Album, StringComparison.OrdinalIgnoreCase);
            // Track counts for the size rule; 0 means unknown and the rule does not fire.
            int libraryAlbumTracks = MusicIntegrator.CountLibraryAlbumTracks(libraryFilePath);
            int incomingAlbumTracks = newIsAlbum ? _counter.CountAlbumSongs(track.PrimaryArtist, track.Album) : 0;

            ReadMirrorFacts(duplicatePath, out double libraryLen, out int libraryArtists);
            var facts = new DupFacts
            {
                NewAlbum = track.Album, LibraryAlbum = libraryAlbum, NewIsAlbum = newIsAlbum, SameAlbum = sameAlbum,
                LibraryIsSingle = libraryIsSingle, LibraryIsCompilation = libraryIsCompilation,
                IncomingAlbumTracks = incomingAlbumTracks, LibraryAlbumTracks = libraryAlbumTracks,
                IncomingArtists = Track.ProcessProperty(track.Artists).Count(x => !string.IsNullOrWhiteSpace(x)),
                LibraryArtists = libraryArtists,
                IncomingLengthSec = ReadLengthSeconds(sourcePath), LibraryLengthSec = libraryLen
            };
            RecommendDuplicate(facts, out char recommendedKey, out string dupReason);

            string optD = "[D] Delete NewMusic copy (keep library)";
            string optL = "[L] Delete library copy (keep new file - will be routed in the next step)";
            string optK = "[K] Keep both";
            string optQ = "[Q] Quit";
            if (recommendedKey == 'L') optL += " (recommended)";
            else if (recommendedKey == 'D') optD += " (recommended)";
            string optionsLine = recommendedKey == 'L'
                ? $"  {optL}   {optD}   {optK}   {optQ}"
                : $"  {optD}   {optL}   {optK}   {optQ}";

            string displayNewFilename = $"{track.Artists} - {track.Title}.mp3";
            _mirror.ReadMirrorTrackInfo(duplicatePath, out string mirrorTrack, out string mirrorAlbum);

            return new DupData
            {
                DuplicatePath = duplicatePath,
                LibraryFilePath = libraryFilePath,
                RelLibraryPath = relLibraryPath,
                RelMirrorPath = relMirrorPath,
                RelNewPath = relNewPath,
                DisplayNewFilename = displayNewFilename,
                MirrorTrack = mirrorTrack,
                MirrorAlbum = mirrorAlbum,
                DupReason = dupReason,
                RecommendedKey = recommendedKey,
                OptionsLine = optionsLine
            };
        }

        /// <summary>Maps a recommendation key to the exact phrase the CLI and clients display.</summary>
        internal static string DupRecommendationText(char key)
        {
            switch (key)
            {
                case 'L': return "Delete library copy";
                case 'D': return "Delete new file";
                default: return "Keep both";
            }
        }
    }
}
