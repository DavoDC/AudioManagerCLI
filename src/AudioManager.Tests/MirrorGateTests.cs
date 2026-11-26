using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace AudioManager
{
    /// <summary>
    /// Tests for MirrorGate (BL-317 / GUI-140): the health gate every AudioMirror commit path runs first, the
    /// --validate-mirror and --commit-mirror commands, and the JSON last line the GUI parses.
    /// Every test runs against a throwaway git repo under the temp folder; nothing touches the real mirror.
    /// </summary>
    internal static class MirrorGateTests
    {
        private static string CreateTempGitRepo()
        {
            string dir = Path.Combine(Path.GetTempPath(), "am_mirrorgate_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            RunProcess("git", "init", dir);
            RunProcess("git", "config user.email test@test.com", dir);
            RunProcess("git", "config user.name Test", dir);
            return dir;
        }

        // Repo with one commit (LastRunInfo.txt only), so later 'git add' calls never fail on a missing path.
        private static string CreateRepoWithInitialCommit()
        {
            string dir = CreateTempGitRepo();
            WriteFile(dir, "LastRunInfo.txt", "run");
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

        private const string GoodRel = "AUDIO_MIRROR/Artists/Alpha/Album1/a.xml";

        // A complete mirror XML with every element TrackXML.Read requires. Year is written as given.
        private static string TrackXml(string title = "Song", string artists = "Alpha", string year = "2020")
        {
            return "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<Track>"
                + "<Title>" + title + "</Title>"
                + "<Artists>" + artists + "</Artists>"
                + "<Album>Album1</Album>"
                + "<Year>" + year + "</Year>"
                + "<TrackNumber>1</TrackNumber>"
                + "<Genres>Rock</Genres>"
                + "<Length>200</Length>"
                + "<AlbumCover><Count>1</Count><Width>1000</Width><Height>1000</Height></AlbumCover>"
                + "<Compilation>true</Compilation>"
                + "</Track>";
        }

        // Runs a gate command with console output captured; returns the exit code and the captured text.
        private static int RunCaptured(string repo, bool commit, string message, out string output)
        {
            var originalOut = Console.Out;
            var capture = new StringWriter();
            int code;
            try
            {
                Console.SetOut(capture);
                code = MirrorGate.RunCommand(repo, commit, message);
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

        public static void Validate_CleanSet_NoProblems()
        {
            string repo = CreateRepoWithInitialCommit();
            try
            {
                WriteFile(repo, GoodRel, TrackXml());
                MirrorGateResult r = MirrorGate.Validate(repo);
                Assert.True(r.Error.Length == 0, "no git error: " + r.Error);
                Assert.True(r.Problems.Count == 0, "clean set has no problems");
                Assert.True(r.Checked == 1, "one changed XML was checked");
                Assert.True(r.Ok, "clean set is ok");
            }
            finally { Cleanup(repo); }
        }

        public static void Validate_YearOutOfRange_Refused()
        {
            string repo = CreateRepoWithInitialCommit();
            try
            {
                WriteFile(repo, GoodRel, TrackXml(year: "201"));
                MirrorGateResult r = MirrorGate.Validate(repo);
                Assert.True(r.Problems.Count == 1, "one problem for a Year 201");
                Assert.Equal(GoodRel, r.Problems[0].File, "problem names the repo-relative file");
                Assert.True(r.Problems[0].Reason.Contains("Year"), "reason names the Year: " + r.Problems[0].Reason);
                Assert.True(!r.Ok, "a problem makes the set not ok");
            }
            finally { Cleanup(repo); }
        }

        public static void Validate_MalformedXml_Refused()
        {
            string repo = CreateRepoWithInitialCommit();
            try
            {
                WriteFile(repo, GoodRel, "<Track><Title>broken");
                MirrorGateResult r = MirrorGate.Validate(repo);
                Assert.True(r.Problems.Count == 1, "malformed XML is one problem");
                Assert.Equal(GoodRel, r.Problems[0].File, "malformed problem names the file");
            }
            finally { Cleanup(repo); }
        }

        public static void Validate_MissingTitle_Refused()
        {
            string repo = CreateRepoWithInitialCommit();
            try
            {
                WriteFile(repo, GoodRel, TrackXml(title: "Missing"));
                MirrorGateResult r = MirrorGate.Validate(repo);
                Assert.True(r.Problems.Count == 1, "a Title of Missing is refused");
                Assert.True(r.Problems[0].Reason.Contains("Title"), "reason names the Title: " + r.Problems[0].Reason);
            }
            finally { Cleanup(repo); }
        }

        public static void Validate_DeletedFile_NotValidated()
        {
            string repo = CreateRepoWithInitialCommit();
            try
            {
                // A committed bad-year file that was then deleted from the working tree: git reports ' D', so it is skipped.
                WriteFile(repo, GoodRel, TrackXml(year: "201"));
                RunProcess("git", "add -A", repo);
                RunProcess("git", "commit -m bad", repo);
                File.Delete(Path.Combine(repo, GoodRel.Replace('/', Path.DirectorySeparatorChar)));
                MirrorGateResult r = MirrorGate.Validate(repo);
                Assert.True(r.Problems.Count == 0, "a deleted file is not validated");
                Assert.True(r.Checked == 0, "nothing was checked");
            }
            finally { Cleanup(repo); }
        }

        public static void CommitMirror_Refused_LeavesHeadUnchanged()
        {
            string repo = CreateRepoWithInitialCommit();
            try
            {
                string before = Head(repo);
                WriteFile(repo, GoodRel, TrackXml(year: "201"));
                int code = RunCaptured(repo, true, "should not land", out string output);
                Assert.True(code == 3, "refused commit exits 3, got " + code);
                Assert.Equal(before, Head(repo), "HEAD unchanged after refusal");
                Assert.True(LastLine(output).Contains("\"committed\": false"), "JSON says not committed");
            }
            finally { Cleanup(repo); }
        }

        public static void CommitMirror_CleanSet_Commits()
        {
            string repo = CreateRepoWithInitialCommit();
            try
            {
                string before = Head(repo);
                WriteFile(repo, GoodRel, TrackXml());
                int code = RunCaptured(repo, true, "Add healthy album", out string output);
                Assert.True(code == 0, "clean commit exits 0, got " + code);
                Assert.True(Head(repo) != before, "HEAD moved on a clean set");
                Assert.Equal("Add healthy album", RunGitOut(repo, "log -1 --format=%s"), "commit subject is the message");
                Assert.True(LastLine(output).Contains("\"committed\": true"), "JSON says committed");
            }
            finally { Cleanup(repo); }
        }

        public static void CommitMirror_JsonLastLine_Parses()
        {
            string repo = CreateRepoWithInitialCommit();
            try
            {
                WriteFile(repo, GoodRel, TrackXml(year: "201"));
                RunCaptured(repo, true, "x", out string output);
                string last = LastLine(output);
                var ser = new JavaScriptSerializer();
                var obj = (System.Collections.Generic.IDictionary<string, object>)ser.DeserializeObject(last);
                foreach (string key in new[] { "ok", "committed", "checked", "problems", "error" })
                    Assert.True(obj.ContainsKey(key), "JSON has key " + key);
                Assert.True((bool)obj["ok"] == false, "refused set is not ok");
                Assert.True((bool)obj["committed"] == false, "refused set is not committed");
                Assert.Equal("", (string)obj["error"], "error is empty string when none");
                var problems = (System.Collections.IEnumerable)obj["problems"];
                int count = 0;
                foreach (object _ in problems) count++;
                Assert.True(count == 1, "one problem in JSON");
            }
            finally { Cleanup(repo); }
        }

        public static void CommitMirror_EmptyMessage_ExitsOneAndCommitsNothing()
        {
            string repo = CreateRepoWithInitialCommit();
            try
            {
                string before = Head(repo);
                WriteFile(repo, GoodRel, TrackXml());
                int code = RunCaptured(repo, true, "   ", out string output);
                Assert.True(code == 1, "an empty message exits 1, got " + code);
                Assert.Equal(before, Head(repo), "HEAD unchanged without a message");
                Assert.True(LastLine(output).Contains("\"ok\": false"), "JSON says not ok");
                Assert.True(!LastLine(output).Contains("\"error\": \"\""), "JSON carries a non-empty error");
            }
            finally { Cleanup(repo); }
        }

        public static void AutoCommit_RefusesOnBadYear()
        {
            string repo = CreateRepoWithInitialCommit();
            try
            {
                string before = Head(repo);
                WriteFile(repo, GoodRel, TrackXml(year: "201"));
                var originalOut = Console.Out;
                var capture = new StringWriter();
                bool result;
                try
                {
                    Console.SetOut(capture);
                    result = AudioMirrorCommitter.TryCommitAt(repo, libCheckerClean: true, trigger: CommitTrigger.Integration);
                }
                finally { Console.SetOut(originalOut); }
                Assert.True(!result, "auto-commit reports not committed on a bad year");
                Assert.Equal(before, Head(repo), "auto-commit left HEAD unchanged");
                Assert.True(capture.ToString().Contains("[WARN]"), "auto-commit prints a [WARN] line");
            }
            finally { Cleanup(repo); }
        }
    }
}
