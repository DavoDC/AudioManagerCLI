using System;
using System.Collections.Generic;
using System.IO;

namespace AudioManager
{
    /// <summary>
    /// Characterisation tests for the AudioMirror path helpers: MirrorIndex.BuildMirrorIndex,
    /// MirrorIndex.DeriveLibraryPathFromMirrorPath (forwarded by MusicIntegrator) and
    /// MirrorIndex.GetMirrorRelPath (forwarded by MusicIntegrator). They pin what the code does today.
    /// Tests named KnownGap pin a current behaviour that looks wrong; a fix should change them on purpose.
    /// Windows-only paths: the test host runs on Windows, so the separator is a backslash.
    /// </summary>
    internal static class MirrorHelperTests
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

        private static void WriteTrack(string mirrorRoot, string relPath, string artists, string title)
        {
            string full = Path.Combine(mirrorRoot, relPath);
            Directory.CreateDirectory(Path.GetDirectoryName(full));
            var sb = new System.Text.StringBuilder("<?xml version=\"1.0\" encoding=\"utf-8\"?><Track>");
            if (artists != null) sb.Append("<Artists>" + artists + "</Artists>");
            if (title != null) sb.Append("<Title>" + title + "</Title>");
            sb.Append("</Track>");
            File.WriteAllText(full, sb.ToString());
        }

        // Roots for the path-only helpers: nothing here touches the disk.
        private static MirrorIndex IndexWithFakeRoots() =>
            new MirrorIndex(new IntegrationRoots(@"C:\NewMusic", @"C:\Lib\Audio", @"C:\AudioMirror", @"C:\Logs"));

        // ---- BuildMirrorIndex ----

        public static void BuildMirrorIndex_MissingMirrorFolder_ReturnsEmptyIndex()
        {
            string dir = NewTempDir();
            try
            {
                var index = new MirrorIndex(new IntegrationRoots(dir, dir, Path.Combine(dir, "no-mirror"), dir));
                Dictionary<string, string> result = null;
                CaptureConsole(() => result = index.BuildMirrorIndex());
                Assert.True(result != null && result.Count == 0, "A missing mirror folder must give an empty index, not an exception");
            }
            finally { Directory.Delete(dir, true); }
        }

        public static void BuildMirrorIndex_NormalTrackInSubfolder_KeysLowercasedArtistAndTitleToFullXmlPath()
        {
            string dir = NewTempDir();
            try
            {
                string mirror = Path.Combine(dir, "AUDIO_MIRROR");
                WriteTrack(mirror, @"Zeta Band\Album\Track.xml", "Zeta Band", "Song One");
                var index = new MirrorIndex(new IntegrationRoots(dir, dir, mirror, dir));
                Dictionary<string, string> result = null;
                CaptureConsole(() => result = index.BuildMirrorIndex());

                string expectedPath = Path.Combine(mirror, @"Zeta Band\Album\Track.xml");
                Assert.True(result.ContainsKey("zeta band\0song one"), "key is lowercased primary artist, NUL, lowercased title");
                Assert.Equal(expectedPath, result["zeta band\0song one"], "value is the full XML path, found in a subfolder");
            }
            finally { Directory.Delete(dir, true); }
        }

        public static void BuildMirrorIndex_FeatParenthetical_IsStrippedFromKeyButRemixIsKept()
        {
            string dir = NewTempDir();
            try
            {
                string mirror = Path.Combine(dir, "AUDIO_MIRROR");
                WriteTrack(mirror, @"Zeta Band\Album\01.xml", "Zeta Band", "God's Plan (feat. Guest)");
                WriteTrack(mirror, @"Zeta Band\Album\02.xml", "Zeta Band", "Song (Remix)");
                var index = new MirrorIndex(new IntegrationRoots(dir, dir, mirror, dir));
                Dictionary<string, string> result = null;
                CaptureConsole(() => result = index.BuildMirrorIndex());

                Assert.True(result.ContainsKey("zeta band\0god's plan"), "(feat. X) is stripped before keying");
                Assert.True(result.ContainsKey("zeta band\0song (remix)"), "(Remix) is a different track and must stay in the key");
            }
            finally { Directory.Delete(dir, true); }
        }

        public static void BuildMirrorIndex_SemicolonArtistList_KeysOnlyThePrimaryArtist()
        {
            string dir = NewTempDir();
            try
            {
                string mirror = Path.Combine(dir, "AUDIO_MIRROR");
                WriteTrack(mirror, @"Zeta Band\Album\Collab.xml", "Zeta Band; Other Guest", "Collab Song");
                var index = new MirrorIndex(new IntegrationRoots(dir, dir, mirror, dir));
                Dictionary<string, string> result = null;
                CaptureConsole(() => result = index.BuildMirrorIndex());

                Assert.True(result.ContainsKey("zeta band\0collab song"), "the first artist is the primary key");
                Assert.True(!result.ContainsKey("other guest\0collab song"), "a featured artist in the list is not keyed");
            }
            finally { Directory.Delete(dir, true); }
        }

        public static void BuildMirrorIndex_MissingOrBlankArtistsOrTitle_AreSkipped()
        {
            string dir = NewTempDir();
            try
            {
                string mirror = Path.Combine(dir, "AUDIO_MIRROR");
                WriteTrack(mirror, @"Zeta Band\Album\NoTitle.xml", "Zeta Band", null);
                WriteTrack(mirror, @"Zeta Band\Album\NoArtists.xml", null, "Ghost Song");
                WriteTrack(mirror, @"Zeta Band\Album\BlankArtist.xml", "   ", "Blank Song");
                WriteTrack(mirror, @"Zeta Band\Album\BlankTitle.xml", "Zeta Band", "   ");
                var index = new MirrorIndex(new IntegrationRoots(dir, dir, mirror, dir));
                Dictionary<string, string> result = null;
                CaptureConsole(() => result = index.BuildMirrorIndex());

                Assert.True(result.Count == 0, $"Tracks without a usable artist or title must not be indexed, got {result.Count} keys");
            }
            finally { Directory.Delete(dir, true); }
        }

        public static void BuildMirrorIndex_MalformedXml_IsSkippedWithWarnAndGoodTracksStillIndexed()
        {
            string dir = NewTempDir();
            try
            {
                string mirror = Path.Combine(dir, "AUDIO_MIRROR");
                WriteTrack(mirror, @"Zeta Band\Album\Good.xml", "Zeta Band", "Good Song");
                File.WriteAllText(Path.Combine(mirror, @"Zeta Band\Album\Broken.xml"), "<Track><Artists>Zeta Band</Artists>");
                var index = new MirrorIndex(new IntegrationRoots(dir, dir, mirror, dir));
                Dictionary<string, string> result = null;
                string output = CaptureConsole(() => result = index.BuildMirrorIndex());

                Assert.True(result.ContainsKey("zeta band\0good song"), "one malformed file must not stop the rest of the index");
                Assert.True(output.IndexOf("[WARN]", StringComparison.Ordinal) >= 0, "a skipped malformed XML must log a [WARN] line");
                Assert.True(output.IndexOf("Broken.xml", StringComparison.Ordinal) >= 0, "the [WARN] line names the skipped file");
            }
            finally { Directory.Delete(dir, true); }
        }

        public static void BuildMirrorIndex_SameKeyInTwoFolders_KeepsSmallestPath()
        {
            string dir = NewTempDir();
            try
            {
                string mirror = Path.Combine(dir, "AUDIO_MIRROR");
                WriteTrack(mirror, @"Zeta Band\Album2\Track.xml", "Zeta Band", "Dup Song");
                WriteTrack(mirror, @"Zeta Band\Album1\Track.xml", "Zeta Band", "Dup Song");
                var index = new MirrorIndex(new IntegrationRoots(dir, dir, mirror, dir));
                Dictionary<string, string> result = null;
                CaptureConsole(() => result = index.BuildMirrorIndex());

                Assert.Equal(Path.Combine(mirror, @"Zeta Band\Album1\Track.xml"), result["zeta band\0dup song"],
                    "a duplicate key keeps the smallest path, whatever order the files were read in");
            }
            finally { Directory.Delete(dir, true); }
        }

        // ---- DeriveLibraryPathFromMirrorPath (MusicIntegrator forwards to MirrorIndex) ----

        public static void DeriveLibraryPath_BackslashMirrorPath_MapsXmlToMp3UnderLibrary()
        {
            string result = IndexWithFakeRoots().DeriveLibraryPathFromMirrorPath(@"C:\AudioMirror\Zeta Band\Album\Track.xml");
            Assert.Equal(@"C:\Lib\Audio\Zeta Band\Album\Track.mp3", result, "the mirror folder is swapped for the library folder and .xml becomes .mp3");
        }

        public static void DeriveLibraryPath_MirrorRootAndXmlExtensionInDifferentCase_StillMaps()
        {
            string result = IndexWithFakeRoots().DeriveLibraryPathFromMirrorPath(@"c:\audiomirror\Zeta Band\Album\Track.XML");
            Assert.Equal(@"C:\Lib\Audio\Zeta Band\Album\Track.mp3", result, "root match and .XML match are both case-insensitive");
        }

        // Pins current behaviour: a fully forward-slashed path does not start with the mirror root, so it comes back unchanged.
        public static void DeriveLibraryPath_FullyForwardSlashedPath_ReturnsInputUnchanged()
        {
            string input = "C:/AudioMirror/Zeta Band/Album/Track.xml";
            string result = IndexWithFakeRoots().DeriveLibraryPathFromMirrorPath(input);
            Assert.Equal(input, result, "forward slashes do not match the backslash mirror root, so no mapping happens");
        }

        // KnownGap: the root matches, but the remainder keeps its forward slash. TrimStart only strips '\',
        // so the rooted "/Zeta Band/..." goes to Path.Combine, which discards the library folder.
        public static void DeriveLibraryPath_KnownGap_MixedSlashes_ReturnsRootedPathOutsideLibrary()
        {
            string result = IndexWithFakeRoots().DeriveLibraryPathFromMirrorPath(@"C:\AudioMirror/Zeta Band/Album/Track.xml");
            Assert.Equal("/Zeta Band/Album/Track.mp3", result, "KnownGap: the library root is lost when the remainder uses '/'");
        }

        // KnownGap: the root check is a plain prefix match, so a sibling folder named "AudioMirror2" is mapped as if inside.
        public static void DeriveLibraryPath_KnownGap_SiblingFolderWithSharedPrefix_IsMappedAsInside()
        {
            string result = IndexWithFakeRoots().DeriveLibraryPathFromMirrorPath(@"C:\AudioMirror2\Zeta Band\Track.xml");
            Assert.Equal(@"C:\Lib\Audio\2\Zeta Band\Track.mp3", result, "KnownGap: a prefix match without a separator check maps a sibling folder");
        }

        public static void DeriveLibraryPath_EmptyInput_ReturnsEmptyUnchanged()
        {
            string result = IndexWithFakeRoots().DeriveLibraryPathFromMirrorPath("");
            Assert.Equal("", result, "an empty path is outside the mirror root and comes back as-is");
        }

        public static void DeriveLibraryPath_NullInput_ReturnsNullAndWarns()
        {
            string result = "not-set";
            string output = CaptureConsole(() => result = IndexWithFakeRoots().DeriveLibraryPathFromMirrorPath(null));
            Assert.True(result == null, "a null path must come back as null, not throw");
            Assert.True(output.IndexOf("[WARN]", StringComparison.Ordinal) >= 0, "the null-input failure is caught and logged as [WARN]");
        }

        // ---- GetMirrorRelPath (static; MusicIntegrator forwards to MirrorIndex) ----

        public static void GetMirrorRelPath_NormalPath_ReturnsLeadingBackslashRelPath()
        {
            Assert.Equal(@"\Artists\X\Y.xml", MirrorIndex.GetMirrorRelPath(@"C:\Music\AUDIO_MIRROR\Artists\X\Y.xml"),
                "the text after the AUDIO_MIRROR folder name, keeping its leading backslash");
        }

        public static void GetMirrorRelPath_MarkerInDifferentCase_IsFound()
        {
            Assert.Equal(@"\Artists\X\Y.xml", MirrorIndex.GetMirrorRelPath(@"c:\music\audio_mirror\Artists\X\Y.xml"),
                "the folder-name search is case-insensitive");
        }

        // KnownGap: forward slashes are returned as-is, so the result has no leading backslash, which LibChecker's path index needs.
        public static void GetMirrorRelPath_KnownGap_ForwardSlashPath_KeepsForwardSlashes()
        {
            Assert.Equal("/Artists/X/Y.xml", MirrorIndex.GetMirrorRelPath("C:/Music/AUDIO_MIRROR/Artists/X/Y.xml"),
                "KnownGap: no separator normalisation, so the result is not a backslash rel path");
        }

        public static void GetMirrorRelPath_NoMarker_ReturnsBackslashAndFileName()
        {
            Assert.Equal(@"\Y.xml", MirrorIndex.GetMirrorRelPath(@"C:\Other\Y.xml"), "backslash path without the marker falls back to backslash + file name");
            Assert.Equal(@"\Y.xml", MirrorIndex.GetMirrorRelPath("C:/Other/Y.xml"), "forward-slash path without the marker also yields the file name");
        }

        public static void GetMirrorRelPath_EmptyOrNull_ReturnsEmptyString()
        {
            Assert.Equal("", MirrorIndex.GetMirrorRelPath(""), "empty input gives an empty string");
            Assert.Equal("", MirrorIndex.GetMirrorRelPath(null), "null input gives an empty string, not null");
        }

        public static void GetMirrorRelPath_MarkerTwice_UsesTheLastOccurrence()
        {
            Assert.Equal(@"\Artists\Y.xml", MirrorIndex.GetMirrorRelPath(@"D:\AUDIO_MIRROR\AUDIO_MIRROR\Artists\Y.xml"),
                "LastIndexOf: the nearest AUDIO_MIRROR to the file wins");
        }

        public static void GetMirrorRelPath_MusicIntegratorForwarder_MatchesMirrorIndex()
        {
            string path = @"C:\Music\AUDIO_MIRROR\Artists\X\Y.xml";
            Assert.Equal(MirrorIndex.GetMirrorRelPath(path), MusicIntegrator.GetMirrorRelPath(path),
                "MusicIntegrator.GetMirrorRelPath must forward unchanged");
        }
    }
}
