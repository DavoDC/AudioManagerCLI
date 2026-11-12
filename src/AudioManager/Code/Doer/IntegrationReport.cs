using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using File = System.IO.File;
using ConfidenceRecord = AudioManager.MusicIntegrator.ConfidenceRecord;
using LogEntry = AudioManager.MusicIntegrator.LogEntry;
using ScannedFile = AudioManager.MusicIntegrator.ScannedFile;

namespace AudioManager
{
    /// <summary>
    /// Console reports for a routing run: the route distribution preview (dry run) and the confidence
    /// report (both modes). Reads run state live through delegates (roots, dry-run flag, scan-ahead result)
    /// and routes through DestinationRouter, so it never holds a stale copy. MusicIntegrator forwards to these members.
    /// </summary>
    internal sealed class IntegrationReport
    {
        private readonly Func<IntegrationRoots> _rootsSource;
        private readonly Func<bool> _dryRunSource;
        private readonly Func<ScanAheadResult> _scanSource;
        private readonly DestinationRouter _router;

        /// <summary>Roots, dry-run flag and scan-ahead result are read live on every use, never copied.</summary>
        internal IntegrationReport(Func<IntegrationRoots> roots, Func<bool> dryRun, Func<ScanAheadResult> scan, DestinationRouter router)
        {
            _rootsSource = roots;
            _dryRunSource = dryRun;
            _scanSource = scan;
            _router = router;
        }

        private IntegrationRoots _roots => _rootsSource();
        private bool dryRun => _dryRunSource();
        private HashSet<string> _compilationAlbums => _scanSource().CompilationAlbums;

        /// <summary>
        /// Prints the route distribution summary line (e.g. "Routes: Artists: 5 | Misc: 3").
        /// Called during dry-run only, before per-file routing output, so the user can spot anomalies.
        /// </summary>
        internal void PrintRouteDistributionPreview(List<ScannedFile> scannedFiles, HashSet<string> newArtistFolders)
        {
            var routeCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var sf in scannedFiles.Where(s => s.IsReadable && s.Duplicate?.SkipRouting != true))
            {
                string preDestDir = _router.GetDestDir(sf.Track, newArtistFolders, _compilationAlbums, out _, out _);
                string cat = MusicIntegrator.GetRouteCategory(preDestDir, _roots.Library);
                routeCounts[cat] = routeCounts.ContainsKey(cat) ? routeCounts[cat] + 1 : 1;
            }
            var summaryParts = routeCounts.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key}: {kv.Value}");
            Console.WriteLine($"Routes: {string.Join("  |  ", summaryParts)}");
        }

        /// <summary>
        /// Prints a confidence report to the console after integration, then returns the same data
        /// as a ConfidenceRecord for WriteExecutionRecord - compute-then-print-then-return, so the
        /// JSON and the console text are always in agreement (execution record contract, section 3.3).
        /// Console output text is unchanged from
        /// before this record existed.
        /// Covers: count check, per-file table, new folders, destination sanity check, errors.
        /// </summary>
        internal ConfidenceRecord PrintConfidenceReport(List<LogEntry> entries, int totalFiles, int movedCount, int skippedCount)
        {
            var record = new ConfidenceRecord();

            Console.WriteLine("\n===========================================================================");
            Console.WriteLine(dryRun ? "  CONFIDENCE REPORT (Dry Run)" : "  CONFIDENCE REPORT");
            Console.WriteLine("===========================================================================\n");

            // 1. Count check
            int expectedMoved = totalFiles - skippedCount;
            bool countOk = dryRun || (movedCount == expectedMoved);
            record.TotalFiles = totalFiles;
            record.Moved = movedCount;
            record.Skipped = skippedCount;
            record.ExpectedMoved = expectedMoved;
            record.CountOk = countOk;
            string countLine = $"  Files in NewMusic: {totalFiles}  |  Moved: {movedCount}  |  Skipped: {skippedCount}";
            Console.WriteLine(countLine);
            if (!countOk)
                Console.WriteLine($"  [ERROR] Count mismatch! Expected {expectedMoved} moved, got {movedCount}.");

            // 2. Per-file table
            foreach (var e in entries)
            {
                string tagNote = e.TagChanges?.Count > 0 ? $" | Tags: {string.Join(", ", e.TagChanges)}" : "";
                string destNote = !string.IsNullOrEmpty(e.Destination) ? $"\n    -> {e.Destination}" : "";
                string detailNote = !string.IsNullOrEmpty(e.Detail) ? $" ({e.Detail})" : "";
                Console.WriteLine($"  [{e.Status?.ToUpperInvariant()}] {e.Filename}{tagNote}{detailNote}{destNote}");
            }

            // 3. New folders created
            record.SanityRan = !dryRun;
            if (!dryRun)
            {
                var movedEntries = entries.Where(e => e.Status == "moved" && !string.IsNullOrEmpty(e.Destination)).ToList();
                var newFolders = new HashSet<string>();
                foreach (var e in movedEntries)
                {
                    string destFolder = Path.GetDirectoryName(e.Destination);
                    if (!string.IsNullOrEmpty(destFolder))
                    {
                        string fullDir = Path.Combine(_roots.Library, destFolder);
                        if (Directory.Exists(fullDir) && Directory.GetFiles(fullDir).Length == 1)
                            newFolders.Add(destFolder);
                    }
                }
                if (newFolders.Count > 0)
                {
                    Console.WriteLine("\n  --- New folders created ---");
                    foreach (var f in newFolders) Console.WriteLine($"  + {f}");
                }
                record.NewFolders = newFolders.OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
            }

            // 4. Destination sanity check (re-read each moved file)
            if (!dryRun)
            {
                var failedSanity = new List<string>();
                int sanityChecked = 0;
                foreach (var e in entries.Where(en => en.Status == "moved"))
                {
                    if (string.IsNullOrEmpty(e.Destination)) continue;
                    sanityChecked++;
                    string fullPath = Path.Combine(_roots.Library, e.Destination);
                    if (!File.Exists(fullPath))
                    {
                        failedSanity.Add($"  [MISSING] {e.Destination}");
                        record.SanityFailures.Add((e.Destination, "missing"));
                        continue;
                    }
                    try
                    {
                        using (TagLib.File tf = TagLib.File.Create(fullPath))
                        {
                            // Readable = OK
                        }
                    }
                    catch
                    {
                        failedSanity.Add($"  [UNREADABLE] {e.Destination}");
                        record.SanityFailures.Add((e.Destination, "unreadable"));
                    }
                }
                record.SanityChecked = sanityChecked;
                if (failedSanity.Count > 0)
                {
                    Console.WriteLine("\n  [ERROR] Destination sanity check FAILED:");
                    foreach (var f in failedSanity) Console.WriteLine(f);
                }
                else if (movedCount > 0)
                {
                    Console.WriteLine($"\n  Sanity check: all {movedCount} moved file(s) exist and are readable.");
                }
            }

            // 5. Error summary
            var errors = entries.Where(e => e.Status == "error").ToList();
            record.Errors = errors.Select(e => (e.Filename, e.Detail ?? "", e.SourceRel)).ToList();
            if (errors.Count > 0)
            {
                Console.WriteLine($"[ERRORS: {errors.Count}]");
                foreach (var e in errors) Console.WriteLine($"- {e.Filename}: {e.Detail}");
            }
            else
            {
                Console.WriteLine("No errors.");
            }

            Console.WriteLine("\n===========================================================================");

            return record;
        }
    }
}
