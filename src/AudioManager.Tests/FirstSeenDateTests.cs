using AudioManager.Code.Modules;
using System;
using System.IO;

namespace AudioManager
{
    /// <summary>
    /// DateAdded (first-seen date) survives mirror regeneration: Reflector reads each existing XML's DateAdded before the
    /// stub overwrites it, and TrackTag restores it when it rebuilds the XML. All data is fake and in temp folders.
    /// </summary>
    internal static class FirstSeenDateTests
    {
        private static string Today() => DateTime.Now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

        private sealed class Env
        {
            public string Root, Audio, Mirror, RepoRoot, Stamp;
        }

        private static Env NewEnv()
        {
            string root = Path.Combine(Path.GetTempPath(), "am_firstseen_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            var e = new Env
            {
                Root = root,
                Audio = Path.Combine(root, "Audio"),
                Mirror = Path.Combine(root, "AUDIO_MIRROR"),
                RepoRoot = Path.GetFullPath(root) + Path.DirectorySeparatorChar,
                Stamp = Path.Combine(root, "LastRunInfo.txt"),
            };
            Directory.CreateDirectory(Path.Combine(e.Audio, "Artists", "Fake Artist", "Fake Album"));
            Directory.CreateDirectory(e.Mirror);
            return e;
        }

        private static string Song(Env e, string name) =>
            TagFixerTests.CreateSilentMp3Fixture(Path.Combine(e.Audio, "Artists", "Fake Artist", "Fake Album"), name, "Fake Title", "Fake Album", "Fake Artist");

        private static string XmlOf(Env e, string name) =>
            Path.Combine(e.Mirror, "Artists", "Fake Artist", "Fake Album", Path.GetFileNameWithoutExtension(name) + ".xml");

        private static TrackTag OldTag(string dateAdded)
        {
            var t = new TrackTag("", "Old", "Old", "Old", "2000", "1", "Pop", "00:00:01", "0", "False", "0", "0");
            t.DateAdded = dateAdded;
            return t;
        }

        private static void RunReflector(Env e)
        {
            var original = Console.Out;
            Console.SetOut(new StringWriter());
            try { new Reflector(e.Audio, e.Mirror, e.RepoRoot, e.Stamp); }
            finally { Console.SetOut(original); }
        }

        private static void Cleanup(Env e, bool savedRegen)
        {
            AgeChecker.RegenMirror = savedRegen;
            try { Directory.Delete(e.Root, true); } catch { }
        }

        public static void Regen_KeepsExistingDateAdded_NeverOverwritten()
        {
            var e = NewEnv();
            bool saved = AgeChecker.RegenMirror;
            try
            {
                Song(e, "one.mp3");
                string xml = XmlOf(e, "one.mp3");
                Directory.CreateDirectory(Path.GetDirectoryName(xml));
                TrackXML.Write(xml, OldTag("2020-05-06"));

                AgeChecker.RegenMirror = true;
                RunReflector(e);
                TrackTag t = new TrackTag(xml);
                Assert.Equal("2020-05-06", t.DateAdded, "regen keeps the stored first-seen date");
                var again = OldTag(null);
                TrackXML.Read(xml, again);
                Assert.Equal("2020-05-06", again.DateAdded, "the rewritten XML holds it");
            }
            finally { Cleanup(e, saved); }
        }

        public static void Regen_NewTrack_GetsTodayAndFileBytes()
        {
            var e = NewEnv();
            bool saved = AgeChecker.RegenMirror;
            try
            {
                string mp3 = Song(e, "two.mp3");
                AgeChecker.RegenMirror = true;
                RunReflector(e);
                TrackTag t = new TrackTag(XmlOf(e, "two.mp3"));
                Assert.Equal(Today(), t.DateAdded, "no earlier XML: today");
                Assert.True(t.FileBytes == new FileInfo(mp3).Length, "FileBytes is the mp3 length");
            }
            finally { Cleanup(e, saved); }
        }

        public static void Regen_ExistingXmlWithoutDateAdded_GetsToday()
        {
            var e = NewEnv();
            bool saved = AgeChecker.RegenMirror;
            try
            {
                Song(e, "three.mp3");
                string xml = XmlOf(e, "three.mp3");
                Directory.CreateDirectory(Path.GetDirectoryName(xml));
                TrackXML.Write(xml, OldTag(null));
                AgeChecker.RegenMirror = true;
                RunReflector(e);
                TrackTag t = new TrackTag(xml);
                Assert.Equal(Today(), t.DateAdded, "no stored value: today");
            }
            finally { Cleanup(e, saved); }
        }

        public static void Incremental_RefreshedTrack_KeepsExistingDateAdded()
        {
            var e = NewEnv();
            bool saved = AgeChecker.RegenMirror;
            try
            {
                string mp3 = Song(e, "four.mp3");
                string xml = XmlOf(e, "four.mp3");
                Directory.CreateDirectory(Path.GetDirectoryName(xml));
                TrackXML.Write(xml, OldTag("2019-01-02"));
                File.SetLastWriteTimeUtc(xml, DateTime.UtcNow.AddDays(-2));
                File.SetLastWriteTimeUtc(mp3, DateTime.UtcNow.AddDays(-1)); // mp3 newer than xml: refresh path

                AgeChecker.RegenMirror = false;
                RunReflector(e);
                TrackTag t = new TrackTag(xml);
                Assert.Equal("2019-01-02", t.DateAdded, "refresh overwrite keeps the stored date");
            }
            finally { Cleanup(e, saved); }
        }
    }
}
