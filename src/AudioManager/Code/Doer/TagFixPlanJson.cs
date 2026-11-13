using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace AudioManager
{
    /// <summary>
    /// Writes the tagfix dry-run plan as structured JSON (data/logs/tagfix-plan.json) for a client.
    ///
    /// Why this exists: a client needs the per-file old and new tag values and file names before it
    /// can show a reviewable diff, and a fingerprint of the NewMusic content the plan was made from,
    /// so a later apply can refuse if the folder changed. The plan is read-only: it is built from the
    /// TagFixer dry-run rows and never touches a file. Contract: docs/Contracts/TagFixJSONFormat.md.
    /// Bump SchemaVersion on any breaking change.
    /// </summary>
    internal static class TagFixPlanJson
    {
        /// <summary>Contract version. Increment when a field is removed or its meaning changes.</summary>
        public const int SchemaVersion = 1;

        /// <summary>Fixed output filename (latest dry run wins) so a client reads a deterministic path.</summary>
        public const string OutputFileName = "tagfix-plan.json";

        /// <summary>
        /// Production entry point: builds the plan from a finished dry-run TagFixer and writes it to
        /// data/logs/tagfix-plan.json. Returns the path written. Only called for tagfix --dry-run.
        /// </summary>
        public static string Write(TagFixer tagFixer)
        {
            string json = Build(Constants.NewMusicPath, tagFixer.Scanned, tagFixer.Results, DateTime.Now);

            if (!Directory.Exists(Constants.LogsPath))
                Directory.CreateDirectory(Constants.LogsPath);
            string outPath = Path.Combine(Constants.LogsPath, OutputFileName);
            AtomicFile.WriteAllText(outPath, json, new UTF8Encoding(false));
            return outPath;
        }

        /// <summary>
        /// Pure builder: produces the plan JSON from the scanned snapshot and the dry-run rows. No file I/O.
        /// Only files with a planned change (a tag fix, or a would-quarantine) appear in "files".
        /// An errored file, or a planned change whose rename is blocked, appears in "needsManual".
        /// </summary>
        public static string Build(string folder, IList<TagFixer.ScannedFile> scanned, IList<TagFixer.FixLog> logs, DateTime generatedAt)
        {
            var files = logs
                .Where(l => l.Status == "would-fix" || l.WouldQuarantine)
                .OrderBy(l => RelativePath(folder, l.SourcePath, l.OriginalFilename), StringComparer.OrdinalIgnoreCase)
                .ToList();

            var manual = logs
                .Where(l => l.Status == "error" || l.NeedsManual)
                .OrderBy(l => RelativePath(folder, l.SourcePath, l.OriginalFilename), StringComparer.OrdinalIgnoreCase)
                .ToList();

            int quarantines = files.Count(f => f.WouldQuarantine);
            int renames = files.Count(f => !f.WouldQuarantine && NewName(f) != f.OriginalFilename);

            var sb = new StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine($"  \"schemaVersion\": {SchemaVersion},");
            sb.AppendLine($"  \"generatedAt\": {JsonText.Quote(generatedAt.ToString("s", CultureInfo.InvariantCulture))},");
            sb.AppendLine("  \"fingerprint\": {");
            sb.AppendLine("    \"algorithm\": \"sha256\",");
            sb.AppendLine($"    \"fileCount\": {scanned.Count},");
            sb.AppendLine($"    \"value\": {JsonText.Quote(Fingerprint(folder, scanned))}");
            sb.AppendLine("  },");
            sb.AppendLine("  \"counts\": {");
            sb.AppendLine($"    \"pending\": {files.Count},");
            sb.AppendLine($"    \"needsManual\": {manual.Count},");
            sb.AppendLine($"    \"renames\": {renames},");
            sb.AppendLine($"    \"quarantines\": {quarantines}");
            sb.AppendLine("  },");

            sb.AppendLine(files.Count == 0 ? "  \"files\": []," : "  \"files\": [");
            for (int i = 0; i < files.Count; i++)
            {
                var f = files[i];
                sb.AppendLine("    {");
                sb.AppendLine($"      \"file\": {JsonText.Quote(RelativePath(folder, f.SourcePath, f.OriginalFilename))},");
                sb.AppendLine($"      \"oldTags\": {TagsJson(f.OldTags)},");
                sb.AppendLine($"      \"newTags\": {TagsJson(f.NewTags)},");
                sb.AppendLine($"      \"oldName\": {JsonText.Quote(f.OriginalFilename)},");
                sb.AppendLine($"      \"newName\": {JsonText.Quote(NewName(f))},");
                sb.AppendLine($"      \"wouldQuarantine\": {(f.WouldQuarantine ? "true" : "false")}");
                sb.AppendLine(i < files.Count - 1 ? "    }," : "    }");
            }
            if (files.Count > 0) sb.AppendLine("  ],");

            sb.AppendLine(manual.Count == 0 ? "  \"needsManual\": []" : "  \"needsManual\": [");
            for (int i = 0; i < manual.Count; i++)
            {
                var m = manual[i];
                string reason = m.Status == "error" ? (m.Detail ?? "error") : (m.ManualReason ?? "needs manual review");
                sb.AppendLine($"    {{\"file\": {JsonText.Quote(RelativePath(folder, m.SourcePath, m.OriginalFilename))}, \"reason\": {JsonText.Quote(reason)}}}{(i < manual.Count - 1 ? "," : "")}");
            }
            if (manual.Count > 0) sb.AppendLine("  ]");
            sb.AppendLine("}");
            return sb.ToString();
        }

        /// <summary>
        /// SHA-256 (lower-case hex) over the sorted NewMusic content: one line per mp3 holding its path
        /// relative to the folder, its size in bytes and its last-write time in UTC ticks. Any add,
        /// remove, rename, resize or touch changes the value, so an apply can refuse a stale plan.
        /// </summary>
        public static string Fingerprint(string folder, IList<TagFixer.ScannedFile> scanned)
        {
            var lines = scanned
                .Select(s => $"{RelativePath(folder, s.Path, Path.GetFileName(s.Path))}|{s.Size}|{s.MtimeUtc.Ticks}")
                .OrderBy(l => l, StringComparer.Ordinal)
                .ToList();
            byte[] bytes = Encoding.UTF8.GetBytes(string.Join("\n", lines));
            using (var sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(bytes);
                return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
            }
        }

        /// <summary>The planned file name: unchanged for a quarantine, the dry-run target name otherwise.</summary>
        private static string NewName(TagFixer.FixLog f) => f.RenameBlocked ? f.OriginalFilename : (f.Filename ?? f.OriginalFilename);

        /// <summary>Path of the file relative to the NewMusic folder, falling back to the bare file name.</summary>
        private static string RelativePath(string folder, string sourcePath, string fallbackName)
        {
            if (string.IsNullOrEmpty(sourcePath)) return fallbackName ?? "";
            string root = folder.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            return sourcePath.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                ? sourcePath.Substring(root.Length)
                : Path.GetFileName(sourcePath);
        }

        internal static string TagsJson(TagFixer.TagState t)
        {
            if (t == null) return "null";
            return "{" +
                $"\"title\": {JsonText.Quote(t.Title)}, " +
                $"\"album\": {JsonText.Quote(t.Album)}, " +
                $"\"artists\": {JsonText.Quote(t.Artists)}, " +
                $"\"genre\": {JsonText.Quote(t.Genre)}, " +
                $"\"compilation\": {(t.Compilation ? "true" : "false")}, " +
                $"\"artCount\": {t.ArtCount}" +
                "}";
        }
    }
}
