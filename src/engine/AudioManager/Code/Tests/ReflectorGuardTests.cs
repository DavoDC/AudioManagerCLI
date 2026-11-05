using System;
using System.Collections.Generic;
using System.IO;

namespace AudioManager
{
    internal static class ReflectorGuardTests
    {
        public static void IsInsideMirrorRepo_AcceptsMirrorFolder()
        {
            Assert.True(Reflector.IsInsideMirrorRepo(Constants.MirrorFolderPath), "mirror folder is inside the mirror repo");
        }

        public static void IsInsideMirrorRepo_RejectsSiblingWithSamePrefix()
        {
            string sibling = Constants.MirrorRepoPath.TrimEnd(Path.DirectorySeparatorChar) + "Old" + Path.DirectorySeparatorChar + "x";
            Assert.True(!Reflector.IsInsideMirrorRepo(sibling), "AudioMirrorOld must not pass");
        }

        public static void IsInsideMirrorRepo_RejectsDotDotEscape()
        {
            string escape = Path.Combine(Constants.MirrorFolderPath, "..", "..", "Other");
            Assert.True(!Reflector.IsInsideMirrorRepo(escape), ".. escape must not pass");
        }

        public static void IsInsideMirrorRepo_RejectsRepoRootItselfAndEmpty()
        {
            Assert.True(!Reflector.IsInsideMirrorRepo(Constants.MirrorRepoPath), "the repo root itself is not a mirror folder");
            Assert.True(!Reflector.IsInsideMirrorRepo(""), "empty path rejected");
        }

        public static void PruneOrphanedXmls_EmptyExpectedSet_DeletesNothing()
        {
            string mirror = Path.Combine(Path.GetTempPath(), "am_prune_empty_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(mirror);
            try
            {
                string a = Path.Combine(mirror, "a.xml");
                string b = Path.Combine(mirror, "b.xml");
                File.WriteAllText(a, "stub");
                File.WriteAllText(b, "stub");
                var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                int pruned = Reflector.PruneOrphanedXmls(mirror, expected);
                Assert.True(pruned == 0, "an empty expected set must prune nothing");
                Assert.True(File.Exists(a) && File.Exists(b), "an empty expected set must not delete any XML");
            }
            finally { Directory.Delete(mirror, recursive: true); }
        }

        public static void ReplaceFileNameSegment_KeepsDirectoryContainingFileNameText()
        {
            // The directory segment contains the filename text; only the final segment may change
            string rel = "Café.mp3 Stuff\\Café.mp3";
            string result = Reflector.ReplaceFileNameSegment(rel, "Caf_.mp3");
            Assert.Equal("Café.mp3 Stuff\\Caf_.mp3", result, "only the final path segment is sanitised");
        }

        public static void ReplaceFileNameSegment_RootLevelFile_ReturnsSanitisedName()
        {
            string result = Reflector.ReplaceFileNameSegment("Café.mp3", "Caf_.mp3");
            Assert.Equal("Caf_.mp3", result, "a root-level file has no directory segment");
        }
    }
}
