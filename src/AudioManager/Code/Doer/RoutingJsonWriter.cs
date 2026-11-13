using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace AudioManager
{
    /// <summary>
    /// Builds the routing and execution JSON and reads or writes the files that hold it. MusicIntegrator forwards to these.
    /// </summary>
    internal static class RoutingJsonWriter
    {
        /// <summary>
        /// Writes routing/execution decisions as structured JSON. Dry runs write
        /// data/logs/routing-{timestamp}.json; real runs additionally write
        /// data/logs/execution-{timestamp}.json (WriteExecutionRecord) with recordType "execution",
        /// dryRun false, and a populated "confidence" block. Schema: {schemaVersion, recordType,
        /// dryRun, generatedAt, "summary": {routes, miscAutoMigrations, miscAutoMigrationTotal,
        /// counts {files, routed, skipped, errors, libraryDuplicates, inBatchDuplicates, unrouted},
        /// compilationAlbums}, "confidence": null | {countCheck, sanityCheck, newFolders,
        /// errorCount, errors}, "files": [ {filename, artist, title, album, destination, reason,
        /// isNewFolder, status, detail, inBatchDuplicate, compilationAlbum, libraryDuplicate + dup*
        /// fields, tagFix (null | {oldName, newName, oldTags, newTags}), tagChanges[]} ]}.
        /// </summary>
        /// <summary>
        /// Writes the top-level "blocked" object when the pre-move MAX_PATH gate stopped the batch. Absent otherwise.
        /// </summary>
        internal static void AppendBlockedBlock(StringBuilder sb, MusicIntegrator.MirrorPathBlock blocked)
        {
            if (blocked == null) return;
            sb.AppendLine("  \"blocked\": {");
            sb.AppendLine($"    \"status\": {JsonText.Quote(blocked.Status)},");
            sb.AppendLine($"    \"action\": {JsonText.Quote(blocked.Action)},");
            sb.AppendLine("    \"files\": [");
            for (int i = 0; i < blocked.Files.Count; i++)
            {
                var (file, length) = blocked.Files[i];
                string comma = i < blocked.Files.Count - 1 ? "," : "";
                sb.AppendLine($"      {{\"file\": {JsonText.Quote(file)}, \"projectedLength\": {length}, \"limit\": {blocked.Limit}}}{comma}");
            }
            sb.AppendLine("    ]");
            sb.AppendLine("  },");
        }

        /// <summary>
        /// Writes the top-level "artistSpellingCandidates" array: artists whose batch and Misc tags carry two or more
        /// spellings. Always present, empty when there are none. A suggestion only; no tag changes (see docs/Contracts/RoutingJSONFormat.md).
        /// </summary>
        internal static void AppendArtistSpellingCandidatesBlock(StringBuilder sb, IList<ArtistSpellingCandidate> candidates)
        {
            var list = candidates ?? new List<ArtistSpellingCandidate>();
            if (list.Count == 0)
            {
                sb.AppendLine("  \"artistSpellingCandidates\": [],");
                return;
            }
            sb.AppendLine("  \"artistSpellingCandidates\": [");
            for (int i = 0; i < list.Count; i++)
            {
                var c = list[i];
                sb.AppendLine("    {");
                sb.AppendLine($"      \"key\": {JsonText.Quote(c.Key)},");
                sb.AppendLine($"      \"suggested\": {JsonText.Quote(c.Suggested)},");
                sb.AppendLine("      \"spellings\": [");
                for (int j = 0; j < c.Spellings.Count; j++)
                {
                    var v = c.Spellings[j];
                    string comma = j < c.Spellings.Count - 1 ? "," : "";
                    sb.AppendLine($"        {{\"spelling\": {JsonText.Quote(v.Spelling)}, \"tracks\": [{string.Join(", ", v.Tracks.Select(t => JsonText.Quote(t)))}], \"count\": {v.Count}}}{comma}");
                }
                sb.AppendLine("      ]");
                sb.AppendLine(i < list.Count - 1 ? "    }," : "    }");
            }
            sb.AppendLine("  ],");
        }

        /// <summary>
        /// Writes the top-level "replacedOriginals" array (execution record only): each library original an L
        /// replacement retired, with the holding path it now lives at. Absent when null or empty.
        /// </summary>
        internal static void AppendReplacedOriginalsBlock(StringBuilder sb, IList<(string original, string held)> replaced)
        {
            if (replaced == null || replaced.Count == 0) return;
            sb.AppendLine("  \"replacedOriginals\": [");
            for (int i = 0; i < replaced.Count; i++)
            {
                string comma = i < replaced.Count - 1 ? "," : "";
                sb.AppendLine($"    {{\"original\": {JsonText.Quote(replaced[i].original)}, \"held\": {JsonText.Quote(replaced[i].held)}}}{comma}");
            }
            sb.AppendLine("  ],");
        }

        /// <summary>
        /// Builds the routing/execution JSON contract text (pure, no I/O) - see the external contract
        /// for the client's routing parser. Internal + static so tests can assert on its
        /// output directly, same pattern as TracksJson.Build. `dryRun` defaults to true and
        /// `confidence` to null so every pre-existing dry-run test call site keeps compiling
        /// unchanged; a real run passes dryRun: false and a populated ConfidenceRecord.
        /// </summary>
        internal static string BuildJson(List<MusicIntegrator.LogEntry> entries, MusicIntegrator.BatchSummary summary = null, bool dryRun = true, MusicIntegrator.ConfidenceRecord confidence = null,
            MusicIntegrator.MirrorPathBlock blocked = null, IList<(string original, string held)> replaced = null,
            string newMusicFingerprint = null, string previewFingerprint = null, IList<ArtistSpellingCandidate> artistSpellingCandidates = null,
            MusicIntegrator.LeftoversSnapshot leftovers = null)
        {
            var sb = new StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine($"  \"schemaVersion\": {MusicIntegrator.SchemaVersion},");
            sb.AppendLine($"  \"recordType\": {JsonText.Quote(dryRun ? "routing" : "execution")},");
            sb.AppendLine($"  \"dryRun\": {(dryRun ? "true" : "false")},");
            sb.AppendLine($"  \"generatedAt\": {JsonText.Quote(DateTime.Now.ToString("s", CultureInfo.InvariantCulture))},");
            // Preview stamp: a dry run records the NewMusic fingerprint it saw; a real run records whether the folder it started
            // on still matches the latest preview (null when no stamped preview exists), so a client can flag a stale preview.
            if (dryRun && newMusicFingerprint != null)
                sb.AppendLine($"  \"newMusicFingerprint\": {JsonText.Quote(newMusicFingerprint)},");
            else if (!dryRun)
            {
                sb.AppendLine($"  \"previewFingerprint\": {JsonText.Quote(previewFingerprint)},");
                string stale = previewFingerprint == null || newMusicFingerprint == null ? "null"
                    : (string.Equals(previewFingerprint, newMusicFingerprint, StringComparison.Ordinal) ? "false" : "true");
                sb.AppendLine($"  \"previewStale\": {stale},");
            }
            AppendSummaryBlock(sb, entries, summary);
            if (!dryRun) AppendLeftoversBlock(sb, leftovers);
            AppendConfidenceBlock(sb, confidence);
            AppendBlockedBlock(sb, blocked);
            AppendReplacedOriginalsBlock(sb, replaced);
            AppendArtistSpellingCandidatesBlock(sb, artistSpellingCandidates);
            sb.AppendLine("  \"files\": [");
            for (int i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                sb.AppendLine("  {");
                sb.AppendLine($"    \"filename\": {JsonText.Quote(e.Filename)},");
                if (!string.IsNullOrEmpty(e.SourceRel)) sb.AppendLine($"    \"sourceRel\": {JsonText.Quote(e.SourceRel)},");
                if (!string.IsNullOrEmpty(e.SourceRel)) sb.AppendLine($"    \"originalSourceRel\": {JsonText.Quote(string.IsNullOrEmpty(e.OriginalSourceRel) ? e.SourceRel : e.OriginalSourceRel)},");
                sb.AppendLine($"    \"artist\": {JsonText.Quote(e.Artists)},");
                sb.AppendLine($"    \"title\": {JsonText.Quote(e.Title)},");
                sb.AppendLine($"    \"album\": {JsonText.Quote(e.Album)},");
                sb.AppendLine($"    \"destination\": {JsonText.Quote(e.Destination)},");
                sb.AppendLine($"    \"reason\": {JsonText.Quote(e.Reason)},");
                sb.AppendLine($"    \"isNewFolder\": {(e.IsNewFolder ? "true" : "false")},");
                sb.AppendLine($"    \"status\": {JsonText.Quote(e.Status)},");
                sb.AppendLine($"    \"detail\": {JsonText.Quote(e.Detail ?? "")},");
                sb.AppendLine($"    \"inBatchDuplicate\": {(e.InBatchDuplicate ? "true" : "false")},");
                sb.AppendLine($"    \"compilationAlbum\": {(e.CompilationAlbum ? "true" : "false")},");
                // Library-duplicate block - distinct from inBatchDuplicate above, whose field name
                // and meaning are unchanged.
                sb.AppendLine($"    \"libraryDuplicate\": {(e.LibraryDuplicate ? "true" : "false")},");
                sb.AppendLine($"    \"dupLibraryPath\": {JsonText.Quote(e.DupLibraryPath)},");
                sb.AppendLine($"    \"dupLibraryTrack\": {JsonText.Quote(e.DupLibraryTrack)},");
                sb.AppendLine($"    \"dupLibraryAlbum\": {JsonText.Quote(e.DupLibraryAlbum)},");
                sb.AppendLine($"    \"dupNewAlbum\": {JsonText.Quote(e.DupNewAlbum)},");
                sb.AppendLine($"    \"dupRecommendationKey\": {JsonText.Quote(e.DupRecommendationKey)},");
                sb.AppendLine($"    \"dupRecommendation\": {JsonText.Quote(e.DupRecommendation)},");
                sb.AppendLine($"    \"dupReason\": {JsonText.Quote(e.DupReason)},");
                if (e.TagFix == null || e.TagFix.OldTags == null)
                    sb.AppendLine("    \"tagFix\": null,");
                else
                    sb.AppendLine($"    \"tagFix\": {{\"oldName\": {JsonText.Quote(e.TagFix.OriginalFilename)}, \"newName\": {JsonText.Quote(e.TagFix.Filename ?? e.TagFix.OriginalFilename)}, \"oldTags\": {TagFixPlanJson.TagsJson(e.TagFix.OldTags)}, \"newTags\": {TagFixPlanJson.TagsJson(e.TagFix.NewTags)}}},");
                sb.Append($"    \"tagChanges\": [");
                if (e.TagChanges?.Count > 0)
                    sb.Append(string.Join(", ", e.TagChanges.Select(t => JsonText.Quote(t))));
                sb.AppendLine("]");
                sb.AppendLine(i < entries.Count - 1 ? "  }," : "  }");
            }
            sb.AppendLine("  ]");
            sb.AppendLine("}");
            return sb.ToString();
        }

        /// <summary>
        /// Appends the batch-level "summary" block: route distribution (derived from the entries'
        /// own destinations, so it can never disagree with the per-file rows a client renders),
        /// the Misc auto-migration counts, and the detected compilation albums. A null summary
        /// still emits the block with empty collections so the contract shape is constant.
        /// </summary>
        internal static void AppendSummaryBlock(StringBuilder sb, List<MusicIntegrator.LogEntry> entries, MusicIntegrator.BatchSummary summary)
        {
            var routeCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in entries)
            {
                if (string.IsNullOrEmpty(e.Destination)) continue;
                string cat = MusicIntegrator.GetRouteCategory(e.Destination);
                routeCounts[cat] = routeCounts.ContainsKey(cat) ? routeCounts[cat] + 1 : 1;
            }
            var ordered = routeCounts.OrderByDescending(kv => kv.Value)
                                     .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                                     .ToList();

            var migrations = summary?.MiscAutoMigrations ?? new Dictionary<string, int>();
            var orderedMigrations = migrations.Where(kv => kv.Value > 0)
                                              .OrderByDescending(kv => kv.Value)
                                              .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                                              .ToList();
            int migrationTotal = orderedMigrations.Sum(kv => kv.Value);

            var comps = (summary?.CompilationAlbums ?? new List<string>())
                        .OrderBy(a => a, StringComparer.OrdinalIgnoreCase).ToList();

            sb.AppendLine("  \"summary\": {");
            sb.AppendLine($"    \"routes\": {{{string.Join(", ", ordered.Select(kv => $"{JsonText.Quote(kv.Key)}: {kv.Value}"))}}},");
            sb.AppendLine($"    \"miscAutoMigrations\": [{string.Join(", ", orderedMigrations.Select(kv => $"{{\"artist\": {JsonText.Quote(kv.Key)}, \"count\": {kv.Value}}}"))}],");
            sb.AppendLine($"    \"miscAutoMigrationTotal\": {migrationTotal},");

            // Stage counts derived from the same rows written to files[], so a GUI reads them
            // without scanning rows. "skipped" covers skip and would-skip statuses.
            int files = entries.Count;
            int routed = entries.Count(e => e.Status == "moved" || e.Status == "would-move");
            int skipped = entries.Count(e => e.Status != null && (e.Status.StartsWith("skip", StringComparison.Ordinal) || e.Status == "would-skip"));
            int errors = entries.Count(e => e.Status == "error");
            int libraryDuplicates = entries.Count(e => e.LibraryDuplicate);
            int inBatchDuplicates = entries.Count(e => e.InBatchDuplicate);
            sb.AppendLine($"    \"counts\": {{\"files\": {files}, \"routed\": {routed}, \"skipped\": {skipped}, \"errors\": {errors}, \"libraryDuplicates\": {libraryDuplicates}, \"inBatchDuplicates\": {inBatchDuplicates}, \"unrouted\": {files - routed}}},");
            sb.AppendLine($"    \"compilationAlbums\": [{string.Join(", ", comps.Select(a => JsonText.Quote(a)))}]");
            sb.AppendLine("  },");
        }

        /// <summary>
        /// Appends the top-level "leftovers" block (execution records only; BuildJson skips it on a dry run).
        /// A null snapshot is written as an empty block, so every execution record carries the field.
        /// </summary>
        internal static void AppendLeftoversBlock(StringBuilder sb, MusicIntegrator.LeftoversSnapshot leftovers)
        {
            var snap = leftovers ?? new MusicIntegrator.LeftoversSnapshot();
            string folders = string.Join(", ", snap.EmptyFolders.Select(f => JsonText.Quote(f)));
            string files = string.Join(", ", snap.OtherFiles.Select(f => JsonText.Quote(f)));
            sb.AppendLine($"  \"leftovers\": {{\"emptyFolders\": [{folders}], \"otherFiles\": [{files}], \"declinedCount\": {snap.DeclinedCount}}},");
        }

        /// <summary>
        /// Appends the "confidence" block: the literal null on a dry run (the sanity check never
        /// runs there), otherwise the object built from a populated ConfidenceRecord. See section
        /// 2.2 of the execution record contract for the exact shape.
        /// </summary>
        internal static void AppendConfidenceBlock(StringBuilder sb, MusicIntegrator.ConfidenceRecord confidence)
        {
            if (confidence == null)
            {
                sb.AppendLine("  \"confidence\": null,");
                return;
            }

            var failures = confidence.SanityFailures ?? new List<(string Destination, string Reason)>();
            var newFolders = (confidence.NewFolders ?? new List<string>())
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
            var errors = confidence.Errors ?? new List<(string Filename, string Detail, string SourceRel)>();
            bool sanityOk = failures.Count == 0;

            sb.AppendLine("  \"confidence\": {");
            sb.AppendLine("    \"countCheck\": {");
            sb.AppendLine($"      \"totalFiles\": {confidence.TotalFiles},");
            sb.AppendLine($"      \"moved\": {confidence.Moved},");
            sb.AppendLine($"      \"skipped\": {confidence.Skipped},");
            sb.AppendLine($"      \"expectedMoved\": {confidence.ExpectedMoved},");
            sb.AppendLine($"      \"ok\": {(confidence.CountOk ? "true" : "false")}");
            sb.AppendLine("    },");
            sb.AppendLine("    \"sanityCheck\": {");
            sb.AppendLine($"      \"ran\": {(confidence.SanityRan ? "true" : "false")},");
            sb.AppendLine($"      \"ok\": {(sanityOk ? "true" : "false")},");
            sb.AppendLine($"      \"checked\": {confidence.SanityChecked},");
            sb.Append("      \"failures\": [");
            sb.Append(string.Join(", ", failures.Select(f =>
                $"{{\"destination\": {JsonText.Quote(f.Destination)}, \"reason\": {JsonText.Quote(f.Reason)}}}")));
            sb.AppendLine("]");
            sb.AppendLine("    },");
            sb.Append("    \"newFolders\": [");
            sb.Append(string.Join(", ", newFolders.Select(f => JsonText.Quote(f))));
            sb.AppendLine("],");
            sb.AppendLine($"    \"errorCount\": {errors.Count},");
            sb.Append("    \"errors\": [");
            sb.Append(string.Join(", ", errors.Select(e =>
                $"{{\"filename\": {JsonText.Quote(e.Filename)}, " +
                (string.IsNullOrEmpty(e.SourceRel) ? "" : $"\"sourceRel\": {JsonText.Quote(e.SourceRel)}, ") +
                $"\"detail\": {JsonText.Quote(e.Detail)}}}")));
            sb.AppendLine("]");
            sb.AppendLine("  },");
        }

        /// <summary>
        /// Snapshot of what NewMusic holds now, for the execution record's "leftovers" block. Read-only.
        /// EmptyFolders: every subdirectory with no file at any depth (so an empty folder and its empty
        /// parents are both listed). OtherFiles: every file that is not an mp3 (the only audio the
        /// run processes); declined mp3s are left out and counted by declinedCount instead. Both lists
        /// are relative to newMusicPath with backslashes, sorted ordinal. A missing folder gives empty lists.
        /// </summary>
        internal static MusicIntegrator.LeftoversSnapshot ComputeLeftovers(string newMusicPath, int declinedCount)
        {
            var snap = new MusicIntegrator.LeftoversSnapshot { DeclinedCount = declinedCount };
            if (!Directory.Exists(newMusicPath)) return snap;

            // A directory is non-empty when a file sits anywhere under it, so mark each file's
            // directory and its ancestors up to (not including) the root.
            var nonEmpty = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string file in Directory.GetFiles(newMusicPath, "*", SearchOption.AllDirectories))
            {
                if (!file.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase))
                    snap.OtherFiles.Add(RelToNewMusicRoot(file, newMusicPath));
                string dir = Path.GetDirectoryName(file);
                while (dir != null && dir.Length > newMusicPath.Length && nonEmpty.Add(dir))
                    dir = Path.GetDirectoryName(dir);
            }

            foreach (string dir in Directory.GetDirectories(newMusicPath, "*", SearchOption.AllDirectories))
            {
                if (!nonEmpty.Contains(dir))
                    snap.EmptyFolders.Add(RelToNewMusicRoot(dir, newMusicPath));
            }

            snap.EmptyFolders.Sort(StringComparer.Ordinal);
            snap.OtherFiles.Sort(StringComparer.Ordinal);
            return snap;
        }

        internal static string RelToNewMusicRoot(string fullPath, string newMusicPath)
        {
            string rel = fullPath.StartsWith(newMusicPath, StringComparison.OrdinalIgnoreCase)
                ? fullPath.Substring(newMusicPath.Length)
                : fullPath;
            return rel.TrimStart('\\', '/').Replace('/', '\\');
        }

        /// <summary>
        /// SHA-256 (hex) over the sorted mp3 files under folder: relative path, size and last-write ticks per file.
        /// A missing folder hashes as empty. Any add, remove, rename, resize or touch changes the value.
        /// </summary>
        internal static string NewMusicFingerprint(string folder)
        {
            var lines = new List<string>();
            try
            {
                if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
                {
                    string root = folder.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
                    foreach (string p in Directory.GetFiles(folder, "*.mp3", SearchOption.AllDirectories))
                    {
                        var fi = new FileInfo(p);
                        string rel = p.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? p.Substring(root.Length) : Path.GetFileName(p);
                        lines.Add($"{rel}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}");
                    }
                }
            }
            catch (Exception) { lines.Add("unreadable"); }
            lines.Sort(StringComparer.Ordinal);
            using (var sha = System.Security.Cryptography.SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(string.Join("\n", lines)))).Replace("-", "").ToLowerInvariant();
        }

        /// <summary>newMusicFingerprint of the newest routing-*.json in logsDir; null when there is none or it carries no stamp.</summary>
        internal static string ReadLatestPreviewFingerprint(string logsDir)
        {
            try
            {
                if (!Directory.Exists(logsDir)) return null;
                string newest = Directory.GetFiles(logsDir, "routing-*.json")
                    .OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal).LastOrDefault();
                if (newest == null) return null;
                var m = System.Text.RegularExpressions.Regex.Match(File.ReadAllText(newest), "\"newMusicFingerprint\"\\s*:\\s*\"([0-9a-zA-Z]+)\"");
                return m.Success ? m.Groups[1].Value : null;
            }
            catch (Exception) { return null; }
        }

        /// <summary>
        /// The write-with-one-retry logic extracted out of WriteExecutionRecord so it is reachable
        /// from a test without reflecting into a private instance method (docs/
        /// design/Execution-Record-Fallback-Removal-Design.md section 5.3, test 11/12) - takes the logs
        /// directory, timestamp and pre-built JSON as plain values and a log sink instead of writing
        /// to Console directly, so a test can capture the printed lines. Returns true when either the
        /// primary or the retry write succeeded, false only when both failed - the caller (a real run)
        /// never inspects this return value today because a failed write must never fail an
        /// integration run that already moved files (see WriteExecutionRecord's own doc comment), but
        /// the return value lets a test assert success/failure without parsing console text.
        /// </summary>
        internal static bool WriteExecutionRecordWithRetry(string logsPath, string timestamp, string json, Action<string> log)
        {
            try
            {
                if (!Directory.Exists(logsPath))
                    Directory.CreateDirectory(logsPath);
                string jsonPath = Path.Combine(logsPath, $"execution-{timestamp}.json");

                AtomicFile.WriteAllText(jsonPath, json, System.Text.Encoding.UTF8);
                log(MusicIntegrator.FormatExecutionJsonLine(jsonPath));
                return true;
            }
            catch (Exception)
            {
                try
                {
                    string retryPath = Path.Combine(logsPath, $"execution-{timestamp}-retry.json");
                    AtomicFile.WriteAllText(retryPath, json, System.Text.Encoding.UTF8);
                    log(MusicIntegrator.FormatExecutionJsonLine(retryPath));
                    return true;
                }
                catch (Exception retryEx)
                {
                    log($"\n  [ERROR] EXECUTION RECORD FAILED: {retryEx.Message}");
                    return false;
                }
            }
        }
    }
}
