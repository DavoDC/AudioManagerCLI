using AudioManager.Code.Modules;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using TagList = System.Collections.Generic.List<AudioManager.Code.Modules.TrackTag>;

namespace AudioManager
{
    /// <summary>
    /// Emits the full library statistics as a single structured JSON document for a client.
    ///
    /// Why this exists: a client's statistics and library views need every number the text
    /// AudioReport.md contains, but parsing markdown is brittle. Rather than reimplement
    /// all the distribution/age/cover logic in Python (which would silently drift from the
    /// C# Analyser over time), this exporter reuses the SAME StatList primitives the text
    /// report uses, so the JSON and the report can never disagree.
    ///
    /// Data contract: see docs/Contracts/AnalysisJSONFormat.md. Bump SchemaVersion on any breaking change.
    /// </summary>
    internal static class StatsJson
    {
        /// <summary>Contract version. Increment when a field is removed or its meaning changes.</summary>
        public const int SchemaVersion = 1;

        /// <summary>Fixed output filename (latest run wins) so a client reads a deterministic path.</summary>
        public const string OutputFileName = "analysis-stats.json";

        /// <summary>
        /// Production entry point: walks the audio folder for the real on-disk size, builds the
        /// JSON, and writes it to data/logs/analysis-stats.json. Returns the path written.
        /// </summary>
        public static string Write(TagList audioTags)
        {
            long libraryBytes = TryGetLibraryBytes();
            string json = Build(audioTags, libraryBytes, LyricsStatus.LoadFile(LyricsStatus.ConfigPath));

            if (!Directory.Exists(Constants.LogsPath))
                Directory.CreateDirectory(Constants.LogsPath);
            string outPath = Path.Combine(Constants.LogsPath, OutputFileName);
            AtomicFile.WriteAllText(outPath, json, Encoding.UTF8);
            return outPath;
        }

        /// <summary>
        /// Pure, unit-testable builder: produces the JSON document from the tag list plus a
        /// pre-computed library byte count (0 if unknown). No file or disk I/O here.
        /// keptStatus (optional) maps a library path to LyricsStatus.NoneFound / Instrumental.
        /// asOfDate / asOfCommit (both or neither) are set only by analysis --as-of: the document then carries an
        /// "asOf" block, "sizeKnown": false (the XMLs hold no file size), and coverKnown / lyricsKnown (false when
        /// the commit's XMLs predate those blocks). Live documents omit all of these.
        /// </summary>
        public static string Build(TagList audioTags, long totalLibraryBytes,
                                   IReadOnlyDictionary<string, string> keptStatus = null,
                                   string asOfDate = null, string asOfCommit = null,
                                   bool coverKnown = true, bool lyricsKnown = true)
        {
            int trackCount = audioTags.Count;

            // Reuse the exact primitives the text report uses - guarantees JSON == report.
            var genreStats = new StatList("Genre", audioTags, t => t.Genres);
            var yearStats = new StatList("Year", audioTags, t => t.Year);
            var decadeDist = StatList.GetDecadeFreqDist(yearStats);
            var artistStatsAll = new StatList("Artists", audioTags, t => t.Artists);
            var tagsExclMusivation = audioTags.Where(t => !t.Genres.Contains("Musivation")).ToList();
            var artistStatsExcl = new StatList("Artists", tagsExclMusivation, t => t.Artists);

            int artistCount = artistStatsAll.SortedFreqDist.Count();
            int genreCount = genreStats.SortedFreqDist.Count();

            // ---- Playback / length ----
            var durations = audioTags
                .Select(t => TryParseSeconds(t.Length))
                .Where(s => s.HasValue)
                .Select(s => s.Value)
                .OrderBy(s => s)
                .ToArray();
            double totalSeconds = durations.Sum();
            double totalHours = Math.Round(totalSeconds / 3600.0, 1);
            int avgSongSeconds = durations.Length > 0 ? (int)Math.Round(totalSeconds / durations.Length) : 0;
            int medianSongSeconds = durations.Length > 0 ? (int)Math.Round(Median(durations)) : 0;
            long avgFileBytes = trackCount > 0 ? totalLibraryBytes / trackCount : 0;

            // ---- Age ----
            int currentYear = DateTime.Now.Year;
            int[] ages = audioTags
                .Where(t => int.TryParse(t.Year, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                .Select(t => currentYear - int.Parse(t.Year, CultureInfo.InvariantCulture))
                .OrderBy(a => a)
                .ToArray();
            bool hasAges = ages.Length > 0;

            // ---- Cover art (mirrors Analyser.PrintCoverArtStatistics) ----
            int missingData = audioTags.Count(t => string.IsNullOrEmpty(t.CoverWidth));
            int noCover = audioTags.Count(t => t.CoverWidth == "0");
            int unknownFormat = audioTags.Count(t => t.CoverWidth == Constants.UnknownCover);
            int hasKnownDim = trackCount - missingData - noCover - unknownFormat;
            int subMin800 = audioTags.Count(t => KnownDim(t, out int w, out int h) && Math.Min(w, h) < 800);
            int nonSquare = audioTags.Count(t => KnownDim(t, out int w, out int h) && w != h);
            int covered800 = audioTags.Count(t => KnownDim(t, out int w, out int h) && Math.Min(w, h) >= 800);
            var coverHistogram = audioTags
                .Where(t => KnownDim(t, out _, out _))
                .GroupBy(t => $"{t.CoverWidth}x{t.CoverHeight}")
                .OrderByDescending(g => g.Count())
                .Take(15)
                .Select(g => new KeyValuePair<string, int>(g.Key, g.Count()))
                .ToList();

            // ---- Tag completeness (all five core tags present) ----
            int complete = audioTags.Count(t =>
                IsPresent(t.Title) && IsPresent(t.Artists) && IsPresent(t.Album) &&
                IsPresent(t.Genres) && IsPresent(t.Year));

            // ---- Lyrics coverage (HasLyrics "True" = has; otherwise the kept list decides; the rest is missing) ----
            var kept = new Dictionary<string, string>(StringComparer.Ordinal);
            if (keptStatus != null)
                foreach (var pair in keptStatus)
                    kept[LyricsStatus.NormaliseKey(pair.Key)] = pair.Value;
            int hasLyrics = 0, noneFound = 0, instrumental = 0, lyricsMissing = 0;
            foreach (TrackTag t in audioTags)
            {
                if (t.HasLyrics == "True") { hasLyrics++; continue; }
                string status;
                kept.TryGetValue(LyricsStatus.NormaliseKey(t.RelPath), out status);
                if (status == LyricsStatus.NoneFound) noneFound++;
                else if (status == LyricsStatus.Instrumental) instrumental++;
                else lyricsMissing++;
            }

            // ---- Serialize ----
            var sb = new StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine($"  \"schemaVersion\": {SchemaVersion},");
            sb.AppendLine($"  \"generatedAt\": {JsonText.Quote(DateTime.Now.ToString("s", CultureInfo.InvariantCulture))},");
            if (asOfDate != null)
            {
                sb.AppendLine($"  \"asOf\": {{\"date\": {JsonText.Quote(asOfDate)}, \"commit\": {JsonText.Quote(asOfCommit ?? "")}}},");
                sb.AppendLine("  \"sizeKnown\": false,");
                sb.AppendLine($"  \"coverKnown\": {(coverKnown ? "true" : "false")},");
                sb.AppendLine($"  \"lyricsKnown\": {(lyricsKnown ? "true" : "false")},");
            }

            sb.AppendLine("  \"summary\": {");
            sb.AppendLine($"    \"trackCount\": {trackCount},");
            sb.AppendLine($"    \"artistCount\": {artistCount},");
            sb.AppendLine($"    \"genreCount\": {genreCount},");
            sb.AppendLine($"    \"totalLibraryBytes\": {totalLibraryBytes},");
            sb.AppendLine($"    \"avgFileBytes\": {avgFileBytes},");
            sb.AppendLine($"    \"totalPlaybackSeconds\": {Num(totalSeconds)},");
            sb.AppendLine($"    \"totalPlaybackHours\": {Num(totalHours)},");
            sb.AppendLine($"    \"avgSongLengthSeconds\": {avgSongSeconds},");
            sb.AppendLine($"    \"medianSongLengthSeconds\": {medianSongSeconds}");
            sb.AppendLine("  },");

            AppendLabelCountArray(sb, "genreDistribution", genreStats.SortedFreqDist, allEntries: true);
            AppendLabelCountArray(sb, "decadeDistribution", decadeDist, allEntries: true);
            AppendLabelCountArray(sb, "yearDistribution", yearStats.SortedFreqDist, allEntries: true);

            sb.AppendLine("  \"topArtists\": {");
            AppendLabelCountArray(sb, "exclMusivation", artistStatsExcl.SortedFreqDist, allEntries: false, indent: 4, trailingComma: true);
            AppendLabelCountArray(sb, "all", artistStatsAll.SortedFreqDist, allEntries: false, indent: 4, trailingComma: false);
            sb.AppendLine("  },");

            sb.AppendLine("  \"ageStats\": {");
            sb.AppendLine($"    \"averageYears\": {(hasAges ? Num(Math.Round(ages.Average(), 1)) : "null")},");
            sb.AppendLine($"    \"medianYears\": {(hasAges ? Num(Math.Round(Median(ages.Select(a => (double)a).ToArray()), 1)) : "null")},");
            sb.AppendLine($"    \"newestYears\": {(hasAges ? ages.First().ToString(CultureInfo.InvariantCulture) : "null")},");
            sb.AppendLine($"    \"oldestYears\": {(hasAges ? ages.Last().ToString(CultureInfo.InvariantCulture) : "null")}");
            sb.AppendLine("  },");

            // Age distribution buckets (aligns with the mockup's Track Age Distribution chart).
            var ageBuckets = new[]
            {
                new KeyValuePair<string, int>("0-2y",   ages.Count(a => a <= 2)),
                new KeyValuePair<string, int>("2-5y",   ages.Count(a => a > 2 && a <= 5)),
                new KeyValuePair<string, int>("5-10y",  ages.Count(a => a > 5 && a <= 10)),
                new KeyValuePair<string, int>("10-20y", ages.Count(a => a > 10 && a <= 20)),
                new KeyValuePair<string, int>("20y+",   ages.Count(a => a > 20)),
            };
            AppendLabelCountArray(sb, "ageDistribution", ageBuckets, allEntries: true);

            sb.AppendLine("  \"coverArt\": {");
            sb.AppendLine($"    \"total\": {trackCount},");
            sb.AppendLine($"    \"hasKnownDims\": {hasKnownDim},");
            sb.AppendLine($"    \"noCover\": {noCover},");
            sb.AppendLine($"    \"missingData\": {missingData},");
            sb.AppendLine($"    \"unknownFormat\": {unknownFormat},");
            sb.AppendLine($"    \"subMin800\": {subMin800},");
            sb.AppendLine($"    \"nonSquare\": {nonSquare},");
            AppendLabelCountArray(sb, "dimensionHistogram", coverHistogram, allEntries: true, indent: 4, trailingComma: false);
            sb.AppendLine("  },");

            sb.AppendLine("  \"tagCompleteness\": {");
            sb.AppendLine($"    \"complete\": {complete},");
            sb.AppendLine($"    \"total\": {trackCount},");
            sb.AppendLine($"    \"percent\": {Percent(complete, trackCount)}");
            sb.AppendLine("  },");

            sb.AppendLine("  \"coverCoverage800\": {");
            sb.AppendLine($"    \"covered\": {covered800},");
            sb.AppendLine($"    \"total\": {trackCount},");
            sb.AppendLine($"    \"percent\": {Percent(covered800, trackCount)}");
            sb.AppendLine("  },");

            sb.AppendLine("  \"lyricsCoverage\": {");
            sb.AppendLine($"    \"total\": {trackCount},");
            sb.AppendLine($"    \"hasLyrics\": {hasLyrics},");
            sb.AppendLine($"    \"noneFound\": {noneFound},");
            sb.AppendLine($"    \"instrumental\": {instrumental},");
            sb.AppendLine($"    \"missing\": {lyricsMissing},");
            sb.AppendLine($"    \"percentHasLyrics\": {Percent(hasLyrics, trackCount)}");
            sb.AppendLine("  }");

            sb.Append("}");
            return sb.ToString();
        }

        // ---- helpers ----

        /// <summary>Appends a "name": [ {"label":..,"count":..}, .. ] block. </summary>
        internal static void AppendLabelCountArray(
            StringBuilder sb, string name, IEnumerable<KeyValuePair<string, int>> data,
            bool allEntries, int indent = 2, bool trailingComma = true)
        {
            var items = allEntries ? data.ToList() : data.Take(50).ToList();
            string pad = new string(' ', indent);
            string ipad = new string(' ', indent + 2);
            sb.AppendLine($"{pad}\"{name}\": [");
            for (int i = 0; i < items.Count; i++)
            {
                string comma = i < items.Count - 1 ? "," : "";
                sb.AppendLine($"{ipad}{{\"label\": {JsonText.Quote(items[i].Key)}, \"count\": {items[i].Value}}}{comma}");
            }
            sb.AppendLine($"{pad}]{(trailingComma ? "," : "")}");
        }

        /// <summary>True if the cover width/height parse to real positive pixel dimensions.</summary>
        private static bool KnownDim(TrackTag t, out int w, out int h)
        {
            w = 0; h = 0;
            if (!int.TryParse(t.CoverWidth, NumberStyles.Integer, CultureInfo.InvariantCulture, out w) || !int.TryParse(t.CoverHeight, NumberStyles.Integer, CultureInfo.InvariantCulture, out h)) return false;
            return w > 0 && h > 0;
        }

        /// <summary>A tag value counts as present if it is non-blank and not the "Missing" sentinel.</summary>
        private static bool IsPresent(string s) =>
            !string.IsNullOrWhiteSpace(s) && !s.Equals(Constants.MissingTag, StringComparison.OrdinalIgnoreCase);

        /// <summary>Parses a .NET TimeSpan length string to seconds, or null if unparseable.</summary>
        private static double? TryParseSeconds(string length)
        {
            if (TimeSpan.TryParse(length, CultureInfo.InvariantCulture, out TimeSpan ts))
                return ts.TotalSeconds;
            return null;
        }

        /// <summary>Median of a pre-sorted array.</summary>
        private static double Median(double[] sorted)
        {
            int n = sorted.Length;
            if (n == 0) return 0;
            return n % 2 == 1
                ? sorted[n / 2]
                : (sorted[n / 2 - 1] + sorted[n / 2]) / 2.0;
        }

        private static string Percent(int part, int total) =>
            total > 0 ? Num(Math.Round(part * 100.0 / total, 1)) : "0";

        /// <summary>Walks the audio library once for a real byte total; 0 if it can't be read.</summary>
        private static long TryGetLibraryBytes() => TryGetLibraryBytes(Constants.AudioFolderPath);

        /// <summary>Walks the given folder for a real mp3 byte total; 0 (with a [WARN] line) if it can't be read.</summary>
        internal static long TryGetLibraryBytes(string audioFolder)
        {
            try
            {
                return new DirectoryInfo(audioFolder)
                    .GetFiles("*.mp3", SearchOption.AllDirectories)
                    .Sum(f => f.Length);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  [WARN] Could not read library size under '{audioFolder}': {ex.Message}");
                return 0;
            }
        }

        /// <summary>Formats a double as a culture-invariant JSON number (always '.' decimal).</summary>
        private static string Num(double d) => d.ToString("0.###", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// analysis --as-of &lt;YYYY-MM-DD|commit-sha&gt; and --as-of-list: the library statistics as they were at an
    /// AudioMirror commit. The commit's XMLs are extracted with git archive into a fresh temp folder (the mirror
    /// working tree is never touched), loaded with TrackXML.Read and summarised by StatsJson.Build, so the numbers
    /// use the same primitives as the live report. Writes only data/history/stats-&lt;commit date&gt;-&lt;sha8&gt;.json.
    /// Read-only with respect to the library and the mirror; never regenerates the mirror and never commits.
    /// </summary>
    internal static class StatsAsOf
    {
        /// <summary>Folder (under data/, or the test root) that holds the as-of documents.</summary>
        internal static string HistoryDir => string.IsNullOrWhiteSpace(Constants.TestRoot)
            ? Path.Combine(Constants.RepoRoot, "data", "history")
            : Path.Combine(Constants.TestRoot, "history");

        internal sealed class ResolvedCommit
        {
            public string FullSha;
            public string Sha8;
            public string Date;     // commit's own date, YYYY-MM-DD
            public string Subject;
        }

        internal sealed class CommitRow
        {
            public string Date;
            public string Sha8;
            public string Subject;
        }

        private static readonly Regex DateRx = new Regex(@"^[0-9]{4}-[0-9]{2}-[0-9]{2}\z");
        private static readonly Regex ShaRx = new Regex(@"^[0-9a-fA-F]{7,40}\z");

        /// <summary>True for a calendar date written YYYY-MM-DD.</summary>
        internal static bool IsDateSpec(string s) =>
            s != null && DateRx.IsMatch(s) &&
            DateTime.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

        /// <summary>True for a hex commit id of 7 to 40 characters.</summary>
        internal static bool IsShaSpec(string s) => s != null && ShaRx.IsMatch(s);

        /// <summary>
        /// Resolves a date (last commit on or before that day, 23:59:59) or a sha (verified to exist) to a commit.
        /// git is injected so tests can fake it; it takes the argument string and returns the result.
        /// </summary>
        internal static bool TryResolve(string spec, Func<string, AudioMirrorCommitter.GitRunResult> git,
                                        out ResolvedCommit commit, out string error)
        {
            commit = null;
            error = null;
            if (IsDateSpec(spec))
            {
                AudioMirrorCommitter.GitRunResult rev = git($"rev-list -1 --before=\"{spec} 23:59:59\" HEAD");
                if (rev.ExitCode != 0)
                {
                    error = "git rev-list failed: " + FirstLine(rev);
                    return false;
                }
                string sha = rev.Stdout.Trim();
                if (sha.Length == 0)
                {
                    error = $"No AudioMirror commit on or before {spec}. Run --as-of-list to see the history.";
                    return false;
                }
                return TryDescribe(sha, git, out commit, out error);
            }
            if (IsShaSpec(spec)) return TryDescribe(spec, git, out commit, out error);
            error = "--as-of takes a date (YYYY-MM-DD) or a commit sha (7 to 40 hex characters). Got: " + spec;
            return false;
        }

        private static bool TryDescribe(string sha, Func<string, AudioMirrorCommitter.GitRunResult> git,
                                        out ResolvedCommit commit, out string error)
        {
            commit = null;
            error = null;
            AudioMirrorCommitter.GitRunResult show = git($"show -s --format=%cs%x09%H%x09%s {sha}^{{commit}}");
            if (show.ExitCode != 0)
            {
                error = $"Unknown AudioMirror commit '{sha}'. {FirstLine(show)}";
                return false;
            }
            List<CommitRow> rows = ParseCommitLog(show.Stdout);
            if (rows.Count == 0)
            {
                error = $"Unknown AudioMirror commit '{sha}'. git returned no commit.";
                return false;
            }
            CommitRow row = rows[0];
            commit = new ResolvedCommit
            {
                FullSha = show.Stdout.Trim().Split('\t')[1].Trim(),
                Sha8 = row.Sha8,
                Date = row.Date,
                Subject = row.Subject,
            };
            return true;
        }

        /// <summary>Output file for a commit: data/history/stats-&lt;commit date&gt;-&lt;sha8&gt;.json.</summary>
        internal static string OutputPath(string historyDir, ResolvedCommit commit) =>
            Path.Combine(historyDir, $"stats-{commit.Date}-{commit.Sha8}.json");

        /// <summary>Parses "date TAB sha TAB subject" lines (the subject may hold colons, never a tab break).</summary>
        internal static List<CommitRow> ParseCommitLog(string stdout)
        {
            var rows = new List<CommitRow>();
            foreach (string raw in (stdout ?? "").Split('\n'))
            {
                string line = raw.TrimEnd('\r');
                if (line.Length == 0) continue;
                string[] parts = line.Split(new[] { '\t' }, 3);
                if (parts.Length < 3 || parts[1].Length < 8) continue;
                rows.Add(new CommitRow { Date = parts[0], Sha8 = parts[1].Substring(0, 8), Subject = parts[2] });
            }
            return rows;
        }

        /// <summary>
        /// Loads every XML under the extracted mirror root into a TrackTag. A file that cannot be read aborts the run
        /// (naming the file) rather than silently leaving a track out of the statistics.
        /// </summary>
        internal static List<TrackTag> LoadTags(string mirrorRoot) => LoadTags(mirrorRoot, out _, out _);

        /// <summary>
        /// Reads with the lenient as-of reader. coverKnown / lyricsKnown are true only when every XML carries that block,
        /// so one old file makes the whole commit's flag false.
        /// </summary>
        internal static List<TrackTag> LoadTags(string mirrorRoot, out bool coverKnown, out bool lyricsKnown)
        {
            var tags = new List<TrackTag>();
            coverKnown = true;
            lyricsKnown = true;
            foreach (string file in Directory.GetFiles(mirrorRoot, "*.xml", SearchOption.AllDirectories))
            {
                // *.xml also matches longer extensions on .NET Framework (8.3 semantics), so filter exactly.
                if (!file.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)) continue;
                var tag = new TrackTag(TrackTag.RelPathFor(file, mirrorRoot), "", "", "", "", "", "", "", "", "", "", "");
                TrackXML.ShapeFlags shape;
                try
                {
                    shape = TrackXML.ReadLenient(file, tag);
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException($"Could not read {file} at this commit: {ex.Message}", ex);
                }
                if (!shape.CoverKnown) coverKnown = false;
                if (!shape.LyricsKnown) lyricsKnown = false;
                tags.Add(tag);
            }
            return tags;
        }

        /// <summary>
        /// Production extractor: git archive of AUDIO_MIRROR at the commit into targetDir (via a tar file), then tar -x.
        /// Returns the extracted mirror folder. Never touches the AudioMirror working tree or index.
        /// </summary>
        internal static string ExtractMirror(string fullSha, string targetDir)
        {
            string tarPath = Path.Combine(targetDir, "snapshot.tar");
            AudioMirrorCommitter.GitRunResult archive = AudioMirrorCommitter.RunGitDetailed(Constants.MirrorRepoPath,
                $"archive --format=tar -o \"{tarPath}\" {fullSha} {Constants.MirrorFolderName}");
            if (archive.ExitCode != 0) throw new InvalidOperationException("git archive failed: " + FirstLine(archive));

            var psi = new ProcessStartInfo("tar", $"-x -f \"{tarPath}\" -C \"{targetDir}\"")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using (Process tar = Process.Start(psi))
            {
                string err = tar.StandardError.ReadToEnd();
                tar.StandardOutput.ReadToEnd();
                if (!tar.WaitForExit(300000)) { try { tar.Kill(); } catch (InvalidOperationException) { } throw new InvalidOperationException("tar timed out extracting the snapshot."); }
                if (tar.ExitCode != 0) throw new InvalidOperationException("tar failed: " + err.Trim());
            }
            File.Delete(tarPath);
            return Path.Combine(targetDir, Constants.MirrorFolderName);
        }

        /// <summary>
        /// Fresh extraction folder under Temp, named short on purpose: XDocument fails past MAX_PATH (260), so the
        /// extracted root must be no longer than the live mirror root (a GUID-length name pushed real files over).
        /// </summary>
        internal static string NewExtractDir() =>
            Path.Combine(Path.GetTempPath(), "amasof" + Guid.NewGuid().ToString("N").Substring(0, 8));

        /// <summary>Production git runner: always inside the AudioMirror repo.</summary>
        internal static AudioMirrorCommitter.GitRunResult GitInMirror(string args) =>
            AudioMirrorCommitter.RunGitDetailed(Constants.MirrorRepoPath, args);

        private static string FirstLine(AudioMirrorCommitter.GitRunResult r)
        {
            string text = ((r.Stderr ?? "") + "\n" + (r.Stdout ?? "")).Trim();
            return text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "(no output)";
        }

        private static bool IsAnalysisWord(string word) =>
            word.Equals("analysis", StringComparison.OrdinalIgnoreCase) ||
            word.Equals("analyse", StringComparison.OrdinalIgnoreCase) ||
            word.Equals("analyze", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Entry point called from Main before any library scan or mirror regen. Returns false when neither
        /// --as-of nor --as-of-list is present (normal flow). Otherwise it handles the request, sets exitCode and
        /// returns true. Refuses any other flag rather than silently ignoring it.
        /// </summary>
        internal static bool TryRun(string[] args, Func<string, AudioMirrorCommitter.GitRunResult> git, string historyDir,
                                    Func<string, string, string> extractMirror, out int exitCode)
        {
            exitCode = 0;
            bool wantsAsOf = args.Any(a => a.Equals("--as-of", StringComparison.OrdinalIgnoreCase));
            bool wantsList = args.Any(a => a.Equals("--as-of-list", StringComparison.OrdinalIgnoreCase));
            if (!wantsAsOf && !wantsList) return false;

            if (args.Length == 0 || !IsAnalysisWord(args[0]))
                return Refuse("--as-of and --as-of-list are only valid with analysis (e.g. analysis --as-of 2025-12-31).", out exitCode);
            if (wantsAsOf && wantsList)
                return Refuse("use only one of --as-of or --as-of-list.", out exitCode);

            string spec = null;
            for (int i = 1; i < args.Length; i++)
            {
                string a = args[i];
                if (a.Equals("--as-of-list", StringComparison.OrdinalIgnoreCase) || a.Equals("--json-output", StringComparison.OrdinalIgnoreCase)) continue;
                if (a.Equals("--as-of", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                        return Refuse("--as-of requires a date (YYYY-MM-DD) or a commit sha.", out exitCode);
                    spec = args[++i];
                    continue;
                }
                return Refuse($"'{a}' is not valid with --as-of (only --json-output is).", out exitCode);
            }

            if (wantsList)
            {
                AudioMirrorCommitter.GitRunResult log = git("log --format=%cs%x09%H%x09%s");
                if (log.ExitCode != 0) return Refuse("git log failed: " + FirstLine(log), out exitCode);
                foreach (CommitRow row in ParseCommitLog(log.Stdout))
                    Console.WriteLine($"{row.Date}\t{row.Sha8}\t{row.Subject}");
                return true;
            }

            if (!TryResolve(spec, git, out ResolvedCommit commit, out string error))
                return Refuse(error, out exitCode);

            string tempDir = NewExtractDir();
            Directory.CreateDirectory(tempDir);
            try
            {
                string mirrorRoot = extractMirror(commit.FullSha, tempDir);
                List<TrackTag> tags = LoadTags(mirrorRoot, out bool coverKnown, out bool lyricsKnown);
                string json = StatsJson.Build(tags, 0, null, commit.Date, commit.FullSha, coverKnown, lyricsKnown);
                Directory.CreateDirectory(historyDir);
                string outPath = OutputPath(historyDir, commit);
                AtomicFile.WriteAllText(outPath, json, Encoding.UTF8);
                Console.WriteLine($" - As-of stats for {commit.Date} ({commit.Sha8}): {tags.Count} tracks");
                Console.WriteLine($" - Written: {outPath}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($" - ERROR: as-of run failed: {ex.Message}");
                Console.WriteLine(" - The library, the mirror and the AudioMirror working tree were not touched.");
                exitCode = 1;
            }
            finally
            {
                try { Directory.Delete(tempDir, true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            return true;
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
