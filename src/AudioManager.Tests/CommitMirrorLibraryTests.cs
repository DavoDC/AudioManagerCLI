using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace AudioManager
{
    /// <summary>
    /// GUI-90 / GUI-88 d: --commit-mirror also runs the library health check (LibChecker over the AUDIO_MIRROR folder)
    /// after the per-file gate, so every issue surfaces in one round. Blocking hit kinds refuse the commit and are listed
    /// in the additive "library" key; fix-later kinds (LibChecker.FixLaterKinds) never refuse. Every test runs against a
    /// throwaway git repo with a throwaway parse cache under the temp folder; nothing touches the real mirror.
    /// </summary>
    internal static class CommitMirrorLibraryTests
    {
        private const string Rel = "AUDIO_MIRROR/Artists/Alpha/Singles/Alpha - Song.xml";

        // A complete mirror XML whose file name and tags satisfy the LibChecker filename rule (artist, " - ", title).
        private static string TrackXml(string title = "Song", string artists = "Alpha", string album = "Album1")
        {
            return "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<Track>"
                + "<Title>" + title + "</Title>"
                + "<Artists>" + artists + "</Artists>"
                + "<Album>" + album + "</Album>"
                + "<Year>2020</Year>"
                + "<TrackNumber>1</TrackNumber>"
                + "<Genres>Rock</Genres>"
                + "<Length>00:03:20.0000000</Length>"
                + "<AlbumCover><Count>1</Count><Width>1000</Width><Height>1000</Height></AlbumCover>"
                + "<Compilation>true</Compilation>"
                + "</Track>";
        }

        private static string CreateRepoWithInitialCommit()
        {
            string dir = Path.Combine(Path.GetTempPath(), "am_mirrorlib_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            RunProcess("git", "init", dir);
            RunProcess("git", "config user.email test@test.com", dir);
            RunProcess("git", "config user.name Test", dir);
            WriteFile(dir, "seed.txt", "seed");
            RunProcess("git", "add -A", dir);
            RunProcess("git", "commit -m initial", dir);
            return dir;
        }

        private static void RunProcess(string exe, string args, string workingDir)
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                WorkingDirectory = workingDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using (var p = Process.Start(psi))
            {
                p.StandardOutput.ReadToEnd();
                p.StandardError.ReadToEnd();
                p.WaitForExit();
            }
        }

        private static string RunGitOut(string workingDir, string args)
        {
            var psi = new ProcessStartInfo("git", args)
            {
                WorkingDirectory = workingDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using (var p = Process.Start(psi))
            {
                string stdout = p.StandardOutput.ReadToEnd();
                string stderr = p.StandardError.ReadToEnd();
                p.WaitForExit();
                return (stdout + stderr).Trim();
            }
        }

        private static string Head(string repo) => RunGitOut(repo, "rev-parse HEAD");

        private static void WriteFile(string repoDir, string relPath, string content)
        {
            string full = Path.Combine(repoDir, relPath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full));
            File.WriteAllText(full, content);
        }

        // Runs --commit-mirror with the parse cache kept under the temp repo and console output captured.
        private static int RunCaptured(string repo, string message, out string output)
        {
            var originalOut = Console.Out;
            var capture = new StringWriter();
            int code;
            try
            {
                Console.SetOut(capture);
                code = MirrorGate.RunCommand(repo, true, message, Path.Combine(repo, "parse-cache.txt"));
            }
            finally
            {
                Console.SetOut(originalOut);
            }
            output = capture.ToString();
            return code;
        }

        private static string LastLine(string output)
        {
            string[] lines = output.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
            return lines.Length == 0 ? string.Empty : lines[lines.Length - 1].Trim();
        }

        private static void Cleanup(string dir)
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }

        // A healthy library set commits exactly as before: exit 0, HEAD moves, the JSON says committed and has no library hits.
        public static void CommitMirror_CleanLibrary_Commits()
        {
            string repo = CreateRepoWithInitialCommit();
            try
            {
                string before = Head(repo);
                WriteFile(repo, Rel, TrackXml());
                int code = RunCaptured(repo, "Add healthy album", out string output);
                string json = LastLine(output);
                Assert.True(code == 0, "clean library commits with exit 0, got " + code + " json: " + json);
                Assert.True(Head(repo) != before, "HEAD moved on a clean library");
                Assert.True(json.Contains("\"committed\": true"), "JSON says committed");
                Assert.True(json.Contains("\"library\": []"), "JSON carries an empty library array, got: " + json);
            }
            finally { Cleanup(repo); }
        }

        // A blocking library hit (filename_mismatch: the file name lacks the title) refuses, lists it in library, commits nothing.
        public static void CommitMirror_BlockingLibraryHit_Refuses()
        {
            string repo = CreateRepoWithInitialCommit();
            try
            {
                string before = Head(repo);
                WriteFile(repo, "AUDIO_MIRROR/Artists/Alpha/Singles/wrong name.xml", TrackXml());
                int code = RunCaptured(repo, "Add mismatched name", out string output);
                string json = LastLine(output);
                Assert.True(code == 3, "a blocking library hit exits 3, got " + code + " json: " + json);
                Assert.Equal(before, Head(repo), "HEAD unchanged on a blocking library hit");
                Assert.True(json.Contains("\"ok\": false") && json.Contains("\"committed\": false"), "JSON says not ok and not committed");
                Assert.True(json.Contains("\"library\": [{\"kind\": \"filename_mismatch\""), "library lists the filename_mismatch hit, got: " + json);
                Assert.True(json.Contains("\"problems\": []"), "per-file problems stay empty for a library-only refusal");
            }
            finally { Cleanup(repo); }
        }

        // A fix-later kind alone (duplicate) is reported in library but does not refuse: the commit goes through.
        public static void CommitMirror_FixLaterLibraryHitOnly_DoesNotRefuse()
        {
            string repo = CreateRepoWithInitialCommit();
            try
            {
                string before = Head(repo);
                WriteFile(repo, Rel, TrackXml());
                WriteFile(repo, "AUDIO_MIRROR/Artists/Alpha/Singles/Alpha - Song 2.xml", TrackXml(album: "Album2"));
                int code = RunCaptured(repo, "Add duplicate", out string output);
                string json = LastLine(output);
                Assert.True(code == 0, "a fix-later hit does not refuse, got " + code + " json: " + json);
                Assert.True(Head(repo) != before, "HEAD moved when only fix-later kinds were found");
                Assert.True(json.Contains("\"committed\": true"), "JSON says committed");
                Assert.True(json.Contains("\"kind\": \"duplicate\""), "the duplicate hit is still listed in library, got: " + json);
            }
            finally { Cleanup(repo); }
        }

        // A Parser failure (no AUDIO_MIRROR folder) becomes result.Error: exit 1, nothing committed, one JSON line last.
        public static void CommitMirror_ParserException_GivesErrorJson()
        {
            string repo = CreateRepoWithInitialCommit();
            try
            {
                string before = Head(repo);
                int code = RunCaptured(repo, "Add nothing", out string output);
                string json = LastLine(output);
                Assert.True(code == 1, "a Parser exception exits 1, got " + code);
                Assert.Equal(before, Head(repo), "HEAD unchanged on a Parser exception");
                Assert.True(json.StartsWith("{") && json.EndsWith("}"), "the last stdout line is still one JSON object");
                Assert.True(json.Contains("\"ok\": false") && json.Contains("\"committed\": false"), "JSON says not ok and not committed");
                Assert.True(!json.Contains("\"error\": \"\""), "JSON carries the Parser error text");
            }
            finally { Cleanup(repo); }
        }
    }
}
