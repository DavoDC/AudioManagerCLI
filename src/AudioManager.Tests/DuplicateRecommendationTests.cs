using System.Collections.Generic;
using AudioManager.Code.Modules;

namespace AudioManager
{
    /// <summary>BL-57, BL-59, BL-62, BL-63: the duplicate recommendation and in-batch duplicate marking.</summary>
    internal static class DuplicateRecommendationTests
    {
        private static MusicIntegrator.DupFacts Facts() => new MusicIntegrator.DupFacts
        {
            NewAlbum = "New Album", LibraryAlbum = "Old Album", NewIsAlbum = true,
            IncomingArtists = 1, LibraryArtists = 1
        };

        public static void Recommend_DifferentLength_KeepsBoth_BecauseOfYouRemix()
        {
            var f = Facts();
            f.IncomingAlbumTracks = 5; f.LibraryAlbumTracks = 12; // size rule alone would say D
            f.IncomingLengthSec = 240; f.LibraryLengthSec = 200;
            MusicIntegrator.RecommendDuplicate(f, out char key, out string reason);
            Assert.True(key == '\0', "A distinct recording (length differs) must not recommend deleting: got " + key);
            Assert.True(reason.Contains("Different recording"), "Reason must name the distinct recording: " + reason);
        }

        public static void Recommend_SameLengthWithinTolerance_StillUsesSizeRule()
        {
            var f = Facts();
            f.IncomingAlbumTracks = 5; f.LibraryAlbumTracks = 12;
            f.IncomingLengthSec = 200; f.LibraryLengthSec = 201.5;
            MusicIntegrator.RecommendDuplicate(f, out char key, out string reason);
            Assert.True(key == 'D', "Same recording, bigger library album: keep library, got " + key);
        }

        public static void Recommend_MoreArtistsIncoming_KeepsIncomingBeforeSameAlbumRule()
        {
            var f = Facts();
            f.SameAlbum = true; f.IncomingArtists = 3; f.LibraryArtists = 1;
            MusicIntegrator.RecommendDuplicate(f, out char key, out string reason);
            Assert.True(key == 'L', "Frankie Sinatra case: 3 artists vs 1 must recommend L even on the same album, got " + key);
            Assert.True(reason.Contains("richer metadata"), reason);
        }

        public static void Recommend_FewerArtistsIncoming_DoesNotForceL()
        {
            var f = Facts();
            f.SameAlbum = true; f.IncomingArtists = 1; f.LibraryArtists = 3;
            MusicIntegrator.RecommendDuplicate(f, out char key, out string reason);
            Assert.True(key == 'D', "Same album, library richer: D, got " + key);
        }

        public static void Recommend_SameSongTwoAlbums_BiggerAlbumWins_TieIncoming()
        {
            var f = Facts();
            f.IncomingAlbumTracks = 5; f.LibraryAlbumTracks = 3;
            MusicIntegrator.RecommendDuplicate(f, out char key, out string reason);
            Assert.True(key == 'L' && reason.Contains("larger album preferred"), "Akira case: 5 vs 3 -> L: " + key + " " + reason);
            f.IncomingAlbumTracks = 3;
            MusicIntegrator.RecommendDuplicate(f, out key, out reason);
            Assert.True(key == 'L' && reason.Contains("same size"), "Tie keeps incoming: " + key + " " + reason);
            f.IncomingAlbumTracks = 2;
            MusicIntegrator.RecommendDuplicate(f, out key, out reason);
            Assert.True(key == 'D' && reason.Contains("keep library"), "Smaller incoming keeps library: " + key + " " + reason);
        }

        private static MusicIntegrator.ScannedFile Scanned(string artist, string title, double len) =>
            new MusicIntegrator.ScannedFile
            {
                IsReadable = true, LengthSec = len,
                Track = new Track { Artists = artist, Title = title, Album = "A" }
            };

        public static void InBatch_SameTitleDifferentLength_NotFlagged_LukisMacIntro()
        {
            var a = Scanned("Lukis Mac", "Intro", 60);
            var b = Scanned("Lukis Mac", "Intro", 95);
            MusicIntegrator.MarkInBatchDuplicates(new List<MusicIntegrator.ScannedFile> { a, b });
            Assert.True(!a.InBatchDuplicate && !b.InBatchDuplicate, "Different-length same-title tracks are not duplicates");
        }

        public static void InBatch_SameTitleSameLength_Flagged()
        {
            var a = Scanned("Lukis Mac", "Intro", 60);
            var b = Scanned("Lukis Mac", "Intro", 61);
            MusicIntegrator.MarkInBatchDuplicates(new List<MusicIntegrator.ScannedFile> { a, b });
            Assert.True(a.InBatchDuplicate && b.InBatchDuplicate, "Same title and length are duplicates");
        }
    }
}
