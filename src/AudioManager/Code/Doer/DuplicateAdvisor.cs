using AudioManager.Code.Modules;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DupData = AudioManager.MusicIntegrator.DupData;
using DupFacts = AudioManager.MusicIntegrator.DupFacts;
using LogEntry = AudioManager.MusicIntegrator.LogEntry;
using ScannedFile = AudioManager.MusicIntegrator.ScannedFile;

namespace AudioManager
{
    /// <summary>
    /// Duplicate advice for the pre-scan: the facts a duplicate is judged on, the one recommendation rule and the
    /// DupData shown to the user, plus the review itself: in-batch marking, the per-duplicate prompt (or the
    /// manifest and no-input safety path) and the menu key reader. Roots, the no-input flag and the manifest are
    /// read live through delegates, and album counts go through the LibraryCounter that MusicIntegrator shares,
    /// so nothing is held as a stale copy. MusicIntegrator forwards to these members.
    /// </summary>
    internal sealed class DuplicateAdvisor
    {
        private readonly Func<IntegrationRoots> _rootsSource;
        private readonly LibraryCounter _counter;
        private readonly Func<bool> _noInputSource;
        private readonly Func<IntegrationManifest> _manifestSource;

        /// <summary>Roots, the no-input flag and the manifest are read live on every use, never copied.</summary>
        internal DuplicateAdvisor(Func<IntegrationRoots> roots, LibraryCounter counter, Func<bool> noInput, Func<IntegrationManifest> manifest)
        {
            _rootsSource = roots;
            _counter = counter;
            _noInputSource = noInput;
            _manifestSource = manifest;
        }

        private IntegrationRoots _roots => _rootsSource();
        private bool noInput => _noInputSource();
        private IntegrationManifest _manifest => _manifestSource();
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

        /// <summary>
        /// Step 2: presents all in-batch duplicates for review before routing begins.
        /// Returns false if the user quits (constructor must return immediately without routing).
        /// </summary>
        internal bool RunDuplicateReview(List<ScannedFile> scannedFiles, List<LogEntry> logEntries)
        {
            var duplicateFiles = scannedFiles.Where(sf => sf.Duplicate != null).ToList();
            if (duplicateFiles.Count == 0) return true;

            Console.WriteLine($"\nReviewing {duplicateFiles.Count} duplicate(s)...");
            foreach (var sf in duplicateFiles)
            {
                if (!PresentDuplicateAndDecide(sf))
                {
                    sf.LogEntry.Status = "quit";
                    logEntries.Add(sf.LogEntry);
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// Flags ScannedFile entries where the same normalised artist+title appears more than once
        /// in the batch. Sets InBatchDuplicate = true on all files sharing a key. Non-blocking.
        /// </summary>
        internal static void MarkInBatchDuplicates(List<ScannedFile> scannedFiles)
        {
            var keyGroups = new Dictionary<string, List<ScannedFile>>(StringComparer.Ordinal);
            foreach (var sf in scannedFiles.Where(f => f.IsReadable && f.Track != null))
            {
                string key = sf.Track.PrimaryArtist.ToLowerInvariant() + "\0" + MusicIntegrator.NormaliseTitleForDedup(sf.Track.Title).ToLowerInvariant();
                if (!keyGroups.ContainsKey(key)) keyGroups[key] = new List<ScannedFile>();
                keyGroups[key].Add(sf);
            }
            foreach (var group in keyGroups.Values.Where(g => g.Count > 1))
                foreach (var sf in group)
                    if (group.Any(o => !ReferenceEquals(o, sf) && !LengthsDiffer(sf.LengthSec, o.LengthSec)))
                        sf.InBatchDuplicate = true;
        }

        /// <summary>
        /// Presents one duplicate to the user and collects their D/L/K/Q decision.
        /// Sets sf.Duplicate.Decision. Returns false if user pressed Q, true otherwise.
        /// Internal (not private) so tests can exercise the noInput/manifest-override path directly.
        /// </summary>
        internal bool PresentDuplicateAndDecide(ScannedFile sf)
        {
            var dup = sf.Duplicate;
            Track track = sf.Track;

            string dupProposed = dup.RecommendedKey == 'L'
                ? "Delete library copy, keep new file"
                : dup.RecommendedKey == 'D'
                    ? "Delete NewMusic copy, keep library"
                    : "No version preference";

            Console.WriteLine();
            Console.WriteLine("===========================================================================");
            Console.WriteLine("  DUPLICATE FOUND");
            Console.WriteLine("===========================================================================");
            Console.WriteLine();
            Console.WriteLine($"  In AudioMirror: {dup.RelMirrorPath}");
            if (!string.IsNullOrEmpty(dup.MirrorTrack))
                Console.WriteLine($"  Track:          {dup.MirrorTrack}");
            if (!string.IsNullOrEmpty(dup.MirrorAlbum))
                Console.WriteLine($"  Album:          {dup.MirrorAlbum}");
            Console.WriteLine();
            Console.WriteLine($"  New file:   {dup.DisplayNewFilename}");
            Console.WriteLine($"  Album:      {track.Album}");
            Console.WriteLine();
            Console.WriteLine($"  Proposed:   {dupProposed}");
            if (!string.IsNullOrEmpty(dup.DupReason))
                Console.WriteLine($"  Reason:     {dup.DupReason}");
            Console.WriteLine();
            Console.WriteLine("---------------------------------------------------------------------------");
            Console.WriteLine(dup.OptionsLine);
            Console.WriteLine("---------------------------------------------------------------------------");

            if (noInput)
            {
                // Review-stage resolution wins over the exe's own recommendation on a real
                // run, carried through the same --manifest mechanism as accept/decline. A duplicate left
                // untouched in Review has no manifest entry for it, so this falls through to
                // the exact same auto-accept-recommendation behavior as before - unresolved
                // batches stay safe by construction, not by a second code path.
                char manifestDecision = _manifest?.GetDupResolution(
                    Path.GetFileName(sf.SourcePath), track.Artists, track.Title, MusicIntegrator.RelToNewMusic(sf.SourcePath, _roots.NewMusic)) ?? '\0';
                if (manifestDecision != '\0')
                {
                    dup.Decision = manifestDecision;
                    Console.WriteLine($"  [MANIFEST] {manifestDecision} (user-resolved in review)");
                    Console.WriteLine();
                    return true;
                }

                char autoDecision = dup.RecommendedKey != '\0' ? dup.RecommendedKey : 'K';
                // Safety: L replaces the library copy and D sends the new file to the Recycle Bin;
                // no human confirmed either, so an unattended run downgrades both to K (Keep Both,
                // non-destructive). Only an explicit manifest resolution (above) can choose D or L.
                if (autoDecision == 'L' || autoDecision == 'D')
                {
                    Console.WriteLine($"  [SAFETY] {autoDecision} not auto-accepted under no-input: {Path.GetFileName(sf.SourcePath)} downgraded to K (keep both)");
                    autoDecision = 'K';
                }
                dup.Decision = autoDecision;
                string autoReason = dup.RecommendedKey == '\0'
                    ? "K (no recommendation - defaulting to Keep Both)"
                    : dup.RecommendedKey != 'K' && autoDecision == 'K'
                        ? $"K ({dup.RecommendedKey} downgraded - no-input never deletes without an explicit resolution)"
                        : $"{autoDecision} (auto-accepted recommended)";
                Console.WriteLine($"  [AUTO] {autoReason}");
                Console.WriteLine();
                return true;
            }

            while (true)
            {
                var key = ReadMenuKey();
                if (key == ConsoleKey.D) { dup.Decision = 'D'; Console.WriteLine(); return true; }
                else if (key == ConsoleKey.L) { dup.Decision = 'L'; Console.WriteLine(); return true; }
                else if (key == ConsoleKey.K) { dup.Decision = 'K'; Console.WriteLine(); return true; }
                else if (key == ConsoleKey.Q) { dup.Decision = 'Q'; Console.WriteLine(); return false; }
            }
        }

        /// <summary>
        /// Reads a menu key using ReadLine so the user must press Enter to confirm.
        /// Prevents right-click paste in Windows Terminal from silently triggering actions.
        /// </summary>
        internal ConsoleKey ReadMenuKey() => ReadMenuKeyFrom(Console.In, Console.Out);

        /// <summary>Testable core. End of input (closed stdin) quits instead of re-prompting forever.</summary>
        internal static ConsoleKey ReadMenuKeyFrom(TextReader reader, TextWriter writer)
        {
            while (true)
            {
                writer.Write("> ");
                string line = reader.ReadLine();
                if (line == null) { writer.WriteLine(); writer.WriteLine("[INPUT] stdin closed, quitting."); return ConsoleKey.Q; }
                string input = line.Trim().ToUpperInvariant();
                switch (input)
                {
                    case "Y": return ConsoleKey.Y;
                    case "N": return ConsoleKey.N;
                    case "D": return ConsoleKey.D;
                    case "L": return ConsoleKey.L;
                    case "K": return ConsoleKey.K;
                    case "Q": return ConsoleKey.Q;
                }
            }
        }
    }
}
