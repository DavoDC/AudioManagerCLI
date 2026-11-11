using System;
using System.Collections.Generic;
using System.IO;
using AudioManager.Code.Modules;

namespace AudioManager
{
    /// <summary>Album count includes library songs of the same album already in Artists/{artist}/Singles/ (MusicLibraryRules "Album Subfolder Rule").</summary>
    internal static class AlbumCountSinglesTests
    {
        private static string TempLib()
        {
            return Path.Combine(Path.GetTempPath(), "am_albsingles_" + Guid.NewGuid().ToString("N").Substring(0, 8));
        }

        private static Dictionary<string, Dictionary<string, int>> BatchCounts(string artist, string album, int count)
        {
            return new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase)
            {
                [artist] = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { [album] = count }
            };
        }

        private static string Route(MusicIntegrator integrator, string artist, string album, out string reason)
        {
            var track = new Track { Artists = artist, Title = "December 4th", Album = album, Genres = "Missing" };
            return integrator.GetDestDir(track, new HashSet<string>(StringComparer.OrdinalIgnoreCase), new HashSet<string>(StringComparer.OrdinalIgnoreCase), out reason, out _);
        }

        public static void Route_LibrarySinglesSameAlbum_CountsTowardAlbumFolder()
        {
            string lib = TempLib();
            try
            {
                string singles = Path.Combine(lib, "Artists", "Artist Alpha", "Singles");
                Directory.CreateDirectory(singles);
                TagFixerTests.CreateSilentMp3Fixture(singles, "Artist Alpha - Lib Song.mp3", "Lib Song", "Test Album", "Artist Alpha");

                var integrator = new MusicIntegrator(lib, BatchCounts("Artist Alpha", "Test Album", 1));
                string dest = Route(integrator, "Artist Alpha", "Test Album", out string reason);

                Assert.Equal(Path.Combine(lib, "Artists", "Artist Alpha", "Test Album"), dest, "1 library Singles song + 1 batch song of the same album -> album folder");
                Assert.True(reason.Contains("2 songs"), "the reason counts the Singles song: " + reason);
            }
            finally { if (Directory.Exists(lib)) Directory.Delete(lib, true); }
        }

        public static void Route_LibrarySinglesDifferentAlbum_DoesNotCount()
        {
            string lib = TempLib();
            try
            {
                string singles = Path.Combine(lib, "Artists", "Artist Alpha", "Singles");
                Directory.CreateDirectory(singles);
                TagFixerTests.CreateSilentMp3Fixture(singles, "Artist Alpha - Other.mp3", "Other", "Other Album", "Artist Alpha");

                var integrator = new MusicIntegrator(lib, BatchCounts("Artist Alpha", "Test Album", 1));
                string dest = Route(integrator, "Artist Alpha", "Test Album", out string reason);

                Assert.Equal(Path.Combine(lib, "Artists", "Artist Alpha", "Singles"), dest, "a Singles song of another album does not count: 1 batch song -> Singles/");
                Assert.True(reason.Contains("only 1 song"), "the reason counts only the batch song: " + reason);
            }
            finally { if (Directory.Exists(lib)) Directory.Delete(lib, true); }
        }
    }
}
