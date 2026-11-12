using System;
using System.Collections.Generic;
using System.IO;

namespace AudioManager
{
    /// <summary>
    /// BL-149: a Misc song migrated for artist X gets its ARTIST tag retagged when it differs from X only in letter case.
    /// BL-151: the same retag also renames the file so its artist text takes the folder spelling.
    /// ENG-80: a failed Misc move removes the Singles folder the migration created. Every test works on a temp library
    /// under %TEMP% that it creates and deletes, never the real library.
    /// </summary>
    internal static class MiscMigrationRetagTests
    {
        private class Fixture
        {
            public string Lib, JournalDir, Song, Dest, SinglesDir;
        }

        private static Fixture NewFixture(string tagArtist, string fileName = "Tj Hickey - Good Song.mp3")
        {
            var f = new Fixture
            {
                Lib = RoutingFixtures.CreateLibraryFixture(),
                JournalDir = Path.Combine(Path.GetTempPath(), "am_retag_journal_" + Guid.NewGuid().ToString("N").Substring(0, 8)),
            };
            string miscDir = Path.Combine(f.Lib, Constants.MiscDir);
            Directory.CreateDirectory(miscDir);
            f.Song = Path.Combine(miscDir, fileName);
            TagFixerTests.CreateSilentMp3Fixture(miscDir, fileName, "Good Song", "Album", tagArtist);
            f.SinglesDir = Path.Combine(f.Lib, Constants.ArtistsDir, "TJ Hickey", Constants.SinglesDir);
            f.Dest = Path.Combine(f.SinglesDir, fileName);
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

        /// <summary>File names in dir with their exact on-disk spelling (File.Exists cannot tell case apart on Windows).</summary>
        private static List<string> ExactNames(string dir)
        {
            var names = new List<string>();
            if (Directory.Exists(dir))
                foreach (string p in Directory.GetFiles(dir)) names.Add(Path.GetFileName(p));
            return names;
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
            integrator._miscMigrationCandidates[artist] = new List<string> { libSong };
            return integrator.RunMiscMigration();
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

        public static void RealRun_CaseOnlyArtistTag_IsRetaggedRenamedAndJournaled()
        {
            var f = NewFixture("Tj Hickey");
            try
            {
                SetArtistTags(f.Song, new[] { "Tj Hickey" }, new[] { "Tj Hickey" });
                int failed = RunMigration(NewIntegrator(f, dryRun: false), "TJ Hickey", f.Song);

                Assert.Equal("0", failed.ToString(), "a case-only retag must not count as a failure");
                string renamed = Path.Combine(f.SinglesDir, "TJ Hickey - Good Song.mp3");
                Assert.True(ExactNames(f.SinglesDir).Contains("TJ Hickey - Good Song.mp3"), "the file name takes the folder spelling of the artist");
                Assert.True(!ExactNames(f.SinglesDir).Contains("Tj Hickey - Good Song.mp3"), "the old file name is gone from Singles");
                Assert.Equal("TJ Hickey", ReadPerformers(renamed)[0], "ARTIST tag retagged to the folder spelling");
                Assert.Equal("TJ Hickey", ReadAlbumArtists(renamed)[0], "ALBUM ARTIST tag retagged to the folder spelling");

                var moves = JournalMoves(JournalPath(f));
                Assert.Equal("1", moves.Count.ToString(), "one journaled move");
                Assert.Equal(f.Song, (string)moves[0]["from"], "journal from stays the original Misc path");
                Assert.Equal(renamed, (string)moves[0]["to"], "journal to is the renamed path");
                Assert.True(moves[0].ContainsKey("retag"), "journal move carries a retag field");
                var retag = (Dictionary<string, object>)moves[0]["retag"];
                Assert.Equal("artist", (string)retag["field"], "retag field is artist");
                Assert.Equal("Tj Hickey", (string)retag["from"], "retag from is the old tag entry");
                Assert.Equal("TJ Hickey", (string)retag["to"], "retag to is the folder spelling");
            }
            finally { Cleanup(f); }
        }

        public static void DryRun_CaseOnlyArtistTag_ChangesNothingAndPrintsWouldRetagAndWouldRename()
        {
            var f = NewFixture("Tj Hickey");
            try
            {
                string output = CaptureConsole(() => RunMigration(NewIntegrator(f, dryRun: true), "TJ Hickey", f.Song));

                Assert.True(File.Exists(f.Song), "a dry run leaves the song where it is");
                Assert.True(!Directory.Exists(f.SinglesDir), "a dry run moves nothing");
                Assert.Equal("Tj Hickey", ReadPerformers(f.Song)[0], "a dry run leaves the tag alone");
                Assert.True(output.Contains("[DRY RUN] Would retag artist 'Tj Hickey' -> 'TJ Hickey': Tj Hickey - Good Song.mp3"),
                    "dry run prints the Would retag line");
                Assert.True(output.Contains("[DRY RUN] Would rename 'Tj Hickey - Good Song.mp3' -> 'TJ Hickey - Good Song.mp3'"),
                    "dry run prints the Would rename line");
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
                Assert.Equal(f.Dest, (string)moves[0]["to"], "journal to is the unchanged destination when nothing was renamed");
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

                string renamed = Path.Combine(f.SinglesDir, "TJ Hickey - Good Song.mp3");
                string[] performers = ReadPerformers(renamed);
                Assert.Equal("2", performers.Length.ToString(), "both performers are kept");
                Assert.Equal("TJ Hickey", performers[0], "the matching entry takes the folder spelling");
                Assert.Equal("Other", performers[1], "the other performer is untouched");
                string[] albumArtists = ReadAlbumArtists(renamed);
                Assert.Equal("TJ Hickey", albumArtists[0], "the matching album artist takes the folder spelling");
                Assert.Equal("Other", albumArtists[1], "the other album artist is untouched");
            }
            finally { Cleanup(f); }
        }

        public static void RealRun_EntryListInFileName_OnlyTheMatchingEntryIsRenamed()
        {
            var f = NewFixture("Tj Hickey", "Tj Hickey; Nate Good - sea side nights.mp3");
            try
            {
                SetArtistTags(f.Song, new[] { "Tj Hickey", "Nate Good" }, new[] { "Tj Hickey", "Nate Good" });
                RunMigration(NewIntegrator(f, dryRun: false), "TJ Hickey", f.Song);

                Assert.True(ExactNames(f.SinglesDir).Contains("TJ Hickey; Nate Good - sea side nights.mp3"),
                    "only the matching artist entry in the file name takes the folder spelling");
                Assert.True(!ExactNames(f.SinglesDir).Contains("Tj Hickey; Nate Good - sea side nights.mp3"), "the old file name is gone");
            }
            finally { Cleanup(f); }
        }

        public static void Revert_RestoresOriginalNameLocationAndOldArtistTag()
        {
            var f = NewFixture("Tj Hickey");
            try
            {
                SetArtistTags(f.Song, new[] { "Tj Hickey" }, new[] { "Tj Hickey" });
                RunMigration(NewIntegrator(f, dryRun: false), "TJ Hickey", f.Song);
                Assert.True(ExactNames(f.SinglesDir).Contains("TJ Hickey - Good Song.mp3"), "precondition: the song was migrated and renamed");

                NewIntegrator(f, dryRun: false).RevertMiscMigration(JournalPath(f));

                string miscDir = Path.GetDirectoryName(f.Song);
                Assert.True(ExactNames(miscDir).Contains("Tj Hickey - Good Song.mp3"), "revert moves the song back to Misc under its original name");
                Assert.Equal("0", ExactNames(f.SinglesDir).Count.ToString(), "revert removes the migrated copy");
                Assert.Equal("Tj Hickey", ReadPerformers(f.Song)[0], "revert restores the old ARTIST tag");
                Assert.Equal("Tj Hickey", ReadAlbumArtists(f.Song)[0], "revert restores the old ALBUM ARTIST tag");
            }
            finally { Cleanup(f); }
        }

        /// <summary>Applies a case-only retag migration to f.Song and returns its journal. The migrated copy is named "TJ Hickey - Good Song.mp3".</summary>
        private static string AppliedJournal(Fixture f)
        {
            SetArtistTags(f.Song, new[] { "Tj Hickey" }, new[] { "Tj Hickey" });
            RunMigration(NewIntegrator(f, dryRun: false), "TJ Hickey", f.Song);
            return JournalPath(f);
        }

        public static void Revert_CountsRevertedSkippedAndFailed()
        {
            var f = NewFixture("Tj Hickey");
            try
            {
                string journal = AppliedJournal(f);
                MusicIntegrator.MiscRevertResult result = NewIntegrator(f, dryRun: false).RevertMiscMigration(journal);

                Assert.Equal("1", result.Reverted.ToString(), "one migrated song is reverted");
                Assert.Equal("0", result.Skipped.ToString(), "nothing is skipped");
                Assert.Equal("0", result.Failed.ToString(), "nothing fails");
            }
            finally { Cleanup(f); }
        }

        public static void Revert_MigratedCopyMissing_IsSkippedNotFailed()
        {
            var f = NewFixture("Tj Hickey");
            try
            {
                string journal = AppliedJournal(f);
                File.Delete(Path.Combine(f.SinglesDir, "TJ Hickey - Good Song.mp3"));
                MusicIntegrator.MiscRevertResult result = NewIntegrator(f, dryRun: false).RevertMiscMigration(journal);

                Assert.Equal("1", result.Skipped.ToString(), "a song whose migrated copy is gone is skipped");
                Assert.Equal("0", result.Reverted.ToString(), "nothing is reverted");
                Assert.Equal("0", result.Failed.ToString(), "a skip is not a failure");
            }
            finally { Cleanup(f); }
        }

        public static void Revert_MigratedCopyLocked_CountsFailedAndLeavesItInPlace()
        {
            var f = NewFixture("Tj Hickey");
            try
            {
                string journal = AppliedJournal(f);
                string migrated = Path.Combine(f.SinglesDir, "TJ Hickey - Good Song.mp3");
                MusicIntegrator.MiscRevertResult result;
                using (new FileStream(migrated, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    result = NewIntegrator(f, dryRun: false).RevertMiscMigration(journal);
                }

                Assert.Equal("1", result.Failed.ToString(), "a move that throws is counted as failed");
                Assert.Equal("0", result.Reverted.ToString(), "a failed move is not counted as reverted");
                Assert.True(File.Exists(migrated), "the song stays at its migrated place when its move fails");
            }
            finally { Cleanup(f); }
        }

        public static void MiscRevertFlag_IsAKnownFlag()
        {
            Assert.True(Program.FindUnknownFlag(new[] { "integrate", "--misc-revert", "journal.json" }) == null,
                "--misc-revert must not be reported as an unknown option");
        }

        public static void MiscRevertFlag_NotGiven_ReturnsFalseAndRunsNothing()
        {
            int code = -1;
            bool handled = Program.TryRunMiscRevert(new[] { "integrate", "--dry-run" }, out code);

            Assert.True(!handled, "without --misc-revert the handler does not claim the run");
        }

        public static void MiscRevertFlag_MissingJournalPath_PrintsErrorAndExitsOne()
        {
            int code = -1;
            bool handled = false;
            string output = CaptureConsole(() => handled = Program.TryRunMiscRevert(new[] { "--misc-revert" }, out code));

            Assert.True(handled, "--misc-revert is claimed even without a path");
            Assert.Equal("1", code.ToString(), "a missing journal path exits 1");
            Assert.True(output.Contains("--misc-revert requires a journal path argument"), "the error names the missing argument");
        }

        public static void MiscRevertFlag_NotAMiscJournal_ExitsOne()
        {
            string notMisc = Path.Combine(Path.GetTempPath(), "am_notmisc_" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".json");
            File.WriteAllText(notMisc, "{\"recordType\": \"execution\", \"schemaVersion\": 1}");
            try
            {
                int code = -1;
                bool handled = false;
                string output = CaptureConsole(() => handled = Program.TryRunMiscRevert(new[] { "--misc-revert", notMisc }, out code));

                Assert.True(handled, "--misc-revert is claimed");
                Assert.Equal("1", code.ToString(), "a journal that is not a Misc journal exits 1");
                Assert.True(output.Contains("ERROR"), "the refusal is printed as an error");
            }
            finally { File.Delete(notMisc); }
        }

        public static void MiscRevertFlag_FailedRevert_ExitsOne()
        {
            var f = NewFixture("Tj Hickey");
            try
            {
                string journal = AppliedJournal(f);
                string migrated = Path.Combine(f.SinglesDir, "TJ Hickey - Good Song.mp3");
                int code = -1;
                string output;
                using (new FileStream(migrated, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    output = CaptureConsole(() => Program.TryRunMiscRevert(new[] { "--misc-revert", journal }, f.Lib, out code));
                }

                Assert.Equal("1", code.ToString(), "failed > 0 exits 1");
                Assert.True(output.Contains("- Misc revert: 0 reverted, 0 skipped, 1 failed"), "the summary line counts the failure");
            }
            finally { Cleanup(f); }
        }

        public static void MiscRevertFlag_CleanRevert_ExitsZeroAndRestoresTheSong()
        {
            var f = NewFixture("Tj Hickey");
            try
            {
                string journal = AppliedJournal(f);
                int code = -1;
                string output = CaptureConsole(() => Program.TryRunMiscRevert(new[] { "--misc-revert", journal }, f.Lib, out code));

                Assert.Equal("0", code.ToString(), "failed is 0, so the exit code is 0");
                Assert.True(output.Contains("- Misc revert: 1 reverted, 0 skipped, 0 failed"), "the summary line is printed");
                Assert.True(File.Exists(f.Song), "the song is back at its original Misc path");
                Assert.Equal("Tj Hickey", ReadPerformers(f.Song)[0], "the old ARTIST tag is restored");
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

        /// <summary>Rewrites the single move in a journal so its from and to point where a test needs them.</summary>
        private static void SetJournalMove(string journalPath, string from, string to)
        {
            var ser = new System.Web.Script.Serialization.JavaScriptSerializer();
            var root = (Dictionary<string, object>)ser.DeserializeObject(File.ReadAllText(journalPath));
            var move = (Dictionary<string, object>)((object[])root["moves"])[0];
            move["from"] = from;
            move["to"] = to;
            File.WriteAllText(journalPath, ser.Serialize(root));
        }

        public static void Revert_RetagFails_SongStaysAtMigratedPlaceAndCountsFailed()
        {
            var f = NewFixture("Tj Hickey");
            string migrated = Path.Combine(f.SinglesDir, "TJ Hickey - Good Song.mp3");
            try
            {
                string journal = AppliedJournal(f);
                // A read-only file cannot be opened for tag writing, so the retag throws, but it can still be moved.
                File.SetAttributes(migrated, FileAttributes.ReadOnly);
                MusicIntegrator.MiscRevertResult result = NewIntegrator(f, dryRun: false).RevertMiscMigration(journal);

                Assert.Equal("1", result.Failed.ToString(), "a retag that throws is counted as failed");
                Assert.Equal("0", result.Reverted.ToString(), "a failed revert is not counted as reverted");
                Assert.True(File.Exists(migrated), "a failed retag leaves the song unmoved at its migrated place");
                Assert.True(!File.Exists(f.Song), "the song is not moved back to Misc when its retag fails");
            }
            finally
            {
                foreach (string p in new[] { migrated, f.Song })
                    if (File.Exists(p)) File.SetAttributes(p, FileAttributes.Normal);
                Cleanup(f);
            }
        }

        public static void Revert_MoveFailsAfterRetag_RestoresMigratedTagAndCountsFailed()
        {
            var f = NewFixture("Tj Hickey");
            try
            {
                string journal = AppliedJournal(f);
                string migrated = Path.Combine(f.SinglesDir, "TJ Hickey - Good Song.mp3");
                // A directory where the old file must go makes the move back throw after the retag has succeeded.
                Directory.CreateDirectory(f.Song);
                MusicIntegrator.MiscRevertResult result = NewIntegrator(f, dryRun: false).RevertMiscMigration(journal);

                Assert.Equal("1", result.Failed.ToString(), "a move that throws after the retag is counted as failed");
                Assert.True(File.Exists(migrated), "the song stays at its migrated place when its move back fails");
                Assert.Equal("TJ Hickey", ReadPerformers(migrated)[0], "the migrated ARTIST tag is restored after the failed move");
            }
            finally { Cleanup(f); }
        }

        public static void Revert_MalformedEntry_FailsAloneAndOtherEntriesStillRevert()
        {
            var f = NewFixture("Tj Hickey");
            try
            {
                string journal = AppliedJournal(f);
                var ser = new System.Web.Script.Serialization.JavaScriptSerializer();
                var root = (Dictionary<string, object>)ser.DeserializeObject(File.ReadAllText(journal));
                var moves = new List<object>((object[])root["moves"]);
                moves.Add("not a move object"); // processed first (newest first), so it must not stop the valid entry
                root["moves"] = moves.ToArray();
                File.WriteAllText(journal, ser.Serialize(root));

                MusicIntegrator.MiscRevertResult result = NewIntegrator(f, dryRun: false).RevertMiscMigration(journal);

                Assert.Equal("1", result.Failed.ToString(), "the malformed entry is counted as failed");
                Assert.Equal("1", result.Reverted.ToString(), "the valid entry is still reverted");
                Assert.True(File.Exists(f.Song), "the valid song is back at its original Misc path");
            }
            finally { Cleanup(f); }
        }

        public static void Revert_FromOutsideLibrary_IsRefusedAndNothingMoves()
        {
            var f = NewFixture("Tj Hickey");
            string outsideDir = Path.Combine(Path.GetTempPath(), "am_outside_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                string journal = AppliedJournal(f);
                string migrated = Path.Combine(f.SinglesDir, "TJ Hickey - Good Song.mp3");
                string outsideFrom = Path.Combine(outsideDir, "outside.mp3");
                SetJournalMove(journal, outsideFrom, migrated);

                MusicIntegrator.MiscRevertResult result = NewIntegrator(f, dryRun: false).RevertMiscMigration(journal);

                Assert.Equal("1", result.Failed.ToString(), "a from path outside the library is refused and counted as failed");
                Assert.Equal("0", result.Reverted.ToString(), "nothing is reverted");
                Assert.True(File.Exists(migrated), "the library song is not moved out of the library");
                Assert.True(!File.Exists(outsideFrom), "nothing is written outside the library");
            }
            finally
            {
                if (Directory.Exists(outsideDir)) Directory.Delete(outsideDir, true);
                Cleanup(f);
            }
        }

        public static void Revert_ToOutsideLibrary_IsRefusedAndOutsideFileIsUntouched()
        {
            var f = NewFixture("Tj Hickey");
            string outsideDir = Path.Combine(Path.GetTempPath(), "am_outside_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                string journal = AppliedJournal(f);
                Directory.CreateDirectory(outsideDir);
                string outsideTo = Path.Combine(outsideDir, "outside.mp3");
                File.WriteAllText(outsideTo, "not a song");
                SetJournalMove(journal, f.Song, outsideTo);

                MusicIntegrator.MiscRevertResult result = NewIntegrator(f, dryRun: false).RevertMiscMigration(journal);

                Assert.Equal("1", result.Failed.ToString(), "a to path outside the library is refused and counted as failed");
                Assert.Equal("0", result.Reverted.ToString(), "nothing is reverted");
                Assert.True(File.Exists(outsideTo), "the file outside the library is left where it is");
                Assert.True(!File.Exists(f.Song), "nothing is moved into the library from outside");
            }
            finally
            {
                if (Directory.Exists(outsideDir)) Directory.Delete(outsideDir, true);
                Cleanup(f);
            }
        }
    }
}
