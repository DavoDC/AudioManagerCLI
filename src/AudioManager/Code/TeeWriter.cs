using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace AudioManager
{
    /// <summary>
    /// A TextWriter that writes to multiple underlying writers simultaneously.
    /// Used to capture console output to file while still displaying it on screen.
    /// Supports tee'ing to console + file, or console + StringWriter for in-memory capture.
    /// </summary>
    internal class TeeWriter : TextWriter
    {
        private readonly TextWriter consoleWriter;
        private readonly TextWriter captureWriter;
        private readonly StreamWriter fileWriter;
        private readonly bool addFileTimestamps;
        private bool atLineStart = true;

        /// <summary>Tee to console + StringWriter (in-memory capture).</summary>
        public TeeWriter(TextWriter consoleWriter, StringWriter captureWriter)
        {
            this.consoleWriter = consoleWriter;
            this.captureWriter = captureWriter;
            this.fileWriter = null;
        }

        /// <summary>Tee to console + file + optional StringWriter.
        /// When addFileTimestamps is true, each line written to the file is prefixed with [yyyy-MM-dd HH:mm:ss].
        /// Console output is never timestamped.</summary>
        public TeeWriter(TextWriter consoleWriter, string logFilePath, StringWriter captureWriter = null, bool addFileTimestamps = false)
        {
            this.consoleWriter = consoleWriter;
            this.captureWriter = captureWriter;
            this.addFileTimestamps = addFileTimestamps;

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(logFilePath));
                string target = UniqueLogPath(logFilePath);
                // FileMode.CreateNew: an existing log is never truncated, even if one appears after the check
                var stream = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                this.fileWriter = new StreamWriter(stream, new UTF8Encoding(true)) { AutoFlush = true };
                FilePath = target;
            }
            catch (Exception ex)
            {
                // If file creation fails, continue without file logging, but say so on the console
                this.fileWriter = null;
                consoleWriter.WriteLine($" - [WARN] Could not open the run log file, this run will NOT be logged to a file: {logFilePath} ({ex.Message})");
            }
        }

        /// <summary>The requested path when free, else the same name with a -2, -3 ... suffix. Never an existing file.</summary>
        private static string UniqueLogPath(string path)
        {
            if (!File.Exists(path)) return path;
            string dir = Path.GetDirectoryName(path);
            string stem = Path.GetFileNameWithoutExtension(path);
            string ext = Path.GetExtension(path);
            for (int n = 2; ; n++)
            {
                string candidate = Path.Combine(dir, $"{stem}-{n}{ext}");
                if (!File.Exists(candidate)) return candidate;
            }
        }

        public TextWriter ConsoleWriter => consoleWriter;
        public string FilePath { get; set; }

        public override Encoding Encoding => consoleWriter.Encoding;

        public override void Write(char value)
        {
            consoleWriter.Write(value);
            captureWriter?.Write(value);
            if (fileWriter != null)
                WriteCharToFile(value);
        }

        // Writes one char to the file with timestamp logic. Does not touch console/capture.
        private void WriteCharToFile(char value)
        {
            if (addFileTimestamps && atLineStart && value != '\r' && value != '\n')
                fileWriter.Write($"[{DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}] ");
            fileWriter.Write(value);
            if (value == '\n') atLineStart = true;
            else if (value != '\r') atLineStart = false;
        }

        public override void WriteLine(string value)
        {
            consoleWriter.WriteLine(value);
            captureWriter?.WriteLine(value);
            if (fileWriter != null)
            {
                string fileValue = value ?? "";
                if (addFileTimestamps && fileValue.IndexOf('\n') >= 0)
                {
                    // Embedded newlines: write char by char so each sub-line gets its own timestamp
                    foreach (char c in fileValue)
                        WriteCharToFile(c);
                    WriteCharToFile('\n'); // WriteLine's trailing newline
                }
                else if (addFileTimestamps && atLineStart)
                {
                    fileWriter.WriteLine($"[{DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}] {fileValue}");
                    atLineStart = true;
                }
                else
                {
                    fileWriter.WriteLine(fileValue);
                    atLineStart = true;
                }
            }
        }

        public override void WriteLine()
        {
            consoleWriter.WriteLine();
            captureWriter?.WriteLine();
            fileWriter?.WriteLine();
            atLineStart = true;
        }

        public override void Flush()
        {
            consoleWriter.Flush();
            captureWriter?.Flush();
            fileWriter?.Flush();
        }

        public void CloseLogFile()
        {
            Flush();
            fileWriter?.Dispose();
        }
    }
}
