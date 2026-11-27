using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using AudioManager.Code.Modules;

namespace AudioManager
{
    /// <summary>
    /// GUI-82 item f: after the real TagFixer rewrites NewMusic, MusicIntegrator re-reads every manifest-selected file
    /// through the same dry projection the pre-flight uses. A residual needsManual row (or a file that cannot be read
    /// back) blocks the batch before any file moves into the library. The real TagFixer cannot be driven through the
    /// public constructor (static Constants paths), so these tests call PostRewriteGate on a temp folder that already
    /// holds the bad file the real rewrite would have left behind.
    /// </summary>
    internal static class PostRewriteGateTests
    {
        private static string TempRoot()
        {
            return Path.Combine(Path.GetTempPath(), "AmPostRewrite_" + Guid.NewGuid().ToString("N").Substring(0, 8));
        }

        private static string Sha256(string path)
        {
            using (var sha = SHA256.Create())
            using (var fs = File.OpenRead(path))
                return BitConverter.ToString(sha.ComputeHash(fs));
        }

        private static IntegrationRoots RootsUnder(string root)
        {
            return new IntegrationRoots(
                Path.Combine(root, "NewMusic"),
                Path.Combine(root, "Audio"),
                Path.Combine(root, "AudioMirror", "AUDIO_MIRROR"),
                Path.Combine(root, "logs"));
        }

        // A rewritten file whose Album is blank: FieldRules blocks a blank Album (bad_field), and no TagFixer step fixes it.
        public static void PostRewriteGate_RewrittenFileWithBlankAlbum_BlocksWithBadField()
        {
            string root = TempRoot();
            try
            {
                var roots = RootsUnder(root);
                Directory.CreateDirectory(roots.NewMusic);
                TagFixerTests.CreateSilentMp3Fixture(roots.NewMusic, "blank.mp3", "Song Title", "", "Primary Artist", 2020);

                var integrator = new MusicIntegrator(roots.Library, roots);
                bool clear = integrator.PostRewriteGate(roots.NewMusic);

                Assert.True(!clear, "a rewritten file with a blank album blocks the batch before any move");
                Assert.True(integrator.NeedsManualBlocked != null && integrator.NeedsManualBlocked.Files.Count == 1, "the block lists one file");
                Assert.Equal("blank.mp3", integrator.NeedsManualBlocked.Files[0].file, "the block names the file");
                Assert.Equal("bad_field", integrator.NeedsManualBlocked.Files[0].rule, "the block uses the existing bad_field rule id");
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }

        // A file the real TagFixer left clean must pass, and the check itself writes nothing.
        public static void PostRewriteGate_CleanRewrittenFile_PassesAndWritesNothing()
        {
            string root = TempRoot();
            try
            {
                var roots = RootsUnder(root);
                Directory.CreateDirectory(roots.NewMusic);
                string file = TagFixerTests.CreateSilentMp3Fixture(roots.NewMusic, "Primary Artist - Song Title.mp3", "Song Title", "Album Name", "Primary Artist", 2020);
                string hashBefore = Sha256(file);

                var integrator = new MusicIntegrator(roots.Library, roots);
                bool clear = integrator.PostRewriteGate(roots.NewMusic);

                Assert.True(clear, "a clean rewritten file passes the post-rewrite gate");
                Assert.True(integrator.NeedsManualBlocked == null, "no block is recorded for a clean batch");
                Assert.Equal(hashBefore, Sha256(file), "the post-rewrite gate is read-only");
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }

        // The TagFixer error path (ProcessFile's outer catch) leaves a file the check cannot read back: it must block as rule "error".
        public static void PostRewriteGate_UnreadableFile_BlocksAsError()
        {
            string root = TempRoot();
            try
            {
                var roots = RootsUnder(root);
                Directory.CreateDirectory(roots.NewMusic);
                File.WriteAllText(Path.Combine(roots.NewMusic, "garbage.mp3"), "this is not an mp3 file");

                var integrator = new MusicIntegrator(roots.Library, roots);
                bool clear = integrator.PostRewriteGate(roots.NewMusic);

                Assert.True(!clear, "a file that cannot be read back blocks the batch");
                Assert.True(integrator.NeedsManualBlocked != null && integrator.NeedsManualBlocked.Files.Count == 1, "the block lists the unreadable file");
                Assert.Equal("garbage.mp3", integrator.NeedsManualBlocked.Files[0].file, "the block names the unreadable file");
                Assert.Equal("error", integrator.NeedsManualBlocked.Files[0].rule, "the block uses the existing error rule id");
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }

        // The manifest filter applies as in the pre-flight: a declined bad file is not read back, so it cannot block.
        public static void PostRewriteGate_ManifestDeclinedBadFile_DoesNotBlock()
        {
            string root = TempRoot();
            try
            {
                var roots = RootsUnder(root);
                Directory.CreateDirectory(roots.NewMusic);
                TagFixerTests.CreateSilentMp3Fixture(roots.NewMusic, "blank.mp3", "Song Title", "", "Primary Artist", 2020);
                TagFixerTests.CreateSilentMp3Fixture(roots.NewMusic, "good.mp3", "Other Song", "Album Name", "Primary Artist", 2020);
                var manifest = IntegrationManifest.Parse(@"[{""filename"": ""good.mp3"", ""sourceRel"": ""good.mp3"", ""artist"": ""Primary Artist"", ""title"": ""Other Song""}]", out string error);
                Assert.True(error == null, $"manifest parses, got: {error}");

                var integrator = new MusicIntegrator(roots.Library, roots);
                bool clear = integrator.PostRewriteGate(roots.NewMusic, manifest);

                Assert.True(clear, "a bad file the manifest declines is not read back and does not block");
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }
    }
}
