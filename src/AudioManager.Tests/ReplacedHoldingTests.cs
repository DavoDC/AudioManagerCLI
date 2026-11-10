using System;
using System.Collections.Generic;
using System.IO;
using AudioManager.Code.Modules;

namespace AudioManager
{
    /// <summary>
    /// Coverage for the replaced-original holding folder: BuildJson's top-level "replacedOriginals" block
    /// (execution record only), and the L (replace) path end to end with ReplaceGuard.HoldingRoot set,
    /// so a committed replacement holds the original under the holding folder and a failed one restores it.
    /// Temp trees only, never the real library. HoldingRoot is reset in finally.
    /// </summary>
    internal static class ReplacedHoldingTests
    {
        private static string MakeTempDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), "AudioManagerTest_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static bool HasBytes(string path, params byte[] expected)
        {
            if (!File.Exists(path)) return false;
            byte[] actual = File.ReadAllBytes(path);
            if (actual.Length != expected.Length) return false;
            for (int i = 0; i < actual.Length; i++)
                if (actual[i] != expected[i]) return false;
            return true;
        }

        private static MusicIntegrator.ScannedFile MakeReplacement(string sourcePath, string artist, string title, string libPath)
        {
            File.WriteAllBytes(sourcePath, new byte[] { 9, 9 });
            var track = new Track { Artists = artist, Title = title, Album = "Missing", Genres = "Missing" };
            return new MusicIntegrator.ScannedFile
            {
                SourcePath = sourcePath,
                Track = track,
                IsReadable = true,
                LogEntry = new MusicIntegrator.LogEntry { Filename = Path.GetFileName(sourcePath), Title = title, Artists = artist },
                Duplicate = new MusicIntegrator.DupData
                {
                    Decision = 'L',
                    LibraryFilePath = libPath,
                    RelLibraryPath = "Old Place/" + Path.GetFileName(libPath),
                    RelNewPath = Path.GetFileName(sourcePath)
                }
            };
        }

        public static void BuildJson_ReplacedList_WritesReplacedOriginalsBlock()
        {
            var replaced = new List<(string original, string held)>
            {
                ("C:/Lib/Misc/A.mp3", "C:/Quarantine/Replaced/20261110-101010/A.mp3"),
                ("C:/Lib/Misc/B.mp3", "C:/Quarantine/Replaced/20261110-101010/B.mp3"),
            };
            string json = MusicIntegrator.BuildJson(new List<MusicIntegrator.LogEntry>(), null, dryRun: false, replaced: replaced);

            Assert.True(json.Contains("\"replacedOriginals\": ["), "execution JSON carries a top-level replacedOriginals array");
            Assert.True(json.Contains("{\"original\": \"C:/Lib/Misc/A.mp3\", \"held\": \"C:/Quarantine/Replaced/20261110-101010/A.mp3\"},"),
                "first entry carries original and held, with a comma before the second");
            Assert.True(json.Contains("{\"original\": \"C:/Lib/Misc/B.mp3\", \"held\": \"C:/Quarantine/Replaced/20261110-101010/B.mp3\"}"),
                "last entry carries original and held with no trailing comma");
            Assert.True(json.Contains("]," + Environment.NewLine + "  \"files\": ["),
                "the replacedOriginals block closes with a comma so the files array stays valid");
        }

        public static void BuildJson_NullOrEmptyReplaced_OmitsReplacedOriginals()
        {
            string fromNull = MusicIntegrator.BuildJson(new List<MusicIntegrator.LogEntry>(), null, dryRun: false, replaced: null);
            string fromEmpty = MusicIntegrator.BuildJson(new List<MusicIntegrator.LogEntry>(), null, dryRun: false,
                replaced: new List<(string original, string held)>());
            Assert.True(!fromNull.Contains("replacedOriginals"), "null replaced list must leave replacedOriginals out");
            Assert.True(!fromEmpty.Contains("replacedOriginals"), "empty replaced list must leave replacedOriginals out");
            Assert.True(fromEmpty.Contains("\"files\": ["), "the files array still follows the summary when the replaced block is absent");
        }

        public static void Replace_CommittedWithHoldingRoot_HoldsOriginalAndRecordsRetired()
        {
            string lib = RoutingFixtures.CreateLibraryFixture();
            string newMusic = MakeTempDir();
            string holding = MakeTempDir();
            try
            {
                ReplaceGuard.HoldingRoot = holding;
                ReplaceGuard.ResetRetired();

                // The library copy sits where the replacement routes ("Bad Artist" has no folder, so Misc).
                string libPath = Path.Combine(lib, Constants.MiscDir, "Bad Artist - Bad Song.mp3");
                Directory.CreateDirectory(Path.GetDirectoryName(libPath));
                File.WriteAllBytes(libPath, new byte[] { 1, 2, 3 });

                var sf = MakeReplacement(Path.Combine(newMusic, "Bad Artist - Bad Song.mp3"), "Bad Artist", "Bad Song", libPath);
                var scannedFiles = new List<MusicIntegrator.ScannedFile> { sf };
                var logEntries = new List<MusicIntegrator.LogEntry>();
                int moved = 0, skipped = 0;

                var integrator = new MusicIntegrator(lib);
                integrator.ExecuteDuplicateDecisions(scannedFiles, new HashSet<string>(), logEntries, ref skipped);
                integrator.RouteAllFiles(scannedFiles, new HashSet<string>(), logEntries, ref moved, ref skipped);

                Assert.Equal("1", moved.ToString(), "the replacement must route into the library path");
                Assert.True(HasBytes(libPath, 9, 9), "the replacement's bytes must sit at the library path");
                Assert.Equal("1", ReplaceGuard.Retired.Count.ToString(), "a committed replacement must record exactly one retired original");
                Assert.Equal(Path.GetFullPath(libPath), ReplaceGuard.Retired[0].original, "retired entry must name the replaced library path");
                string held = ReplaceGuard.Retired[0].held;
                Assert.True(held.StartsWith(Path.GetFullPath(holding), StringComparison.OrdinalIgnoreCase), "the original must be held under the holding folder");
                Assert.True(HasBytes(held, 1, 2, 3), "the held file must keep the original library bytes");
                Assert.True(!File.Exists(libPath + ".replaced.bak"), "commit must leave no staged bak in the library");
            }
            finally
            {
                ReplaceGuard.HoldingRoot = null;
                ReplaceGuard.ResetRetired();
                RoutingFixtures.Cleanup(lib);
                if (Directory.Exists(newMusic)) Directory.Delete(newMusic, recursive: true);
                if (Directory.Exists(holding)) Directory.Delete(holding, recursive: true);
            }
        }

        public static void Replace_FailedWithHoldingRoot_RestoresOriginalAndHoldsNothing()
        {
            string lib = RoutingFixtures.CreateLibraryFixture();
            string newMusic = MakeTempDir();
            string holding = MakeTempDir();
            try
            {
                ReplaceGuard.HoldingRoot = holding;
                ReplaceGuard.ResetRetired();

                string libDir = Path.Combine(lib, "Old Place");
                Directory.CreateDirectory(libDir);
                string libPath = Path.Combine(libDir, "Bad Artist - Bad Song.mp3");
                File.WriteAllBytes(libPath, new byte[] { 1, 2, 3 });

                // Block the replacement's destination (Misc is a file) so routing throws after the L stage.
                File.WriteAllBytes(Path.Combine(lib, Constants.MiscDir), new byte[0]);

                var sf = MakeReplacement(Path.Combine(newMusic, "Bad Artist - Bad Song.mp3"), "Bad Artist", "Bad Song", libPath);
                var scannedFiles = new List<MusicIntegrator.ScannedFile> { sf };
                var logEntries = new List<MusicIntegrator.LogEntry>();
                int moved = 0, skipped = 0;

                var integrator = new MusicIntegrator(lib);
                integrator.ExecuteDuplicateDecisions(scannedFiles, new HashSet<string>(), logEntries, ref skipped);
                integrator.RouteAllFiles(scannedFiles, new HashSet<string>(), logEntries, ref moved, ref skipped);

                Assert.Equal("0", moved.ToString(), "a blocked replacement must not count as moved");
                Assert.True(HasBytes(libPath, 1, 2, 3), "a failed replacement must restore the original bytes at the library path");
                Assert.True(!File.Exists(libPath + ".replaced.bak"), "rollback must consume the staged bak");
                Assert.Equal("0", ReplaceGuard.Retired.Count.ToString(), "a rolled-back replacement must record nothing as retired");
                Assert.True(!Directory.Exists(holding) || Directory.GetFileSystemEntries(holding).Length == 0,
                    "the holding folder must stay empty or absent after a failed replacement");
            }
            finally
            {
                ReplaceGuard.HoldingRoot = null;
                ReplaceGuard.ResetRetired();
                RoutingFixtures.Cleanup(lib);
                if (Directory.Exists(newMusic)) Directory.Delete(newMusic, recursive: true);
                if (Directory.Exists(holding)) Directory.Delete(holding, recursive: true);
            }
        }
    }
}
