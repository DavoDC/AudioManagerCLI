using System;
using System.Collections.Generic;
using System.IO;

namespace AudioManager
{
    // AUDIOMANAGER_TEST_ROOT must redirect every library, mirror and new-music path. These tests use the pure
    // Constants.ResolveRoots seam (no filesystem access), so they never touch the real Audio folder,
    // the real AudioMirror repo or the real Downloads\NewMusic folder.
    internal static class TestRootIsolationTests
    {
        private const string FakeTestRoot = @"D:\AmTestRootFixture";
        private const string FakeUserBase = @"C:\FakeHome\";
        private const string FakeRepoRoot = @"C:\FakeGitHubRepos\AudioManagerCLI";

        public static void TestRoot_EveryResolvedPathStaysUnderTestRoot()
        {
            var r = Constants.ResolveRoots(FakeTestRoot, FakeUserBase, FakeRepoRoot);
            AssertUnderRoot(r.NewMusic, "NewMusic");
            AssertUnderRoot(r.Quarantine, "Quarantine");
            AssertUnderRoot(r.MirrorRepo, "MirrorRepo");
            AssertUnderRoot(r.Audio, "Audio");
        }

        public static void TestRoot_MirrorRepoIsTestRootAudioMirrorWithTrailingSeparator()
        {
            var r = Constants.ResolveRoots(FakeTestRoot, FakeUserBase, FakeRepoRoot);
            string expected = Path.Combine(FakeTestRoot, "AudioMirror") + Path.DirectorySeparatorChar;
            Assert.Equal(expected, r.MirrorRepo, "mirror repo is <testRoot>\\AudioMirror\\");
        }

        public static void TestRoot_AudioFolderIsTestRootAudio()
        {
            var r = Constants.ResolveRoots(FakeTestRoot, FakeUserBase, FakeRepoRoot);
            Assert.Equal(Path.Combine(FakeTestRoot, "Audio"), r.Audio, "library is <testRoot>\\Audio");
        }

        public static void TestRoot_NewMusicAndQuarantineAreTestRootSiblings()
        {
            var r = Constants.ResolveRoots(FakeTestRoot, FakeUserBase, FakeRepoRoot);
            Assert.Equal(Path.Combine(FakeTestRoot, "NewMusic"), r.NewMusic, "new music is <testRoot>\\NewMusic");
            Assert.Equal(Path.Combine(FakeTestRoot, "NewMusic-Quarantine"), r.Quarantine, "quarantine is <testRoot>\\NewMusic-Quarantine");
        }

        public static void TestRoot_NullKeepsRealPaths()
        {
            var r = Constants.ResolveRoots(null, FakeUserBase, FakeRepoRoot);
            Assert.Equal(Path.Combine(FakeUserBase, "Downloads", "NewMusic"), r.NewMusic, "unset: new music is <home>\\Downloads\\NewMusic");
            Assert.Equal(Path.Combine(FakeUserBase, "Downloads", "NewMusic-Quarantine"), r.Quarantine, "unset: quarantine is <home>\\Downloads\\NewMusic-Quarantine");
            Assert.Equal(Path.Combine(FakeUserBase, "Audio"), r.Audio, "unset: library is <home>\\Audio");
            string expectedMirror = Path.GetFullPath(Path.Combine(FakeRepoRoot, "..", "AudioMirror")) + Path.DirectorySeparatorChar;
            Assert.Equal(expectedMirror, r.MirrorRepo, "unset: mirror is <repoRoot>\\..\\AudioMirror\\");
        }

        public static void TestRoot_WhitespaceIsTreatedAsUnset()
        {
            var unset = Constants.ResolveRoots(null, FakeUserBase, FakeRepoRoot);
            var blank = Constants.ResolveRoots("   ", FakeUserBase, FakeRepoRoot);
            Assert.Equal(unset.NewMusic, blank.NewMusic, "whitespace testRoot: new music matches unset");
            Assert.Equal(unset.Audio, blank.Audio, "whitespace testRoot: library matches unset");
            Assert.Equal(unset.MirrorRepo, blank.MirrorRepo, "whitespace testRoot: mirror matches unset");
        }

        // Guard: any new code that builds a mirror path with a literal "AudioMirror" must go through Constants,
        // or it can escape AUDIOMANAGER_TEST_ROOT. Constants.cs is the only allowed place for that literal.
        public static void SourceLiteral_AudioMirrorOnlyInConstants()
        {
            string codeDir = Path.Combine(Constants.RepoRoot, "src", "AudioManager", "Code");
            Assert.True(Directory.Exists(codeDir), "source folder must exist for the literal scan: " + codeDir);

            var files = Directory.GetFiles(codeDir, "*.cs", SearchOption.AllDirectories);
            Assert.True(files.Length > 0, "literal scan found no .cs files under " + codeDir);

            var offenders = new List<string>();
            foreach (string file in files)
            {
                if (string.Equals(Path.GetFileName(file), "Constants.cs", StringComparison.OrdinalIgnoreCase)) continue;
                string[] lines = File.ReadAllLines(file);
                for (int i = 0; i < lines.Length; i++)
                {
                    if (lines[i].IndexOf("\"AudioMirror\"", StringComparison.Ordinal) >= 0)
                    {
                        offenders.Add(Path.GetFileName(file) + ":" + (i + 1));
                    }
                }
            }

            Assert.True(offenders.Count == 0,
                "literal \"AudioMirror\" outside Constants.cs (route it through Constants so AUDIOMANAGER_TEST_ROOT applies): "
                + string.Join(", ", offenders));
        }

        private static void AssertUnderRoot(string path, string label)
        {
            Assert.True(path.StartsWith(FakeTestRoot, StringComparison.OrdinalIgnoreCase),
                label + " must start with the test root but was: " + path);
        }
    }
}
