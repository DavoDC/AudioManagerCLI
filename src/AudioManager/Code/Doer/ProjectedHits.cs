using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace AudioManager
{
    /// <summary>
    /// Projected-LibChecker hits as client data (routing JSON "projectedHits"). Each hit carries the LibChecker rule id,
    /// the NewMusic-relative source file when the hit is on an incoming track (null for library tracks), the AudioMirror
    /// path, the message and the other members of a duplicate pair. Hits the user accepted (Accept rows) or kept
    /// (keep= rows) are dropped, so a hit never reappears after the client accepts it. Contract: docs/Contracts/RoutingJSONFormat.md.
    /// </summary>
    internal static class ProjectedHits
    {
        internal sealed class Item
        {
            public string Rule, SourceFile, MirrorPath, Message;
            public List<string> RelatedFiles = new List<string>();
        }

        /// <summary>keep= token that silences each rule (unwanted_text only; missing_tag has no keep token).</summary>
        private static readonly string[] UnwantedKeeps = { "title", "artist", "album", "casing" };

        /// <summary>
        /// Pure mapper. mirrorToSource maps a synthesized mirror path to its NewMusic-relative source file (case-insensitive).
        /// </summary>
        internal static List<Item> Build(IEnumerable<LibChecker.LibCheckerHit> hits, IDictionary<string, string> mirrorToSource, TagFixExceptions exceptions)
        {
            var result = new List<Item>();
            foreach (var h in hits)
            {
                if (h.Kind == "empty_library") continue;
                mirrorToSource.TryGetValue(h.Path ?? "", out string src);
                if (src != null && exceptions != null)
                {
                    string name = System.IO.Path.GetFileName(src);
                    if (exceptions.Accepts(name, h.Kind, src)) continue;
                    if (h.Kind == "unwanted_text" && UnwantedKeeps.Any(k => exceptions.Keeps(name, k, src))) continue;
                }
                var item = new Item { Rule = h.Kind, SourceFile = src, MirrorPath = h.Path, Message = h.Detail };
                foreach (string r in h.Related)
                    item.RelatedFiles.Add(mirrorToSource.TryGetValue(r, out string rs) ? rs : r);
                result.Add(item);
            }
            return result;
        }

        /// <summary>Appends the top-level "projectedHits" array (always present on a dry run, empty when clean).</summary>
        internal static void AppendBlock(StringBuilder sb, IList<Item> items)
        {
            if (items == null || items.Count == 0) { sb.AppendLine("  \"projectedHits\": [],"); return; }
            sb.AppendLine("  \"projectedHits\": [");
            for (int i = 0; i < items.Count; i++)
            {
                var it = items[i];
                string rel = string.Join(", ", it.RelatedFiles.Select(JsonText.Quote));
                string src = it.SourceFile == null ? "null" : JsonText.Quote(it.SourceFile);
                sb.AppendLine($"    {{\"rule\": {JsonText.Quote(it.Rule)}, \"sourceFile\": {src}, \"mirrorPath\": {JsonText.Quote(it.MirrorPath)}, \"message\": {JsonText.Quote(it.Message)}, \"relatedFiles\": [{rel}]}}{(i < items.Count - 1 ? "," : "")}");
            }
            sb.AppendLine("  ],");
        }
    }
}
