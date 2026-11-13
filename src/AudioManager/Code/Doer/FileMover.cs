using AudioManager.Code.Modules;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using File = System.IO.File;
using LogEntry = AudioManager.MusicIntegrator.LogEntry;
using ScannedFile = AudioManager.MusicIntegrator.ScannedFile;

namespace AudioManager
{
    /// <summary>
    /// Step 3b: routes each file to its library destination. A dry run collects the output blocks for the caller to print
    /// sorted by destination; a real run moves each file, then commits or rolls back its replacement ledger entry, and
    /// removes any folders a failed move created. MusicIntegrator forwards RouteAllFiles to this class.
    /// </summary>
    internal sealed class FileMover
    {
        private readonly Func<IntegrationRoots> _rootsSource;
        private readonly Func<bool> _dryRunSource;
        private readonly Func<ScanAheadResult> _scanSource;
        private readonly DestinationRouter _router;
        private readonly ReplacementLedger _ledger;

        /// <summary>Roots, dry-run flag and scan-ahead result are read live on every use, never copied.</summary>
        internal FileMover(Func<IntegrationRoots> roots, Func<bool> dryRun, Func<ScanAheadResult> scan, DestinationRouter router, ReplacementLedger ledger)
        {
            _rootsSource = roots;
            _dryRunSource = dryRun;
            _scanSource = scan;
            _router = router;
            _ledger = ledger;
        }

        private IntegrationRoots _roots => _rootsSource();
        private bool dryRun => _dryRunSource();
        private HashSet<string> _compilationAlbums => _scanSource().CompilationAlbums;
        private HashSet<string> _dryReplacedLibPaths => _ledger._dryReplacedLibPaths;
        private Dictionary<string, string> _lStagedBySource => _ledger._lStagedBySource;

        /// <summary>
        /// Step 3b: routes all files. D-decided duplicates and blocked L-decided duplicates were already handled
        /// in ExecuteDuplicateDecisions (3a); unblocked L files (real and dry-run) and K files fall through to routing here.
        /// In dry-run mode, routing output blocks are collected and returned for the caller to print
        /// sorted by destination path; otherwise returns null.
        /// </summary>
        internal List<(string destPath, string block)> RouteAllFiles(List<ScannedFile> scannedFiles,
            HashSet<string> newArtistFolders, List<LogEntry> logEntries, ref int movedCount, ref int skippedCount)
        {
            var dryRunRoutingOutputs = dryRun ? new List<(string destPath, string block)>() : null;
            var plannedDests = dryRun ? new HashSet<string>(StringComparer.OrdinalIgnoreCase) : null;
            foreach (var sf in scannedFiles)
            {
                var entry = sf.LogEntry;

                if (!sf.IsReadable)
                {
                    Console.WriteLine();
                    Console.WriteLine("===========================================================================");
                    Console.WriteLine($"[ERROR] Skipping unreadable file: {Path.GetFileName(sf.SourcePath)}");
                    Console.WriteLine("===========================================================================");
                    Console.WriteLine($"Full path: {sf.SourcePath}");
                    Console.WriteLine($"Error details: {sf.ReadError}");
                    Console.WriteLine("The rest of the batch will continue.\n");
                    entry.Status = "error";
                    entry.Detail = $"unreadable: {sf.ReadError}";
                    logEntries.Add(entry); skippedCount++;
                    _ledger.RollbackIfStaged(sf);
                    continue;
                }

                // Skip files fully resolved during duplicate execution (3a)
                if (sf.Duplicate?.SkipRouting == true) continue;

                List<string> createdDirs = null; // folders this file's move created; removed again if the move fails
                try
                {
                    Track track = sf.Track;
                    string primaryArtist = track.PrimaryArtist;

                    // Skip if un-routable (tags are already clean from TagFixer)
                    if (track.Title.Equals(Constants.MissingTag) || track.Artists.Equals(Constants.MissingTag) || primaryArtist.Equals(Constants.MissingTag))
                    {
                        Console.WriteLine($"- Skipped '{Path.GetFileName(sf.SourcePath)}': missing required tag");
                        entry.Status = "skipped"; entry.Detail = "missing required tag";
                        logEntries.Add(entry); skippedCount++;
                        _ledger.RollbackIfStaged(sf);
                        continue;
                    }

                    // Build destination filename
                    string destFilename = MusicIntegrator.BuildDestFilename(track);

                    // Determine destination directory and whether it requires creating a new artist folder
                    string destDir = _router.GetDestDir(track, newArtistFolders, _compilationAlbums, out string reason, out bool isNewFolder);
                    entry.Reason = reason;
                    entry.IsNewFolder = isNewFolder;
                    entry.InBatchDuplicate = sf.InBatchDuplicate;
                    entry.CompilationAlbum = _compilationAlbums != null
                        && !string.IsNullOrEmpty(track.Album)
                        && _compilationAlbums.Contains(track.Album);

                    string destPath = Path.Combine(destDir, destFilename);

                    // Compute relative destination path for display
                    string relativeDest = destPath;
                    if (destPath.StartsWith(_roots.Library, StringComparison.OrdinalIgnoreCase))
                    {
                        int startIndex = _roots.Library.Length;
                        if (startIndex <= destPath.Length)
                        {
                            relativeDest = destPath.Substring(startIndex);
                            if (relativeDest.StartsWith("\\", StringComparison.Ordinal) || relativeDest.StartsWith("/", StringComparison.Ordinal))
                                relativeDest = relativeDest.Substring(1);
                        }
                    }

                    entry.Destination = relativeDest;
                    string routeSummary = _router.GetRouteSummary(relativeDest);

                    // Album suffix shown on header line for quick context. New-folder flag shown on Route line.
                    string albumSuffix = (!string.IsNullOrEmpty(track.Album) && !track.Album.Equals(Constants.MissingTag))
                        ? $" [{track.Album}]" : "";
                    string routeNewNote = isNewFolder ? " *" : "";

                    // Dry run: a destination occupied only by the library copy this run replaces counts as free (staged in the real run)
                    // An L slot owned by another replacement is reserved (staged in the real run, planned in dry run): this file is a duplicate here.
                    if (_ledger.IsReservedForOtherReplacement(destPath, sf.SourcePath)
                        || MusicIntegrator.IsDestBlocked(destPath, dryRun && _dryReplacedLibPaths.Contains(destPath) ? destPath : null, plannedDests))
                    {
                        Console.WriteLine(MusicIntegrator.FormatSkipLine(track.Artists, track.Title));
                        Console.WriteLine();
                        entry.Status = dryRun ? "would-skip" : "skipped"; entry.Detail = "already exists at destination";
                        logEntries.Add(entry); skippedCount++;
                        _ledger.RollbackIfStaged(sf);
                    }
                    else if (dryRun)
                    {
                        var sb = new StringBuilder();
                        if (sf.InBatchDuplicate)
                            sb.AppendLine($"[WARN: IN-BATCH DUPLICATE] {Path.GetFileName(sf.SourcePath)}");
                        sb.AppendLine(MusicIntegrator.FormatAutoLine(track.Artists, track.Title, albumSuffix));
                        foreach (var change in entry.TagChanges)
                            sb.AppendLine($" > {change}");
                        sb.AppendLine($" Route: {routeSummary}{routeNewNote}");
                        sb.AppendLine($" Reason: {reason}");
                        sb.AppendLine($" Path: {relativeDest.Replace('\\', '/')}");
                        sb.AppendLine();
                        dryRunRoutingOutputs.Add((relativeDest, sb.ToString()));
                        plannedDests.Add(destPath);
                        entry.Status = "would-move";
                        logEntries.Add(entry); movedCount++;
                    }
                    else
                    {
                        if (sf.InBatchDuplicate)
                            Console.WriteLine($"[WARN: IN-BATCH DUPLICATE] {Path.GetFileName(sf.SourcePath)}");
                        createdDirs = CreateDirectoryTracked(destDir);
                        if (_lStagedBySource.TryGetValue(sf.SourcePath, out string landingLibPath))
                            ReplaceGuard.MarkLanding(landingLibPath, destPath);
                        File.Move(sf.SourcePath, destPath);
                        _ledger.CommitIfStaged(sf);
                        movedCount++;
                        Console.WriteLine(MusicIntegrator.FormatAutoLine(track.Artists, track.Title, albumSuffix));
                        foreach (var change in entry.TagChanges)
                            Console.WriteLine($" > {change}");
                        Console.WriteLine($" Route: {routeSummary}{routeNewNote}");
                        Console.WriteLine($" Reason: {reason}");
                        Console.WriteLine($" Path: {relativeDest.Replace('\\', '/')}");
                        Console.WriteLine();
                        entry.Status = "moved";
                        logEntries.Add(entry);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine();
                    Console.WriteLine("===========================================================================");
                    Console.WriteLine($"[ERROR] Skipping file that failed to route: {Path.GetFileName(sf.SourcePath)}");
                    Console.WriteLine("===========================================================================");
                    Console.WriteLine($"Full path: {sf.SourcePath}");
                    Console.WriteLine($"Error details: {ex.Message}");
                    Console.WriteLine("The rest of the batch will continue.\n");
                    entry.Status = "error";
                    entry.Detail = $"routing failed: {ex.Message}";
                    logEntries.Add(entry); skippedCount++;
                    _ledger.RollbackIfStaged(sf);
                    RemoveEmptyCreatedDirs(createdDirs);
                }
            }
            return dryRunRoutingOutputs;
        }

        /// <summary>Creates dir and any missing parents. Returns the folders this call created, deepest first.</summary>
        internal static List<string> CreateDirectoryTracked(string dir)
        {
            var created = new List<string>();
            for (string d = dir; d != null && !Directory.Exists(d); d = Path.GetDirectoryName(d))
                created.Add(d);
            Directory.CreateDirectory(dir);
            return created;
        }

        /// <summary>Removes folders a failed move left behind: only the ones created by its CreateDirectoryTracked call, and only while empty.</summary>
        internal static void RemoveEmptyCreatedDirs(List<string> created)
        {
            if (created == null) return;
            foreach (string d in created)
            {
                try
                {
                    if (Directory.Exists(d) && Directory.GetFileSystemEntries(d).Length == 0)
                        Directory.Delete(d);
                }
                catch (Exception) { /* best effort: a folder we cannot remove is left as it is */ }
            }
        }
    }
}
