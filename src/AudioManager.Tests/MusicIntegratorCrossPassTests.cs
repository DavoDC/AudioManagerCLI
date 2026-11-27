using System;
using System.Collections.Generic;
using System.IO;
using AudioManager.Code.Modules;

namespace AudioManager
{
    /// <summary>
    /// Pins three behaviours that span the pre-flight and the real pass of one MusicIntegrator instance, exactly as the
    /// code behaves today (see the decomposition design, section 8). Each test builds temp roots only and never touches
    /// the real library, NewMusic or mirror. They describe current behaviour, not endorsed behaviour: if one of them
    /// starts failing, a cross-pass rule changed on purpose and the pin is to be updated with it.
    /// </summary>
    internal static class MusicIntegratorCrossPassTests
    {
        private static string TempRoot()
        {
            return Path.Combine(Path.GetTempPath(), "AmCrossPass_" + Guid.NewGuid().ToString("N").Substring(0, 8));
        }

        private static IntegrationRoots RootsUnder(string root)
        {
            return new IntegrationRoots(
                Path.Combine(root, "NewMusic"),
                Path.Combine(root, "Audio"),
                Path.Combine(root, "AudioMirror", "AUDIO_MIRROR"),
                Path.Combine(root, "logs"));
        }

        private static void WriteMiscXml(string mirror, string name, string artist)
        {
            string dir = Path.Combine(mirror, Constants.MiscDir);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, name + ".xml"),
                "<Song><Artists>" + artist + "</Artists><Title>" + name + "</Title></Song>");
        }

        // The candidates dictionary is reset per scan-ahead, so an artist promoted by the first scan-ahead (the pre-flight)
        // is not migrated when the second scan-ahead (the real pass) no longer promotes them.
        public static void MiscCandidates_AddedByFirstScanAhead_ClearedBySecondScanAheadThatDoesNotPromote()
        {
            string root = TempRoot();
            try
            {
                var roots = RootsUnder(root);
                WriteMiscXml(roots.Mirror, "misc one", "Promoted Artist");
                WriteMiscXml(roots.Mirror, "misc two", "Promoted Artist");
                Directory.CreateDirectory(roots.NewMusic);
                string first = TagFixerTests.CreateSilentMp3Fixture(roots.NewMusic, "a.mp3", "Song A", "Album A", "Promoted Artist");
                string second = TagFixerTests.CreateSilentMp3Fixture(roots.NewMusic, "b.mp3", "Song B", "Album B", "Unrelated Artist");

                var integrator = new MusicIntegrator(roots.Library, roots);

                // 2 Misc songs + 1 in batch = 3 -> promoted.
                var promoted = integrator.RunScanAhead(new[] { first });
                Assert.True(promoted.Contains("Promoted Artist"), "first batch must promote the artist (precondition)");
                Assert.True(integrator._miscMigrationCandidates.ContainsKey("Promoted Artist"), "first scan-ahead records the candidate");

                // The second batch holds only an unrelated artist: nothing is promoted this time.
                var promotedAgain = integrator.RunScanAhead(new[] { second });
                Assert.True(!promotedAgain.Contains("Promoted Artist"), "second batch must not promote the artist (precondition)");

                Assert.True(!integrator._miscMigrationCandidates.ContainsKey("Promoted Artist"), "the second scan-ahead clears the candidate the first one recorded");
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }

        // The pre-flight projects the final (tag-fixed) tags in both modes. The fixture's raw title carries a
        // "(feat. ...)" parenthetical that the fix strips, so the raw destination is longer than the fixed one. The
        // mirror is padded so the raw path is over the limit and the fixed path is under it: both gates pass because
        // both use the fixed title.
        public static void PreflightMirrorGate_RealMode_ProjectsFixedTags_SameAsDryMode()
        {
            string root = TempRoot();
            try
            {
                var roots = RootsUnder(root);
                Directory.CreateDirectory(roots.NewMusic);
                TagFixerTests.CreateSilentMp3Fixture(roots.NewMusic, "fixture.mp3",
                    "Song Title (feat. Featured Artist)", "Album Name", "Primary Artist", 2020);
                string mirror = MirrorPadded(root, PadExtra);

                var real = new MusicIntegrator(roots.Library, roots);
                bool realClear = real.PreflightMirrorGate(roots.NewMusic, mirror);

                // Roots constructor (temp roots only), then flip the private dryRun flag: the dryRun constructor would read the real mirror.
                var dry = new MusicIntegrator(roots.Library, roots);
                dry.dryRun = true;
                bool dryClear = dry.PreflightMirrorGate(roots.NewMusic, mirror);

                Assert.True(realClear, "real mode projects the fixed (shorter) title: passes");
                Assert.True(real.Blocked == null || real.Blocked.Files.Count == 0, "nothing is blocked in real mode");
                Assert.True(dryClear, "dry mode projects the fixed (shorter) title on the same mirror: passes");
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }

        // BL-319: a real-run pre-flight refuses a file the dry TagFixer marks needsManual (here missing_tag, no Year).
        // The refusal writes nothing: the file keeps its name and bytes and no other file appears in NewMusic.
        public static void PreflightMirrorGate_RealMode_NeedsManualFile_BlocksAndWritesNothing()
        {
            string root = TempRoot();
            try
            {
                var roots = RootsUnder(root);
                Directory.CreateDirectory(roots.NewMusic);
                string file = TagFixerTests.CreateSilentMp3Fixture(roots.NewMusic, "noyear.mp3", "Song Title", "Album Name", "Primary Artist");
                DateTime stamp = File.GetLastWriteTimeUtc(file);
                string mirror = MirrorPadded(root, 0);

                var real = new MusicIntegrator(roots.Library, roots);
                bool clear = real.PreflightMirrorGate(roots.NewMusic, mirror);

                Assert.True(!clear, "a file the dry run marks needsManual (missing Year) blocks the real pre-flight");
                Assert.True(File.Exists(file), "the blocked file is not renamed or moved");
                Assert.True(File.GetLastWriteTimeUtc(file) == stamp, "the blocked file is not rewritten");
                Assert.True(Directory.GetFiles(roots.NewMusic, "*", SearchOption.AllDirectories).Length == 1, "nothing is added to NewMusic");
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }

        // BL-319 parity: the rule the real pre-flight block names for a file is the rule the dry plan shows for that file.
        public static void PreflightBlock_RuleMatchesDryPlanRule_ForSameFile()
        {
            string root = TempRoot();
            try
            {
                var roots = RootsUnder(root);
                Directory.CreateDirectory(roots.NewMusic);
                TagFixerTests.CreateSilentMp3Fixture(roots.NewMusic, "noyear.mp3", "Song Title", "Album Name", "Primary Artist");

                var dry = new TagFixer(roots.NewMusic, true, null, null);
                Assert.True(dry.LogsByFile.ContainsKey("noyear.mp3"), "the dry plan holds the file");
                TagFixer.FixLog dryLog = dry.LogsByFile["noyear.mp3"];
                Assert.True(dryLog.NeedsManual, "the dry plan marks the file needsManual");
                string dryRule = TagFixPlanJson.ManualRule(dryLog);

                var real = new MusicIntegrator(roots.Library, roots);
                Assert.True(!real.PreflightMirrorGate(roots.NewMusic, MirrorPadded(root, 0)), "the real pre-flight blocks the batch");
                Assert.True(real.NeedsManualBlocked != null && real.NeedsManualBlocked.Files.Count == 1, "the real block lists one file");
                Assert.Equal("noyear.mp3", real.NeedsManualBlocked.Files[0].file, "the block names the same file");
                Assert.Equal(dryRule, real.NeedsManualBlocked.Files[0].rule, "the block names the same rule as the dry plan");
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }

        // BL-319 guard: a file with every field set passes the pre-flight exactly as before.
        public static void PreflightMirrorGate_RealMode_CompleteFile_Passes()
        {
            string root = TempRoot();
            try
            {
                var roots = RootsUnder(root);
                Directory.CreateDirectory(roots.NewMusic);
                TagFixerTests.CreateSilentMp3Fixture(roots.NewMusic, "complete.mp3", "Song Title", "Album Name", "Primary Artist", 2020);
                var real = new MusicIntegrator(roots.Library, roots);
                Assert.True(real.PreflightMirrorGate(roots.NewMusic, MirrorPadded(root, 0)), "a complete file passes the pre-flight");
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }

        // Measured with a probe pad of 400: the raw destination projects 163 + pad characters, the fixed one 156 + pad.
        // Pad 100 gives the raw path 263 (over the 259 limit) and the fixed path 256 (under it), so the test would fail
        // if raw tags were used.
        private const int PadExtra = 100;

        private static string MirrorPadded(string root, int extra)
        {
            return Path.Combine(root, "AudioMirror", "AUDIO_MIRROR") + new string((char)120, extra);
        }

        // The per-instance album count cache keeps the first library count, so a second call on the same
        // instance after the folder changed returns the stale value; a fresh instance sees the change (3, not 2), which
        // shows the cache is what holds the old number.
        public static void CountAlbumSongs_SecondCallAfterLibraryChange_ReturnsCachedCount()
        {
            string root = TempRoot();
            try
            {
                string lib = Path.Combine(root, "Audio");
                string album = Path.Combine(lib, Constants.ArtistsDir, "Cache Artist", "Cache Album");
                Directory.CreateDirectory(album);
                File.WriteAllBytes(Path.Combine(album, "one.mp3"), new byte[] { 1 });
                File.WriteAllBytes(Path.Combine(album, "two.mp3"), new byte[] { 1 });

                var integrator = new MusicIntegrator(lib, RootsUnder(root));
                Assert.Equal("2", integrator.CountAlbumSongs("Cache Artist", "Cache Album").ToString(), "first call counts the library files");

                File.WriteAllBytes(Path.Combine(album, "three.mp3"), new byte[] { 1 });

                Assert.Equal("2", integrator.CountAlbumSongs("Cache Artist", "Cache Album").ToString(),
                    "cached: still 2 on the same instance (would be 3 without the cache)");
                var fresh = new MusicIntegrator(lib, RootsUnder(root));
                Assert.Equal("3", fresh.CountAlbumSongs("Cache Artist", "Cache Album").ToString(), "a fresh instance sees the new file");
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }
    }
}
