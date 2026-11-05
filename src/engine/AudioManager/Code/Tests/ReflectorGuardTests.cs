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
    }
}
