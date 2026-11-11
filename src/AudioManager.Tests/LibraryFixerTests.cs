using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace AudioManager
{
    /// <summary>
    /// Library fix for LibChecker album_in_singles: plan, apply, revert. Every library and output folder is a
    /// temp folder created by the test and deleted in finally. Nothing reads NewMusic, Audio or the mirror.
    /// </summary>
    internal static class LibraryFixerTests
    {
        private static string TempLib()
        {
            string root = Path.Combine(Path.GetTempPath(), "am_libfix_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(Path.Combine(root, "Artists"));
            return root;
        }

        private static string Scratch()
        {
            string dir = Path.Combine(Path.GetTempPath(), "am_libfix_out_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static void Cleanup(string dir)
        {
            if (dir != null && Directory.Exists(dir)) Directory.Delete(dir, true);
        }

        private static string ArtistDir(string lib, string artist)
        {
            return Path.Combine(lib, "Artists", artist);
        }

        /// <summary>Tagged mp3 in Artists/{artist}/Singles/ with the given album tag.</summary>
        private static string Single(string lib, string artist, string file, string album)
        {
            string dir = Path.Combine(ArtistDir(lib, artist), "Singles");
            Directory.CreateDirectory(dir);
            return TagFixerTests.CreateSilentMp3Fixture(dir, file, "Title " + file, album, artist);
        }

        /// <summary>Album folder Artists/{artist}/{folder}/ holding count tagged songs.</summary>
        private static void AlbumFolder(string lib, string artist, string folder, int count)
        {
            string dir = Path.Combine(ArtistDir(lib, artist), folder);
            Directory.CreateDirectory(dir);
            for (int i = 1; i <= count; i++)
                TagFixerTests.CreateSilentMp3Fixture(dir, "Track " + i + ".mp3", "Track " + i, folder, artist);
        }

        private static string Rel(string root, string full)
        {
            return full.Substring(root.Length).TrimStart('\\');
        }

        /// <summary>Sorted list of every file and folder under root, relative to root. Compares a layout exactly.</summary>
        private static string Snapshot(string root)
        {
            var lines = new List<string>();
            foreach (string f in Directory.GetFiles(root, "*", SearchOption.AllDirectories)) lines.Add("F " + Rel(root, f));
            foreach (string d in Directory.GetDirectories(root, "*", SearchOption.AllDirectories)) lines.Add("D " + Rel(root, d));
            lines.Sort(StringComparer.Ordinal);
            return string.Join("\n", lines);
        }

        private static LibraryFixer.PlannedMove FindMove(LibraryFixer.FixPlan plan, string fileName)
        {
            foreach (var m in plan.Moves)
                if (m.From.EndsWith("\\" + fileName, StringComparison.OrdinalIgnoreCase)) return m;
            return null;
        }

        public static void Plan_TwoSinglesOfOneAlbumNoFolder_PlansTwoMovesCreatingFolder()
        {
            string lib = TempLib();
            try
            {
                Single(lib, "Artist Alpha", "Artist Alpha - One.mp3", "Test Album");
                Single(lib, "Artist Alpha", "Artist Alpha - Two.mp3", "Test Album");

                LibraryFixer.FixPlan plan = LibraryFixer.BuildAlbumInSinglesPlan(lib);

                Assert.True(plan.Moves.Count == 2, "two Singles songs of one album, no album folder -> 2 moves, got " + plan.Moves.Count);
                Assert.True(plan.Skipped.Count == 0, "no skips expected");
                LibraryFixer.PlannedMove one = FindMove(plan, "Artist Alpha - One.mp3");
                Assert.True(one != null, "move for One.mp3 present");
                Assert.Equal(Path.Combine("Artists", "Artist Alpha", "Singles", "Artist Alpha - One.mp3"), one.From, "from is the Singles file, relative to the library root");
                Assert.Equal(Path.Combine("Artists", "Artist Alpha", "Test Album", "Artist Alpha - One.mp3"), one.To, "to is the new album folder, relative to the library root");
                Assert.Equal("Artist Alpha", one.Artist, "artist recorded");
                Assert.Equal("Test Album", one.Album, "album recorded");
                Assert.True(one.CreatesFolder, "no existing album folder -> createsFolder");
            }
            finally { Cleanup(lib); }
        }

        public static void Plan_SingleWithExistingAlbumFolder_MovesIntoExistingFolder()
        {
            string lib = TempLib();
            try
            {
                Single(lib, "Artist Alpha", "Artist Alpha - One.mp3", "Test Album");
                AlbumFolder(lib, "Artist Alpha", "Test Album", 1);

                LibraryFixer.FixPlan plan = LibraryFixer.BuildAlbumInSinglesPlan(lib);

                Assert.True(plan.Moves.Count == 1, "1 Singles song + album folder with 1 song -> 1 move, got " + plan.Moves.Count);
                LibraryFixer.PlannedMove one = plan.Moves[0];
                Assert.Equal(Path.Combine("Artists", "Artist Alpha", "Test Album", "Artist Alpha - One.mp3"), one.To, "moves into the existing album folder");
                Assert.True(!one.CreatesFolder, "existing folder -> createsFolder false");
            }
            finally { Cleanup(lib); }
        }

        public static void Plan_LoneSingle_PlansNoMove()
        {
            string lib = TempLib();
            try
            {
                Single(lib, "Artist Alpha", "Artist Alpha - Lone.mp3", "Lone Album");

                LibraryFixer.FixPlan plan = LibraryFixer.BuildAlbumInSinglesPlan(lib);

                Assert.True(plan.Moves.Count == 0, "a lone single is not an album in Singles -> no move, got " + plan.Moves.Count);
                Assert.True(plan.Skipped.Count == 0, "a lone single is not skipped either");
            }
            finally { Cleanup(lib); }
        }

        public static void Plan_DestinationExists_IsSkippedWithReason()
        {
            string lib = TempLib();
            try
            {
                Single(lib, "Artist Alpha", "Artist Alpha - One.mp3", "Test Album");
                Single(lib, "Artist Alpha", "Artist Alpha - Two.mp3", "Test Album");
                // A file with the same name already sits in the album folder the moves would target.
                string albumDir = Path.Combine(ArtistDir(lib, "Artist Alpha"), "Test Album");
                Directory.CreateDirectory(albumDir);
                TagFixerTests.CreateSilentMp3Fixture(albumDir, "Artist Alpha - One.mp3", "Clash", "Test Album", "Artist Alpha");

                LibraryFixer.FixPlan plan = LibraryFixer.BuildAlbumInSinglesPlan(lib);

                Assert.True(plan.Moves.Count == 1, "only the non-clashing song moves, got " + plan.Moves.Count);
                Assert.True(FindMove(plan, "Artist Alpha - Two.mp3") != null, "Two.mp3 is the planned move");
                Assert.True(plan.Skipped.Count == 1, "the clashing move is skipped, got " + plan.Skipped.Count);
                Assert.True(plan.Skipped[0].From.EndsWith("Artist Alpha - One.mp3", StringComparison.OrdinalIgnoreCase), "skipped entry is One.mp3");
                Assert.True(!string.IsNullOrEmpty(plan.Skipped[0].Reason), "skipped entry carries a reason");
            }
            finally { Cleanup(lib); }
        }

        public static void Apply_ThenRevert_RestoresExactLayoutAndRemovesCreatedFolder()
        {
            string lib = TempLib();
            string scratch = Scratch();
            try
            {
                Single(lib, "Artist Alpha", "Artist Alpha - One.mp3", "Test Album");
                Single(lib, "Artist Alpha", "Artist Alpha - Two.mp3", "Test Album");
                Single(lib, "Artist Alpha", "Artist Alpha - Lone.mp3", "Lone Album");
                string before = Snapshot(lib);

                string planPath = Path.Combine(scratch, "libfix-plan.json");
                string journalPath = Path.Combine(scratch, "libfix-journal-test.json");
                LibraryFixer.WritePlan(LibraryFixer.BuildAlbumInSinglesPlan(lib), planPath);

                LibraryFixer.FixResult applied = LibraryFixer.Apply(planPath, journalPath, lib);
                Assert.Equal("applied", applied.Status, "apply status");
                Assert.True(applied.Moved == 2, "2 files moved, got " + applied.Moved);
                Assert.True(File.Exists(Path.Combine(lib, "Artists", "Artist Alpha", "Test Album", "Artist Alpha - One.mp3")), "One.mp3 moved into the album folder");
                Assert.True(!File.Exists(Path.Combine(lib, "Artists", "Artist Alpha", "Singles", "Artist Alpha - One.mp3")), "One.mp3 gone from Singles");
                Assert.True(File.Exists(journalPath), "journal written");

                LibraryFixer.FixJournal journal = LibraryFixer.ReadJournal(journalPath);
                Assert.True(journal.Moves.Count == 2, "journal lists both done moves, got " + journal.Moves.Count);
                Assert.True(journal.Moves[0].CreatedFolder != null, "the first move recorded the folder it created");
                Assert.True(journal.Moves[1].CreatedFolder == null, "the second move reused that folder, so it created none");

                LibraryFixer.FixResult reverted = LibraryFixer.Revert(journalPath, lib);
                Assert.Equal("reverted", reverted.Status, "revert status");
                Assert.True(reverted.Reverted == 2, "2 files reverted, got " + reverted.Reverted);
                Assert.True(reverted.FoldersRemoved == 1, "the created album folder was removed, got " + reverted.FoldersRemoved);
                Assert.Equal(before, Snapshot(lib), "revert restores the exact original layout");
            }
            finally { Cleanup(lib); Cleanup(scratch); }
        }

        public static void Apply_StalePlan_RefusesBeforeAnyMove()
        {
            string lib = TempLib();
            string scratch = Scratch();
            try
            {
                Single(lib, "Artist Alpha", "Artist Alpha - One.mp3", "Test Album");
                Single(lib, "Artist Alpha", "Artist Alpha - Two.mp3", "Test Album");
                string planPath = Path.Combine(scratch, "libfix-plan.json");
                string journalPath = Path.Combine(scratch, "libfix-journal-test.json");
                LibraryFixer.WritePlan(LibraryFixer.BuildAlbumInSinglesPlan(lib), planPath);

                // Remove one planned source after planning: the plan no longer matches the library.
                File.Delete(Path.Combine(lib, "Artists", "Artist Alpha", "Singles", "Artist Alpha - Two.mp3"));
                string before = Snapshot(lib);

                LibraryFixer.FixResult result = LibraryFixer.Apply(planPath, journalPath, lib);
                Assert.Equal("stale_plan", result.Status, "missing from -> stale_plan");
                Assert.True(result.Moved == 0, "nothing moved");
                Assert.Equal(before, Snapshot(lib), "no file touched when the plan is stale");
                Assert.True(!File.Exists(journalPath), "no journal written when nothing was applied");
            }
            finally { Cleanup(lib); Cleanup(scratch); }
        }

        public static void Apply_WhileAnotherApplyHoldsTheLock_IsLockedAndMovesNothing()
        {
            string lib = TempLib();
            string scratch = Scratch();
            try
            {
                Single(lib, "Artist Alpha", "Artist Alpha - One.mp3", "Test Album");
                Single(lib, "Artist Alpha", "Artist Alpha - Two.mp3", "Test Album");
                string planPath = Path.Combine(scratch, "libfix-plan.json");
                string journalPath = Path.Combine(scratch, "libfix-journal-test.json");
                LibraryFixer.WritePlan(LibraryFixer.BuildAlbumInSinglesPlan(lib), planPath);
                string before = Snapshot(lib);

                using (new FileStream(Path.Combine(scratch, LibraryFixer.LockFileName), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
                {
                    LibraryFixer.FixResult result = LibraryFixer.Apply(planPath, journalPath, lib);
                    Assert.Equal("apply_locked", result.Status, "a second apply is refused");
                    Assert.True(result.Moved == 0, "nothing moved");
                }
                Assert.Equal(before, Snapshot(lib), "library untouched while locked");

                // The lock is released after a normal apply, so a later apply is not blocked.
                Assert.Equal("applied", LibraryFixer.Apply(planPath, journalPath, lib).Status, "apply after the lock is gone");
                Assert.True(!File.Exists(Path.Combine(scratch, LibraryFixer.LockFileName)), "lock file removed after apply");
            }
            finally { Cleanup(lib); Cleanup(scratch); }
        }

        public static void Apply_PlanForDifferentRoot_IsRefused()
        {
            string lib = TempLib();
            string other = TempLib();
            string scratch = Scratch();
            try
            {
                Single(lib, "Artist Alpha", "Artist Alpha - One.mp3", "Test Album");
                Single(lib, "Artist Alpha", "Artist Alpha - Two.mp3", "Test Album");
                string planPath = Path.Combine(scratch, "libfix-plan.json");
                string journalPath = Path.Combine(scratch, "libfix-journal-test.json");
                LibraryFixer.WritePlan(LibraryFixer.BuildAlbumInSinglesPlan(lib), planPath);
                string before = Snapshot(lib);

                LibraryFixer.FixResult result = LibraryFixer.Apply(planPath, journalPath, other);
                Assert.Equal("refused", result.Status, "plan libraryRoot differs from the argument -> refused");
                Assert.True(result.Moved == 0, "nothing moved");
                Assert.Equal(before, Snapshot(lib), "the planned library is untouched");
            }
            finally { Cleanup(lib); Cleanup(other); Cleanup(scratch); }
        }

        public static void Revert_NeverOverwritesAnOccupiedOriginalPath()
        {
            string lib = TempLib();
            string scratch = Scratch();
            try
            {
                Single(lib, "Artist Alpha", "Artist Alpha - One.mp3", "Test Album");
                Single(lib, "Artist Alpha", "Artist Alpha - Two.mp3", "Test Album");
                string planPath = Path.Combine(scratch, "libfix-plan.json");
                string journalPath = Path.Combine(scratch, "libfix-journal-test.json");
                LibraryFixer.WritePlan(LibraryFixer.BuildAlbumInSinglesPlan(lib), planPath);
                Assert.Equal("applied", LibraryFixer.Apply(planPath, journalPath, lib).Status, "apply first");

                // Someone put a new file back at One.mp3's original Singles path.
                string occupied = Path.Combine(lib, "Artists", "Artist Alpha", "Singles", "Artist Alpha - One.mp3");
                File.WriteAllText(occupied, "new content", Encoding.UTF8);

                LibraryFixer.FixResult result = LibraryFixer.Revert(journalPath, lib);
                Assert.True(result.Skipped == 1, "the occupied original is skipped, got " + result.Skipped);
                Assert.True(result.Reverted == 1, "the other file still reverts, got " + result.Reverted);
                Assert.Equal("partial", result.Status, "a skipped move makes the revert partial");
                Assert.Equal("new content", File.ReadAllText(occupied, Encoding.UTF8), "the occupying file is never overwritten");
                Assert.True(File.Exists(Path.Combine(lib, "Artists", "Artist Alpha", "Test Album", "Artist Alpha - One.mp3")), "the moved One.mp3 stays where it is");
            }
            finally { Cleanup(lib); Cleanup(scratch); }
        }

        public static void Plan_WrittenJson_HasSchemaKindAndCounts()
        {
            string lib = TempLib();
            string scratch = Scratch();
            try
            {
                Single(lib, "Artist Alpha", "Artist Alpha - One.mp3", "Test Album");
                Single(lib, "Artist Alpha", "Artist Alpha - Two.mp3", "Test Album");
                string planPath = Path.Combine(scratch, "libfix-plan.json");
                LibraryFixer.WritePlan(LibraryFixer.BuildAlbumInSinglesPlan(lib), planPath);

                string text = File.ReadAllText(planPath, Encoding.UTF8);
                Assert.True(text.Contains("\"schema\": \"libfix-plan-1\""), "schema field");
                Assert.True(text.Contains("\"kind\": \"album_in_singles\""), "kind field");
                Assert.True(text.Contains("\"counts\": {\"moves\": 2, \"skipped\": 0}"), "counts field");
                LibraryFixer.FixPlan back = LibraryFixer.ReadPlan(planPath);
                Assert.True(back.Moves.Count == 2, "plan reads back with its moves");
                Assert.Equal(lib, back.LibraryRoot, "libraryRoot round-trips");
            }
            finally { Cleanup(lib); Cleanup(scratch); }
        }

        public static void FindUnknownFlag_LibFixFlags_AreKnown()
        {
            Assert.True(Program.FindUnknownFlag(new[] { "x", "--libfix-plan" }) == null, "--libfix-plan is a known flag");
            Assert.True(Program.FindUnknownFlag(new[] { "x", "--libfix-apply", @"C:\t\p.json" }) == null, "--libfix-apply with a plan path is known");
            Assert.True(Program.FindUnknownFlag(new[] { "x", "--libfix-revert", @"C:\t\j.json" }) == null, "--libfix-revert with a journal path is known");
            Assert.Equal("--libfix-aply", Program.FindUnknownFlag(new[] { "x", "--libfix-aply" }), "a mistyped libfix flag is still reported");
        }
    }
}
