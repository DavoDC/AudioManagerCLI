using AudioManager.Code.Modules;
using System;
using System.IO;

namespace AudioManager
{
    /// <summary>
    /// TrackTag.RelPathFor anchors the relative path on the mirror root, so an artist folder whose
    /// name contains the marker text does not truncate the path. Falls back to the marker search
    /// only when the file is not under the mirror root.
    /// </summary>
    internal static class TrackTagRelPathTests
    {
        private static string TempMirrorRoot() =>
            Path.Combine(Path.GetTempPath(), "am_relpath_" + Guid.NewGuid().ToString("N"), Constants.MirrorFolderName);

        public static void RelPath_ArtistFolderContainingMarkerText_IsRemainderAfterMirrorRoot()
        {
            string mirrorRoot = TempMirrorRoot();
            string filePath = Path.Combine(mirrorRoot, "Artists", "AUDIO_MIRROR Fan", "Singles", "song.xml");

            string rel = TrackTag.RelPathFor(filePath, mirrorRoot);

            Assert.Equal("\\Artists\\AUDIO_MIRROR Fan\\Singles\\song.xml", rel,
                "RelPath must start at the mirror root, not at the marker inside the artist folder name");
        }

        public static void RelPath_MirrorRootMatchedIgnoringCase_IsRemainderAfterMirrorRoot()
        {
            string mirrorRoot = TempMirrorRoot();
            string filePath = mirrorRoot.ToLowerInvariant() + "\\Artists\\X\\Singles\\song.xml";

            string rel = TrackTag.RelPathFor(filePath, mirrorRoot);

            Assert.Equal("\\Artists\\X\\Singles\\song.xml", rel, "mirror root prefix must match ignoring case");
        }

        public static void RelPath_PathOutsideMirrorRoot_FallsBackToMarkerSearch()
        {
            string mirrorRoot = TempMirrorRoot();
            string rel = TrackTag.RelPathFor("C:/elsewhere/AUDIO_MIRROR/Artists/X/a.xml", mirrorRoot);

            Assert.Equal("/Artists/X/a.xml", rel, "a path outside the root keeps the old marker-based behaviour");
        }

        public static void RelPath_NoMarkerAndOutsideRoot_ReturnsNull()
        {
            string mirrorRoot = TempMirrorRoot();
            string rel = TrackTag.RelPathFor("C:/elsewhere/Artists/X/a.xml", mirrorRoot);

            Assert.True(rel == null, "a path with no mirror root and no marker has no relative path");
        }
    }
}
