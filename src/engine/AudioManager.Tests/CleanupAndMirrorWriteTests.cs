using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AudioManager
{
    /// <summary>
    /// Pins the mirror writer (Reflector.CreateFolders, CreateFile, the incremental XML prune and the
    /// non-MP3 reporting in ProcessNonMP3) through the internal Reflector(audioRoot, mirrorPath, repoRoot,
    /// stamp) constructor, on temp trees only. Every test sets AgeChecker.RegenMirror explicitly and restores it.
    /// MusicIntegrator.CleanupNewMusicFolder is not covered here: it reads the fixed Constants.NewMusicPath.
    /// </summary>
    internal static class CleanupAndMirrorWriteTests
    {
        private static string NewRoot()
        {
            return Path.Combine(Path.GetTempPath(), "am_mirrorwrite_" + Guid.NewGuid().ToString("N").Substring(0, 8));
        }

        private static void Cleanup(string root)
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }

        private static void Put(string path, string content)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, content);
        }

        // Runs the real Reflector constructor (incremental or regen) and returns the captured console output.
        private static string Build(string root, string audio, string mirror, bool regen)
        {
            bool saved = AgeChecker.RegenMirror;
            var originalOut = Console.Out;
            var capture = new StringWriter();
            try
            {
                AgeChecker.RegenMirror = regen;
                Console.SetOut(capture);
                new Reflector(audio, mirror, Path.GetFullPath(root) + Path.DirectorySeparatorChar, Path.Combine(root, "LastRunInfo.txt"));
            }
            finally
            {
                Console.SetOut(originalOut);
                AgeChecker.RegenMirror = saved;
            }
            return capture.ToString();
        }

        // Every file under dir as a backslash relative path, ordinal-sorted.
        private static List<string> FilesUnder(string dir)
        {
            return Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
                .Select(f => f.Substring(dir.Length).TrimStart(Path.DirectorySeparatorChar))
                .OrderBy(f => f, StringComparer.Ordinal)
                .ToList();
        }

        private static void AssertFiles(List<string> actual, string label, params string[] expected)
        {
            var sortedExpected = expected.OrderBy(e => e, StringComparer.Ordinal).ToList();
            Assert.True(actual.SequenceEqual(sortedExpected), label + " - got: " + string.Join(" | ", actual));
        }

        public static void CleanupMirror_IncrementalBuild_MirrorsEmptyAndNestedFolders()
        {
            string root = NewRoot();
            try
            {
                string audio = Path.Combine(root, "Audio");
                string mirror = Path.Combine(root, "AUDIO_MIRROR");
                Directory.CreateDirectory(Path.Combine(audio, "Artist", "Album"));
                Directory.CreateDirectory(Path.Combine(audio, "Empty"));
                Put(Path.Combine(audio, "Artist", "Single", "a.mp3"), "x");

                Build(root, audio, mirror, regen: false);

                Assert.True(Directory.Exists(Path.Combine(mirror, "Artist", "Album")), "an empty nested folder is mirrored");
                Assert.True(Directory.Exists(Path.Combine(mirror, "Empty")), "an empty top-level folder is mirrored");
                Assert.True(Directory.Exists(Path.Combine(mirror, "Artist", "Single")), "a folder holding an MP3 is mirrored");
                AssertFiles(FilesUnder(mirror), "only the one XML is a file in the mirror", "Artist\\Single\\a.xml");
            }
            finally { Cleanup(root); }
        }

        public static void CleanupMirror_MissingAudioRoot_CreatesNoMirror()
        {
            string root = NewRoot();
            string mirror = Path.Combine(root, "AUDIO_MIRROR");
            bool threw = false;
            bool saved = AgeChecker.RegenMirror;
            var originalOut = Console.Out;
            try
            {
                AgeChecker.RegenMirror = false;
                Console.SetOut(new StringWriter());
                new Reflector(Path.Combine(root, "NoSuchAudio"), mirror, Path.GetFullPath(root) + Path.DirectorySeparatorChar, null);
            }
            catch (Exception) { threw = true; }
            finally
            {
                Console.SetOut(originalOut);
                AgeChecker.RegenMirror = saved;
            }
            try
            {
                Assert.True(threw, "a missing audio root must fail the build");
                Assert.True(!Directory.Exists(mirror), "no mirror folder is created before the audio root enumeration fails");
                Assert.True(!Directory.Exists(mirror + ".staging"), "no staging folder is created either");
            }
            finally { Cleanup(root); }
        }

        public static void CleanupMirror_XmlHoldsRealMp3Path()
        {
            string root = NewRoot();
            try
            {
                string audio = Path.Combine(root, "Audio");
                string mirror = Path.Combine(root, "AUDIO_MIRROR");
                string mp3 = Path.Combine(audio, "Artist", "a.mp3");
                Put(mp3, "x");

                Build(root, audio, mirror, regen: false);

                string xml = Path.Combine(mirror, "Artist", "a.xml");
                Assert.True(File.Exists(xml), "the MP3 gets an XML at the same relative path");
                Assert.Equal(mp3, File.ReadAllText(xml), "the XML body is exactly the real MP3 path");
            }
            finally { Cleanup(root); }
        }

        public static void CleanupMirror_NonMp3Files_LeaveNoMirrorFiles()
        {
            string root = NewRoot();
            try
            {
                string audio = Path.Combine(root, "Audio");
                string mirror = Path.Combine(root, "AUDIO_MIRROR");
                Put(Path.Combine(audio, "Artist", "a.mp3"), "x");
                Put(Path.Combine(audio, "Artist", "cover.jpg"), "x");
                Put(Path.Combine(audio, "Artist", "folder.ini"), "x");
                Put(Path.Combine(audio, "notes.txt"), "x");

                Build(root, audio, mirror, regen: false);

                AssertFiles(FilesUnder(mirror), "non-MP3 files write nothing into the mirror", "Artist\\a.xml");
            }
            finally { Cleanup(root); }
        }

        public static void CleanupMirror_UppercaseMp3Extension_IsMirrored()
        {
            string root = NewRoot();
            try
            {
                string audio = Path.Combine(root, "Audio");
                string mirror = Path.Combine(root, "AUDIO_MIRROR");
                Put(Path.Combine(audio, "Artist", "B.MP3"), "x");

                Build(root, audio, mirror, regen: false);

                AssertFiles(FilesUnder(mirror), "an upper-case .MP3 is treated as an MP3", "Artist\\B.xml");
            }
            finally { Cleanup(root); }
        }

        public static void CleanupMirror_IncrementalRerun_KeepsFreshXml()
        {
            string root = NewRoot();
            try
            {
                string audio = Path.Combine(root, "Audio");
                string mirror = Path.Combine(root, "AUDIO_MIRROR");
                string mp3 = Path.Combine(audio, "Artist", "a.mp3");
                Put(mp3, "x");
                Build(root, audio, mirror, regen: false);

                string xml = Path.Combine(mirror, "Artist", "a.xml");
                File.WriteAllText(xml, "sentinel");
                File.SetLastWriteTimeUtc(mp3, DateTime.UtcNow.AddMinutes(-5));
                File.SetLastWriteTimeUtc(xml, DateTime.UtcNow);

                string output = Build(root, audio, mirror, regen: false);

                Assert.Equal("sentinel", File.ReadAllText(xml), "an XML newer than its MP3 is not rewritten");
                Assert.True(!output.Contains("XMLs refreshed"), "no refresh is reported when nothing is stale");
            }
            finally { Cleanup(root); }
        }

        public static void CleanupMirror_StaleXml_IsRefreshedWithRealPath()
        {
            string root = NewRoot();
            try
            {
                string audio = Path.Combine(root, "Audio");
                string mirror = Path.Combine(root, "AUDIO_MIRROR");
                string mp3 = Path.Combine(audio, "Artist", "a.mp3");
                Put(mp3, "x");
                Build(root, audio, mirror, regen: false);

                string xml = Path.Combine(mirror, "Artist", "a.xml");
                File.WriteAllText(xml, "old");
                File.SetLastWriteTimeUtc(xml, DateTime.UtcNow.AddMinutes(-5));
                File.SetLastWriteTimeUtc(mp3, DateTime.UtcNow);

                string output = Build(root, audio, mirror, regen: false);

                Assert.Equal(mp3, File.ReadAllText(xml), "an MP3 edited after its XML rewrites the XML with the real path");
                Assert.True(output.Contains(" - XMLs refreshed (MP3 updated since last analysis): 1"), "the single refresh is reported");
            }
            finally { Cleanup(root); }
        }

        public static void CleanupMirror_DeletedMp3_PrunesOnlyItsXml()
        {
            string root = NewRoot();
            try
            {
                string audio = Path.Combine(root, "Audio");
                string mirror = Path.Combine(root, "AUDIO_MIRROR");
                Put(Path.Combine(audio, "Artist", "a.mp3"), "x");
                string gone = Path.Combine(audio, "Artist", "b.mp3");
                Put(gone, "x");
                Build(root, audio, mirror, regen: false);
                AssertFiles(FilesUnder(mirror), "both XMLs exist after the first build", "Artist\\a.xml", "Artist\\b.xml");

                File.Delete(gone);
                string output = Build(root, audio, mirror, regen: false);

                AssertFiles(FilesUnder(mirror), "only the XML of the deleted MP3 is pruned", "Artist\\a.xml");
                Assert.True(output.Contains(" - XMLs pruned (MP3 deleted since last analysis): 1"), "the single prune is reported");
            }
            finally { Cleanup(root); }
        }

        public static void CleanupMirror_EmptyLibrary_PrunesNothing()
        {
            string root = NewRoot();
            try
            {
                string audio = Path.Combine(root, "Audio");
                string mirror = Path.Combine(root, "AUDIO_MIRROR");
                Directory.CreateDirectory(Path.Combine(audio, "Empty"));
                string orphan = Path.Combine(mirror, "Gone", "orphan.xml");
                Put(orphan, "stub");

                Build(root, audio, mirror, regen: false);

                Assert.True(File.Exists(orphan), "an audio root with zero files must not prune the mirror (failed-enumeration guard)");
            }
            finally { Cleanup(root); }
        }

        public static void CleanupMirror_NonAsciiMp3_XmlNameIsAscii()
        {
            string root = NewRoot();
            try
            {
                string audio = Path.Combine(root, "Audio");
                string mirror = Path.Combine(root, "AUDIO_MIRROR");
                Put(Path.Combine(audio, "Artist", "Café.mp3"), "x");

                Build(root, audio, mirror, regen: false);

                string[] xmls = Directory.GetFiles(Path.Combine(mirror, "Artist"), "*.xml");
                Assert.True(xmls.Length == 1, "one XML is written for the accented MP3");
                string name = Path.GetFileName(xmls[0]);
                Assert.True(name.All(c => c < 128), "the mirror XML name is ASCII only, got: " + name);
            }
            finally { Cleanup(root); }
        }

        public static void CleanupMirror_NonMp3AllExpected_ReportedAsExpected()
        {
            string root = NewRoot();
            try
            {
                string audio = Path.Combine(root, "Audio");
                string mirror = Path.Combine(root, "AUDIO_MIRROR");
                Put(Path.Combine(audio, "Artist", "a.mp3"), "x");
                Put(Path.Combine(audio, "Artist", "folder.ini"), "x");
                Put(Path.Combine(audio, "notes.txt"), "x");

                string output = Build(root, audio, mirror, regen: false);

                Assert.True(output.Contains("Non-MP3 files found: 2 (all expected)"), "ini and txt are expected types");
                Assert.True(!output.Contains("UNEXPECTED"), "no UNEXPECTED flag when every non-MP3 type is expected");
                Assert.True(!output.Contains("Extension list:"), "no extension list when every type is expected");
            }
            finally { Cleanup(root); }
        }

        public static void CleanupMirror_NonMp3Unexpected_ListsOnlyUnexpectedFiles()
        {
            string root = NewRoot();
            try
            {
                string audio = Path.Combine(root, "Audio");
                string mirror = Path.Combine(root, "AUDIO_MIRROR");
                Put(Path.Combine(audio, "Artist", "a.mp3"), "x");
                Put(Path.Combine(audio, "Artist", "folder.ini"), "x");
                Put(Path.Combine(audio, "Artist", "cover.jpg"), "x");

                string output = Build(root, audio, mirror, regen: false);

                Assert.True(output.Contains("Non-MP3 files found: 2 (UNEXPECTED!)"), "a .jpg makes the non-MP3 set unexpected");
                Assert.True(output.Contains("  - Extension list: .ini, .jpg"), "the extension list is sorted and lists every non-MP3 type");
                Assert.True(output.Contains("    .jpg (1 files):"), "the unexpected group is headed by its extension and count");
                Assert.True(output.Contains("      - Artist\\cover.jpg"), "the unexpected file is listed by its relative path");
                Assert.True(!output.Contains("    .ini ("), "an expected extension is not listed as unexpected");
            }
            finally { Cleanup(root); }
        }
    }
}
