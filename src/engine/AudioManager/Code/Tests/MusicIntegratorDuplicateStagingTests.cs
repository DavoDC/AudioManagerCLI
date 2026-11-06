using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using AudioManager.Code.Modules;

namespace AudioManager
{
    /// <summary>
    /// Direct tests for MusicIntegrator's duplicate review and L-stage bookkeeping: RunDuplicateReview
    /// (noInput path, with and without a manifest), CommitIfStaged / RollbackIfStaged / RollbackAllStaged
    /// (driven by a real L stage from ExecuteDuplicateDecisions), and dry-run parity for D and L decisions.
    /// Private methods are reached by reflection, as in MusicIntegratorDryRunParity2Tests. Temp trees only.
    /// </summary>
    internal static class MusicIntegratorDuplicateStagingTests
    {
        private const string BakSuffix = ".replaced.bak";

        private static string NewMusicDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), "AudioManagerTest_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static string LibCopyPath(string lib, string artist, string title) =>
            Path.Combine(lib, "Old Place", artist + " - " + title + ".mp3");

        private static void WriteLibCopy(string libPath, byte marker)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(libPath));
            File.WriteAllBytes(libPath, new byte[] { marker });
        }

        private static bool HasMarker(string path, byte marker) =>
            File.Exists(path) && File.ReadAllBytes(path).Length == 1 && File.ReadAllBytes(path)[0] == marker;

        /// <summary>Builds a duplicate in NewMusic (real file on disk) with a library copy path and a recommendation.</summary>
        private static MusicIntegrator.ScannedFile MakeDup(string sourcePath, string artist, string title, string libPath, char recommended)
        {
            File.WriteAllBytes(sourcePath, new byte[] { 9 });
            var track = new Track { Artists = artist, Title = title, Album = "Missing", Genres = "Missing" };
            return new MusicIntegrator.ScannedFile
            {
                SourcePath = sourcePath,
                Track = track,
                IsReadable = true,
                LogEntry = new MusicIntegrator.LogEntry { Filename = Path.GetFileName(sourcePath), Title = title, Artists = artist },
                Duplicate = new MusicIntegrator.DupData
                {
                    RecommendedKey = recommended,
                    OptionsLine = "[D] delete new  [L] replace library  [K] keep both",
                    LibraryFilePath = libPath,
                    RelLibraryPath = "Old Place/" + Path.GetFileName(libPath),
                    RelNewPath = Path.GetFileName(sourcePath),
                    DisplayNewFilename = Path.GetFileName(sourcePath)
                }
            };
        }

        private static IntegrationManifest ManifestFor(string filename, char resolution)
        {
            string json = "[{\"filename\": \"" + filename + "\", \"artist\": \"Good Artist\", \"title\": \"Good Song\", \"dupResolution\": \"" + resolution + "\"}]";
            var manifest = IntegrationManifest.Parse(json, out string error);
            Assert.True(manifest != null && error == null, "test manifest must parse, got: " + error);
            return manifest;
        }

        private static bool InvokeRunDuplicateReview(MusicIntegrator integrator, List<MusicIntegrator.ScannedFile> files, List<MusicIntegrator.LogEntry> logs)
        {
            MethodInfo method = typeof(MusicIntegrator).GetMethod("RunDuplicateReview", BindingFlags.Instance | BindingFlags.NonPublic);
            return (bool)method.Invoke(integrator, new object[] { files, logs });
        }

        private static void InvokeStagedHelper(string methodName, MusicIntegrator integrator, MusicIntegrator.ScannedFile sf)
        {
            MethodInfo method = typeof(MusicIntegrator).GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
            method.Invoke(integrator, new object[] { sf });
        }

        private static void InvokeRollbackAllStaged(MusicIntegrator integrator)
        {
            MethodInfo method = typeof(MusicIntegrator).GetMethod("RollbackAllStaged", BindingFlags.Instance | BindingFlags.NonPublic);
            method.Invoke(integrator, null);
        }

        // ExecuteDuplicateDecisions acts only on a Decision set by review; these fixtures carry the
        // recommended key as their decision, the same value an unattended review would have chosen.
        private static int RunExecute(MusicIntegrator integrator, List<MusicIntegrator.ScannedFile> files, List<MusicIntegrator.LogEntry> logs)
        {
            foreach (var sf in files)
            {
                if (sf.Duplicate != null) sf.Duplicate.Decision = sf.Duplicate.RecommendedKey;
            }
            int skipped = 0;
            integrator.ExecuteDuplicateDecisions(files, new HashSet<string>(), logs, ref skipped);
            return skipped;
        }

        private static void CleanUp(string lib, string newMusic)
        {
            RoutingFixtures.Cleanup(lib);
            if (Directory.Exists(newMusic)) Directory.Delete(newMusic, recursive: true);
        }

        // ---- RunDuplicateReview (noInput path) ----

        public static void RunDuplicateReview_NoDuplicates_ReturnsTrueAndAddsNothing()
        {
            var integrator = new MusicIntegrator("unused-library-root", true, null);
            var logs = new List<MusicIntegrator.LogEntry>();
            bool keepGoing = InvokeRunDuplicateReview(integrator, new List<MusicIntegrator.ScannedFile>(), logs);
            Assert.True(keepGoing, "with no duplicates the review must not stop the run");
            Assert.Equal("0", logs.Count.ToString(), "with no duplicates the review must add no log entries");
        }

        public static void RunDuplicateReview_NoInput_AutoAcceptsRecommendedD()
        {
            string lib = RoutingFixtures.CreateLibraryFixture();
            string newMusic = NewMusicDir();
            try
            {
                string source = Path.Combine(newMusic, "Good Artist - Good Song.mp3");
                var sf = MakeDup(source, "Good Artist", "Good Song", LibCopyPath(lib, "Good Artist", "Good Song"), 'D');
                var integrator = new MusicIntegrator(lib, true, null);
                var files = new List<MusicIntegrator.ScannedFile> { sf };
                var logs = new List<MusicIntegrator.LogEntry>();

                bool keepGoing = InvokeRunDuplicateReview(integrator, files, logs);

                Assert.True(keepGoing, "auto-accept must not stop the review");
                Assert.Equal("D", sf.Duplicate.Decision.ToString(), "a D recommendation must be auto-accepted as D");
                Assert.True(File.Exists(source), "the review itself must not delete the NewMusic file");
            }
            finally
            {
                CleanUp(lib, newMusic);
            }
        }

        public static void RunDuplicateReview_NoInput_DowngradesRecommendedLToK()
        {
            string lib = RoutingFixtures.CreateLibraryFixture();
            string newMusic = NewMusicDir();
            try
            {
                string libCopy = LibCopyPath(lib, "Good Artist", "Good Song");
                WriteLibCopy(libCopy, 1);
                string source = Path.Combine(newMusic, "Good Artist - Good Song.mp3");
                var sf = MakeDup(source, "Good Artist", "Good Song", libCopy, 'L');
                var integrator = new MusicIntegrator(lib, true, null);
                var files = new List<MusicIntegrator.ScannedFile> { sf };

                bool keepGoing = InvokeRunDuplicateReview(integrator, files, new List<MusicIntegrator.LogEntry>());

                Assert.True(keepGoing, "a downgraded L must not stop the review");
                Assert.Equal("K", sf.Duplicate.Decision.ToString(), "an unattended L must be downgraded to K, never replace the library copy");
                Assert.True(HasMarker(libCopy, 1), "the review must not touch the library copy");
            }
            finally
            {
                CleanUp(lib, newMusic);
            }
        }

        public static void RunDuplicateReview_NoInput_NoRecommendation_DefaultsToK()
        {
            string lib = RoutingFixtures.CreateLibraryFixture();
            string newMusic = NewMusicDir();
            try
            {
                var sf = MakeDup(Path.Combine(newMusic, "Good Artist - Good Song.mp3"), "Good Artist", "Good Song",
                    LibCopyPath(lib, "Good Artist", "Good Song"), '\0');
                var integrator = new MusicIntegrator(lib, true, null);

                InvokeRunDuplicateReview(integrator, new List<MusicIntegrator.ScannedFile> { sf }, new List<MusicIntegrator.LogEntry>());

                Assert.Equal("K", sf.Duplicate.Decision.ToString(), "with no recommendation the review must default to K (Keep Both)");
            }
            finally
            {
                CleanUp(lib, newMusic);
            }
        }

        public static void RunDuplicateReview_ManifestResolution_WinsOverRecommendationAndKeepsL()
        {
            string lib = RoutingFixtures.CreateLibraryFixture();
            string newMusic = NewMusicDir();
            try
            {
                var sf = MakeDup(Path.Combine(newMusic, "Good Artist - Good Song.mp3"), "Good Artist", "Good Song",
                    LibCopyPath(lib, "Good Artist", "Good Song"), 'D');
                var manifest = ManifestFor("Good Artist - Good Song.mp3", 'L');
                var integrator = new MusicIntegrator(lib, false, true, manifest);

                InvokeRunDuplicateReview(integrator, new List<MusicIntegrator.ScannedFile> { sf }, new List<MusicIntegrator.LogEntry>());

                Assert.Equal("L", sf.Duplicate.Decision.ToString(), "a UI-resolved L in the manifest must win over the D recommendation and must not be downgraded");
            }
            finally
            {
                CleanUp(lib, newMusic);
            }
        }

        public static void RunDuplicateReview_DryRun_ReachesSameDecisionsAndTouchesNothing()
        {
            string libDry = RoutingFixtures.CreateLibraryFixture();
            string libReal = RoutingFixtures.CreateLibraryFixture();
            string newDry = NewMusicDir();
            string newReal = NewMusicDir();
            try
            {
                var dryD = MakeDup(Path.Combine(newDry, "Good Artist - Good Song.mp3"), "Good Artist", "Good Song", LibCopyPath(libDry, "Good Artist", "Good Song"), 'D');
                var dryL = MakeDup(Path.Combine(newDry, "Other Artist - Other Song.mp3"), "Other Artist", "Other Song", LibCopyPath(libDry, "Other Artist", "Other Song"), 'L');
                var realD = MakeDup(Path.Combine(newReal, "Good Artist - Good Song.mp3"), "Good Artist", "Good Song", LibCopyPath(libReal, "Good Artist", "Good Song"), 'D');
                var realL = MakeDup(Path.Combine(newReal, "Other Artist - Other Song.mp3"), "Other Artist", "Other Song", LibCopyPath(libReal, "Other Artist", "Other Song"), 'L');
                WriteLibCopy(LibCopyPath(libDry, "Other Artist", "Other Song"), 2);
                WriteLibCopy(LibCopyPath(libReal, "Other Artist", "Other Song"), 2);

                var dryRunner = new MusicIntegrator(libDry, true, true, null);
                var realRunner = new MusicIntegrator(libReal, false, true, null);
                InvokeRunDuplicateReview(dryRunner, new List<MusicIntegrator.ScannedFile> { dryD, dryL }, new List<MusicIntegrator.LogEntry>());
                InvokeRunDuplicateReview(realRunner, new List<MusicIntegrator.ScannedFile> { realD, realL }, new List<MusicIntegrator.LogEntry>());

                Assert.Equal(realD.Duplicate.Decision.ToString(), dryD.Duplicate.Decision.ToString(), "dry review must reach the same D decision as the real review");
                Assert.Equal(realL.Duplicate.Decision.ToString(), dryL.Duplicate.Decision.ToString(), "dry review must reach the same L decision as the real review");
                Assert.True(File.Exists(dryD.SourcePath) && File.Exists(dryL.SourcePath), "a dry review must leave every NewMusic file in place");
                Assert.True(HasMarker(LibCopyPath(libDry, "Other Artist", "Other Song"), 2), "a dry review must leave the library copy untouched");
                Assert.True(!File.Exists(LibCopyPath(libDry, "Other Artist", "Other Song") + BakSuffix), "a dry review must not stage any library copy");
            }
            finally
            {
                CleanUp(libDry, newDry);
                CleanUp(libReal, newReal);
            }
        }

        // ---- CommitIfStaged / RollbackIfStaged / RollbackAllStaged ----

        public static void CommitIfStaged_AfterRealLStage_DeletesBakAndForgetsStage()
        {
            string lib = RoutingFixtures.CreateLibraryFixture();
            string newMusic = NewMusicDir();
            try
            {
                string libCopy = LibCopyPath(lib, "Good Artist", "Good Song");
                WriteLibCopy(libCopy, 1);
                var sf = MakeDup(Path.Combine(newMusic, "Good Artist - Good Song.mp3"), "Good Artist", "Good Song", libCopy, 'L');
                var integrator = new MusicIntegrator(lib, false, true, null);
                var files = new List<MusicIntegrator.ScannedFile> { sf };
                RunExecute(integrator, files, new List<MusicIntegrator.LogEntry>());
                Assert.True(HasMarker(libCopy + BakSuffix, 1), "precondition: the real L stage must move the library copy aside");
                Assert.True(!File.Exists(libCopy), "precondition: the library copy must be gone while staged");

                InvokeStagedHelper("CommitIfStaged", integrator, sf);

                Assert.True(!File.Exists(libCopy + BakSuffix), "commit must delete the staged backup once the replacement has landed");
                Assert.True(!File.Exists(libCopy), "commit must not restore the library copy");

                InvokeRollbackAllStaged(integrator);
                Assert.True(!File.Exists(libCopy), "a committed stage must be forgotten, so RollbackAllStaged must not resurrect it");
            }
            finally
            {
                CleanUp(lib, newMusic);
            }
        }

        public static void RollbackIfStaged_AfterRealLStage_RestoresLibraryCopy()
        {
            string lib = RoutingFixtures.CreateLibraryFixture();
            string newMusic = NewMusicDir();
            try
            {
                string libCopy = LibCopyPath(lib, "Good Artist", "Good Song");
                WriteLibCopy(libCopy, 1);
                var sf = MakeDup(Path.Combine(newMusic, "Good Artist - Good Song.mp3"), "Good Artist", "Good Song", libCopy, 'L');
                var integrator = new MusicIntegrator(lib, false, true, null);
                RunExecute(integrator, new List<MusicIntegrator.ScannedFile> { sf }, new List<MusicIntegrator.LogEntry>());

                InvokeStagedHelper("RollbackIfStaged", integrator, sf);

                Assert.True(HasMarker(libCopy, 1), "rollback must restore the original library copy bytes");
                Assert.True(!File.Exists(libCopy + BakSuffix), "rollback must consume the staged backup");

                InvokeRollbackAllStaged(integrator);
                Assert.True(HasMarker(libCopy, 1), "a rolled-back stage must be forgotten, so RollbackAllStaged must leave it alone");
            }
            finally
            {
                CleanUp(lib, newMusic);
            }
        }

        public static void RollbackIfStaged_LibraryPathOccupied_KeepsBakAndOccupant()
        {
            string lib = RoutingFixtures.CreateLibraryFixture();
            string newMusic = NewMusicDir();
            try
            {
                string libCopy = LibCopyPath(lib, "Good Artist", "Good Song");
                WriteLibCopy(libCopy, 1);
                var sf = MakeDup(Path.Combine(newMusic, "Good Artist - Good Song.mp3"), "Good Artist", "Good Song", libCopy, 'L');
                var integrator = new MusicIntegrator(lib, false, true, null);
                RunExecute(integrator, new List<MusicIntegrator.ScannedFile> { sf }, new List<MusicIntegrator.LogEntry>());
                WriteLibCopy(libCopy, 3);

                InvokeStagedHelper("RollbackIfStaged", integrator, sf);

                Assert.True(HasMarker(libCopy, 3), "rollback must never overwrite an occupying file at the library path");
                Assert.True(HasMarker(libCopy + BakSuffix, 1), "with the library path occupied the staged backup must be kept for manual recovery");
            }
            finally
            {
                CleanUp(lib, newMusic);
            }
        }

        public static void RollbackAllStaged_RestoresOnlyStillPendingStages()
        {
            string lib = RoutingFixtures.CreateLibraryFixture();
            string newMusic = NewMusicDir();
            try
            {
                string copyA = LibCopyPath(lib, "Good Artist", "Good Song");
                string copyB = LibCopyPath(lib, "Other Artist", "Other Song");
                WriteLibCopy(copyA, 1);
                WriteLibCopy(copyB, 2);
                var sfA = MakeDup(Path.Combine(newMusic, "Good Artist - Good Song.mp3"), "Good Artist", "Good Song", copyA, 'L');
                var sfB = MakeDup(Path.Combine(newMusic, "Other Artist - Other Song.mp3"), "Other Artist", "Other Song", copyB, 'L');
                var integrator = new MusicIntegrator(lib, false, true, null);
                RunExecute(integrator, new List<MusicIntegrator.ScannedFile> { sfA, sfB }, new List<MusicIntegrator.LogEntry>());

                InvokeStagedHelper("CommitIfStaged", integrator, sfA);
                InvokeRollbackAllStaged(integrator);

                Assert.True(!File.Exists(copyA), "a committed stage must not be restored by RollbackAllStaged");
                Assert.True(HasMarker(copyB, 2), "a stage still pending at the end of routing must be restored by RollbackAllStaged");
                Assert.True(!File.Exists(copyB + BakSuffix), "RollbackAllStaged must consume the pending backup");
            }
            finally
            {
                CleanUp(lib, newMusic);
            }
        }

        public static void CommitAndRollbackIfStaged_UnstagedSource_AreNoOps()
        {
            string lib = RoutingFixtures.CreateLibraryFixture();
            string newMusic = NewMusicDir();
            try
            {
                string libCopy = LibCopyPath(lib, "Good Artist", "Good Song");
                WriteLibCopy(libCopy, 1);
                var sf = MakeDup(Path.Combine(newMusic, "Good Artist - Good Song.mp3"), "Good Artist", "Good Song", libCopy, 'K');
                var integrator = new MusicIntegrator(lib, false, true, null);

                InvokeStagedHelper("CommitIfStaged", integrator, sf);
                InvokeStagedHelper("RollbackIfStaged", integrator, sf);

                Assert.True(HasMarker(libCopy, 1), "commit or rollback of a source that was never staged must leave the library copy alone");
                Assert.True(!File.Exists(libCopy + BakSuffix), "no backup must appear for an unstaged source");
            }
            finally
            {
                CleanUp(lib, newMusic);
            }
        }

        // ---- dry-run parity ----

        public static void DryRun_DuplicateDecisions_TouchNothingAndReportDecisions()
        {
            string lib = RoutingFixtures.CreateLibraryFixture();
            string newMusic = NewMusicDir();
            try
            {
                string libCopy = LibCopyPath(lib, "Other Artist", "Other Song");
                WriteLibCopy(libCopy, 2);
                var sfD = MakeDup(Path.Combine(newMusic, "Good Artist - Good Song.mp3"), "Good Artist", "Good Song", LibCopyPath(lib, "Good Artist", "Good Song"), 'D');
                var sfL = MakeDup(Path.Combine(newMusic, "Other Artist - Other Song.mp3"), "Other Artist", "Other Song", libCopy, 'L');
                var integrator = new MusicIntegrator(lib, dryRun: true);
                var logs = new List<MusicIntegrator.LogEntry>();

                RunExecute(integrator, new List<MusicIntegrator.ScannedFile> { sfD, sfL }, logs);

                Assert.True(File.Exists(sfD.SourcePath), "a dry run must not delete the NewMusic duplicate");
                Assert.True(File.Exists(sfL.SourcePath), "a dry run must not touch the NewMusic copy of an L replacement");
                Assert.True(HasMarker(libCopy, 2), "a dry run must leave the library copy in place");
                Assert.True(!File.Exists(libCopy + BakSuffix), "a dry run must not stage the library copy");
                Assert.Equal("would-delete", sfD.LogEntry.Status, "dry-run D must report would-delete, the real run's deleted");
                Assert.Equal("would-replace", sfL.LogEntry.Status, "dry-run L with a free destination must report would-replace");
            }
            finally
            {
                CleanUp(lib, newMusic);
            }
        }

        public static void RealRun_DuplicateDecisions_MatchDryRunDecisions()
        {
            string lib = RoutingFixtures.CreateLibraryFixture();
            string newMusic = NewMusicDir();
            try
            {
                string libCopy = LibCopyPath(lib, "Other Artist", "Other Song");
                WriteLibCopy(libCopy, 2);
                var sfD = MakeDup(Path.Combine(newMusic, "Good Artist - Good Song.mp3"), "Good Artist", "Good Song", LibCopyPath(lib, "Good Artist", "Good Song"), 'D');
                var sfL = MakeDup(Path.Combine(newMusic, "Other Artist - Other Song.mp3"), "Other Artist", "Other Song", libCopy, 'L');
                var integrator = new MusicIntegrator(lib, false, true, null);
                var sent = new List<string>();
                Action<string> original = RecycleBin.Sender;
                try
                {
                    // Fake bin: records the path and removes the file, as the Recycle Bin would.
                    RecycleBin.Sender = path => { sent.Add(path); File.Delete(path); };
                    RunExecute(integrator, new List<MusicIntegrator.ScannedFile> { sfD, sfL }, new List<MusicIntegrator.LogEntry>());
                }
                finally
                {
                    RecycleBin.Sender = original;
                }

                Assert.Equal("1", sent.Count.ToString(), "real D must send exactly one NewMusic duplicate to the Recycle Bin");
                Assert.Equal(sfD.SourcePath, sent[0], "real D must send the NewMusic duplicate, not the library copy");
                Assert.True(!File.Exists(sfD.SourcePath), "real D must remove the NewMusic duplicate via the bin (dry run would-delete)");
                Assert.Equal("deleted", sfD.LogEntry.Status, "real D status must be deleted, matching the dry run's would-delete");
                Assert.True(File.Exists(libCopy + BakSuffix) && !File.Exists(libCopy), "real L must stage the library copy aside (dry run would-replace)");
                Assert.True(File.Exists(sfL.SourcePath), "real L leaves the new file for 3b routing to place");
                Assert.True(!sfL.Duplicate.SkipRouting, "real L must not be skipped in 3a; it is routed in 3b");
            }
            finally
            {
                CleanUp(lib, newMusic);
            }
        }

        // Dry-run L with a free destination is not counted as skipped in 3a; 3b counts it as moved, as the real run does.
        public static void DryRun_ReplaceWithFreeDestination_NotCountedAsSkipped()
        {
            string lib = RoutingFixtures.CreateLibraryFixture();
            string newMusic = NewMusicDir();
            try
            {
                string libCopy = LibCopyPath(lib, "Other Artist", "Other Song");
                WriteLibCopy(libCopy, 2);
                var sfL = MakeDup(Path.Combine(newMusic, "Other Artist - Other Song.mp3"), "Other Artist", "Other Song", libCopy, 'L');
                var integrator = new MusicIntegrator(lib, dryRun: true);

                int skipped = RunExecute(integrator, new List<MusicIntegrator.ScannedFile> { sfL }, new List<MusicIntegrator.LogEntry>());

                Assert.Equal("0", skipped.ToString(), "dry-run 3a must not count a replacement that real 3b routes as skipped");
            }
            finally
            {
                CleanUp(lib, newMusic);
            }
        }

        public static void DryRun_LibraryFileMissing_ReportsSameErrorAsRealRun()
        {
            string libDry = RoutingFixtures.CreateLibraryFixture();
            string libReal = RoutingFixtures.CreateLibraryFixture();
            string newDry = NewMusicDir();
            string newReal = NewMusicDir();
            try
            {
                var dry = MakeDup(Path.Combine(newDry, "Good Artist - Good Song.mp3"), "Good Artist", "Good Song", LibCopyPath(libDry, "Good Artist", "Good Song"), 'L');
                var real = MakeDup(Path.Combine(newReal, "Good Artist - Good Song.mp3"), "Good Artist", "Good Song", LibCopyPath(libReal, "Good Artist", "Good Song"), 'L');
                var dryLogs = new List<MusicIntegrator.LogEntry>();
                var realLogs = new List<MusicIntegrator.LogEntry>();

                int drySkipped = RunExecute(new MusicIntegrator(libDry, dryRun: true), new List<MusicIntegrator.ScannedFile> { dry }, dryLogs);
                int realSkipped = RunExecute(new MusicIntegrator(libReal, false, true, null), new List<MusicIntegrator.ScannedFile> { real }, realLogs);

                Assert.Equal("error", dry.LogEntry.Status, "dry run must report a missing library file as an error");
                Assert.Equal("error", real.LogEntry.Status, "real run must report a missing library file as an error");
                Assert.Equal(real.LogEntry.Detail, dry.LogEntry.Detail, "dry-run detail must equal the real run's for a missing library file");
                Assert.Equal(realSkipped.ToString(), drySkipped.ToString(), "dry-run skipped count must equal the real run's for a missing library file");
                Assert.True(File.Exists(dry.SourcePath) && File.Exists(real.SourcePath), "neither run may delete the NewMusic file when the library copy is missing");
            }
            finally
            {
                CleanUp(libDry, newDry);
                CleanUp(libReal, newReal);
            }
        }
    }
}
