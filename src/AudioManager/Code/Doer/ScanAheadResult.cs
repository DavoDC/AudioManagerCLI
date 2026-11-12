using System.Collections.Generic;

namespace AudioManager
{
    /// <summary>
    /// The values MusicIntegrator.RunScanAhead hands to its later readers (GetDestDir, PreScanFiles,
    /// the gates, the JSON writer, the LibChecker projection). Immutable: RunScanAhead publishes by
    /// replacing the whole object, in the same order and at the same points as the old field writes.
    /// A null member means "not set", exactly as the old nullable fields did.
    /// </summary>
    internal sealed class ScanAheadResult
    {
        internal Dictionary<string, int> BatchCounts { get; }
        internal Dictionary<string, string> Spellings { get; }
        internal List<ArtistSpellingCandidate> SpellingCandidates { get; }
        internal Dictionary<string, int> MiscCounts { get; }
        internal Dictionary<string, int> SourcesCounts { get; }
        /// <summary>primaryArtist -> album -> count.</summary>
        internal Dictionary<string, Dictionary<string, int>> BatchAlbumCounts { get; }
        internal HashSet<string> CompilationAlbums { get; }
        internal Dictionary<string, (string artists, string title, string album, string genres, uint year)> BatchTagCache { get; }

        internal ScanAheadResult(
            Dictionary<string, int> batchCounts,
            Dictionary<string, string> spellings,
            List<ArtistSpellingCandidate> spellingCandidates,
            Dictionary<string, int> miscCounts,
            Dictionary<string, int> sourcesCounts,
            Dictionary<string, Dictionary<string, int>> batchAlbumCounts,
            HashSet<string> compilationAlbums,
            Dictionary<string, (string artists, string title, string album, string genres, uint year)> batchTagCache)
        {
            BatchCounts = batchCounts;
            Spellings = spellings;
            SpellingCandidates = spellingCandidates;
            MiscCounts = miscCounts;
            SourcesCounts = sourcesCounts;
            BatchAlbumCounts = batchAlbumCounts;
            CompilationAlbums = compilationAlbums;
            BatchTagCache = batchTagCache;
        }

        /// <summary>The state before any scan-ahead ran: everything null except an empty candidate list.</summary>
        internal static ScanAheadResult Empty()
        {
            return new ScanAheadResult(null, null, new List<ArtistSpellingCandidate>(), null, null, null, null, null);
        }

        internal ScanAheadResult WithBatchTagCache(Dictionary<string, (string artists, string title, string album, string genres, uint year)> v)
        {
            return new ScanAheadResult(BatchCounts, Spellings, SpellingCandidates, MiscCounts, SourcesCounts, BatchAlbumCounts, CompilationAlbums, v);
        }

        internal ScanAheadResult WithCompilationAlbums(HashSet<string> v)
        {
            return new ScanAheadResult(BatchCounts, Spellings, SpellingCandidates, MiscCounts, SourcesCounts, BatchAlbumCounts, v, BatchTagCache);
        }

        internal ScanAheadResult WithBatchCountsAndSpellings(Dictionary<string, int> batchCounts, Dictionary<string, string> spellings)
        {
            return new ScanAheadResult(batchCounts, spellings, SpellingCandidates, MiscCounts, SourcesCounts, BatchAlbumCounts, CompilationAlbums, BatchTagCache);
        }

        internal ScanAheadResult WithFinalCounts(
            List<ArtistSpellingCandidate> spellingCandidates,
            Dictionary<string, Dictionary<string, int>> batchAlbumCounts,
            Dictionary<string, int> miscCounts,
            Dictionary<string, int> sourcesCounts)
        {
            return new ScanAheadResult(BatchCounts, Spellings, spellingCandidates, miscCounts, sourcesCounts, batchAlbumCounts, CompilationAlbums, BatchTagCache);
        }
    }
}
