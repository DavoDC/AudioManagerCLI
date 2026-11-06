using System;
using System.IO;
using System.Threading;

namespace AudioManager
{
    /// <summary>
    /// Stage-then-commit for the L duplicate decision (replace the library copy with the new file).
    /// The library copy is moved aside to a ".replaced.bak" sibling instead of being deleted, so a
    /// failed replacement can be rolled back. Before the move into the library, MarkLanding writes a
    /// ".replaced.dest" marker naming the destination; the marker proves the replace completed, so an
    /// interrupted run can tell "landed" (drop the bak) from "never landed" (restore the bak).
    /// Commit deletes the bak and marker once the replacement has landed; Rollback restores the bak.
    /// Nothing here ever overwrites an existing file.
    /// </summary>
    internal static class ReplaceGuard
    {
        private const string BakSuffix = ".replaced.bak";
        private const string MarkerSuffix = ".replaced.dest";

        /// <summary>
        /// Moves libPath to libPath + ".replaced.bak" and returns the bak path. A stale bak is first
        /// resolved via ResolveOne (dropped when its marker proves the replace landed, restored when
        /// it did not). Throws if a bak still exists afterwards (never overwrite a previous stage) or
        /// if the move fails.
        /// </summary>
        internal static string Stage(string libPath)
        {
            string bakPath = libPath + BakSuffix;
            if (File.Exists(bakPath)) ResolveOne(bakPath);
            if (File.Exists(bakPath))
                throw new IOException($"Refusing to stage: backup already exists: {bakPath}");
            File.Move(libPath, bakPath);
            return bakPath;
        }

        /// <summary>
        /// Records, before the move into the library, that destPath is the landing spot for libPath.
        /// If the process dies after this marker is written, the sweep can tell whether the move happened.
        /// </summary>
        internal static void MarkLanding(string libPath, string destPath)
        {
            File.WriteAllText(libPath + MarkerSuffix, destPath);
        }

        /// <summary>
        /// Deletes the staged bak once the replacement has landed, then the landing marker. The bak
        /// delete is tried up to 3 times; on final failure a WARN is printed and the marker is kept.
        /// A missing bak is a no-op.
        /// </summary>
        internal static void Commit(string bakPath)
        {
            bool deleted = false;
            Exception last = null;
            for (int attempt = 1; attempt <= 3 && !deleted; attempt++)
            {
                try
                {
                    if (File.Exists(bakPath)) File.Delete(bakPath);
                    deleted = true;
                }
                catch (Exception ex)
                {
                    last = ex;
                    if (attempt < 3) Thread.Sleep(100);
                }
            }
            if (!deleted)
            {
                Console.WriteLine($"  [WARN] Could not delete staged backup {bakPath}: {last?.Message}");
                return;
            }
            if (!bakPath.EndsWith(BakSuffix, StringComparison.OrdinalIgnoreCase)) return;
            string markerPath = bakPath.Substring(0, bakPath.Length - BakSuffix.Length) + MarkerSuffix;
            try
            {
                if (File.Exists(markerPath)) File.Delete(markerPath);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  [WARN] Could not delete landing marker {markerPath}: {ex.Message}");
            }
        }

        /// <summary>
        /// Deletes the landing marker if present (it is stale once the library copy is being
        /// restored), then moves the bak back to libPath if the bak exists and libPath does not. If
        /// libPath is occupied, the bak is left in place and a WARN is printed so nothing is overwritten.
        /// </summary>
        internal static void Rollback(string bakPath, string libPath)
        {
            try
            {
                string markerPath = libPath + MarkerSuffix;
                if (File.Exists(markerPath)) File.Delete(markerPath);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  [WARN] Could not delete landing marker for {libPath}: {ex.Message}");
            }
            if (!File.Exists(bakPath)) return;
            if (File.Exists(libPath))
            {
                Console.WriteLine($"  [WARN] Library path occupied; staged backup kept at: {bakPath}");
                return;
            }
            try
            {
                File.Move(bakPath, libPath);
                Console.WriteLine($"  Restored library copy: {libPath}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  [WARN] Could not restore {libPath} from {bakPath}: {ex.Message}");
            }
        }

        /// <summary>
        /// Resolves one staged bak. If its landing marker names an existing destination other than the
        /// bak, the replace completed: the bak and marker are deleted and 0 is returned. Otherwise, if
        /// the library copy exists the bak is kept with a WARN (returns 0). Otherwise the bak is moved
        /// back to the library path, the marker is deleted, and 1 is returned. Never throws.
        /// </summary>
        private static int ResolveOne(string bakPath)
        {
            try
            {
                string libPath = bakPath.Substring(0, bakPath.Length - BakSuffix.Length);
                string markerPath = libPath + MarkerSuffix;
                if (File.Exists(markerPath))
                {
                    string destPath = File.ReadAllText(markerPath).Trim();
                    if (destPath.Length > 0 && File.Exists(destPath) && !string.Equals(destPath, bakPath, StringComparison.OrdinalIgnoreCase))
                    {
                        File.Delete(bakPath);
                        File.Delete(markerPath);
                        Console.WriteLine($"  [SWEEP] Completed interrupted replace: {bakPath}");
                        return 0;
                    }
                }
                if (File.Exists(libPath))
                {
                    Console.WriteLine($"  [WARN] Orphaned staged backup kept (library copy exists): {bakPath}");
                    return 0;
                }
                File.Move(bakPath, libPath);
                if (File.Exists(markerPath)) File.Delete(markerPath);
                Console.WriteLine($"  [SWEEP] Restored interrupted replace: {libPath}");
                return 1;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  [WARN] Could not sweep staged backup {bakPath}: {ex.Message}");
                return 0;
            }
        }

        /// <summary>
        /// Startup sweep over libraryRoot. Every "*.replaced.bak" is resolved by ResolveOne: a bak whose
        /// landing marker proves the replace completed is dropped, a bak whose library sibling is missing
        /// is moved back, and a bak whose sibling exists is left in place with a WARN. Then every
        /// "*.replaced.dest" marker whose bak no longer exists is deleted. Returns the number restored.
        /// </summary>
        internal static int SweepOrphans(string libraryRoot)
        {
            if (string.IsNullOrEmpty(libraryRoot) || !Directory.Exists(libraryRoot)) return 0;

            int restored = 0;
            string[] baks;
            string[] markers;
            try
            {
                baks = Directory.GetFiles(libraryRoot, "*" + BakSuffix, SearchOption.AllDirectories);
                // Windows 3-char extension patterns also match longer extensions (x.replaced.bakX);
                // keep only true baks so the Substring below never builds a truncated restore target.
                baks = Array.FindAll(baks, p => p.EndsWith(BakSuffix, StringComparison.OrdinalIgnoreCase));
                markers = Directory.GetFiles(libraryRoot, "*" + MarkerSuffix, SearchOption.AllDirectories);
                markers = Array.FindAll(markers, p => p.EndsWith(MarkerSuffix, StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  [WARN] Could not scan for staged backups under {libraryRoot}: {ex.Message}");
                return 0;
            }

            foreach (string bakPath in baks)
                restored += ResolveOne(bakPath);

            foreach (string markerPath in markers)
            {
                try
                {
                    string bakPath = markerPath.Substring(0, markerPath.Length - MarkerSuffix.Length) + BakSuffix;
                    if (File.Exists(bakPath)) continue;
                    File.Delete(markerPath);
                    Console.WriteLine($"  [SWEEP] Deleted stray landing marker: {markerPath}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  [WARN] Could not sweep landing marker {markerPath}: {ex.Message}");
                }
            }
            return restored;
        }
    }
}
