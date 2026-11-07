using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;

namespace AudioManager
{
    /// <summary>
    /// Covers the swallowed-exception sites that can run without a real music library.
    /// Sites that need a real AudioMirror / NewMusic / library tree (the MusicIntegrator
    /// XML count loops, CountAlbumSongs, CountAkiraTheDonPersonSongs, IsAlbumFolderCompilation,
    /// BuildMirrorIndex) are not covered here because their input paths are fixed Constants.
    /// </summary>
    internal static class SwallowLoggingTests
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

        // ---- TagFixer artist overrides read (TagFixer.cs ~391) ----

        public static void ArtistOverrides_MalformedFile_LogsWarningWithPath()
        {
            string dir = NewTempDir();
            try
            {
                string path = Path.Combine(dir, "artist-name-overrides.xml");
                File.WriteAllText(path, "<Artists><Artist canonical=\"Jay-Z\" <<< not xml");

                Dictionary<string, string> result = null;
                string output = CaptureConsole(() => result = TagFixer.LoadArtistOverrides(path));

                Assert.True(result != null, "Malformed overrides file must still return a dictionary");
                Assert.True(output.IndexOf("[WARN]", StringComparison.Ordinal) >= 0,
                    "Malformed overrides file should log a [WARN] line");
                Assert.True(output.IndexOf(path, StringComparison.Ordinal) >= 0,
                    "The [WARN] line should include the overrides path");
            }
            finally { Directory.Delete(dir, true); }
        }

        public static void ArtistOverrides_MissingFile_ReturnsEmptyWithoutWarning()
        {
            string dir = NewTempDir();
            try
            {
                string path = Path.Combine(dir, "no-such-overrides.xml");
                Dictionary<string, string> result = null;
                string output = CaptureConsole(() => result = TagFixer.LoadArtistOverrides(path));

                Assert.True(result != null && result.Count == 0, "Missing overrides file should yield an empty dictionary");
                Assert.True(output.IndexOf("[WARN]", StringComparison.Ordinal) < 0,
                    "A missing overrides file is normal and must not warn");
            }
            finally { Directory.Delete(dir, true); }
        }

        public static void ArtistOverrides_ValidFile_MapsVariantWithoutWarning()
        {
            string dir = NewTempDir();
            try
            {
                string path = Path.Combine(dir, "artist-name-overrides.xml");
                File.WriteAllText(path,
                    "<ArtistOverrides>\r\n" +
                    "  <Artist canonical=\"Jay-Z\" variant=\"JAYŸ-Z\" />\r\n" +
                    "</ArtistOverrides>\r\n");

                Dictionary<string, string> result = null;
                string output = CaptureConsole(() => result = TagFixer.LoadArtistOverrides(path));

                Assert.True(result.ContainsKey("JAYŸ-Z") && result["JAYŸ-Z"] == "Jay-Z",
                    "Variant attribute should map to the canonical form");
                Assert.True(output.IndexOf("[WARN]", StringComparison.Ordinal) < 0,
                    "A valid overrides file must not warn");
            }
            finally { Directory.Delete(dir, true); }
        }

        public static void ArtistOverrides_GetTwice_LoadsFileOnce()
        {
            TagFixer.ResetArtistOverridesCache();
            int before = TagFixer.OverridesLoadCount;
            Dictionary<string, string> first = TagFixer.GetArtistOverrides();
            Dictionary<string, string> second = TagFixer.GetArtistOverrides();
            int loads = TagFixer.OverridesLoadCount - before;
            Assert.True(loads == 1, $"Expected exactly one overrides load across two calls, got {loads}");
            Assert.True(ReferenceEquals(first, second), "Second call must return the cached dictionary instance");
        }

        // ---- MusicIntegrator alias read (MusicIntegrator.cs ~1495) ----

        public static void ArtistAliases_MalformedFile_LogsWarningWithPath()
        {
            string dir = NewTempDir();
            try
            {
                string path = Path.Combine(dir, "artist-aliases.xml");
                File.WriteAllText(path, "<Aliases><Alias from=\"A\" to=\"B\" <<< not xml");

                Dictionary<string, string> result = null;
                string output = CaptureConsole(() => result = MusicIntegrator.LoadArtistAliases(path));

                Assert.True(result != null, "Malformed aliases file must still return a dictionary");
                Assert.True(output.IndexOf("[WARN]", StringComparison.Ordinal) >= 0,
                    "Malformed aliases file should log a [WARN] line");
                Assert.True(output.IndexOf(path, StringComparison.Ordinal) >= 0,
                    "The [WARN] line should include the aliases path");
            }
            finally { Directory.Delete(dir, true); }
        }

        // ---- TagFixer culture-sensitive lowercase (TagFixer.cs ~463) ----

        public static void ExtractAndFixArtists_TurkishCulture_UppercaseNameTitleCasesCorrectly()
        {
            var original = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("tr-TR");
                List<string> artists = TagFixer.ExtractAndFixArtists("Song Title", "IDIOT");
                Assert.True(artists.Count == 1, $"Expected one artist, got {artists.Count}");
                Assert.Equal("Idiot", artists[0], "All-uppercase artist must title-case the same under tr-TR as under invariant");
            }
            finally { Thread.CurrentThread.CurrentCulture = original; }
        }
    }
}
