using System;
using System.IO;
using AudioManager.Code.Modules;

namespace AudioManager
{
    /// <summary>
    /// The real integrate pre-flight runs the dry TagFixer and scan-ahead, which print scan lines that the real pass
    /// prints again. Pre-flight must be quiet, but the MAX_PATH gate output and the "gate passed" line must still print,
    /// and Console.Out must be restored afterwards. Temp folders only, under %TEMP%.
    /// </summary>
    internal static class PreflightQuietTests
    {
        private static string TempRoot()
        {
            return Path.Combine(Path.GetTempPath(), "AmPreflightQuiet_" + Guid.NewGuid().ToString("N").Substring(0, 8));
        }

        public static void PreflightMirrorGate_PrintsNoScanLines_AndRestoresConsoleOut()
        {
            string root = TempRoot();
            var originalOut = Console.Out;
            try
            {
                string lib = Path.Combine(root, "Audio");
                string newMusic = Path.Combine(root, "NewMusic");
                Directory.CreateDirectory(newMusic);
                TagFixerTests.CreateSilentMp3Fixture(newMusic, "fixture.mp3",
                    "Song Title (feat. Featured Artist)", "Album Name (Deluxe Edition)", "Primary Artist");
                string mirror = Path.Combine(root, "AudioMirror", "AUDIO_MIRROR");

                var captured = new StringWriter();
                Console.SetOut(captured);
                bool clear;
                try
                {
                    var integrator = new MusicIntegrator(lib, dryRun: false);
                    clear = integrator.PreflightMirrorGate(newMusic, mirror);
                }
                finally
                {
                    Console.SetOut(originalOut);
                }

                string output = captured.ToString();
                Assert.True(clear, "a normal batch must pass pre-flight");
                Assert.True(!output.Contains("Fixing music tags"),
                    "pre-flight must not print the dry TagFixer header; captured output was: " + output);
                Assert.True(!output.Contains("Scan-ahead: reading"),
                    "pre-flight must not print the scan-ahead line; captured output was: " + output);
                Assert.True(output.Contains("Pre-flight: mirror path gate passed"),
                    "the gate-passed line must still print; captured output was: " + output);
                Assert.True(ReferenceEquals(originalOut, Console.Out),
                    "Console.Out must be restored to the same writer after pre-flight");
            }
            finally
            {
                Console.SetOut(originalOut);
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }

        public static void QuitAfterTagFixNote_SaysTagsAlreadyRewritten()
        {
            string note = MusicIntegrator.QuitAfterTagFixNote();
            Assert.True(note.Contains("already rewritten"),
                "the quit note must say NewMusic tags were already rewritten; got: " + note);
            Assert.True(note.Contains("tagfix --dry-run --json-output"),
                "the quit note must name the plan flow; got: " + note);
        }
    }
}
