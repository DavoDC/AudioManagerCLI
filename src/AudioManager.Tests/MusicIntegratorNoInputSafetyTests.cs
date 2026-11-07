using System;
using System.Collections.Generic;
using System.IO;
using AudioManager.Code.Modules;

namespace AudioManager
{
    /// <summary>
    /// Safety guard for --no-input runs: a duplicate whose recommendation is L (delete/replace the
    /// library copy) must never be auto-accepted, because no human confirmed it. Uses the test-only
    /// MusicIntegrator(testLibraryPath, noInput, manifest) constructor and real temp files only.
    /// </summary>
    internal static class MusicIntegratorNoInputSafetyTests
    {
        public static void NoInput_RecommendedL_IsNotAutoAccepted_LibraryAndNewFileStayIntact()
        {
            string lib = RoutingFixtures.CreateLibraryFixture();
            string newMusic = Path.Combine(Path.GetTempPath(), "AudioManagerTest_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(newMusic);
            try
            {
                string libDir = Path.Combine(lib, "Old Place");
                Directory.CreateDirectory(libDir);
                string libPath = Path.Combine(libDir, "Good Artist - Good Song.mp3");
                File.WriteAllBytes(libPath, new byte[] { 1, 2, 3 });

                string newPath = Path.Combine(newMusic, "Good Artist - Good Song.mp3");
                File.WriteAllBytes(newPath, new byte[] { 9, 9, 9, 9 });

                var sf = new MusicIntegrator.ScannedFile
                {
                    SourcePath = newPath,
                    Track = new Track { Artists = "Good Artist", Title = "Good Song", Album = "Missing", Genres = "Missing" },
                    IsReadable = true,
                    LogEntry = new MusicIntegrator.LogEntry { Filename = Path.GetFileName(newPath), Title = "Good Song", Artists = "Good Artist" },
                    Duplicate = new MusicIntegrator.DupData
                    {
                        RecommendedKey = 'L',
                        LibraryFilePath = libPath,
                        RelLibraryPath = "Old Place/" + Path.GetFileName(libPath),
                        RelMirrorPath = "Old Place\\Good Artist - Good Song.mp3",
                        RelNewPath = Path.GetFileName(newPath),
                        DisplayNewFilename = Path.GetFileName(newPath),
                        OptionsLine = "  [D] ...   [L] ...   [K] ...   [Q] ...",
                    },
                };

                var integrator = new MusicIntegrator(lib, noInput: true, manifest: null);
                integrator.PresentDuplicateAndDecide(sf);

                Assert.True(sf.Duplicate.Decision != 'L', "noInput must never auto-accept the L (replace library copy) decision");
                Assert.Equal("K", sf.Duplicate.Decision.ToString(), "L recommendation downgrades to the non-destructive Keep Both");

                var logEntries = new List<MusicIntegrator.LogEntry>();
                int skipped = 0;
                integrator.ExecuteDuplicateDecisions(new List<MusicIntegrator.ScannedFile> { sf }, new HashSet<string>(), logEntries, ref skipped);

                Assert.True(File.Exists(libPath), "library copy must stay in the library under noInput");
                Assert.Equal("3", new FileInfo(libPath).Length.ToString(), "library copy must keep its bytes");
                Assert.True(File.Exists(newPath), "new file must not be deleted under noInput");
                Assert.Equal("0", Directory.GetFiles(lib, "*.replaced.bak", SearchOption.AllDirectories).Length.ToString(),
                    "no library copy may be staged for replacement under noInput");
            }
            finally
            {
                RoutingFixtures.Cleanup(lib);
                if (Directory.Exists(newMusic)) Directory.Delete(newMusic, recursive: true);
            }
        }
    }
}
