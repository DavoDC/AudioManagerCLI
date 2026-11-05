using System;
using System.IO;

namespace AudioManager
{
    /// <summary>
    /// Stage-then-commit for the L duplicate decision (replace the library copy with the new file).
    /// The library copy is moved aside to a ".replaced.bak" sibling instead of being deleted, so a
    /// failed replacement can be rolled back. Commit deletes the bak once the replacement has landed;
    /// Rollback restores it. Nothing here ever overwrites an existing file.
    /// </summary>
    internal static class ReplaceGuard
    {
        private const string BakSuffix = ".replaced.bak";

        /// <summary>
        /// Moves libPath to libPath + ".replaced.bak" and returns the bak path. Throws if the bak
        /// already exists (never overwrite a previous stage) or if the move fails.
        /// </summary>
        internal static string Stage(string libPath)
        {
            string bakPath = libPath + BakSuffix;
            if (File.Exists(bakPath))
                throw new IOException($"Refusing to stage: backup already exists: {bakPath}");
            File.Move(libPath, bakPath);
            return bakPath;
        }

        /// <summary>Deletes the staged bak once the replacement has landed. Missing bak is a no-op.</summary>
        internal static void Commit(string bakPath)
        {
            try
            {
                if (File.Exists(bakPath)) File.Delete(bakPath);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  [WARN] Could not delete staged backup {bakPath}: {ex.Message}");
            }
        }

        /// <summary>
        /// Moves the bak back to libPath if the bak exists and libPath does not. If libPath is
        /// occupied, the bak is left in place and a WARN is printed so nothing is overwritten.
        /// </summary>
        internal static void Rollback(string bakPath, string libPath)
        {
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
    }
}
