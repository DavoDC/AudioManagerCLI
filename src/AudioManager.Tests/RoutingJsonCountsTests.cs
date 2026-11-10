using System.Collections.Generic;

namespace AudioManager
{
    /// <summary>
    /// Coverage for the "counts" object inside the routing JSON "summary" block: per-stage integer
    /// counts derived from the same rows written to files[], so a GUI can read them without
    /// scanning rows. Pure string assertions against BuildJson - no filesystem, no console.
    /// </summary>
    internal static class RoutingJsonCountsTests
    {
        private static MusicIntegrator.LogEntry Row(string filename, string status) =>
            new MusicIntegrator.LogEntry { Filename = filename, Status = status };

        public static void RoutingJsonCounts_MixedStatusesCountEachStage()
        {
            var entries = new List<MusicIntegrator.LogEntry>
            {
                Row("a.mp3", "moved"),
                Row("b.mp3", "would-move"),
                Row("c.mp3", "skipped"),
                Row("d.mp3", "would-skip"),
                Row("e.mp3", "error"),
                new MusicIntegrator.LogEntry { Filename = "f.mp3", Status = "moved", LibraryDuplicate = true },
                new MusicIntegrator.LogEntry { Filename = "g.mp3", Status = "skipped", InBatchDuplicate = true },
                Row("h.mp3", "would-replace"),
            };
            string json = MusicIntegrator.BuildJson(entries);
            Assert.True(json.Contains("\"files\": 8"), "files is the total row count");
            Assert.True(json.Contains("\"routed\": 3"), "moved and would-move rows are routed");
            Assert.True(json.Contains("\"skipped\": 3"), "skipped and would-skip rows are skipped");
            Assert.True(json.Contains("\"errors\": 1"), "error rows counted");
            Assert.True(json.Contains("\"libraryDuplicates\": 1"), "libraryDuplicate true counted");
            Assert.True(json.Contains("\"inBatchDuplicates\": 1"), "inBatchDuplicate true counted");
            Assert.True(json.Contains("\"unrouted\": 5"), "unrouted is files minus routed");
        }

        public static void RoutingJsonCounts_EmptyListGivesAllZeros()
        {
            string json = MusicIntegrator.BuildJson(new List<MusicIntegrator.LogEntry>());
            Assert.True(json.Contains("\"counts\": {"), "counts object present on an empty batch");
            Assert.True(json.Contains("\"files\": 0"), "files zero");
            Assert.True(json.Contains("\"routed\": 0"), "routed zero");
            Assert.True(json.Contains("\"skipped\": 0"), "skipped zero");
            Assert.True(json.Contains("\"errors\": 0"), "errors zero");
            Assert.True(json.Contains("\"libraryDuplicates\": 0"), "libraryDuplicates zero");
            Assert.True(json.Contains("\"inBatchDuplicates\": 0"), "inBatchDuplicates zero");
            Assert.True(json.Contains("\"unrouted\": 0"), "unrouted zero");
        }
    }
}
