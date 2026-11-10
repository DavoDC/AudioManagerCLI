using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace AudioManager
{
    /// <summary>The Misc auto-migration records each real move in logs/misc-migration-*.json; a dry run writes nothing.</summary>
    internal static class MiscMigrationJournalTests
    {
        private static string[] Run(bool dryRun, out string from, out string to)
        {
            string lib = RoutingFixtures.CreateLibraryFixture();
            string journalDir = Path.Combine(Path.GetTempPath(), "am_miscjournal_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                from = Path.Combine(lib, Constants.MiscDir, "Good Artist - Good Song.mp3");
                Directory.CreateDirectory(Path.GetDirectoryName(from));
                File.WriteAllBytes(from, new byte[] { 1 });
                to = Path.Combine(lib, Constants.ArtistsDir, "Good Artist", Constants.SinglesDir, "Good Artist - Good Song.mp3");

                var integrator = new MusicIntegrator(lib, dryRun: dryRun);
                integrator.MigrationJournalDir = journalDir;
                var candidates = (Dictionary<string, List<string>>)typeof(MusicIntegrator)
                    .GetField("_miscMigrationCandidates", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(integrator);
                candidates["Good Artist"] = new List<string> { from };
                typeof(MusicIntegrator).GetMethod("RunMiscMigration", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(integrator, null);

                bool moved = File.Exists(to);
                Assert.True(moved == !dryRun, "real run moves the song, dry run leaves it");
                return Directory.Exists(journalDir) ? Directory.GetFiles(journalDir, "misc-migration-*.json") : new string[0];
            }
            finally
            {
                RoutingFixtures.Cleanup(lib);
                if (Directory.Exists(journalDir)) Directory.Delete(journalDir, true);
            }
        }

        public static void RealMiscMigration_WritesJournalWithFromAndTo()
        {
            string journalDir = null;
            string lib = RoutingFixtures.CreateLibraryFixture();
            journalDir = Path.Combine(Path.GetTempPath(), "am_miscjournal_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                string from = Path.Combine(lib, Constants.MiscDir, "Good Artist - Good Song.mp3");
                Directory.CreateDirectory(Path.GetDirectoryName(from));
                File.WriteAllBytes(from, new byte[] { 1 });
                string to = Path.Combine(lib, Constants.ArtistsDir, "Good Artist", Constants.SinglesDir, "Good Artist - Good Song.mp3");

                var integrator = new MusicIntegrator(lib, dryRun: false);
                integrator.MigrationJournalDir = journalDir;
                var candidates = (Dictionary<string, List<string>>)typeof(MusicIntegrator)
                    .GetField("_miscMigrationCandidates", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(integrator);
                candidates["Good Artist"] = new List<string> { from };
                typeof(MusicIntegrator).GetMethod("RunMiscMigration", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(integrator, null);

                string[] files = Directory.Exists(journalDir) ? Directory.GetFiles(journalDir, "misc-migration-*.json") : new string[0];
                Assert.Equal("1", files.Length.ToString(), "one journal file per migration run");
                string json = File.ReadAllText(files[0]);
                Assert.True(json.Contains(JsonText.Quote(from)), "journal names the source path");
                Assert.True(json.Contains(JsonText.Quote(to)), "journal names the destination path");
            }
            finally
            {
                RoutingFixtures.Cleanup(lib);
                if (Directory.Exists(journalDir)) Directory.Delete(journalDir, true);
            }
        }

        public static void DryRunMiscMigration_WritesNoJournal()
        {
            string[] files = Run(true, out _, out _);
            Assert.Equal("0", files.Length.ToString(), "a dry run must not write a journal");
        }
    }
}
