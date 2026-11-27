using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace AudioManager
{
    /// <summary>
    /// analysis --import-report &lt;AudioReport.txt&gt;: turns one old text report into a stats snapshot,
    /// data/history/stats-&lt;date&gt;-report.json, where the date comes from the report's filename.
    ///
    /// The old reports hold only the printed tables, so the snapshot carries the track count, the artist, genre,
    /// year and decade distributions, and the artist and genre table sizes. Everything else is zeroed and the
    /// flags (sizeKnown, coverKnown, lyricsKnown, lengthKnown) are false. Read-only with respect to the library;
    /// returns before mode parsing. Contract: docs/Contracts/AnalysisJSONFormat.md.
    /// </summary>
    internal static class StatsReportImport
    {
        /// <summary>Accepted report layouts: A (artists, genre, year), B (adds decade), C (adds the Musivation split).</summary>
        internal sealed class Parsed
        {
            public int TrackCount;
            public List<KeyValuePair<string, int>> Artists = new List<KeyValuePair<string, int>>();
            public List<KeyValuePair<string, int>> ArtistsExcl = new List<KeyValuePair<string, int>>();
            public List<KeyValuePair<string, int>> Genre = new List<KeyValuePair<string, int>>();
            public List<KeyValuePair<string, int>> Year = new List<KeyValuePair<string, int>>();
            public List<KeyValuePair<string, int>> Decade = new List<KeyValuePair<string, int>>();
        }

        private static readonly Regex TagsRx = new Regex(@"^\s*-\s*Tags parsed:\s*([0-9]+)\s*$");
        private static readonly Regex ExclHeadRx = new Regex(@"^#\s+Artists Statistics\s*\(Excluding Musivation\)");
        private static readonly Regex ArtistsHeadRx = new Regex(@"^#\s+Artists Statistics");
        private static readonly Regex GenreHeadRx = new Regex(@"^#\s+Genre Statistics");
        private static readonly Regex YearHeadRx = new Regex(@"^#\s+Year Statistics");
        private static readonly Regex DecadeHeadRx = new Regex(@"^#\s+Decade Statistics");
        // Era A/B rows: "50.00%   name   count". Era C rows also start with a rank: "1   50.00%   name   count".
        private static readonly Regex RowRx = new Regex(@"^\s*(?:[0-9]+\s+)?[0-9]+(?:\.[0-9]+)?%\s+(.+?)\s+([0-9]+)\s*$");

        /// <summary>
        /// Pure parser: reads the tracks line and the printed tables. Refuses (false, with a message) when the
        /// track count or a required table (artists, genre, year) is missing, or a table appears twice.
        /// </summary>
        internal static bool TryParse(IEnumerable<string> lines, out Parsed parsed, out string error)
        {
            parsed = new Parsed();
            error = null;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            bool hasTags = false;
            List<KeyValuePair<string, int>> current = null;

            foreach (string raw in lines)
            {
                string line = raw ?? "";
                Match tags = TagsRx.Match(line);
                if (tags.Success)
                {
                    parsed.TrackCount = int.Parse(tags.Groups[1].Value, CultureInfo.InvariantCulture);
                    hasTags = true;
                    current = null;
                    continue;
                }

                if (line.StartsWith("#", StringComparison.Ordinal))
                {
                    string key = null;
                    if (ExclHeadRx.IsMatch(line)) { key = "excl"; current = parsed.ArtistsExcl; }
                    else if (ArtistsHeadRx.IsMatch(line)) { key = "artists"; current = parsed.Artists; }
                    else if (GenreHeadRx.IsMatch(line)) { key = "genre"; current = parsed.Genre; }
                    else if (YearHeadRx.IsMatch(line)) { key = "year"; current = parsed.Year; }
                    else if (DecadeHeadRx.IsMatch(line)) { key = "decade"; current = parsed.Decade; }

                    if (key == null) continue; // a column header ("#     %   Artists ...") keeps the section open
                    if (!seen.Add(key))
                    {
                        error = $"the report has a second '{line.Trim()}' table; refusing to guess which one is right";
                        return false;
                    }
                    continue;
                }

                if (current == null) continue;
                Match row = RowRx.Match(line);
                if (row.Success)
                {
                    int count;
                    if (!int.TryParse(row.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out count))
                    {
                        error = $"unreadable count in row '{line.Trim()}'";
                        return false;
                    }
                    current.Add(new KeyValuePair<string, int>(row.Groups[1].Value.Trim(), count));
                    continue;
                }

                // Blank lines and the "%" column header keep the table open; any other text ends it.
                string trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith("%", StringComparison.Ordinal)) continue;
                current = null;
            }

            if (!hasTags)
            {
                error = "no ' - Tags parsed: N' line in the report, so the track count is unknown";
                return false;
            }
            if (!seen.Contains("artists")) { error = "no '# Artists Statistics' table in the report"; return false; }
            if (!seen.Contains("genre")) { error = "no '# Genre Statistics' table in the report"; return false; }
            if (!seen.Contains("year")) { error = "no '# Year Statistics' table in the report"; return false; }
            return true;
        }

        /// <summary>The snapshot document. Same keys as StatsJson.Build; unmeasured sections are zero or null.</summary>
        internal static string Build(Parsed p, string asOfDate, DateTime now)
        {
            var sb = new StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine($"  \"schemaVersion\": {StatsJson.SchemaVersion},");
            sb.AppendLine($"  \"generatedAt\": {JsonText.Quote(now.ToString("s", CultureInfo.InvariantCulture))},");
            sb.AppendLine($"  \"asOf\": {{\"date\": {JsonText.Quote(asOfDate)}, \"commit\": \"report\"}},");
            sb.AppendLine("  \"sizeKnown\": false,");
            sb.AppendLine("  \"coverKnown\": false,");
            sb.AppendLine("  \"lyricsKnown\": false,");
            sb.AppendLine("  \"lengthKnown\": false,");

            sb.AppendLine("  \"summary\": {");
            sb.AppendLine($"    \"trackCount\": {p.TrackCount},");
            sb.AppendLine($"    \"artistCount\": {p.Artists.Count},");
            sb.AppendLine($"    \"genreCount\": {p.Genre.Count},");
            sb.AppendLine("    \"totalLibraryBytes\": 0,");
            sb.AppendLine("    \"avgFileBytes\": 0,");
            sb.AppendLine("    \"totalPlaybackSeconds\": 0,");
            sb.AppendLine("    \"totalPlaybackHours\": 0,");
            sb.AppendLine("    \"avgSongLengthSeconds\": 0,");
            sb.AppendLine("    \"medianSongLengthSeconds\": 0");
            sb.AppendLine("  },");

            StatsJson.AppendLabelCountArray(sb, "genreDistribution", p.Genre, allEntries: true);
            StatsJson.AppendLabelCountArray(sb, "decadeDistribution", p.Decade, allEntries: true);
            StatsJson.AppendLabelCountArray(sb, "yearDistribution", p.Year, allEntries: true);

            sb.AppendLine("  \"topArtists\": {");
            StatsJson.AppendLabelCountArray(sb, "exclMusivation", p.ArtistsExcl, allEntries: false, indent: 4, trailingComma: true);
            StatsJson.AppendLabelCountArray(sb, "all", p.Artists, allEntries: false, indent: 4, trailingComma: false);
            sb.AppendLine("  },");

            sb.AppendLine("  \"ageStats\": {");
            sb.AppendLine("    \"averageYears\": null,");
            sb.AppendLine("    \"medianYears\": null,");
            sb.AppendLine("    \"newestYears\": null,");
            sb.AppendLine("    \"oldestYears\": null");
            sb.AppendLine("  },");
            var emptyBuckets = new[]
            {
                new KeyValuePair<string, int>("0-2y", 0),
                new KeyValuePair<string, int>("2-5y", 0),
                new KeyValuePair<string, int>("5-10y", 0),
                new KeyValuePair<string, int>("10-20y", 0),
                new KeyValuePair<string, int>("20y+", 0),
            };
            StatsJson.AppendLabelCountArray(sb, "ageDistribution", emptyBuckets, allEntries: true);

            sb.AppendLine("  \"coverArt\": {");
            sb.AppendLine($"    \"total\": {p.TrackCount},");
            sb.AppendLine("    \"hasKnownDims\": 0,");
            sb.AppendLine("    \"noCover\": 0,");
            sb.AppendLine("    \"missingData\": 0,");
            sb.AppendLine("    \"unknownFormat\": 0,");
            sb.AppendLine("    \"subMin800\": 0,");
            sb.AppendLine("    \"nonSquare\": 0,");
            StatsJson.AppendLabelCountArray(sb, "dimensionHistogram", new KeyValuePair<string, int>[0], allEntries: true, indent: 4, trailingComma: false);
            sb.AppendLine("  },");

            sb.AppendLine("  \"tagCompleteness\": {");
            sb.AppendLine("    \"complete\": 0,");
            sb.AppendLine($"    \"total\": {p.TrackCount},");
            sb.AppendLine("    \"percent\": 0");
            sb.AppendLine("  },");

            sb.AppendLine("  \"coverCoverage800\": {");
            sb.AppendLine("    \"covered\": 0,");
            sb.AppendLine($"    \"total\": {p.TrackCount},");
            sb.AppendLine("    \"percent\": 0");
            sb.AppendLine("  },");

            sb.AppendLine("  \"lyricsCoverage\": {");
            sb.AppendLine($"    \"total\": {p.TrackCount},");
            sb.AppendLine("    \"hasLyrics\": 0,");
            sb.AppendLine("    \"noneFound\": 0,");
            sb.AppendLine("    \"instrumental\": 0,");
            sb.AppendLine("    \"missing\": 0,");
            sb.AppendLine("    \"percentHasLyrics\": 0");
            sb.AppendLine("  }");

            sb.Append("}");
            return sb.ToString();
        }

        /// <summary>Folder-relative output name: stats-&lt;date&gt;-report.json (rerunning overwrites it).</summary>
        internal static string OutputPath(string historyDir, string date) =>
            Path.Combine(historyDir, $"stats-{date}-report.json");

        /// <summary>
        /// Entry point called from Main before any library scan. Returns false when --import-report is absent
        /// (normal flow). Otherwise it handles the request, sets exitCode and returns true. Refuses any other flag.
        /// </summary>
        internal static bool TryRun(string[] args, string historyDir, out int exitCode)
        {
            exitCode = 0;
            int idx = Array.FindIndex(args, a => a.Equals("--import-report", StringComparison.OrdinalIgnoreCase));
            if (idx < 0) return false;

            if (args.Length == 0 || !IsAnalysisWord(args[0]))
                return Refuse("--import-report is only valid with analysis (e.g. analysis --import-report 2024-11-30-AudioReport.txt).", out exitCode);
            if (idx + 1 >= args.Length || args[idx + 1].StartsWith("--", StringComparison.Ordinal))
                return Refuse("--import-report requires the path of an AudioReport.txt.", out exitCode);
            for (int i = 1; i < args.Length; i++)
            {
                if (i == idx) { i++; continue; } // the path after --import-report
                return Refuse($"'{args[i]}' is not valid with --import-report.", out exitCode);
            }

            string path = args[idx + 1];
            string fileName = Path.GetFileName(path);
            string date = fileName.Length >= 10 ? fileName.Substring(0, 10) : "";
            if (!StatsAsOf.IsDateSpec(date))
                return Refuse($"'{fileName}' does not start with a YYYY-MM-DD date, so its snapshot date is unknown.", out exitCode);
            if (!File.Exists(path))
                return Refuse($"report not found: {path}", out exitCode);

            string[] lines;
            try { lines = File.ReadAllLines(path, Encoding.UTF8); }
            catch (Exception ex) { return Refuse("cannot read the report: " + ex.Message, out exitCode); }

            Parsed parsed;
            string error;
            if (!TryParse(lines, out parsed, out error)) return Refuse(error, out exitCode);

            string json = Build(parsed, date, DateTime.Now);
            Directory.CreateDirectory(historyDir);
            string outPath = OutputPath(historyDir, date);
            AtomicFile.WriteAllText(outPath, json, Encoding.UTF8);
            Console.WriteLine($" - Imported report {date}: {parsed.TrackCount} tracks");
            Console.WriteLine($" - Written: {outPath}");
            return true;
        }

        private static bool IsAnalysisWord(string word)
        {
            string w = word.ToLowerInvariant();
            return w == "analysis" || w == "analyse" || w == "analyze";
        }

        private static bool Refuse(string message, out int exitCode)
        {
            string text = message.EndsWith(".", StringComparison.Ordinal) ? message : message + ".";
            Console.WriteLine($" - ERROR: {text} Nothing was run.");
            exitCode = 1;
            return true;
        }
    }
}
