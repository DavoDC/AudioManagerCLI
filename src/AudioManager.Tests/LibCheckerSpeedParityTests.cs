using AudioManager.Code.Modules;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace AudioManager
{
    // Characterisation pins for the LibChecker exception index and precompiled feat./edit regexes (speedup 1).
    // The expected text was recorded from the pre-index LibChecker; the output must stay byte-identical after it.
    // The timing test prints constructor times only and asserts nothing about speed.
    internal static class LibCheckerSpeedParityTests
    {
        private const string ExpectedRecording = "2b09affdb3b8dcf3 hits=35";

        private static string ExceptionsXml() =>
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<Exceptions>\n" +
            "  <Exception unwanted=\"feat.\"><Title equals=\"Song (feat. Someone)\"/><Artists equals=\"Known Artist\"/></Exception>\n" +
            "  <Exception unwanted=\"EDIT\"><Artists contains=\"Edit Artist\"/></Exception>\n" +
            "  <Exception unwanted=\"explicit\"><Artists equals=\"Clean Artist\"/></Exception>\n" +
            "  <Exception unwanted=\"*\"><Album contains=\"Live\"/></Exception>\n" +
            "  <Exception unwanted=\"original\"></Exception>\n" +
            "  <Exception unwanted=\"ft.\"><Title equalz=\"Left Ft. Mix\"/></Exception>\n" +
            "  <Exception unwanted=\"version\"><Genre contains=\"Pop\"/></Exception>\n" +
            "  <Exception unwanted=\"ft.\"><Title contains=\"\"/></Exception>\n" +
            "  <Exception><Title equals=\"Other (feat. Y)\"/></Exception>\n" +
            "  <Exception unwanted=\"bonus\"><Title equals=\"Other (feat. Y)\"/><Album equals=\"Other Album\"/></Exception>\n" +
            "  <Exception unwanted=\"*\"><Title equals=\"Wild Title (edit)\"/></Exception>\n" +
            "</Exceptions>";

        private static TrackTag Tag(string artist, string title, string album, string genre = "Hip-Hop", string file = null)
        {
            string name = file ?? (artist + " - " + title);
            return new TrackTag("\\Artists\\" + artist + "\\Singles\\" + name + ".xml",
                title, artist, album, "2020", "1", genre, "00:03:00.0000000", "1", "True", "1200", "1200");
        }

        private static List<TrackTag> SyntheticTags() => new List<TrackTag>
        {
            Tag("Known Artist", "Song (feat. Someone)", "Test Album"),
            Tag("Other Artist", "Other (feat. Y)", "Other Album"),
            Tag("Edit Artist", "Song (Radio Edit)", "Plain Album"),
            Tag("Edition Artist", "Edition Song", "Plain Album"),
            Tag("Left Artist", "Theft Song", "Plain Album"),
            Tag("Left Artist", "Left Ft. Mix", "Plain Album"),
            Tag("Clean Artist", "Track explicit", "Live Album bonus"),
            Tag("Clean Artist", "Track explicit", "Studio Album"),
            Tag("Mixed Case", "Original VERSION", "Studio ORIGINAL"),
            Tag("Mixed Case", "Wild Title (edit)", "Studio Album"),
            Tag("Pop Artist", "Version Pop", "Studio Album", "Pop"),
            Tag("Pop Artist", "Version Pop", "Studio Album", "Rock"),
            Tag("Bonus Artist", "Song bonus", "Studio Album"),
            Tag("Feat Artist", "Song ft. Guest", "Studio Album"),
            Tag("Feat Artist", "Song (FT. Guest)", "Studio Album"),
            Tag("Feat Artist", "Missing title (Radio edit) feat. Z", "Studio Album"),
        };

        // The synthetic 6000-track library: mostly clean, some exception-matching and some dirty tracks
        private static List<TrackTag> LargeTags()
        {
            var list = new List<TrackTag>(6000);
            for (int i = 0; i < 6000; i++)
            {
                string artist = "Artist " + (i % 400);
                string title = "Track " + i + (i % 50 == 0 ? " (feat. Guest)" : "") + (i % 70 == 0 ? " (Radio Edit)" : "");
                string album = "Album " + (i % 900) + (i % 300 == 0 ? " Live" : "");
                list.Add(Tag(artist, title, album, i % 3 == 0 ? "Pop" : "Hip-Hop"));
            }
            return list;
        }

        private static string Serialise(LibChecker checker)
        {
            var sb = new StringBuilder();
            foreach (var h in checker.Hits)
            {
                sb.Append(h.Kind).Append('|').Append(h.Path).Append('|').Append(h.Detail)
                  .Append('|').Append(string.Join(",", h.Related)).Append('\n');
            }
            sb.Append("TotalHits=").Append(checker.TotalHits)
              .Append(" IsClean=").Append(checker.IsClean)
              .Append(" HasBlockingHits=").Append(checker.HasBlockingHits)
              .Append(" LowRes=").Append(checker.LowResCovers.Count).Append('\n');
            return sb.ToString();
        }

        private static LibChecker Build(List<TrackTag> tags, string exceptionsXml)
        {
            string path = Path.Combine(Path.GetTempPath(), "am_parity_" + Guid.NewGuid().ToString("N") + ".xml");
            try
            {
                File.WriteAllText(path, exceptionsXml);
                return new LibChecker(tags, exceptionsPath: path);
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }

        // 64-bit FNV-1a over the UTF-8 bytes: a stable fingerprint of the whole serialised output
        private static string Fingerprint(string text)
        {
            ulong h = 14695981039346656037UL;
            foreach (byte b in Encoding.UTF8.GetBytes(text)) { h ^= b; h *= 1099511628211UL; }
            return h.ToString("x16");
        }

        public static void Parity_SyntheticTags_HitsMatchRecording()
        {
            LibChecker checker = Build(SyntheticTags(), ExceptionsXml());
            string actual = Fingerprint(Serialise(checker)) + " hits=" + checker.Hits.Count;
            Assert.Equal(ExpectedRecording, actual, "LibChecker hits must match the pre-index recording");
        }

        // Prints constructor time over three runs on a 6000-track list. Asserts nothing about speed.
        public static void Timing_Constructor_6000Tracks_PrintsStopwatch()
        {
            List<TrackTag> tags = LargeTags();
            string path = Path.Combine(Path.GetTempPath(), "am_timing_" + Guid.NewGuid().ToString("N") + ".xml");
            try
            {
                File.WriteAllText(path, ExceptionsXml());
                for (int run = 1; run <= 3; run++)
                {
                    var sw = Stopwatch.StartNew();
                    var checker = new LibChecker(tags, exceptionsPath: path);
                    sw.Stop();
                    // stderr, not Console.Out: the runner discards Out for passing tests, so timing would be lost
                    Console.Error.WriteLine($"TIMING LibChecker ctor 6000 tracks run {run}: {sw.Elapsed.TotalMilliseconds:F1} ms (hits {checker.TotalHits})");
                }
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }
    }
}
