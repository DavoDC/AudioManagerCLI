using System;
using System.IO;
using System.Text;

namespace AudioManager
{
    /// <summary>
    /// Atomic text writes for files a client reads (analysis JSON, routing and execution JSON).
    ///
    /// Why: File.WriteAllText straight to the destination lets a crash or a concurrent reader see a
    /// torn or empty file. The text goes to a sibling "path.tmp" first, then replaces the destination
    /// in one step. Same approach as TrackXML.Write. On failure the tmp file is removed and the
    /// exception propagates unchanged.
    /// </summary>
    internal static class AtomicFile
    {
        internal static void WriteAllText(string path, string text, Encoding enc)
        {
            string tmpPath = path + ".tmp";
            try
            {
                File.WriteAllText(tmpPath, text, enc);

                // Atomic promotion: replace an existing file, or move into place for the first write
                if (File.Exists(path))
                    File.Replace(tmpPath, path, null);
                else
                    File.Move(tmpPath, path);
            }
            catch
            {
                DeleteTempFile(tmpPath);
                throw;
            }
        }

        private static void DeleteTempFile(string tmpPath)
        {
            try
            {
                if (File.Exists(tmpPath)) File.Delete(tmpPath);
            }
            catch (Exception ex)
            {
                Console.WriteLine($" - [!] Could not delete temp file {tmpPath}: {ex.Message}");
            }
        }
    }
}
