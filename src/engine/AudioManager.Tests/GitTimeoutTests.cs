using System;
using System.Diagnostics;
using System.Threading;

namespace AudioManager
{
    /// <summary>
    /// Tests for AudioMirrorCommitter.RunProcessBounded, the shared bounded wait used by every git call.
    /// A process still running after the timeout must be killed and reported as timed out;
    /// a fast process must return its stdout as before.
    /// </summary>
    internal static class GitTimeoutTests
    {
        private static ProcessStartInfo Psi(string exe, string args)
        {
            return new ProcessStartInfo(exe, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
        }

        // True once the process no longer runs (gone, or exited). Polls for up to ~2 s.
        private static bool IsGone(int pid)
        {
            for (int i = 0; i < 20; i++)
            {
                try
                {
                    using (Process p = Process.GetProcessById(pid))
                    {
                        if (p.HasExited) return true;
                    }
                }
                catch (ArgumentException)
                {
                    return true;
                }
                catch (InvalidOperationException)
                {
                    return true;
                }
                Thread.Sleep(100);
            }
            return false;
        }

        public static void RunProcessBounded_HungProcess_TimesOutAndKillsProcess()
        {
            var sw = Stopwatch.StartNew();
            int pid;
            AudioMirrorCommitter.GitRunResult result = AudioMirrorCommitter.RunProcessBounded(
                Psi("powershell", "-NoProfile -Command \"Start-Sleep -Seconds 8\""), 500, out pid);
            sw.Stop();

            Assert.True(result.TimedOut, "a process still running after the timeout must report TimedOut");
            Assert.True(result.ExitCode == -1, "a timed-out run must use the failure exit code -1");
            Assert.True(result.Stderr.Contains("timed out"), "the failure message must name the timeout");
            Assert.True(sw.ElapsedMilliseconds < 3000, "the bounded wait must return within ~3 s, took " + sw.ElapsedMilliseconds + " ms");
            Assert.True(pid > 0, "the started process id must be reported so the kill can be checked");
            Assert.True(IsGone(pid), "the timed-out process must be killed");
        }

        public static void RunProcessBounded_FastCommand_ReturnsOutput()
        {
            int pid;
            AudioMirrorCommitter.GitRunResult result = AudioMirrorCommitter.RunProcessBounded(
                Psi("cmd", "/c echo hi"), 30000, out pid);

            Assert.True(!result.TimedOut, "a fast command must not time out");
            Assert.True(result.ExitCode == 0, "a fast command must keep its exit code");
            Assert.Equal("hi", result.Stdout.Trim(), "stdout must be returned as before");
        }
    }
}
