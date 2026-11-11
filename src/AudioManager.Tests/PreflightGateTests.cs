using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using AudioManager.Code.Modules;

namespace AudioManager
{
    /// <summary>
    /// The real integrate run checks the pre-move MAX_PATH gate on the FINAL tag values before the real TagFixer
    /// rewrites anything. A blocked batch must leave NewMusic untouched (bytes, tags and file name). The public
    /// constructor reads static Constants paths, so these tests drive PreflightMirrorGate with temp folders only.
    /// </summary>
    internal static class PreflightGateTests
    {
        private static string TempRoot()
        {
            return Path.Combine(Path.GetTempPath(), "AmPreflight_" + Guid.NewGuid().ToString("N").Substring(0, 8));
        }

        private static string Sha256(string path)
        {
            using (var sha = SHA256.Create())
            using (var fs = File.OpenRead(path))
                return BitConverter.ToString(sha.ComputeHash(fs));
        }

        // A mirror path padded so a planned Artists\ destination projects to target characters (the Misc and Artists
        // folders differ in length, so target sits above 260 for both).
        private static string MirrorProjectingTo(string root, string lib, int target)
        {
            string mirror0 = Path.Combine(root, "AudioMirror", "AUDIO_MIRROR");
            string probe = Path.Combine(lib, "Artists", "Primary Artist", "Primary Artist - Song Title.mp3");
            int got = Reflector.ProjectedStagingTempPath(mirror0, lib, probe).Length;
            return mirror0 + new string('x', target - got);
        }

        // Fixture whose TagFixer fix changes its tags: a feat. parenthetical in the title and a Deluxe album suffix.
        private static string MakeFixture(string newMusic)
        {
            Directory.CreateDirectory(newMusic);
            return TagFixerTests.CreateSilentMp3Fixture(newMusic, "fixture.mp3",
                "Song Title (feat. Featured Artist)", "Album Name (Deluxe Edition)", "Primary Artist");
        }

        public static void Preflight_BlockedBatch_LeavesNewMusicBytesTagsAndNameUntouched()
        {
            string root = TempRoot();
            try
            {
                string lib = Path.Combine(root, "Audio");
                string newMusic = Path.Combine(root, "NewMusic");
                string src = MakeFixture(newMusic);
                string hashBefore = Sha256(src);
                string mirror = MirrorProjectingTo(root, lib, 270);

                var integrator = new MusicIntegrator(lib, dryRun: false);
                bool clear = integrator.PreflightMirrorGate(newMusic, mirror);

                Assert.True(!clear, "a planned destination projecting past the limit must block the batch in pre-flight");
                Assert.True(integrator.Blocked != null, "the block must be recorded so the routing record can say path_too_long");
                Assert.Equal("path_too_long", integrator.Blocked.Status, "the routing record status is path_too_long");
                Assert.Equal(hashBefore, Sha256(src), "pre-flight must not rewrite the NewMusic file bytes");
                Assert.Equal("fixture.mp3", Path.GetFileName(Directory.GetFiles(newMusic)[0]), "pre-flight must not rename the file");
                Assert.Equal("1", Directory.GetFiles(newMusic).Length.ToString(), "pre-flight must not create or move files");

                using (TagLib.File f = TagLib.File.Create(src))
                {
                    Assert.Equal("Song Title (feat. Featured Artist)", f.Tag.Title, "NewMusic title tag must be untouched after a blocked pre-flight");
                    Assert.Equal("Album Name (Deluxe Edition)", f.Tag.Album, "NewMusic album tag must be untouched after a blocked pre-flight");
                }
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }

        public static void Preflight_CleanBatch_PassesAndWritesNothing()
        {
            string root = TempRoot();
            try
            {
                string lib = Path.Combine(root, "Audio");
                string newMusic = Path.Combine(root, "NewMusic");
                string src = MakeFixture(newMusic);
                string hashBefore = Sha256(src);
                string mirror = Path.Combine(root, "AudioMirror", "AUDIO_MIRROR");

                var integrator = new MusicIntegrator(lib, dryRun: false);
                bool clear = integrator.PreflightMirrorGate(newMusic, mirror);

                Assert.True(clear, "a normal batch must pass pre-flight");
                Assert.True(integrator.Blocked == null, "no block must be recorded for a normal batch");
                Assert.Equal(hashBefore, Sha256(src), "pre-flight is read-only: the dry TagFixer must not rewrite bytes");
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }
    }
}
