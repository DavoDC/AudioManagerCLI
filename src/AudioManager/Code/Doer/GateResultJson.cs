using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace AudioManager
{
    /// <summary>
    /// Writes the pre-integration gate outcome as structured JSON (data/logs/integrate-gate.json) for a client.
    ///
    /// Why this exists: when integrate stops at the gate (mirror out of sync, LibChecker dirty) the exe
    /// writes no routing JSON, so a client could only show raw console text. This file gives the client a
    /// status code, the dirty mirror files and the LibChecker issue count to render without parsing text.
    /// Written for every integrate run with --json-output, pass or fail. Bump SchemaVersion on a breaking change.
    /// </summary>
    internal static class GateResultJson
    {
        public const int SchemaVersion = 1;
        public const string OutputFileName = "integrate-gate.json";

        /// <summary>Builds and writes the file; returns the path written.</summary>
        public static string Write(Program.PreIntegrationGateResult result, IList<string> mirrorStatusLines, int libCheckerIssues)
        {
            string json = Build(result, mirrorStatusLines, libCheckerIssues, DateTime.Now);
            if (!Directory.Exists(Constants.LogsPath))
                Directory.CreateDirectory(Constants.LogsPath);
            string outPath = Path.Combine(Constants.LogsPath, OutputFileName);
            AtomicFile.WriteAllText(outPath, json, new UTF8Encoding(false));
            return outPath;
        }

        /// <summary>Removes an older gate file so a crashed gate can never leave a stale "passed". Missing folder or file is fine.</summary>
        public static void ClearStale(string logsDir)
        {
            try
            {
                string p = Path.Combine(logsDir, OutputFileName);
                if (File.Exists(p)) File.Delete(p);
            }
            catch (Exception) { /* best effort; WriteError or Write overwrites it anyway */ }
        }

        /// <summary>Writes status gate_error (the gate threw before it could decide) with the message.</summary>
        public static void WriteError(string logsDir, string message)
        {
            if (!Directory.Exists(logsDir)) Directory.CreateDirectory(logsDir);
            var sb = new StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine($"  \"schemaVersion\": {SchemaVersion},");
            sb.AppendLine($"  \"generatedAt\": {JStr(DateTime.Now.ToString("s", CultureInfo.InvariantCulture))},");
            sb.AppendLine("  \"status\": \"gate_error\",");
            sb.AppendLine("  \"action\": \"fix_gate_error\",");
            sb.AppendLine($"  \"error\": {JStr(message)}");
            sb.AppendLine("}");
            AtomicFile.WriteAllText(Path.Combine(logsDir, OutputFileName), sb.ToString(), new UTF8Encoding(false));
        }

        /// <summary>Pure builder. mirrorStatusLines are raw 'git status --porcelain' lines; LastRunInfo.txt is ignored.</summary>
        public static string Build(Program.PreIntegrationGateResult result, IList<string> mirrorStatusLines, int libCheckerIssues, DateTime generatedAt)
        {
            var dirty = (mirrorStatusLines ?? new List<string>())
                .Where(l => !string.IsNullOrWhiteSpace(l) && !l.Contains("LastRunInfo.txt"))
                .ToList();
            bool mirrorDirty = result == Program.PreIntegrationGateResult.MirrorStale;

            var sb = new StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine($"  \"schemaVersion\": {SchemaVersion},");
            sb.AppendLine($"  \"generatedAt\": {JStr(generatedAt.ToString("s", CultureInfo.InvariantCulture))},");
            sb.AppendLine($"  \"status\": {JStr(StatusCode(result))},");
            sb.AppendLine($"  \"action\": {JStr(ActionCode(result))},");
            sb.AppendLine($"  \"mirrorDirty\": {(mirrorDirty ? "true" : "false")},");
            sb.AppendLine($"  \"mirrorDirtyCount\": {(mirrorDirty ? dirty.Count : 0)},");
            sb.AppendLine($"  \"libCheckerIssues\": {libCheckerIssues},");
            var shown = mirrorDirty ? dirty : new List<string>();
            sb.AppendLine(shown.Count == 0 ? "  \"mirrorDirtyFiles\": []" : "  \"mirrorDirtyFiles\": [");
            for (int i = 0; i < shown.Count; i++)
                sb.AppendLine($"    {JStr(shown[i].Trim())}{(i < shown.Count - 1 ? "," : "")}");
            if (shown.Count > 0) sb.AppendLine("  ]");
            sb.AppendLine("}");
            return sb.ToString();
        }

        internal static string StatusCode(Program.PreIntegrationGateResult r)
        {
            switch (r)
            {
                case Program.PreIntegrationGateResult.MirrorRepoMissing: return "mirror_repo_missing";
                case Program.PreIntegrationGateResult.MirrorStale: return "mirror_stale";
                case Program.PreIntegrationGateResult.LibCheckerDirty: return "libchecker_dirty";
                case Program.PreIntegrationGateResult.GitStatusFailed: return "git_error";
                default: return "passed";
            }
        }

        private static string ActionCode(Program.PreIntegrationGateResult r)
        {
            switch (r)
            {
                case Program.PreIntegrationGateResult.MirrorRepoMissing: return "restore_mirror_repo";
                case Program.PreIntegrationGateResult.MirrorStale: return "commit_mirror";
                case Program.PreIntegrationGateResult.LibCheckerDirty: return "fix_library_issues";
                case Program.PreIntegrationGateResult.GitStatusFailed: return "fix_git";
                default: return "none";
            }
        }

        private static string JStr(string s)
        {
            if (s == null) return "null";
            return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "").Replace("\n", "\\n") + "\"";
        }
    }
}
