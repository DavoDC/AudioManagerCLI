using System;
using System.Collections.Generic;
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
        /// Holding folder for retired originals. Null (the default) keeps the legacy behaviour: the bak is
        /// deleted. When set, a landed bak is moved into a timestamped folder under it instead (see Retire).
        /// </summary>
        internal static string HoldingRoot;

        /// <summary>Each retired original: original = the library path, held = where the bak now lives.</summary>
        internal static readonly List<(string original, string held)> Retired = new List<(string original, string held)>();

        internal static void ResetRetired()
        {
            Retired.Clear();
        }

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
            AtomicFile.WriteAllText(libPath + MarkerSuffix, destPath, new System.Text.UTF8Encoding(false));
        }

        /// <summary>
        /// True when both paths are on the same volume (same rooted drive or UNC share, case-insensitive).
        /// </summary>
        internal static bool SameVolume(string a, string b)
        {
            return string.Equals(Path.GetPathRoot(Path.GetFullPath(a)), Path.GetPathRoot(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Moves a landed bak into a timestamped folder under HoldingRoot (yyyyMMdd-HHmmss, created on
        /// demand) and returns the held path. The held name is the original file name; a clash gets
        /// " (2)", " (3)" before the extension, never an overwrite. A rename, so the bak must be on the
        /// same volume as HoldingRoot: otherwise an [ERROR] is printed and nothing is moved or deleted.
        /// Returns null on any failure (a WARN or ERROR is printed). Never throws.
        /// </summary>
        internal static string Retire(string bakPath)
        {
            try
            {
                if (!bakPath.EndsWith(BakSuffix, StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine($"  [WARN] Not a staged backup, not retired: {bakPath}");
                    return null;
                }
                string full = Path.GetFullPath(bakPath);
                string root = Path.GetFullPath(HoldingRoot);
                if (!SameVolume(full, root))
                {
                    Console.WriteLine($"  [ERROR] Holding root {root} is on a different volume ({Path.GetPathRoot(root)}) than {full} ({Path.GetPathRoot(full)}); staged backup left in place, not moved or deleted.");
                    return null;
                }
                string original = full.Substring(0, full.Length - BakSuffix.Length);
                string folder = Path.Combine(root, DateTime.Now.ToString("yyyyMMdd-HHmmss"));
                Directory.CreateDirectory(folder);

                string name = Path.GetFileName(original);
                string stem = Path.GetFileNameWithoutExtension(name);
                string ext = Path.GetExtension(name);
                string held = Path.Combine(folder, name);
                for (int n = 2; File.Exists(held) || Directory.Exists(held); n++)
                    held = Path.Combine(folder, $"{stem} ({n}){ext}");

                File.Move(full, held);
                Retired.Add((original, held));
                Console.WriteLine($"  [RETIRE] Held original: {held}");
                return held;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  [WARN] Could not retire staged backup {bakPath}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Commit once the replacement has landed. With HoldingRoot set, the bak is retired into the
        /// holding folder (a failed retire keeps the bak and marker, with its WARN or ERROR). Otherwise
        /// the bak is deleted as before, tried up to 3 times; on final failure a WARN is printed and the
        /// marker is kept. A missing bak is a no-op. On success the landing marker is deleted.
        /// </summary>
        internal static void Commit(string bakPath)
        {
            if (!File.Exists(bakPath))
            {
                DeleteLandingMarker(bakPath);
                return;
            }
            if (HoldingRoot == null)
            {
                // Never permanently delete a library original: with no holding folder keep bak and marker.
                Console.WriteLine($"  [WARN] No holding folder set; staged library original kept at: {bakPath}");
                return;
            }
            if (Retire(bakPath) == null) return;
            DeleteLandingMarker(bakPath);
        }

        /// <summary>Deletes the bak (up to 3 tries). Returns false after a printed WARN on final failure.</summary>
        private static bool DeleteBak(string bakPath)
        {
            Exception last = null;
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    if (File.Exists(bakPath)) File.Delete(bakPath);
                    return true;
                }
                catch (Exception ex)
                {
                    last = ex;
                    if (attempt < 3) Thread.Sleep(100);
                }
            }
            Console.WriteLine($"  [WARN] Could not delete staged backup {bakPath}: {last?.Message}");
            return false;
        }

        /// <summary>Deletes the landing marker that belongs to bakPath, if present. A failure is a WARN.</summary>
        private static void DeleteLandingMarker(string bakPath)
        {
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
                        if (HoldingRoot == null)
                        {
                            Console.WriteLine($"  [WARN] No holding folder set; staged library original kept at: {bakPath}");
                            return 0;
                        }
                        if (Retire(bakPath) == null) return 0;
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
        /// Read-only twin of SweepOrphans for a dry run: the full paths of every true "*.replaced.bak" and
        /// "*.replaced.dest" under libraryRoot. Changes nothing.
        /// </summary>
        internal static List<string> ListOrphans(string libraryRoot)
        {
            var found = new List<string>();
            if (string.IsNullOrEmpty(libraryRoot) || !Directory.Exists(libraryRoot)) return found;
            try
            {
                foreach (string suffix in new[] { BakSuffix, MarkerSuffix })
                    found.AddRange(Array.FindAll(Directory.GetFiles(libraryRoot, "*" + suffix, SearchOption.AllDirectories),
                        p => p.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  [WARN] Could not scan for staged backups under {libraryRoot}: {ex.Message}");
            }
            return found;
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
