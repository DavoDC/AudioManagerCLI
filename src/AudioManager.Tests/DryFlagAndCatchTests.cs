using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace AudioManager
{
    /// <summary>
    /// Covers two fixes: analysis mode rejects --dry-run (it has no dry path and would regenerate
    /// the mirror and auto-commit), and the return-in-catch sites that used to fail silently now
    /// log a [WARN] line with the path and exception message. Return values are unchanged.
    ///
    /// Not covered here (need a real library or mirror tree, or are unreachable without one):
    /// MusicIntegrator.FindDuplicateInMirror / BuildMirrorIndex (read Constants paths),
    /// AudioMirrorCommitter.RunGitDetailed catch (needs a broken git install), TracksJson.TryGetAddedDate
    /// and ReportWriter git-hash catches (best-effort by design, fixed Constants paths).
    /// </summary>
    internal static class DryFlagAndCatchTests
    {
        private static string CaptureConsole(Action action)
        {
            var capture = new StringWriter();
            TextWriter original = Console.Out;
            Console.SetOut(capture);
            try { action(); }
            finally { Console.SetOut(original); }
            return capture.ToString();
        }

        private static string NewTempDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), "AudioManagerTest_" + Guid.NewGuid());
            Directory.CreateDirectory(dir);
            return dir;
        }

        // ---- Item 1: analysis mode rejects --dry-run (Program.cs mode dispatch) ----

        public static void AnalysisRejectsDryRun_AnalysisWithDryRun_IsRejected()
        {
            Assert.True(Program.AnalysisRejectsDryRun(1, true),
                "analysis mode (1) with --dry-run must be rejected, it would otherwise regen and auto-commit");
        }

        public static void AnalysisRejectsDryRun_AnalysisWithoutDryRun_IsAccepted()
        {
            Assert.True(!Program.AnalysisRejectsDryRun(1, false),
                "analysis mode without --dry-run must still run");
        }

        public static void AnalysisRejectsDryRun_IntegrateAndTagfixWithDryRun_AreAccepted()
        {
            Assert.True(!Program.AnalysisRejectsDryRun(2, true), "integrate --dry-run must not be affected");
            Assert.True(!Program.AnalysisRejectsDryRun(3, true), "tagfix --dry-run must not be affected");
        }

        public static void AddIndexKey_SameKeyAnyOrder_KeepsSmallestPath()
        {
            var a = new System.Collections.Concurrent.ConcurrentDictionary<string, string>();
            MusicIntegrator.AddIndexKey(a, "k", @"C:\m\B.xml"); MusicIntegrator.AddIndexKey(a, "k", @"C:\m\A.xml");
            var b = new System.Collections.Concurrent.ConcurrentDictionary<string, string>();
            MusicIntegrator.AddIndexKey(b, "k", @"C:\m\A.xml"); MusicIntegrator.AddIndexKey(b, "k", @"C:\m\B.xml");
            Assert.Equal(@"C:\m\A.xml", a["k"], "duplicate pick must not depend on arrival order");
            Assert.Equal(@"C:\m\A.xml", b["k"], "duplicate pick must not depend on arrival order");
        }

        public static void FindUnknownFlag_MistypedDryRun_IsReported()
        {
            Assert.Equal("--dryrun", Program.FindUnknownFlag(new[] { "integrate", "--dryrun", "--no-input" }),
                "a mistyped --dry-run must be rejected, not run live");
            Assert.Equal("--dry-run=true", Program.FindUnknownFlag(new[] { "integrate", "--dry-run=true" }), "= form is unknown");
        }

        public static void FindUnknownFlag_KnownFlagsAndValues_AreAccepted()
        {
            Assert.True(Program.FindUnknownFlag(new[] { "integrate", "--dry-run", "--no-input", "--json-output", "--no-auto-commit", "--manifest", @"C:\t\m.xml" }) == null, "known flags + manifest path");
            Assert.True(Program.FindUnknownFlag(new[] { "tagfix", "--apply-plan" }) == null, "apply-plan");
            Assert.True(Program.FindUnknownFlag(new[] { "tagfix", "--revert", @"C:\t\j.json" }) == null, "revert with path");
            Assert.True(Program.FindUnknownFlag(new[] { "analysis", "--force-regen", "--mirror-retry" }) == null, "analysis flags");
        }

        public static void FindUnknownFlag_BareWordAfterMode_IsReported()
        {
            Assert.Equal("dry-run", Program.FindUnknownFlag(new[] { "integrate", "dry-run" }),
                "a bare dry-run word must be rejected, it would otherwise run a real integrate");
            Assert.Equal("stray", Program.FindUnknownFlag(new[] { "integrate", "--no-input", "stray" }),
                "a stray word that follows a boolean flag is not a value");
        }

        public static void FindUnknownFlag_DashVariantsOfDryRun_AreReported()
        {
            string emDash = ((char)0x2014).ToString() + "dry-run";
            string enDash = ((char)0x2013).ToString() + "dry-run";
            Assert.Equal(emDash, Program.FindUnknownFlag(new[] { "integrate", emDash }),
                "a long-dash dry-run must be rejected");
            Assert.Equal(enDash, Program.FindUnknownFlag(new[] { "integrate", enDash }),
                "a medium-dash dry-run must be rejected");
        }

        public static void FindUnknownFlag_ValueAfterValueTakingFlag_IsAccepted()
        {
            Assert.True(Program.FindUnknownFlag(new[] { "integrate", "--manifest", "m.xml" }) == null, "--manifest value");
            Assert.True(Program.FindUnknownFlag(new[] { "tagfix", "--revert", "j.json" }) == null, "--revert value");
            Assert.True(Program.FindUnknownFlag(new[] { "x", "--libfix-apply", "p.json" }) == null, "--libfix-apply value");
            Assert.True(Program.FindUnknownFlag(new[] { "x", "--libfix-revert", "j.json" }) == null, "--libfix-revert value");
            Assert.True(Program.FindUnknownFlag(new[] { "x", "--misc-revert", "j.json" }) == null, "--misc-revert value");
        }

        public static void TagfixRevertExitCode_AnySkippedOrFailed_IsOne()
        {
            Assert.True(Program.TagfixRevertExitCode(0, 0) == 0, "clean revert exits 0");
            Assert.True(Program.TagfixRevertExitCode(0, 2) == 1, "a partial revert (skipped, edited since) must exit 1");
            Assert.True(Program.TagfixRevertExitCode(1, 0) == 1, "a failed revert exits 1");
        }

        public static void ValidateTagfixArgs_RevertWithoutPath_IsRejected()
        {
            Assert.True(Program.ValidateTagfixArgs(new[] { "tagfix", "--revert" }) != null,
                "--revert with no path must be rejected, not fall through to a real run");
            Assert.True(Program.ValidateTagfixArgs(new[] { "tagfix", "--revert", "--dry-run" }) != null,
                "a flag after --revert is not a journal path");
            Assert.True(Program.ValidateTagfixArgs(new[] { "tagfix", "--revert", "j.json" }) == null,
                "--revert with a path is valid");
        }

        public static void ValidateTagfixArgs_DryRunWithApplyPlan_IsRejected()
        {
            Assert.True(Program.ValidateTagfixArgs(new[] { "tagfix", "--dry-run", "--apply-plan" }) != null,
                "--dry-run with --apply-plan must be rejected, it would apply for real");
            Assert.True(Program.ValidateTagfixArgs(new[] { "tagfix", "--apply-plan" }) == null, "--apply-plan alone is valid");
            Assert.True(Program.ValidateTagfixArgs(new[] { "tagfix", "--dry-run" }) == null, "--dry-run alone is valid");
        }

        public static void ValidateTagfixArgs_DryRunWithRevert_IsRejected()
        {
            Assert.True(Program.ValidateTagfixArgs(new[] { "tagfix", "--dry-run", "--revert", "j.json" }) != null,
                "--dry-run with --revert must be rejected, the revert branch ignores dry run and would run for real");
            Assert.True(Program.ValidateTagfixArgs(new[] { "tagfix", "--revert", "j.json" }) == null, "--revert alone is valid");
        }

        // ---- Item 2a: StatsJson library byte walk (Doer/Analyser/StatsJson.cs) ----

        public static void StatsJsonLibraryBytes_MissingFolder_ReturnsZeroAndWarnsWithPath()
        {
            string dir = NewTempDir();
            try
            {
                string missing = Path.Combine(dir, "no-such-audio-folder");
                long bytes = -1;
                string output = CaptureConsole(() => bytes = StatsJson.TryGetLibraryBytes(missing));

                Assert.True(bytes == 0, $"Unreadable library must still report 0 bytes, got {bytes}");
                Assert.True(output.IndexOf("[WARN]", StringComparison.Ordinal) >= 0,
                    "Unreadable library folder must log a [WARN] line");
                Assert.True(output.IndexOf(missing, StringComparison.Ordinal) >= 0,
                    "The [WARN] line should include the library path");
            }
            finally { Directory.Delete(dir, true); }
        }

        public static void StatsJsonLibraryBytes_ReadableFolder_SumsMp3SizesWithoutWarning()
        {
            string dir = NewTempDir();
            try
            {
                string sub = Path.Combine(dir, "Artist");
                Directory.CreateDirectory(sub);
                File.WriteAllBytes(Path.Combine(dir, "a.mp3"), new byte[3]);
                File.WriteAllBytes(Path.Combine(sub, "b.mp3"), new byte[5]);
                File.WriteAllBytes(Path.Combine(sub, "ignored.txt"), new byte[100]);

                long bytes = -1;
                string output = CaptureConsole(() => bytes = StatsJson.TryGetLibraryBytes(dir));

                Assert.True(bytes == 8, $"Expected 8 mp3 bytes, got {bytes}");
                Assert.True(output.IndexOf("[WARN]", StringComparison.Ordinal) < 0,
                    "A readable library must not warn");
            }
            finally { Directory.Delete(dir, true); }
        }

        // ---- Item 2b: MusicIntegrator.DeriveLibraryPathFromMirrorPath (instance, private) ----

        public static void DeriveLibraryPath_InvalidChars_ReturnsInputAndWarns()
        {
            string dir = NewTempDir();
            try
            {
                var integrator = new MusicIntegrator(dir);
                // Concatenate, not Path.Combine: Combine rejects the invalid '|' in the test itself.
                string badXml = Constants.MirrorFolderPath + "\\bad|name.xml";
                MethodInfo method = typeof(MusicIntegrator).GetMethod(
                    "DeriveLibraryPathFromMirrorPath", BindingFlags.Instance | BindingFlags.NonPublic);

                string result = null;
                string output = CaptureConsole(() => result = (string)method.Invoke(integrator, new object[] { badXml }));

                Assert.Equal(badXml, result, "Failure must still return the mirror XML path unchanged");
                Assert.True(output.IndexOf("[WARN]", StringComparison.Ordinal) >= 0,
                    "Path derivation failure must log a [WARN] line");
                Assert.True(output.IndexOf(badXml, StringComparison.Ordinal) >= 0,
                    "The [WARN] line should include the mirror XML path");
            }
            finally { Directory.Delete(dir, true); }
        }

        // ---- Item 2c: MusicIntegrator.IsAlbumFolderCompilation (instance, private) ----

        public static void IsAlbumFolderCompilation_InvalidPath_ReturnsFalseAndWarns()
        {
            string dir = NewTempDir();
            try
            {
                var integrator = new MusicIntegrator(dir);
                string badXml = "C:\\Music|Mirror\\Album\\track.xml";
                MethodInfo method = typeof(MusicIntegrator).GetMethod(
                    "IsAlbumFolderCompilation", BindingFlags.Instance | BindingFlags.NonPublic);

                bool result = true;
                string output = CaptureConsole(() => result = (bool)method.Invoke(integrator, new object[] { badXml }));

                Assert.True(!result, "Unreadable album must still report not-a-compilation");
                Assert.True(output.IndexOf("[WARN]", StringComparison.Ordinal) >= 0,
                    "Compilation check failure must log a [WARN] line");
                Assert.True(output.IndexOf(badXml, StringComparison.Ordinal) >= 0,
                    "The [WARN] line should include the XML path");
            }
            finally { Directory.Delete(dir, true); }
        }
    }
}
