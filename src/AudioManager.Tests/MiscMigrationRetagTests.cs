using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace AudioManager
{
    /// <summary>
    /// BL-149: a Misc song migrated for artist X gets its ARTIST tag retagged when it differs from X only in letter case.
    /// ENG-80: a failed Misc move removes the Singles folder the migration created. Every test works on a temp library
    /// under %TEMP% that it creates and deletes, never the real library.
    /// </summary>
    internal static class MiscMigrationRetagTests
    {
        private class Fixture
        {
            public string Lib, JournalDir, Song, Dest, SinglesDir;
        }

        private static Fixture NewFixture(string tagArtist)
        {
            var f = new Fixture
            {
                Lib = RoutingFixtures.CreateLibraryFixture(),
                JournalDir = Path.Combine(Path.GetTempPath(), "am_retag_journal_" + Guid.NewGuid().ToString("N").Substring(0, 8)),
            };
            string miscDir = Path.Combine(f.Lib, Constants.MiscDir);
            Directory.CreateDirectory(miscDir);
            f.Song = Path.Combine(miscDir, "Tj Hickey - Good Song.mp3");
            TagFixerTests.CreateSilentMp3Fixture(miscDir, "Tj Hickey - Good Song.mp3", "Good Song", "Album", tagArtist);
            f.SinglesDir = Path.Combine(f.Lib, Constants.ArtistsDir, "TJ Hickey", Constants.SinglesDir);
            f.Dest = Path.Combine(f.SinglesDir, "Tj Hickey - Good Song.mp3");
            return f;
        }

        private static void Cleanup(Fixture f)
        {
            RoutingFixtures.Cleanup(f.Lib);
            if (Directory.Exists(f.JournalDir)) Directory.Delete(f.JournalDir, true);
        }

        private static void SetArtistTags(string path, string[] performers, string[] albumArtists)
        {
            using (var tf = TagLib.File.Create(path))
            {
                tf.Tag.Performers = performers;
                tf.Tag.AlbumArtists = albumArtists;
                tf.Save();
            }
        }

        private static string[] ReadPerformers(string path)
        {
            using (var tf = TagLib.File.Create(path)) return tf.Tag.Performers ?? new string[0];
        }

        private static string[] ReadAlbumArtists(string path)
        {
            using (var tf = TagLib.File.Create(path)) return tf.Tag.AlbumArtists ?? new string[0];
        }

        private static MusicIntegrator NewIntegrator(Fixture f, bool dryRun)
        {
            var integrator = new MusicIntegrator(f.Lib, dryRun: dryRun);
            integrator.MigrationJournalDir = f.JournalDir;
            return integrator;
        }

        /// <summary>Sets the candidate for artist, invokes the private RunMiscMigration and returns its failed count.</summary>
        private static int RunMigration(MusicIntegrator integrator, string artist, string libSong)
        {
            var candidates = (Dictionary<string, List<string>>)typeof(MusicIntegrator)
                .GetField("_miscMigrationCandidates", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(integrator);
            candidates[artist] = new List<string> { libSong };
            object result = typeof(MusicIntegrator).GetMethod("RunMiscMigration", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(integrator, null);
            return result is int ? (int)result : -1;
        }

        private static string JournalPath(Fixture f)
        {
            string[] files = Directory.Exists(f.JournalDir) ? Directory.GetFiles(f.JournalDir, "misc-migration-*.json") : new string[0];
            Assert.Equal("1", files.Length.ToString(), "one journal file per migration run");
            return files[0];
        }

        private static List<Dictionary<string, object>> JournalMoves(string journalPath)
        {
            var ser = new System.Web.Script.Serialization.JavaScriptSerializer();
            var root = (Dictionary<string, object>)ser.DeserializeObject(File.ReadAllText(journalPath));
            var moves = new List<Dictionary<string, object>>();
            foreach (object m in (object[])root["moves"]) moves.Add((Dictionary<string, object>)m);
            return moves;
        }

        /// <summary>Captures what the migration prints while body runs.</summary>
        private static string CaptureConsole(Action body)
        {
            var saved = Console.Out;
            var sw = new StringWriter();
            try
            {
                Console.SetOut(sw);
                body();
            }
            finally
            {
                Console.SetOut(saved);
            }
            return sw.ToString();
        }

        public static void RealRun_CaseOnlyArtistTag_IsRetaggedAndJournaled()
        {
            var f = NewFixture("Tj Hickey");
            try
            {
                SetArtistTags(f.Song, new[] { "Tj Hickey" }, new[] { "Tj Hickey" });
                int failed = RunMigration(NewIntegrator(f, dryRun: false), "TJ Hickey", f.Song);

                Assert.Equal("0", failed.ToString(), "a case-only retag must not count as a failure");
                Assert.True(File.Exists(f.Dest), "the song moves to the artist's Singles folder");
                Assert.Equal("TJ Hickey", ReadPerformers(f.Dest)[0], "ARTIST tag retagged to the folder spelling");
                Assert.Equal("TJ Hickey", ReadAlbumArtists(f.Dest)[0], "ALBUM ARTIST tag retagged to the folder spelling");

                var moves = JournalMoves(JournalPath(f));
                Assert.Equal("1", moves.Count.ToString(), "one journaled move");
                Assert.True(moves[0].ContainsKey("retag"), "journal move carries a retag field");
                var retag = (Dictionary<string, object>)moves[0]["retag"];
                Assert.Equal("artist", (string)retag["field"], "retag field is artist");
                Assert.Equal("Tj Hickey", (string)retag["from"], "retag from is the old tag entry");
                Assert.Equal("TJ Hickey", (string)retag["to"], "retag to is the folder spelling");
            }
            finally { Cleanup(f); }
        }

        public static void DryRun_CaseOnlyArtistTag_ChangesNothingAndPrintsWouldRetag()
        {
            var f = NewFixture("Tj Hickey");
            try
            {
                string output = CaptureConsole(() => RunMigration(NewIntegrator(f, dryRun: true), "TJ Hickey", f.Song));

                Assert.True(File.Exists(f.Song), "a dry run leaves the song where it is");
                Assert.True(!File.Exists(f.Dest), "a dry run moves nothing");
                Assert.Equal("Tj Hickey", ReadPerformers(f.Song)[0], "a dry run leaves the tag alone");
                Assert.True(output.Contains("[DRY RUN] Would retag artist 'Tj Hickey' -> 'TJ Hickey': Tj Hickey - Good Song.mp3"),
                    "dry run prints the Would retag line");
                Assert.Equal("0", Directory.Exists(f.JournalDir) ? Directory.GetFiles(f.JournalDir).Length.ToString() : "0", "a dry run writes no journal");
            }
            finally { Cleanup(f); }
        }

        public static void RealRun_TagAlreadyFolderSpelling_HasNoRetagField()
        {
            var f = NewFixture("TJ Hickey");
            try
            {
                SetArtistTags(f.Song, new[] { "TJ Hickey" }, new[] { "TJ Hickey" });
                RunMigration(NewIntegrator(f, dryRun: false), "TJ Hickey", f.Song);

                Assert.True(File.Exists(f.Dest), "the song still moves");
                var moves = JournalMoves(JournalPath(f));
                Assert.Equal("1", moves.Count.ToString(), "one journaled move");
                Assert.True(!moves[0].ContainsKey("retag"), "no retag field when the tag already matches exactly");
                Assert.Equal("TJ Hickey", ReadPerformers(f.Dest)[0], "tag unchanged");
            }
            finally { Cleanup(f); }
        }

        public static void RealRun_MultiArtistTag_ReplacesOnlyTheMatchingEntry()
        {
            var f = NewFixture("Tj Hickey");
            try
            {
                SetArtistTags(f.Song, new[] { "Tj Hickey", "Other" }, new[] { "Tj Hickey", "Other" });
                RunMigration(NewIntegrator(f, dryRun: false), "TJ Hickey", f.Song);

                string[] performers = ReadPerformers(f.Dest);
                Assert.Equal("2", performers.Length.ToString(), "both performers are kept");
                Assert.Equal("TJ Hickey", performers[0], "the matching entry takes the folder spelling");
                Assert.Equal("Other", performers[1], "the other performer is untouched");
                string[] albumArtists = ReadAlbumArtists(f.Dest);
                Assert.Equal("TJ Hickey", albumArtists[0], "the matching album artist takes the folder spelling");
                Assert.Equal("Other", albumArtists[1], "the other album artist is untouched");
            }
            finally { Cleanup(f); }
        }

        public static void Revert_RestoresLocationAndOldArtistTag()
        {
            var f = NewFixture("Tj Hickey");
            try
            {
                SetArtistTags(f.Song, new[] { "Tj Hickey" }, new[] { "Tj Hickey" });
                RunMigration(NewIntegrator(f, dryRun: false), "TJ Hickey", f.Song);
                Assert.True(File.Exists(f.Dest), "precondition: the song was migrated");

                MethodInfo revert = typeof(MusicIntegrator).GetMethod("RevertMiscMigration", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.True(revert != null, "RevertMiscMigration(string journalPath) must exist");
                revert.Invoke(NewIntegrator(f, dryRun: false), new object[] { JournalPath(f) });

                Assert.True(File.Exists(f.Song), "revert moves the song back to Misc");
                Assert.True(!File.Exists(f.Dest), "revert removes the migrated copy");
                Assert.Equal("Tj Hickey", ReadPerformers(f.Song)[0], "revert restores the old ARTIST tag");
                Assert.Equal("Tj Hickey", ReadAlbumArtists(f.Song)[0], "revert restores the old ALBUM ARTIST tag");
            }
            finally { Cleanup(f); }
        }

        public static void RealRun_MoveFails_RemovesSinglesFolderItCreated()
        {
            var f = NewFixture("TJ Hickey");
            try
            {
                // Holding the source open with FileShare.None makes File.Move throw while the Singles folder does not exist yet.
                int failed;
                using (new FileStream(f.Song, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    failed = RunMigration(NewIntegrator(f, dryRun: false), "TJ Hickey", f.Song);
                }

                Assert.Equal("1", failed.ToString(), "a failed move is reported as one failure");
                Assert.True(File.Exists(f.Song), "the song stays in the library when its move fails");
                Assert.True(!Directory.Exists(f.SinglesDir), "the Singles folder the migration created is removed after a failed move");
            }
            finally { Cleanup(f); }
        }
    }
}
