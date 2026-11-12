using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace AudioManager
{
    /// <summary>
    /// Pure JSON text builders for the routing and execution records (no I/O). MusicIntegrator forwards to these.
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
            sb.AppendLine($"    \"status\": {JStr(blocked.Status)},");
            sb.AppendLine($"    \"action\": {JStr(blocked.Action)},");
            sb.AppendLine("    \"files\": [");
            for (int i = 0; i < blocked.Files.Count; i++)
            {
                var (file, length) = blocked.Files[i];
                string comma = i < blocked.Files.Count - 1 ? "," : "";
                sb.AppendLine($"      {{\"file\": {JStr(file)}, \"projectedLength\": {length}, \"limit\": {blocked.Limit}}}{comma}");
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
                sb.AppendLine($"      \"key\": {JStr(c.Key)},");
                sb.AppendLine($"      \"suggested\": {JStr(c.Suggested)},");
                sb.AppendLine("      \"spellings\": [");
                for (int j = 0; j < c.Spellings.Count; j++)
                {
                    var v = c.Spellings[j];
                    string comma = j < c.Spellings.Count - 1 ? "," : "";
                    sb.AppendLine($"        {{\"spelling\": {JStr(v.Spelling)}, \"tracks\": [{string.Join(", ", v.Tracks.Select(t => JStr(t)))}], \"count\": {v.Count}}}{comma}");
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
                sb.AppendLine($"    {{\"original\": {JStr(replaced[i].original)}, \"held\": {JStr(replaced[i].held)}}}{comma}");
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
            sb.AppendLine($"  \"recordType\": {JStr(dryRun ? "routing" : "execution")},");
            sb.AppendLine($"  \"dryRun\": {(dryRun ? "true" : "false")},");
            sb.AppendLine($"  \"generatedAt\": {JStr(DateTime.Now.ToString("s", CultureInfo.InvariantCulture))},");
            // Preview stamp: a dry run records the NewMusic fingerprint it saw; a real run records whether the folder it started
            // on still matches the latest preview (null when no stamped preview exists), so a client can flag a stale preview.
            if (dryRun && newMusicFingerprint != null)
                sb.AppendLine($"  \"newMusicFingerprint\": {JStr(newMusicFingerprint)},");
            else if (!dryRun)
            {
                sb.AppendLine($"  \"previewFingerprint\": {JStr(previewFingerprint)},");
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
                sb.AppendLine($"    \"filename\": {JStr(e.Filename)},");
                if (!string.IsNullOrEmpty(e.SourceRel)) sb.AppendLine($"    \"sourceRel\": {JStr(e.SourceRel)},");
                if (!string.IsNullOrEmpty(e.SourceRel)) sb.AppendLine($"    \"originalSourceRel\": {JStr(string.IsNullOrEmpty(e.OriginalSourceRel) ? e.SourceRel : e.OriginalSourceRel)},");
                sb.AppendLine($"    \"artist\": {JStr(e.Artists)},");
                sb.AppendLine($"    \"title\": {JStr(e.Title)},");
                sb.AppendLine($"    \"album\": {JStr(e.Album)},");
                sb.AppendLine($"    \"destination\": {JStr(e.Destination)},");
                sb.AppendLine($"    \"reason\": {JStr(e.Reason)},");
                sb.AppendLine($"    \"isNewFolder\": {(e.IsNewFolder ? "true" : "false")},");
                sb.AppendLine($"    \"status\": {JStr(e.Status)},");
                sb.AppendLine($"    \"detail\": {JStr(e.Detail ?? "")},");
                sb.AppendLine($"    \"inBatchDuplicate\": {(e.InBatchDuplicate ? "true" : "false")},");
                sb.AppendLine($"    \"compilationAlbum\": {(e.CompilationAlbum ? "true" : "false")},");
                // Library-duplicate block - distinct from inBatchDuplicate above, whose field name
                // and meaning are unchanged.
                sb.AppendLine($"    \"libraryDuplicate\": {(e.LibraryDuplicate ? "true" : "false")},");
                sb.AppendLine($"    \"dupLibraryPath\": {JStr(e.DupLibraryPath)},");
                sb.AppendLine($"    \"dupLibraryTrack\": {JStr(e.DupLibraryTrack)},");
                sb.AppendLine($"    \"dupLibraryAlbum\": {JStr(e.DupLibraryAlbum)},");
                sb.AppendLine($"    \"dupNewAlbum\": {JStr(e.DupNewAlbum)},");
                sb.AppendLine($"    \"dupRecommendationKey\": {JStr(e.DupRecommendationKey)},");
                sb.AppendLine($"    \"dupRecommendation\": {JStr(e.DupRecommendation)},");
                sb.AppendLine($"    \"dupReason\": {JStr(e.DupReason)},");
                if (e.TagFix == null || e.TagFix.OldTags == null)
                    sb.AppendLine("    \"tagFix\": null,");
                else
                    sb.AppendLine($"    \"tagFix\": {{\"oldName\": {JStr(e.TagFix.OriginalFilename)}, \"newName\": {JStr(e.TagFix.Filename ?? e.TagFix.OriginalFilename)}, \"oldTags\": {TagFixPlanJson.TagsJson(e.TagFix.OldTags)}, \"newTags\": {TagFixPlanJson.TagsJson(e.TagFix.NewTags)}}},");
                sb.Append($"    \"tagChanges\": [");
                if (e.TagChanges?.Count > 0)
                    sb.Append(string.Join(", ", e.TagChanges.Select(t => JStr(t))));
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
            sb.AppendLine($"    \"routes\": {{{string.Join(", ", ordered.Select(kv => $"{JStr(kv.Key)}: {kv.Value}"))}}},");
            sb.AppendLine($"    \"miscAutoMigrations\": [{string.Join(", ", orderedMigrations.Select(kv => $"{{\"artist\": {JStr(kv.Key)}, \"count\": {kv.Value}}}"))}],");
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
            sb.AppendLine($"    \"compilationAlbums\": [{string.Join(", ", comps.Select(a => JStr(a)))}]");
            sb.AppendLine("  },");
        }

        /// <summary>
        /// Appends the top-level "leftovers" block (execution records only; BuildJson skips it on a dry run).
        /// A null snapshot is written as an empty block, so every execution record carries the field.
        /// </summary>
        internal static void AppendLeftoversBlock(StringBuilder sb, MusicIntegrator.LeftoversSnapshot leftovers)
        {
            var snap = leftovers ?? new MusicIntegrator.LeftoversSnapshot();
            string folders = string.Join(", ", snap.EmptyFolders.Select(f => JStr(f)));
            string files = string.Join(", ", snap.OtherFiles.Select(f => JStr(f)));
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
                $"{{\"destination\": {JStr(f.Destination)}, \"reason\": {JStr(f.Reason)}}}")));
            sb.AppendLine("]");
            sb.AppendLine("    },");
            sb.Append("    \"newFolders\": [");
            sb.Append(string.Join(", ", newFolders.Select(f => JStr(f))));
            sb.AppendLine("],");
            sb.AppendLine($"    \"errorCount\": {errors.Count},");
            sb.Append("    \"errors\": [");
            sb.Append(string.Join(", ", errors.Select(e =>
                $"{{\"filename\": {JStr(e.Filename)}, " +
                (string.IsNullOrEmpty(e.SourceRel) ? "" : $"\"sourceRel\": {JStr(e.SourceRel)}, ") +
                $"\"detail\": {JStr(e.Detail)}}}")));
            sb.AppendLine("]");
            sb.AppendLine("  },");
        }

        /// <summary>JSON-escapes a string value, returning a quoted JSON string or null. Escapes the
        /// full C0 control range: the standard short escapes (\b \f \n \r \t) plus a \u-prefixed
        /// four-hex-digit escape for any other character below U+0020. \r is emitted as \\r rather
        /// than silently stripped - a
        /// malformed record from an unescaped control character (e.g. in a raw exception message
        /// landing in "detail") must never be produced. See the execution record contract, section 6.</summary>
        internal static string JStr(string s)
        {
            if (s == null) return "null";
            var sb = new StringBuilder(s.Length + 2);
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"': sb.Append("\\\""); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ')
                            sb.Append("\\u").Append(((int)c).ToString("x4", System.Globalization.CultureInfo.InvariantCulture));
                        else
                            sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }
    }
}
