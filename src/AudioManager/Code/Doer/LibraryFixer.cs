using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace AudioManager
{
    /// <summary>
    /// Library fix for the LibChecker hit album_in_singles: an album whose songs sit in Artists/{artist}/Singles/
    /// while the album has enough songs for its own folder (RulesEngine.UsesAlbumSubfolder). The fix moves those
    /// Singles songs into Artists/{artist}/{album}/.
    ///
    /// Three steps, each a separate command: Plan (read-only, writes libfix-plan.json), Apply (moves files, writes
    /// a journal after every move), Revert (moves journaled files back, never overwrites, removes folders it created
    /// only when they are empty). Every path is given explicitly, so the tests run on temp folders. Nothing here
    /// reads Constants, NewMusic or the mirror, and nothing touches a path outside &lt;libraryRoot&gt;\Artists.
    /// </summary>
    internal static class LibraryFixer
    {
        internal const string PlanSchema = "libfix-plan-1";
        internal const string JournalSchema = "libfix-journal-1";
        internal const string ResultSchema = "libfix-result-1";
        internal const string KindAlbumInSingles = "album_in_singles";
        internal const string PlanFileName = "libfix-plan.json";
        internal const string ResultFileName = "libfix-result.json";

        private const string ArtistsDirName = "Artists";
        private const string SinglesDirName = "Singles";

        internal class PlannedMove
        {
            public string From;
            public string To;
            public string Artist;
            public string Album;
            public bool CreatesFolder;
        }

        internal class SkippedMove
        {
            public string From;
            public string To;
            public string Reason;
        }

        internal class FixPlan
        {
            public string LibraryRoot;
            public DateTime Generated;
            public List<PlannedMove> Moves = new List<PlannedMove>();
            public List<SkippedMove> Skipped = new List<SkippedMove>();
        }

        internal class JournalMove
        {
            public string From;
            public string To;
            // Relative folder this move created, or null when the destination folder already existed.
            public string CreatedFolder;
        }

        internal class FixJournal
        {
            public string LibraryRoot;
            public DateTime Generated;
            public List<JournalMove> Moves = new List<JournalMove>();
        }

        internal class FixResult
        {
            // "apply" or "revert".
            public string Operation;
            // apply: applied | stale_plan | refused | failed. revert: reverted | partial | refused | failed.
            public string Status;
            public string Message;
            public string JournalPath;
            public int Moved;
            public int Reverted;
            public int Skipped;
            public int Failed;
            public int FoldersRemoved;
            public List<string> Notes = new List<string>();
        }

        /// <summary>
        /// Plans the album_in_singles moves for one library. For each artist, groups the Singles songs by normalised
        /// album tag, counts the songs already in the album folder plus the Singles songs of that album, and plans a
        /// move for every Singles song of an album that reaches the subfolder threshold. Read-only.
        /// </summary>
        internal static FixPlan BuildAlbumInSinglesPlan(string libraryRoot)
        {
            var plan = new FixPlan { LibraryRoot = libraryRoot, Generated = DateTime.Now };
            string artistsRoot = Path.Combine(libraryRoot, ArtistsDirName);
            if (!Directory.Exists(artistsRoot)) return plan;

            foreach (string artistDir in SortedDirectories(artistsRoot))
            {
                string artist = Path.GetFileName(artistDir);
                string singlesDir = Path.Combine(artistDir, SinglesDirName);
                if (!Directory.Exists(singlesDir)) continue;

                foreach (var group in SinglesByAlbum(singlesDir))
                {
                    string album = group.Key;
                    string existingFolder = FindAlbumFolder(artistDir, album);
                    int existingSongs = existingFolder != null
                        ? Directory.GetFiles(existingFolder, "*.mp3", SearchOption.AllDirectories).Length
                        : 0;
                    if (!RulesEngine.UsesAlbumSubfolder(existingSongs + group.Value.Count)) continue;

                    string folderName = existingFolder != null
                        ? Path.GetFileName(existingFolder)
                        : MusicIntegrator.SanitiseFolderName(album);
                    if (string.IsNullOrWhiteSpace(folderName) || IsSinglesName(folderName)) continue;

                    foreach (string songPath in group.Value)
                    {
                        string fileName = Path.GetFileName(songPath);
                        string from = Path.Combine(ArtistsDirName, artist, SinglesDirName, fileName);
                        string to = Path.Combine(ArtistsDirName, artist, folderName, fileName);
                        string toAbs = Path.Combine(artistDir, folderName, fileName);
                        if (File.Exists(toAbs))
                        {
                            plan.Skipped.Add(new SkippedMove { From = from, To = to, Reason = "destination file already exists" });
                            continue;
                        }
                        plan.Moves.Add(new PlannedMove
                        {
                            From = from,
                            To = to,
                            Artist = artist,
                            Album = album,
                            CreatesFolder = existingFolder == null
                        });
                    }
                }
            }

            plan.Moves.Sort((a, b) => string.Compare(a.From, b.From, StringComparison.OrdinalIgnoreCase));
            plan.Skipped.Sort((a, b) => string.Compare(a.From, b.From, StringComparison.OrdinalIgnoreCase));
            return plan;
        }

        /// <summary>Writes the plan JSON atomically. Creates the folder when needed.</summary>
        internal static void WritePlan(FixPlan plan, string path)
        {
            CreateFolderFor(path);
            AtomicFile.WriteAllText(path, PlanJson(plan), new UTF8Encoding(false));
        }

        /// <summary>Reads a plan written by WritePlan. Throws InvalidDataException when the file is not a libfix plan.</summary>
        internal static FixPlan ReadPlan(string path)
        {
            Dictionary<string, object> root = ParseRoot(path, PlanSchema);
            if (Str(root, "kind") != KindAlbumInSingles)
                throw new InvalidDataException("not an album_in_singles plan: " + path);

            var plan = new FixPlan { LibraryRoot = Str(root, "libraryRoot"), Generated = ParseDate(root) };
            foreach (Dictionary<string, object> m in Objects(root, "moves"))
            {
                plan.Moves.Add(new PlannedMove
                {
                    From = RequiredStr(m, "from", path),
                    To = RequiredStr(m, "to", path),
                    Artist = Str(m, "artist"),
                    Album = Str(m, "album"),
                    CreatesFolder = Bool(m, "createsFolder")
                });
            }
            foreach (Dictionary<string, object> s in Objects(root, "skipped"))
            {
                plan.Skipped.Add(new SkippedMove
                {
                    From = RequiredStr(s, "from", path),
                    To = Str(s, "to"),
                    Reason = Str(s, "reason")
                });
            }
            if (plan.LibraryRoot == null) throw new InvalidDataException("plan has no libraryRoot: " + path);
            return plan;
        }

        /// <summary>
        /// Moves every planned file. Refuses when the plan was made for another libraryRoot (refused), or when any
        /// source is missing or any destination exists (stale_plan), checked for all moves before the first move.
        /// The journal is written before the first move and again after each move, so a failure leaves a record.
        /// </summary>
        internal static FixResult Apply(string planPath, string journalPath, string libraryRoot)
        {
            var result = new FixResult { Operation = "apply", JournalPath = journalPath };

            FixPlan plan;
            try { plan = ReadPlan(planPath); }
            catch (Exception ex)
            {
                return Finish(result, "failed", "could not read plan: " + ex.Message, failed: 1);
            }

            if (!SameRoot(plan.LibraryRoot, libraryRoot))
                return Finish(result, "refused", "plan was made for a different library root; nothing was moved");

            foreach (PlannedMove m in plan.Moves)
            {
                if (!UnderArtists(libraryRoot, m.From) || !UnderArtists(libraryRoot, m.To))
                    return Finish(result, "refused", "plan names a path outside Artists: " + m.From + " -> " + m.To);
            }

            foreach (PlannedMove m in plan.Moves)
            {
                if (!File.Exists(Path.Combine(libraryRoot, m.From)))
                    return Finish(result, "stale_plan", "source missing, plan is stale: " + m.From);
                if (File.Exists(Path.Combine(libraryRoot, m.To)))
                    return Finish(result, "stale_plan", "destination exists, plan is stale: " + m.To);
            }

            var journal = new FixJournal { LibraryRoot = libraryRoot, Generated = DateTime.Now };
            try { WriteJournal(journal, journalPath); }
            catch (Exception ex)
            {
                return Finish(result, "failed", "could not write journal: " + ex.Message, failed: 1);
            }

            foreach (PlannedMove m in plan.Moves)
            {
                string absFrom = Path.Combine(libraryRoot, m.From);
                string absTo = Path.Combine(libraryRoot, m.To);
                string destDir = Path.GetDirectoryName(absTo);
                string createdFolder = null;
                try
                {
                    if (!Directory.Exists(destDir))
                    {
                        Directory.CreateDirectory(destDir);
                        createdFolder = Path.GetDirectoryName(m.To);
                    }
                    File.Move(absFrom, absTo);
                }
                catch (Exception ex)
                {
                    if (createdFolder != null) TryRemoveEmptyFolder(destDir);
                    result.Moved = journal.Moves.Count;
                    return Finish(result, "failed", "move failed at " + m.From + ": " + ex.Message, failed: 1);
                }

                journal.Moves.Add(new JournalMove { From = m.From, To = m.To, CreatedFolder = createdFolder });
                try { WriteJournal(journal, journalPath); }
                catch (Exception ex)
                {
                    result.Moved = journal.Moves.Count;
                    return Finish(result, "failed", "moved " + m.From + " but could not update the journal: " + ex.Message, failed: 1);
                }
            }

            result.Moved = journal.Moves.Count;
            return Finish(result, "applied", "moved " + result.Moved + " file(s)");
        }

        /// <summary>
        /// Moves each journaled file back, newest first. A file is moved only when its destination exists and its
        /// original path is free; otherwise it is skipped with a note. Folders the apply created are removed only
        /// when empty. Status reverted when everything went back, partial otherwise.
        /// </summary>
        internal static FixResult Revert(string journalPath, string libraryRoot)
        {
            var result = new FixResult { Operation = "revert", JournalPath = journalPath };

            FixJournal journal;
            try { journal = ReadJournal(journalPath); }
            catch (Exception ex)
            {
                return Finish(result, "failed", "could not read journal: " + ex.Message, failed: 1);
            }

            if (!SameRoot(journal.LibraryRoot, libraryRoot))
                return Finish(result, "refused", "journal was written for a different library root; nothing was moved");

            foreach (JournalMove m in journal.Moves)
            {
                if (!UnderArtists(libraryRoot, m.From) || !UnderArtists(libraryRoot, m.To))
                    return Finish(result, "refused", "journal names a path outside Artists: " + m.From + " -> " + m.To);
            }

            for (int i = journal.Moves.Count - 1; i >= 0; i--)
            {
                JournalMove m = journal.Moves[i];
                string absFrom = Path.Combine(libraryRoot, m.From);
                string absTo = Path.Combine(libraryRoot, m.To);
                if (!File.Exists(absTo))
                {
                    result.Skipped++;
                    result.Notes.Add("not present, not moved back: " + m.To);
                    continue;
                }
                if (File.Exists(absFrom))
                {
                    result.Skipped++;
                    result.Notes.Add("original path occupied, not overwritten: " + m.From);
                    continue;
                }
                try
                {
                    CreateFolderFor(absFrom);
                    File.Move(absTo, absFrom);
                    result.Reverted++;
                }
                catch (Exception ex)
                {
                    result.Failed++;
                    result.Notes.Add("revert failed for " + m.To + ": " + ex.Message);
                }
            }

            for (int i = journal.Moves.Count - 1; i >= 0; i--)
            {
                string folder = journal.Moves[i].CreatedFolder;
                if (folder == null) continue;
                string absFolder = Path.Combine(libraryRoot, folder);
                if (Directory.Exists(absFolder) && TryRemoveEmptyFolder(absFolder)) result.FoldersRemoved++;
            }

            string status = result.Skipped == 0 && result.Failed == 0 ? "reverted" : "partial";
            return Finish(result, status, "reverted " + result.Reverted + " file(s), skipped " + result.Skipped);
        }

        /// <summary>Writes the result JSON atomically (apply and revert only; plan writes no result).</summary>
        internal static void WriteResult(FixResult r, string path, DateTime generated)
        {
            CreateFolderFor(path);
            var sb = new StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine($"  \"schema\": {JsonText.Quote(ResultSchema)},");
            sb.AppendLine($"  \"operation\": {JsonText.Quote(r.Operation)},");
            sb.AppendLine($"  \"generated\": {JsonText.Quote(generated.ToString("s", CultureInfo.InvariantCulture))},");
            sb.AppendLine($"  \"status\": {JsonText.Quote(r.Status)},");
            sb.AppendLine($"  \"message\": {JsonText.Quote(r.Message)},");
            sb.AppendLine($"  \"journal\": {JsonText.Quote(r.JournalPath)},");
            sb.AppendLine($"  \"counts\": {{\"moved\": {r.Moved}, \"reverted\": {r.Reverted}, \"skipped\": {r.Skipped}, \"failed\": {r.Failed}, \"foldersRemoved\": {r.FoldersRemoved}}},");
            sb.AppendLine(r.Notes.Count == 0 ? "  \"notes\": []" : "  \"notes\": [");
            for (int i = 0; i < r.Notes.Count; i++)
                sb.AppendLine($"    {JsonText.Quote(r.Notes[i])}{(i < r.Notes.Count - 1 ? "," : "")}");
            if (r.Notes.Count > 0) sb.AppendLine("  ]");
            sb.AppendLine("}");
            AtomicFile.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        }

        /// <summary>Journal file name for one apply run: libfix-journal-yyyyMMdd-HHmmss.json.</summary>
        internal static string JournalFileName(DateTime now)
        {
            return "libfix-journal-" + now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".json";
        }

        internal static FixJournal ReadJournal(string path)
        {
            Dictionary<string, object> root = ParseRoot(path, JournalSchema);
            var journal = new FixJournal { LibraryRoot = Str(root, "libraryRoot"), Generated = ParseDate(root) };
            foreach (Dictionary<string, object> m in Objects(root, "moves"))
            {
                journal.Moves.Add(new JournalMove
                {
                    From = RequiredStr(m, "from", path),
                    To = RequiredStr(m, "to", path),
                    CreatedFolder = Str(m, "createdFolder")
                });
            }
            if (journal.LibraryRoot == null) throw new InvalidDataException("journal has no libraryRoot: " + path);
            return journal;
        }

        // ---- planning helpers ----

        /// <summary>Singles songs of one artist keyed by normalised album tag. Unreadable or untagged files are skipped.</summary>
        private static SortedDictionary<string, List<string>> SinglesByAlbum(string singlesDir)
        {
            var byAlbum = new SortedDictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (string file in Directory.GetFiles(singlesDir, "*.mp3"))
            {
                string fileAlbum;
                try
                {
                    using (TagLib.File tf = TagLib.File.Create(file))
                        fileAlbum = tf.Tag.Album;
                }
                catch { continue; }
                if (string.IsNullOrEmpty(fileAlbum)) continue;
                string key = RulesEngine.NormalizeAlbum(fileAlbum);
                if (string.IsNullOrWhiteSpace(key)) continue;
                List<string> files;
                if (!byAlbum.TryGetValue(key, out files))
                {
                    files = new List<string>();
                    byAlbum[key] = files;
                }
                files.Add(file);
            }
            foreach (var files in byAlbum.Values) files.Sort(StringComparer.OrdinalIgnoreCase);
            return byAlbum;
        }

        /// <summary>
        /// Existing album folder for an album in one artist folder: the exact sanitised name first, else a subfolder
        /// whose normalised name equals the album (case-insensitive). Never Singles. Null when there is none.
        /// </summary>
        private static string FindAlbumFolder(string artistDir, string album)
        {
            string exact = Path.Combine(artistDir, MusicIntegrator.SanitiseFolderName(album));
            if (Directory.Exists(exact) && !IsSinglesName(Path.GetFileName(exact))) return exact;
            foreach (string dir in SortedDirectories(artistDir))
            {
                string name = Path.GetFileName(dir);
                if (IsSinglesName(name)) continue;
                if (string.Equals(RulesEngine.NormalizeAlbum(name), album, StringComparison.OrdinalIgnoreCase)) return dir;
            }
            return null;
        }

        private static bool IsSinglesName(string name)
        {
            return string.Equals(name, SinglesDirName, StringComparison.OrdinalIgnoreCase);
        }

        private static List<string> SortedDirectories(string parent)
        {
            var dirs = Directory.GetDirectories(parent).ToList();
            dirs.Sort(StringComparer.OrdinalIgnoreCase);
            return dirs;
        }

        // ---- scope and filesystem helpers ----

        /// <summary>True when rel (relative to libraryRoot) resolves to a path strictly under libraryRoot\Artists.</summary>
        private static bool UnderArtists(string libraryRoot, string rel)
        {
            if (string.IsNullOrWhiteSpace(rel)) return false;
            string artistsPrefix = Path.GetFullPath(Path.Combine(libraryRoot, ArtistsDirName)).TrimEnd('\\') + "\\";
            string full = Path.GetFullPath(Path.Combine(libraryRoot, rel));
            return full.StartsWith(artistsPrefix, StringComparison.OrdinalIgnoreCase);
        }

        private static bool SameRoot(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            return string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        }

        private static void CreateFolderFor(string filePath)
        {
            string dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        }

        /// <summary>Removes the folder only when it has no entries. Returns true when removed.</summary>
        private static bool TryRemoveEmptyFolder(string dir)
        {
            try
            {
                if (!Directory.Exists(dir)) return false;
                if (Directory.GetFileSystemEntries(dir).Length != 0) return false;
                Directory.Delete(dir);
                return true;
            }
            catch { return false; }
        }

        private static FixResult Finish(FixResult result, string status, string message, int failed = 0)
        {
            result.Status = status;
            result.Message = message;
            if (failed > 0) result.Failed = failed;
            return result;
        }

        // ---- JSON writing (hand-built, same style as TagFixApplier and TagFixPlanJson) ----

        private static string PlanJson(FixPlan plan)
        {
            var sb = new StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine($"  \"schema\": {JsonText.Quote(PlanSchema)},");
            sb.AppendLine($"  \"kind\": {JsonText.Quote(KindAlbumInSingles)},");
            sb.AppendLine($"  \"libraryRoot\": {JsonText.Quote(plan.LibraryRoot)},");
            sb.AppendLine($"  \"generated\": {JsonText.Quote(plan.Generated.ToString("s", CultureInfo.InvariantCulture))},");
            sb.AppendLine(plan.Moves.Count == 0 ? "  \"moves\": []," : "  \"moves\": [");
            for (int i = 0; i < plan.Moves.Count; i++)
            {
                PlannedMove m = plan.Moves[i];
                sb.AppendLine($"    {{\"from\": {JsonText.Quote(m.From)}, \"to\": {JsonText.Quote(m.To)}, \"artist\": {JsonText.Quote(m.Artist)}, \"album\": {JsonText.Quote(m.Album)}, \"createsFolder\": {(m.CreatesFolder ? "true" : "false")}}}{(i < plan.Moves.Count - 1 ? "," : "")}");
            }
            if (plan.Moves.Count > 0) sb.AppendLine("  ],");
            sb.AppendLine(plan.Skipped.Count == 0 ? "  \"skipped\": []," : "  \"skipped\": [");
            for (int i = 0; i < plan.Skipped.Count; i++)
            {
                SkippedMove s = plan.Skipped[i];
                sb.AppendLine($"    {{\"from\": {JsonText.Quote(s.From)}, \"to\": {JsonText.Quote(s.To)}, \"reason\": {JsonText.Quote(s.Reason)}}}{(i < plan.Skipped.Count - 1 ? "," : "")}");
            }
            if (plan.Skipped.Count > 0) sb.AppendLine("  ],");
            sb.AppendLine($"  \"counts\": {{\"moves\": {plan.Moves.Count}, \"skipped\": {plan.Skipped.Count}}}");
            sb.AppendLine("}");
            return sb.ToString();
        }

        private static void WriteJournal(FixJournal journal, string path)
        {
            CreateFolderFor(path);
            var sb = new StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine($"  \"schema\": {JsonText.Quote(JournalSchema)},");
            sb.AppendLine($"  \"libraryRoot\": {JsonText.Quote(journal.LibraryRoot)},");
            sb.AppendLine($"  \"generated\": {JsonText.Quote(journal.Generated.ToString("s", CultureInfo.InvariantCulture))},");
            sb.AppendLine(journal.Moves.Count == 0 ? "  \"moves\": []" : "  \"moves\": [");
            for (int i = 0; i < journal.Moves.Count; i++)
            {
                JournalMove m = journal.Moves[i];
                sb.AppendLine($"    {{\"from\": {JsonText.Quote(m.From)}, \"to\": {JsonText.Quote(m.To)}, \"createdFolder\": {JsonText.Quote(m.CreatedFolder)}}}{(i < journal.Moves.Count - 1 ? "," : "")}");
            }
            if (journal.Moves.Count > 0) sb.AppendLine("  ]");
            sb.AppendLine("}");
            AtomicFile.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        }

        // ---- JSON reading (System.Web.Script.Serialization, as TagFixApplier reads its journal) ----

        private static Dictionary<string, object> ParseRoot(string path, string expectedSchema)
        {
            var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            var root = serializer.DeserializeObject(File.ReadAllText(path, Encoding.UTF8)) as Dictionary<string, object>;
            if (root == null) throw new InvalidDataException("not a JSON object: " + path);
            if (Str(root, "schema") != expectedSchema) throw new InvalidDataException("unexpected schema in " + path);
            return root;
        }

        private static string Str(Dictionary<string, object> d, string key)
        {
            object value;
            return d.TryGetValue(key, out value) ? value as string : null;
        }

        private static bool Bool(Dictionary<string, object> d, string key)
        {
            object value;
            return d.TryGetValue(key, out value) && value is bool && (bool)value;
        }

        private static string RequiredStr(Dictionary<string, object> d, string key, string path)
        {
            string value = Str(d, key);
            if (string.IsNullOrEmpty(value)) throw new InvalidDataException("entry has no " + key + ": " + path);
            return value;
        }

        private static DateTime ParseDate(Dictionary<string, object> root)
        {
            DateTime generated;
            return DateTime.TryParse(Str(root, "generated"), CultureInfo.InvariantCulture, DateTimeStyles.None, out generated)
                ? generated : DateTime.MinValue;
        }

        private static IEnumerable<Dictionary<string, object>> Objects(Dictionary<string, object> root, string key)
        {
            object value;
            if (!root.TryGetValue(key, out value) || value == null) yield break;
            var list = value as System.Collections.IEnumerable;
            if (list == null) yield break;
            foreach (object item in list)
            {
                var d = item as Dictionary<string, object>;
                if (d != null) yield return d;
            }
        }
    }
}
