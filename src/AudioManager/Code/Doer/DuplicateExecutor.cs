using AudioManager.Code.Modules;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using File = System.IO.File;
using LogEntry = AudioManager.MusicIntegrator.LogEntry;
using ScannedFile = AudioManager.MusicIntegrator.ScannedFile;

namespace AudioManager
{
    /// <summary>
    /// Step 3a: executes the D (delete from NewMusic) and L (replace in library) duplicate decisions before per-file
    /// routing, so their console output is grouped. Writes the replacement ledger (_ledger), which the file mover and
    /// the Misc migration read. MusicIntegrator forwards ExecuteDuplicateDecisions to this class.
    /// </summary>
    internal sealed class DuplicateExecutor
    {
        private readonly Func<IntegrationRoots> _rootsSource;
        private readonly Func<bool> _dryRunSource;
        private readonly Func<ScanAheadResult> _scanSource;
        private readonly DestinationRouter _router;
        private readonly ReplacementLedger _ledger;

        /// <summary>Roots, dry-run flag and scan-ahead result are read live on every use, never copied.</summary>
        internal DuplicateExecutor(Func<IntegrationRoots> roots, Func<bool> dryRun, Func<ScanAheadResult> scan, DestinationRouter router, ReplacementLedger ledger)
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
        private Dictionary<string, string> _lStaged => _ledger._lStaged;
        private Dictionary<string, string> _lStagedBySource => _ledger._lStagedBySource;
        private HashSet<string> _dryReplacedLibPaths => _ledger._dryReplacedLibPaths;
        private HashSet<string> _lDeletedLibraryPaths => _ledger._lDeletedLibraryPaths;

        /// <summary>
        /// Step 3a: executes all duplicate decisions (D delete-from-NewMusic, L replace-in-library) so
        /// their console output is grouped before per-file routing output begins. K files produce no
        /// output here and fall through to routing in Step 3b (RouteAllFiles).
        /// Returns false if a Q decision slipped through (safety net; Step 2 already exits before this
        /// point in the normal case).
        /// </summary>
        internal bool ExecuteDuplicateDecisions(List<ScannedFile> scannedFiles, HashSet<string> newArtistFolders,
            List<LogEntry> logEntries, ref int skippedCount)
        {
            foreach (var sf in scannedFiles.Where(sf2 => sf2.Duplicate != null))
            {
                var entry = sf.LogEntry;
                var dup = sf.Duplicate;
                Track track = sf.Track;
                char decision = dup.Decision;

                // Safety net: Q in Step 2 already exits the constructor, but guard anyway
                if (decision == 'Q')
                {
                    entry.Status = "quit";
                    logEntries.Add(entry);
                    return false;
                }

                if (decision == 'D')
                {
                    // The library duplicate is the only other copy. If it is gone (removed, or staged aside by an
                    // L decision for another source in this run), sending the NewMusic file would leave no copy: keep it.
                    if (!File.Exists(dup.LibraryFilePath) || _lStaged.ContainsKey(dup.LibraryFilePath)
                        || (dryRun && _dryReplacedLibPaths.Contains(dup.LibraryFilePath)))
                    {
                        if (dryRun)
                        {
                            Console.WriteLine($"[DRY RUN] Library copy missing, would keep NewMusic file: {dup.RelNewPath}");
                            entry.Status = "would-keep";
                            entry.Detail = "duplicate (would keep: library copy missing)";
                        }
                        else
                        {
                            Console.WriteLine($"  [ERROR] Kept NewMusic file {dup.RelNewPath}: library copy missing: {dup.RelLibraryPath}");
                            entry.Status = "error";
                            entry.Detail = "duplicate (kept: library copy missing)";
                        }
                        logEntries.Add(entry); skippedCount++;
                        Console.WriteLine();
                        dup.SkipRouting = true;
                        continue;
                    }
                    if (dryRun)
                    {
                        Console.WriteLine($"[DRY RUN] Would send to Recycle Bin from NewMusic: {dup.RelNewPath}");
                        entry.Status = "would-delete";
                        entry.Detail = "duplicate (would delete)";
                    }
                    else
                    {
                        try
                        {
                            RecycleBin.Send(sf.SourcePath);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"  [ERROR] Could not send to Recycle Bin from NewMusic {dup.RelNewPath}: {ex.Message}");
                            entry.Status = "error";
                            entry.Detail = "duplicate (delete failed: " + ex.GetType().Name + ")";
                            logEntries.Add(entry); skippedCount++;
                            Console.WriteLine();
                            dup.SkipRouting = true;
                            continue;
                        }
                        Console.WriteLine($"  Sent to Recycle Bin from NewMusic: {dup.RelNewPath}");
                        entry.Status = "deleted";
                        entry.Detail = "duplicate (deleted)";
                    }
                    logEntries.Add(entry); skippedCount++;
                    Console.WriteLine();
                    dup.SkipRouting = true;
                }
                else if (decision == 'L')
                {
                    if (dryRun && !File.Exists(dup.LibraryFilePath))
                    {
                        Console.WriteLine($"  [WARN] Library file not found: {dup.RelLibraryPath}");
                        entry.Status = "error";
                        entry.Detail = "duplicate (library file not found)";
                        logEntries.Add(entry); skippedCount++;
                        Console.WriteLine();
                        dup.SkipRouting = true;
                    }
                    else if (dryRun && File.Exists(dup.LibraryFilePath + ".replaced.bak"))
                    {
                        // Real run: ReplaceGuard.Stage throws while a stale bak exists. Plain check here, since a dry run never calls Stage or ResolveOne.
                        Console.WriteLine($"  [ERROR] Could not stage library copy {dup.RelLibraryPath}: Refusing to stage: backup already exists: {dup.LibraryFilePath}.replaced.bak");
                        entry.Status = "error";
                        entry.Detail = "duplicate (library stage failed)";
                        logEntries.Add(entry); skippedCount++;
                        Console.WriteLine();
                        dup.SkipRouting = true;
                    }
                    else if (dryRun)
                    {
                        string lDestDir = _router.GetDestDir(track, newArtistFolders, _compilationAlbums, out string lReason, out _);
                        string lDestFile = Reflector.SanitiseFilename(track.Artists) + " - " + Reflector.SanitiseFilename(track.Title) + ".mp3";
                        string lDestPath = Path.Combine(lDestDir, lDestFile);
                        string lRelDest = lDestPath.StartsWith(_roots.Library, StringComparison.OrdinalIgnoreCase)
                            ? lDestPath.Substring(_roots.Library.Length).TrimStart('\\', '/')
                            : lDestPath;
                        // Real run: the library copy is staged, then 3b routes with no batch set, so only an occupied
                        // destination (other than the copy being replaced) skips the replacement.
                        if (MusicIntegrator.IsDestBlocked(lDestPath, dup.LibraryFilePath, null))
                        {
                            Console.WriteLine(MusicIntegrator.FormatSkipLine(track.Artists, track.Title));
                            Console.WriteLine();
                            // Real run sets Reason and Destination in 3b before the skip; mirror them here.
                            entry.Reason = lReason;
                            entry.Destination = lRelDest;
                            entry.Status = "would-skip"; entry.Detail = "already exists at destination";
                            logEntries.Add(entry); skippedCount++;
                            dup.SkipRouting = true;
                        }
                        else
                        {
                            Console.WriteLine($"[DRY RUN] Would delete from library: {dup.RelLibraryPath}");
                            Console.WriteLine($"[DRY RUN] Would keep new file: {dup.DisplayNewFilename}");
                            Console.WriteLine($"[DRY RUN] Would route to: {lRelDest}");
                            entry.Status = "would-replace";
                            entry.Detail = "duplicate (would replace)";
                            _dryReplacedLibPaths.Add(dup.LibraryFilePath);
                            _ledger.RegisterSlotOwner(dup.LibraryFilePath, sf.SourcePath);
                            // SkipRouting stays false: 3b routes the new file and logs the would-move, as the real run does
                        }
                    }
                    else
                    {
                        if (File.Exists(dup.LibraryFilePath))
                        {
                            try
                            {
                                // Stage, never delete: the library copy is restored if routing the replacement fails
                                string bakPath = ReplaceGuard.Stage(dup.LibraryFilePath);
                                _lStaged[dup.LibraryFilePath] = bakPath;
                                _lStagedBySource[sf.SourcePath] = dup.LibraryFilePath;
                                _ledger.RegisterSlotOwner(dup.LibraryFilePath, sf.SourcePath);
                                _lDeletedLibraryPaths.Add(dup.LibraryFilePath);
                                Console.WriteLine($"  Staged library copy for replacement: {dup.RelLibraryPath}");
                                Console.WriteLine($"  Integrating replacement: {dup.RelNewPath}");
                                entry.Detail = "duplicate (library replaced)";
                                Console.WriteLine();
                                // SkipRouting stays false: new file is routed in 3b
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"  [ERROR] Could not stage library copy {dup.RelLibraryPath}: {ex.Message}");
                                entry.Status = "error";
                                entry.Detail = "duplicate (library stage failed)";
                                logEntries.Add(entry); skippedCount++;
                                Console.WriteLine();
                                dup.SkipRouting = true;
                            }
                        }
                        else
                        {
                            Console.WriteLine($"  [WARN] Library file not found: {dup.RelLibraryPath}");
                            entry.Status = "error";
                            entry.Detail = "duplicate (library file not found)";
                            logEntries.Add(entry); skippedCount++;
                            Console.WriteLine();
                            dup.SkipRouting = true;
                        }
                    }
                }
                // K: no output in 3a; routed normally in 3b
            }
            return true;
        }
    }
}
