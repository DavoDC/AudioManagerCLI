using System;
using System.Collections.Generic;
using System.IO;
using AudioManager.Code.Modules;

namespace AudioManager
{
    /// <summary>
    /// Pre-move MAX_PATH gate for the integrate run. The regen stages each XML at mirror.stg\rel.xml.tmp, so a
    /// planned destination whose projected staged path reaches 259+ characters must block the batch before any
    /// file moves. Path arithmetic is pure; the gate test uses real temp files only, never the real library.
    /// </summary>
    internal static class MirrorPathGateTests
    {
        private static string TempRoot()
        {
            return Path.Combine(Path.GetTempPath(), "AmMirrorGate_" + Guid.NewGuid().ToString("N").Substring(0, 8));
        }

        // An mp3 path under audioRoot whose projected staged temp path is exactly target characters long.
        private static string Mp3ProjectingTo(string mirror, string audioRoot, int target)
        {
            string probe = Path.Combine(audioRoot, "Artist", "a.mp3");
            int got = Reflector.ProjectedStagingTempPath(mirror, audioRoot, probe).Length;
            return Path.Combine(audioRoot, "Artist", new string('a', 1 + target - got) + ".mp3");
        }

        public static void ProjectedStagingTempPath_ExistingXml246_ProjectsTo254()
        {
            string root = TempRoot();
            string mirror = Path.Combine(root, "AudioMirror", "AUDIO_MIRROR");
            string audioRoot = Path.Combine(root, "Audio");
            string name = new string('a', 246 - mirror.Length - 12);
            string existingXml = Path.ChangeExtension(Path.Combine(mirror, "Artist", name + ".mp3"), ".xml");
            Assert.Equal("246", existingXml.Length.ToString(), "precondition: the existing mirror XML path is 246 characters");

            string projected = Reflector.ProjectedStagingTempPath(mirror, audioRoot, Path.Combine(audioRoot, "Artist", name + ".mp3"));

            Assert.Equal("254", projected.Length.ToString(), "the staged temp path adds .stg and .tmp (8 characters)");
            Assert.Equal(Path.Combine(mirror + ".stg", "Artist", name + ".xml") + ".tmp", projected, "projected path is mirror.stg\\rel.xml.tmp");
        }

        public static void FindOverlongMirrorPaths_Projected260_IsReported_Projected254_IsNot()
        {
            string root = TempRoot();
            string mirror = Path.Combine(root, "AudioMirror", "AUDIO_MIRROR");
            string audioRoot = Path.Combine(root, "Audio");
            string overlong = Mp3ProjectingTo(mirror, audioRoot, 260);
            string fine = Mp3ProjectingTo(mirror, audioRoot, 254);

            var found = Reflector.FindOverlongMirrorPaths(mirror, audioRoot, new[] { fine, overlong });

            Assert.Equal("1", found.Count.ToString(), "only the 260-character projection is over the limit");
            Assert.Equal(overlong, found[0].file, "the offending planned destination is named");
            Assert.Equal("260", found[0].length.ToString(), "its projected length is reported");
        }

        public static void RunMirrorPathGate_PlannedDestinationAt260_BlocksAndMovesNothing()
        {
            string root = TempRoot();
            string lib = Path.Combine(root, "Audio");
            string newMusic = Path.Combine(root, "NewMusic");
            Directory.CreateDirectory(newMusic);
            string src = Path.Combine(newMusic, "Test Artist - Gate Song.mp3");
            File.WriteAllBytes(src, new byte[] { 3 });
            var track = new Track { Artists = "Test Artist", Title = "Gate Song", Album = "Missing", Genres = "Missing" };
            var sf = new MusicIntegrator.ScannedFile
            {
                SourcePath = src,
                Track = track,
                IsReadable = true,
                LogEntry = new MusicIntegrator.LogEntry { Filename = Path.GetFileName(src), Title = track.Title, Artists = track.Artists }
            };
            var integrator = new MusicIntegrator(lib, dryRun: false);
            var newArtistFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string destDir = integrator.GetDestDir(track, newArtistFolders, new HashSet<string>(StringComparer.OrdinalIgnoreCase), out _, out _);
            string dest = Path.Combine(destDir, "Test Artist - Gate Song.mp3");

            // Pad the mirror folder name until the real planned destination projects to exactly 260.
            string mirror0 = Path.Combine(root, "AudioMirror", "AUDIO_MIRROR");
            int base0 = Reflector.ProjectedStagingTempPath(mirror0, lib, dest).Length;
            string mirror = mirror0 + new string('x', 260 - base0);
            Assert.Equal("260", Reflector.ProjectedStagingTempPath(mirror, lib, dest).Length.ToString(), "precondition: planned destination projects to 260");

            bool clear = integrator.RunMirrorPathGate(new List<MusicIntegrator.ScannedFile> { sf }, newArtistFolders, mirror);

            Assert.True(!clear, "a planned destination at 260 must block the batch");
            Assert.True(integrator.Blocked != null, "the block must be recorded on the integrator");
            Assert.Equal("path_too_long", integrator.Blocked.Status, "status is path_too_long");
            Assert.Equal("rename_or_shorten", integrator.Blocked.Action, "action is rename_or_shorten");
            Assert.Equal("260", integrator.Blocked.Files[0].length.ToString(), "the offending file is listed with its projected length");
            Assert.True(File.Exists(src), "the source file must stay in NewMusic: nothing moved");
            Assert.True(!File.Exists(dest), "no file may land at the planned destination");
        }

        public static void RunMirrorPathGate_NormalBatch_IsNotBlocked()
        {
            string root = TempRoot();
            string lib = Path.Combine(root, "Audio");
            string newMusic = Path.Combine(root, "NewMusic");
            Directory.CreateDirectory(newMusic);
            string src = Path.Combine(newMusic, "Test Artist - Gate Song.mp3");
            File.WriteAllBytes(src, new byte[] { 3 });
            var track = new Track { Artists = "Test Artist", Title = "Gate Song", Album = "Missing", Genres = "Missing" };
            var sf = new MusicIntegrator.ScannedFile
            {
                SourcePath = src,
                Track = track,
                IsReadable = true,
                LogEntry = new MusicIntegrator.LogEntry { Filename = Path.GetFileName(src), Title = track.Title, Artists = track.Artists }
            };
            var integrator = new MusicIntegrator(lib, dryRun: false);
            string mirror = Path.Combine(root, "AudioMirror", "AUDIO_MIRROR");

            bool clear = integrator.RunMirrorPathGate(new List<MusicIntegrator.ScannedFile> { sf }, new HashSet<string>(StringComparer.OrdinalIgnoreCase), mirror);

            Assert.True(clear, "a normal batch must not be blocked");
            Assert.True(integrator.Blocked == null, "no block must be recorded for a normal batch");
            Assert.True(File.Exists(src), "the gate itself never moves files");
        }

        public static void BuildJson_Blocked_WritesBlockedObjectWithFileLengths()
        {
            var block = new MusicIntegrator.MirrorPathBlock
            {
                Files = new List<(string file, int length)> { (@"C:\Audio\Artist\long.mp3", 260) }
            };

            string json = MusicIntegrator.BuildJson(new List<MusicIntegrator.LogEntry>(), null, dryRun: true, confidence: null, blocked: block);

            Assert.True(json.Contains("\"blocked\": {"), "routing JSON carries a top-level blocked object");
            Assert.True(json.Contains("\"status\": \"path_too_long\""), "blocked.status is path_too_long");
            Assert.True(json.Contains("\"action\": \"rename_or_shorten\""), "blocked.action is rename_or_shorten");
            Assert.True(json.Contains("\"projectedLength\": 260"), "each blocked file carries projectedLength");
            Assert.True(json.Contains("\"limit\": 259"), "each blocked file carries the limit");
        }
    }
}
