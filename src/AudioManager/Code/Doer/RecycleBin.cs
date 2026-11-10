using System;
using Microsoft.VisualBasic.FileIO;

namespace AudioManager
{
    /// <summary>
    /// Sends a file to the Windows Recycle Bin instead of deleting it for good. Used by the importer's
    /// duplicate D decision: the NewMusic copy goes to the bin so it can still be recovered.
    /// Send throws on failure so the caller's catch reports the file as an error and keeps the batch going.
    /// shortcut: Sender is a static test seam (no interface), keep it internal and restore the original in a
    /// finally block; upgrade to an injected interface if a second caller needs a different sender.
    /// </summary>
    internal static class RecycleBin
    {
        /// <summary>The real sender moves the file to the Recycle Bin; tests swap in a fake.</summary>
        internal static Action<string> Sender = path =>
            FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);

        /// <summary>True when path's volume has a Recycle Bin. Tests swap it.</summary>
        internal static Func<string, bool> BinCheck = path =>
        {
            try
            {
                string root = System.IO.Path.GetPathRoot(System.IO.Path.GetFullPath(path));
                if (string.IsNullOrEmpty(root)) return false;
                return new System.IO.DriveInfo(root).DriveType == System.IO.DriveType.Fixed
                    && System.IO.Directory.Exists(System.IO.Path.Combine(root, "$Recycle.Bin"));
            }
            catch { return false; }
        };

        /// <summary>Sends the file at path to the Recycle Bin via Sender. Throws if the volume has no bin
        /// (the shell would delete permanently) or the send fails.</summary>
        internal static void Send(string path)
        {
            if (!BinCheck(path))
                throw new InvalidOperationException("No Recycle Bin on the volume of " + path + "; file kept, not deleted.");
            Sender(path);
        }
    }
}
