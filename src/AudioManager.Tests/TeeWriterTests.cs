using System;
using System.IO;

namespace AudioManager
{
    /// <summary>
    /// Tests for TeeWriter - the dual console+file writer used for all session logging.
    /// Key invariant (DevContext.md): WriteLine(string) with embedded '\n' MUST process
    /// char-by-char via WriteCharToFile so each sub-line gets its own timestamp.
    /// </summary>
    internal static class TeeWriterTests
    {
        // A path that does not exist yet: TeeWriter never overwrites an existing log file
        private static string TempFile() => Path.Combine(Path.GetTempPath(), "tee_" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".log");

        public static void TeeWriter_TimestampPrefix_IncludesDate()
        {
            string path = TempFile();
            try
            {
                var tee = new TeeWriter(TextWriter.Null, path, addFileTimestamps: true);
                tee.WriteLine("Dated Line");
                tee.CloseLogFile();
                string line = File.ReadAllLines(path)[0];
                Assert.True(System.Text.RegularExpressions.Regex.IsMatch(line, @"^\[\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\] Dated Line$"),
                    "prefix must be [yyyy-MM-dd HH:mm:ss], got: " + line);
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }

        public static void TeeWriter_ExistingLogFile_IsNeverOverwritten()
        {
            string path = TempFile();
            string actual = null;
            try
            {
                File.WriteAllText(path, "OLD CONTENT");
                var tee = new TeeWriter(TextWriter.Null, path, addFileTimestamps: false);
                tee.WriteLine("new content");
                tee.CloseLogFile();
                actual = tee.FilePath;
                Assert.Equal("OLD CONTENT", File.ReadAllText(path), "existing same-name log must be untouched");
                Assert.True(actual != null && actual != path && File.Exists(actual), "new log goes to a different file");
                Assert.True(File.ReadAllText(actual).Contains("new content"), "new log holds the new text");
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
                if (actual != null && File.Exists(actual)) File.Delete(actual);
            }
        }

        public static void TeeWriter_FileOpenFails_PrintsConsoleWarning()
        {
            string blocker = TempFile();
            File.WriteAllText(blocker, "x");
            try
            {
                var console = new StringWriter();
                // The parent of the log path is a file, so the directory cannot be created
                var tee = new TeeWriter(console, Path.Combine(blocker, "sub", "run.log"));
                Assert.True(console.ToString().Contains("[WARN]") && console.ToString().Contains("log"),
                    "a failed log open must print a visible warning, got: " + console);
                tee.WriteLine("still works");
                Assert.True(console.ToString().Contains("still works"), "console output continues");
            }
            finally { if (File.Exists(blocker)) File.Delete(blocker); }
        }

        // ---- StringWriter capture (no console, no file) ----

        public static void TeeWriter_WriteChar_GoesToCaptureWriter()
        {
            var capture = new StringWriter();
            var tee = new TeeWriter(TextWriter.Null, capture);
            tee.Write('A');
            Assert.Equal("A", capture.ToString(), "written char should appear in capture writer");
        }

        public static void TeeWriter_WriteLine_GoesToCaptureWriter()
        {
            var capture = new StringWriter();
            var tee = new TeeWriter(TextWriter.Null, capture);
            tee.WriteLine("Hello World");
            Assert.True(capture.ToString().Contains("Hello World"),
                "written line should appear in capture writer");
        }

        // ---- File output ----

        public static void TeeWriter_WriteToFile_FileContainsText()
        {
            string path = TempFile();
            try
            {
                var tee = new TeeWriter(TextWriter.Null, path, addFileTimestamps: false);
                tee.WriteLine("Test Content");
                tee.CloseLogFile();

                string content = File.ReadAllText(path);
                Assert.True(content.Contains("Test Content"),
                    "file should contain the written line");
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }

        public static void TeeWriter_WithTimestamps_FileLineHasTimestampPrefix()
        {
            // Each line written to file should be prefixed with [HH:mm:ss]
            string path = TempFile();
            try
            {
                var tee = new TeeWriter(TextWriter.Null, path, addFileTimestamps: true);
                tee.WriteLine("Timestamped Line");
                tee.CloseLogFile();

                string[] lines = File.ReadAllLines(path);
                Assert.True(lines.Length > 0, "file should have at least one line");
                Assert.True(lines[0].StartsWith("["), "timestamped line should start with [");
                Assert.True(lines[0].Contains("] Timestamped Line"),
                    "line should contain timestamp bracket and then the content");
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }

        public static void TeeWriter_EmbeddedNewline_EachSublineHasTimestamp()
        {
            // DevContext.md invariant: WriteLine(string) with embedded '\n' must write char-by-char
            // so each sub-line gets its own [HH:mm:ss] prefix - not a single timestamp for the block.
            string path = TempFile();
            try
            {
                var tee = new TeeWriter(TextWriter.Null, path, addFileTimestamps: true);
                tee.WriteLine("Line One\nLine Two");
                tee.CloseLogFile();

                string[] lines = File.ReadAllLines(path);
                Assert.True(lines.Length >= 2, "embedded newline should produce at least 2 file lines");
                Assert.True(lines[0].StartsWith("["), "first sub-line should have timestamp prefix");
                Assert.True(lines[1].StartsWith("["), "second sub-line should have timestamp prefix");
                Assert.True(lines[0].Contains("Line One"), "first sub-line should contain first content");
                Assert.True(lines[1].Contains("Line Two"), "second sub-line should contain second content");
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }
    }
}
