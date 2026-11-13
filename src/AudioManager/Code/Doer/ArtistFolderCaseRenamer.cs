using System;
using System.Collections.Generic;
using System.IO;
using File = System.IO.File;

namespace AudioManager
{
    /// <summary>
    /// Plans, applies (journaled before the first rename) and reverts case-only renames of Artists folders.
    /// MusicIntegrator forwards to this class.
    /// </summary>
    internal sealed class ArtistFolderCaseRenamer
    {
        private readonly Func<string> _libraryPathSource;
        private readonly Func<IntegrationRoots> _rootsSource;
        private readonly Func<string> _journalDirSource;

        /// <summary>Library path, roots and journal folder are read live on every use, never copied.</summary>
        internal ArtistFolderCaseRenamer(Func<string> libraryPath, Func<IntegrationRoots> roots, Func<string> journalDir)
        {
            _libraryPathSource = libraryPath;
            _rootsSource = roots;
            _journalDirSource = journalDir;
        }

        private string _libraryPath => _libraryPathSource();
        private IntegrationRoots _roots => _rootsSource();
        private string MigrationJournalDir => _journalDirSource();

        /// <summary>Existing Artists folders whose name differs from the tag's folder name ONLY by case, as {on-disk name, tag spelling}. Never plans a rename across different names.</summary>
        internal List<KeyValuePair<string, string>> PlanArtistFolderCaseRenames(IEnumerable<string> primaryTags)
        {
            var plan = new List<KeyValuePair<string, string>>();
            string root = Path.Combine(_libraryPath, Constants.ArtistsDir);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string tag in primaryTags)
            {
                if (string.IsNullOrEmpty(tag)) continue;
                string want = MusicIntegrator.SanitiseFolderName(tag);
                string onDisk = FindDirNameIgnoreCase(root, want);
                if (onDisk != null && !onDisk.Equals(want, StringComparison.Ordinal)
                    && onDisk.Equals(want, StringComparison.OrdinalIgnoreCase) && seen.Add(onDisk))
                    plan.Add(new KeyValuePair<string, string>(onDisk, want));
            }
            return plan;
        }

        /// <summary>Case-only renames under Artists, journaled BEFORE the first rename (artist-folder-rename-*.json). Returns the journal path, or null when the plan is empty.</summary>
        internal string ApplyArtistFolderCaseRenames(List<KeyValuePair<string, string>> plan)
        {
            if (plan == null || plan.Count == 0) return null;
            string root = Path.Combine(_libraryPath, Constants.ArtistsDir);
            string dir = MigrationJournalDir ?? _roots.Logs;
            Directory.CreateDirectory(dir);
            string journal = Path.Combine(dir, "artist-folder-rename-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + ".json");
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("{\"recordType\": \"artistFolderCaseRename\", \"schemaVersion\": 1, \"root\": " + JsonText.Quote(root) + ", \"renames\": [");
            for (int i = 0; i < plan.Count; i++)
                sb.AppendLine($"  {{\"from\": {JsonText.Quote(plan[i].Key)}, \"to\": {JsonText.Quote(plan[i].Value)}}}" + (i < plan.Count - 1 ? "," : ""));
            sb.AppendLine("]}");
            AtomicFile.WriteAllText(journal, sb.ToString(), new System.Text.UTF8Encoding(false));
            foreach (var r in plan) CaseOnlyFolderMove(root, r.Key, r.Value);
            return journal;
        }

        /// <summary>Undoes ApplyArtistFolderCaseRenames from its journal: each folder goes back to its old spelling.</summary>
        internal void RevertArtistFolderCaseRenames(string journalPath)
        {
            string text = File.ReadAllText(journalPath);
            var rootMatch = System.Text.RegularExpressions.Regex.Match(text, "\"root\": \"((?:[^\"\\\\]|\\\\.)*)\"");
            if (!rootMatch.Success) throw new InvalidDataException("not an artist folder rename journal: " + journalPath);
            string root = rootMatch.Groups[1].Value.Replace("\\\\", "\\");
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(text, "\"from\": \"([^\"]*)\", \"to\": \"([^\"]*)\""))
                CaseOnlyFolderMove(root, m.Groups[2].Value, m.Groups[1].Value);
        }

        /// <summary>Case-only folder rename through a temporary name (a direct move onto itself is a no-op on Windows).</summary>
        internal static void CaseOnlyFolderMove(string root, string from, string to)
        {
            if (!from.Equals(to, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("not a case-only rename: " + from + " -> " + to);
            string src = Path.Combine(root, from);
            if (!Directory.Exists(src)) return;
            string tmp = Path.Combine(root, to + ".casetmp-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.Move(src, tmp);
            Directory.Move(tmp, Path.Combine(root, to));
        }

        /// <summary>The on-disk name of a child folder of parent matching name ignoring case, or null.</summary>
        internal static string FindDirNameIgnoreCase(string parent, string name)
        {
            if (!Directory.Exists(parent)) return null;
            foreach (string d in Directory.GetDirectories(parent))
            {
                string n = Path.GetFileName(d);
                if (n.Equals(name, StringComparison.OrdinalIgnoreCase)) return n;
            }
            // Windows normalises some names (a trailing dot is dropped), so the folder exists though no listed name matches.
            return Directory.Exists(Path.Combine(parent, name)) ? name : null;
        }
    }
}
