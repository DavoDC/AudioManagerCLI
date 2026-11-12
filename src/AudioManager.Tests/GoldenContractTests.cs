using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace AudioManager
{
    // Contract rule (docs/README.md, Contracts): every golden in tests/golden/ is a real engine run captured on a
    // synthetic sandbox by scripts/dev/golden/make_goldens.py, never a hand-typed guess. These tests pin the keys and
    // identity values each golden must carry, so a renamed or dropped field fails the build. The rows are read from
    // the contract docs; scripts/dev/golden/check_goldens.py diffs the full key sets against a fresh real run.
    internal static class GoldenContractTests
    {
        // Each row: golden name, contract doc, identity "key=value;key=value" (empty for none), then the required
        // dotted paths. "[]" marks an array whose first row must exist, so the sandbox has to hold a case of that kind.
        private static readonly List<string[]> Specs = new List<string[]>
        {
            Row("routing-dry-run", "RoutingJSONFormat.md", "recordType=routing;schemaVersion=1",
                "newMusicFingerprint", "artistSpellingCandidates", "summary.counts.files", "summary.counts.routed",
                "summary.counts.skipped", "summary.counts.errors", "summary.counts.unrouted",
                "summary.counts.libraryDuplicates", "summary.counts.inBatchDuplicates", "files[].filename",
                "files[].sourceRel", "files[].originalSourceRel", "files[].status", "files[].tagFix"),
            Row("execution", "RoutingJSONFormat.md", "recordType=execution;schemaVersion=1",
                "previewFingerprint", "previewStale", "artistSpellingCandidates", "summary.counts.routed",
                "files[].filename", "files[].sourceRel", "files[].originalSourceRel", "files[].status", "leftovers.emptyFolders",
                "leftovers.otherFiles[]", "leftovers.declinedCount"),
            Row("fingerprint", "RoutingJSONFormat.md", "", "newMusicFingerprint"),
            Row("integrate-gate-mirror-stale", "GateResultJSONFormat.md", "status=mirror_stale;schemaVersion=1",
                "action", "mirrorDirty", "mirrorDirtyCount", "mirrorDirtyFiles[]", "libCheckerIssues", "generatedAt"),
            Row("integrate-gate-libchecker-dirty", "GateResultJSONFormat.md", "status=libchecker_dirty;schemaVersion=1",
                "action", "libCheckerIssues", "generatedAt"),
            Row("integrate-gate-passed", "GateResultJSONFormat.md", "status=passed;schemaVersion=1",
                "action", "libCheckerIssues", "generatedAt"),
            Row("integrate-gate-mirror-repo-missing", "GateResultJSONFormat.md",
                "status=mirror_repo_missing;schemaVersion=1", "action", "generatedAt"),
            Row("mirror-result", "MirrorResultJSONFormat.md", "recordType=mirrorResult;schemaVersion=1;status=ok",
                "generatedAt", "error", "mirrorCommitted", "libcheckerHitCount", "hits", "retry"),
            Row("mirror-result-libchecker-issues", "MirrorResultJSONFormat.md",
                "recordType=mirrorResult;schemaVersion=1;status=libchecker_issues",
                "mirrorCommitted", "libcheckerHitCount", "hits[].kind", "hits[].path", "hits[].detail", "retry"),
            Row("mirror-result-skipped", "MirrorResultJSONFormat.md", "recordType=mirrorResult;schemaVersion=1;status=skipped",
                "mirrorCommitted", "libcheckerHitCount", "hits", "retry"),
            Row("tagfix-plan", "TagFixJSONFormat.md", "schemaVersion=1",
                "generatedAt", "fingerprint.algorithm", "fingerprint.fileCount", "fingerprint.value", "counts.pending",
                "counts.needsManual", "counts.renames", "counts.quarantines", "files[].file", "files[].oldName",
                "files[].newName", "files[].wouldQuarantine", "files[].oldTags.artCount", "files[].newTags.title",
                "needsManual"),
            Row("tagfix-apply-result", "TagFixApplyJSONFormat.md", "status=applied",
                "oldFingerprint", "newFingerprint", "journal", "bytesNeeded", "bytesFree", "counts.applied",
                "counts.alreadyDone", "counts.failed", "counts.quarantined", "counts.needsManual", "counts.pendingAfter",
                "failures"),
            Row("tagfix-apply-result-stale-plan", "TagFixApplyJSONFormat.md", "status=stale_plan",
                "oldFingerprint", "newFingerprint", "journal", "failures"),
            Row("tagfix-journal", "TagFixApplyJSONFormat.md", "schemaVersion=1",
                "generatedAt", "folder", "quarantineRoot", "entries[].file", "entries[].oldName", "entries[].newName",
                "entries[].oldTitle", "entries[].newTitle", "entries[].backup", "entries[].quarantinedTo",
                "entries[].detail", "entries[].status"),
            Row("libfix-plan", "LibFixJSONFormat.md", "schema=libfix-plan-1;kind=album_in_singles",
                "libraryRoot", "generated", "moves[].from", "moves[].to", "moves[].artist", "moves[].album",
                "moves[].createsFolder", "skipped", "counts.moves", "counts.skipped"),
            Row("libfix-journal", "LibFixJSONFormat.md", "schema=libfix-journal-1",
                "libraryRoot", "generated", "moves[].from", "moves[].to", "moves[].createdFolder"),
            Row("libfix-apply-result", "LibFixJSONFormat.md", "schema=libfix-result-1;operation=apply;status=applied",
                "generated", "message", "journal", "counts.moved", "counts.reverted", "counts.skipped", "counts.failed",
                "counts.foldersRemoved", "notes"),
            Row("libfix-revert-result", "LibFixJSONFormat.md", "schema=libfix-result-1;operation=revert;status=reverted",
                "generated", "message", "journal", "counts.moved", "counts.reverted", "counts.skipped", "counts.failed",
                "counts.foldersRemoved", "notes"),
            Row("analysis-stats", "AnalysisJSONFormat.md", "schemaVersion=1",
                "generatedAt", "summary.trackCount", "summary.totalLibraryBytes", "genreDistribution[]",
                "decadeDistribution", "yearDistribution", "topArtists.exclMusivation", "topArtists.all",
                "ageStats.averageYears", "ageDistribution[].label", "coverArt.total", "tagCompleteness.percent",
                "coverCoverage800.percent"),
            Row("analysis-tracks", "AnalysisJSONFormat.md", "schemaVersion=1",
                "generatedAt", "trackCount", "tracks[].id", "tracks[].title", "tracks[].artists", "tracks[].primaryArtist",
                "tracks[].album", "tracks[].year", "tracks[].decade", "tracks[].genres", "tracks[].lengthSeconds",
                "tracks[].compilation", "tracks[].coverWidth", "tracks[].hasArt", "tracks[].hiResArt",
                "tracks[].addedDate", "tracks[].filePath"),
            Row("routing-blocked-path-too-long", "RoutingJSONFormat.md", "recordType=routing;schemaVersion=1",
                "blocked.status", "blocked.action", "blocked.files[].file", "blocked.files[].projectedLength",
                "blocked.files[].limit", "summary.counts.files", "artistSpellingCandidates"),
            Row("execution-replaced-originals", "RoutingJSONFormat.md", "recordType=execution;schemaVersion=1;previewStale=False",
                "previewFingerprint", "replacedOriginals[].original", "replacedOriginals[].held", "leftovers.declinedCount"),
            Row("execution-preview-stale", "RoutingJSONFormat.md", "recordType=execution;schemaVersion=1;previewStale=True",
                "previewFingerprint", "previewStale", "leftovers.declinedCount"),
            Row("tagfix-apply-result-partial", "TagFixApplyJSONFormat.md", "status=partial",
                "oldFingerprint", "newFingerprint", "journal", "bytesNeeded", "bytesFree", "counts.applied",
                "counts.alreadyDone", "counts.failed", "counts.quarantined", "counts.needsManual", "counts.pendingAfter",
                "failures[]"),
            Row("tagfix-apply-result-files-in-use", "TagFixApplyJSONFormat.md", "status=files_in_use",
                "oldFingerprint", "newFingerprint", "journal", "bytesNeeded", "bytesFree", "counts.applied",
                "counts.alreadyDone", "counts.failed", "counts.quarantined", "counts.needsManual", "counts.pendingAfter",
                "failures[]"),
        };

        private static string[] Row(string name, string contract, string identity, params string[] required)
        {
            return new[] { name, contract, identity }.Concat(required).ToArray();
        }

        private static string FindRepoRoot()
        {
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            while (!string.IsNullOrEmpty(dir))
            {
                if (File.Exists(Path.Combine(dir, "README.md")) && Directory.Exists(Path.Combine(dir, "docs", "Contracts")))
                    return dir;
                dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
            }
            return null;
        }

        private static object Parse(string path)
        {
            var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            return serializer.DeserializeObject(File.ReadAllText(path));
        }

        // True when the dotted path exists. "[]" steps into the first row of a non-empty array.
        private static bool Has(object node, string path)
        {
            object current = node;
            foreach (string segment in path.Split('.'))
            {
                bool rows = segment.EndsWith("[]", StringComparison.Ordinal);
                string key = rows ? segment.Substring(0, segment.Length - 2) : segment;
                var obj = current as Dictionary<string, object>;
                if (obj == null || !obj.ContainsKey(key))
                    return false;
                current = obj[key];
                if (rows)
                {
                    var list = current as object[];
                    if (list == null || list.Length == 0)
                        return false;
                    current = list[0];
                }
            }
            return true;
        }

        public static void EveryGoldenHasItsDocumentedKeysAndIdentity()
        {
            string root = FindRepoRoot();
            Assert.True(root != null, "repo root with README.md and docs/Contracts not found from the test base directory");
            string goldenDir = Path.Combine(root, "tests", "golden");
            foreach (string[] spec in Specs)
            {
                string name = spec[0];
                string file = Path.Combine(goldenDir, name + ".json");
                Assert.True(File.Exists(file), "missing golden " + name + ".json (run scripts\\dev\\golden\\make_goldens.py)");
                var doc = Parse(file) as Dictionary<string, object>;
                Assert.True(doc != null, name + ".json is not a JSON object");

                if (spec[2].Length > 0)
                {
                    foreach (string pair in spec[2].Split(';'))
                    {
                        string[] kv = pair.Split(new[] { '=' }, 2);
                        Assert.True(doc.ContainsKey(kv[0]), name + ": missing top-level key " + kv[0]);
                        string actual = Convert.ToString(doc[kv[0]], CultureInfo.InvariantCulture);
                        Assert.Equal(kv[1], actual, name + "." + kv[0]);
                    }
                }
                for (int i = 3; i < spec.Length; i++)
                    Assert.True(Has(doc, spec[i]), name + ": missing documented key " + spec[i] +
                        " (or the sandbox lacks the case that fills it)");
            }
        }

        public static void EveryContractJsonDocHasAGoldenAndEveryGoldenHasASpec()
        {
            string root = FindRepoRoot();
            Assert.True(root != null, "repo root not found");
            string contracts = Path.Combine(root, "docs", "Contracts");
            string goldenDir = Path.Combine(root, "tests", "golden");
            var specced = Specs.Select(s => s[1]).Distinct().ToList();

            foreach (string doc in Directory.GetFiles(contracts, "*JSON*.md"))
            {
                string contract = Path.GetFileName(doc);
                Assert.True(specced.Contains(contract), "contract " + contract + " has no golden in GoldenContractTests");
            }
            foreach (string contract in specced)
                Assert.True(File.Exists(Path.Combine(contracts, contract)), "spec names a contract doc that is gone: " + contract);
            foreach (string file in Directory.GetFiles(goldenDir, "*.json"))
            {
                string name = Path.GetFileNameWithoutExtension(file);
                Assert.True(Specs.Any(s => s[0] == name), "golden " + name + ".json has no spec row; add it to GoldenContractTests");
            }
        }

        public static void GoldensHoldNoMachinePaths()
        {
            string root = FindRepoRoot();
            Assert.True(root != null, "repo root not found");
            foreach (string file in Directory.GetFiles(Path.Combine(root, "tests", "golden"), "*.json"))
            {
                string text = File.ReadAllText(file);
                string label = Path.GetFileName(file);
                Assert.True(!Regex.IsMatch(text, @"[A-Za-z]:[\\/]"), label + " holds a drive-letter path; normalise it to <ROOT>");
                Assert.True(text.IndexOf("am_golden_", StringComparison.Ordinal) < 0, label + " holds the sandbox folder name");
                Assert.True(text.IndexOf("\\Users\\", StringComparison.OrdinalIgnoreCase) < 0, label + " holds a user profile path");
            }
        }

        private static Dictionary<string, object> LoadGolden(string name)
        {
            string file = Path.Combine(FindRepoRoot(), "tests", "golden", name + ".json");
            Assert.True(File.Exists(file), "missing golden " + name + ".json (run scripts\\dev\\golden\\make_goldens.py)");
            return (Dictionary<string, object>)Parse(file);
        }

        // The blocked record must name the status the gate writes and a projected length at or over the limit.
        public static void PathTooLongGoldenCarriesTheBlockedValues()
        {
            var blocked = LoadGolden("routing-blocked-path-too-long")["blocked"] as Dictionary<string, object>;
            Assert.True(blocked != null, "routing-blocked-path-too-long: blocked is not an object");
            Assert.Equal("path_too_long", (string)blocked["status"], "blocked.status");
            Assert.Equal("rename_or_shorten", (string)blocked["action"], "blocked.action");
            var first = (Dictionary<string, object>)((object[])blocked["files"])[0];
            int length = Convert.ToInt32(first["projectedLength"], CultureInfo.InvariantCulture);
            int limit = Convert.ToInt32(first["limit"], CultureInfo.InvariantCulture);
            Assert.Equal("259", limit.ToString(CultureInfo.InvariantCulture), "blocked.files[].limit");
            Assert.True(length >= limit, "blocked.files[].projectedLength is under the limit");
        }

        // A replaced original is held under the Replaced folder, never deleted.
        public static void ReplacedOriginalsGoldenNamesTheHeldFile()
        {
            var rows = (object[])LoadGolden("execution-replaced-originals")["replacedOriginals"];
            Assert.True(rows.Length > 0, "execution-replaced-originals: replacedOriginals is empty");
            var row = (Dictionary<string, object>)rows[0];
            Assert.True(((string)row["held"]).IndexOf("\\Replaced\\", StringComparison.Ordinal) >= 0,
                "replacedOriginals[].held is not under the Replaced holding folder");
        }

        // partial: a file the apply could not read is a failure, never a silent skip; the rest was applied.
        public static void PartialGoldenNamesTheUnreadableFile()
        {
            var golden = LoadGolden("tagfix-apply-result-partial");
            var failures = (object[])golden["failures"];
            Assert.True(failures.Length > 0, "tagfix-apply-result-partial: failures is empty");
            Assert.True(((string)failures[0]).IndexOf("unreadable, not changed", StringComparison.Ordinal) >= 0,
                "failure text drifted: " + failures[0]);
            var counts = (Dictionary<string, object>)golden["counts"];
            Assert.True(Convert.ToInt32(counts["failed"]) > 0, "tagfix-apply-result-partial: counts.failed is 0");
        }

        // files_in_use names every locked file with the operator instruction the contract documents.
        public static void FilesInUseGoldenNamesTheLockedFile()
        {
            var failures = (object[])LoadGolden("tagfix-apply-result-files-in-use")["failures"];
            Assert.True(failures.Length > 0, "tagfix-apply-result-files-in-use: failures is empty");
            string first = (string)failures[0];
            Assert.True(first.EndsWith(": open in another program; close it and run again", StringComparison.Ordinal),
                "failure text drifted: " + first);
        }
    }
}
