using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AudioManager
{
    /// <summary>
    /// Removes empty subfolders from the NewMusic staging folder after integration, deepest first; a folder holding any file (at any depth) is kept and the root is never removed.
    /// Reads the dry-run flag live through a delegate. MusicIntegrator forwards to this class.
    /// </summary>
    internal sealed class NewMusicCleanup
    {
        private readonly Func<bool> _dryRunSource;

        internal NewMusicCleanup(Func<bool> dryRun) { _dryRunSource = dryRun; }

        private bool dryRun => _dryRunSource();

        /// <summary>
        /// Cleans up empty subdirectories in the NewMusic staging folder after integration.
        /// Removes every subfolder with no file at any depth, deepest first. A folder holding any file, and its
        /// parents, are kept; the root is never removed. Remaining files are reported, never blocking the cleanup.
        /// No-op if NewMusic doesn't exist or has no subdirectories to clean.
        /// </summary>
        internal void CleanupNewMusicFolder(string newMusicPath)
        {
            if (!Directory.Exists(newMusicPath)) return;

            // A folder is kept when a file sits anywhere under it (its parents included); files that remain
            // (integration skipped them, or they are leftovers like .tagfix.bak) never block the removal of
            // folders with no file at any depth.
            var remainingFiles = Directory.GetFiles(newMusicPath, "*", SearchOption.AllDirectories);
            var nonEmpty = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string file in remainingFiles)
            {
                string dir = Path.GetDirectoryName(file);
                while (dir != null && dir.Length > newMusicPath.Length && nonEmpty.Add(dir))
                    dir = Path.GetDirectoryName(dir);
            }

            // Collect empty subdirectories, deepest first so parents are empty by the time we reach them
            var emptyDirs = Directory.GetDirectories(newMusicPath, "*", SearchOption.AllDirectories)
                .Where(d => !nonEmpty.Contains(d))
                .OrderByDescending(d => d.Length)
                .ToList();

            if (emptyDirs.Count == 0)
            {
                if (remainingFiles.Length > 0)
                    Console.WriteLine($"\n[INFO] NewMusic cleanup: {remainingFiles.Length} file(s) remain, 0 empty folder(s) removed. Review remaining files manually.");
                return;
            }

            Console.WriteLine("\n===========================================================================");
            Console.WriteLine(dryRun ? "NewMusic Cleanup (Dry Run)" : "NewMusic Cleanup");
            Console.WriteLine("===========================================================================");

            int cleanedCount = 0;
            foreach (var dir in emptyDirs)
            {
                string relDir = dir.StartsWith(newMusicPath, StringComparison.OrdinalIgnoreCase)
                    ? dir.Substring(newMusicPath.Length).TrimStart('\\', '/')
                    : dir;

                if (dryRun)
                {
                    Console.WriteLine($"[DRY RUN] Would remove empty folder: {relDir}");
                    cleanedCount++;
                }
                else
                {
                    try
                    {
                        if (Directory.Exists(dir) && Directory.GetFileSystemEntries(dir).Length == 0)
                        {
                            Directory.Delete(dir);
                            Console.WriteLine($"  Removed empty folder: {relDir}");
                            cleanedCount++;
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"  [WARN] Could not remove '{relDir}': {ex.Message}");
                    }
                }
            }

            Console.WriteLine();
            Console.WriteLine(dryRun
                ? $"Would remove: {cleanedCount} empty folder(s)"
                : $"Cleaned up: {cleanedCount} empty folder(s)");
            if (remainingFiles.Length > 0)
                Console.WriteLine(dryRun
                    ? $"[INFO] NewMusic cleanup: {remainingFiles.Length} file(s) remain, {cleanedCount} empty folder(s) would be removed. Review remaining files manually."
                    : $"[INFO] NewMusic cleanup: {remainingFiles.Length} file(s) remain, {cleanedCount} empty folder(s) removed. Review remaining files manually.");
        }
    }
}
