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
    /// Library tag fix: the two safe fixes for files already in the library, with a journal so they can be reverted.
    ///   title_trim - Title has leading or trailing whitespace: trim it, and rename the file to match the trimmed title.
    ///   tcmp       - the real TCMP frame is not set: set it (every library track is a compilation, see TagFixer).
    /// Plan is read-only. Apply re-checks each file against the plan (a changed file is skipped, never guessed at),
    /// never overwrites a rename target, and writes the journal after every file. Revert undoes a journal's entries.
    /// Every path is given explicitly, so the tests run on temp folders. More fixes are added by extending Detect.
    /// </summary>
    internal static class LibraryTagFixer
    {
        internal const string PlanSchema = "libtagfix-plan-1";
        internal const string JournalSchema = "libtagfix-journal-1";
        internal const string PlanFileName = "libtagfix-plan.json";
        internal const string ResultFileName = "libtagfix-result.json";
        internal const string FixTitleTrim = "title_trim";
        internal const string FixTcmp = "tcmp";

        internal class Entry
        {
            public string Path;          // relative to the library root
            public string NewPath;       // relative; equals Path when no rename is needed
            public List<string> Fixes = new List<string>();
            public string OldTitle;
            public string NewTitle;
            public bool OldCompilation;
        }

        internal class Plan
        {
            public string LibraryRoot;
            public List<Entry> Entries = new List<Entry>();
        }

        internal class Result
        {
            public string Operation;     // apply | revert
            public string Status;        // applied | reverted | partial | failed
            public string Message;
            public string JournalPath;
            public int Done;
            public int Skipped;
            public int Failed;
            public List<string> Notes = new List<string>();
        }

        /// <summary>The fixes one file needs, or null when it is fine. Pure on the values.</summary>
        internal static List<string> Detect(string title, bool compilation)
        {
            var fixes = new List<string>();
            if (!string.IsNullOrEmpty(title) && title != title.Trim()) fixes.Add(FixTitleTrim);
            if (!compilation) fixes.Add(FixTcmp);
            return fixes.Count == 0 ? null : fixes;
        }

        internal static Plan BuildPlan(string libraryRoot)
        {
            var plan = new Plan { LibraryRoot = libraryRoot };
            if (!Directory.Exists(libraryRoot)) return plan;
            foreach (string file in Directory.EnumerateFiles(libraryRoot, "*.mp3", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    using (var tf = TagLib.File.Create(file))
                    {
                        var id3 = (TagLib.Id3v2.Tag)tf.GetTag(TagLib.TagTypes.Id3v2, false);
                        bool comp = id3 != null && id3.IsCompilation;
                        string title = tf.Tag.Title ?? "";
                        List<string> fixes = Detect(title, comp);
                        if (fixes == null) continue;
                        string rel = Rel(libraryRoot, file);
                        var e = new Entry { Path = rel, NewPath = rel, Fixes = fixes, OldTitle = title, NewTitle = fixes.Contains(FixTitleTrim) ? title.Trim() : title, OldCompilation = comp };
                        if (fixes.Contains(FixTitleTrim)) e.NewPath = RenamedRel(rel, tf.Tag.JoinedPerformers, e.NewTitle);
                        plan.Entries.Add(e);
                    }
                }
                catch (Exception) { /* unreadable file: LibChecker reports it, this fix leaves it alone */ }
            }
            return plan;
        }

        /// <summary>"Artists - Title.mp3" beside the original; the original name when artists or title sanitise to nothing.</summary>
        private static string RenamedRel(string rel, string artists, string title)
        {
            string a = Reflector.SanitiseFilename((artists ?? "").Replace("/", ";"));
            string t = Reflector.SanitiseFilename(title);
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(t)) return rel;
            string dir = System.IO.Path.GetDirectoryName(rel);
            return System.IO.Path.Combine(dir ?? "", a + " - " + t + ".mp3");
        }

        internal static Result Apply(Plan plan, string journalPath, string libraryRoot)
        {
            var result = new Result { Operation = "apply", JournalPath = journalPath };
            var journal = new Plan { LibraryRoot = libraryRoot };
            foreach (Entry e in plan.Entries)
            {
                string src = System.IO.Path.Combine(libraryRoot, e.Path);
                string dst = System.IO.Path.Combine(libraryRoot, e.NewPath);
                bool rename = !string.Equals(e.Path, e.NewPath, StringComparison.Ordinal);
                try
                {
                    if (!File.Exists(src)) { result.Skipped++; result.Notes.Add("missing: " + e.Path); continue; }
                    if (rename && File.Exists(dst)) { result.Skipped++; result.Notes.Add("rename target exists: " + e.NewPath); continue; }
                    using (var tf = TagLib.File.Create(src))
                    {
                        var id3 = (TagLib.Id3v2.Tag)tf.GetTag(TagLib.TagTypes.Id3v2, true);
                        if ((tf.Tag.Title ?? "") != e.OldTitle || id3.IsCompilation != e.OldCompilation)
                        { result.Skipped++; result.Notes.Add("changed since the plan: " + e.Path); continue; }
                        if (e.Fixes.Contains(FixTitleTrim)) tf.Tag.Title = e.NewTitle;
                        if (e.Fixes.Contains(FixTcmp)) id3.IsCompilation = true;
                        tf.Save();
                    }
                    if (rename) File.Move(src, dst);
                    journal.Entries.Add(e);
                    WriteJson(journalPath, JournalSchema, journal);
                    result.Done++;
                }
                catch (Exception ex)
                {
                    result.Failed++;
                    result.Notes.Add("failed: " + e.Path + ": " + ex.Message);
                }
            }
            result.Status = result.Failed > 0 ? "partial" : "applied";
            result.Message = $"{result.Done} fixed, {result.Skipped} skipped, {result.Failed} failed.";
            return result;
        }

        internal static Result Revert(string journalPath, string libraryRoot)
        {
            var result = new Result { Operation = "revert", JournalPath = journalPath };
            Plan journal = ReadJson(journalPath, JournalSchema);
            foreach (Entry e in journal.Entries)
            {
                string orig = System.IO.Path.Combine(libraryRoot, e.Path);
                string now = System.IO.Path.Combine(libraryRoot, e.NewPath);
                bool rename = !string.Equals(e.Path, e.NewPath, StringComparison.Ordinal);
                try
                {
                    if (!File.Exists(now)) { result.Skipped++; result.Notes.Add("missing: " + e.NewPath); continue; }
                    if (rename && File.Exists(orig)) { result.Skipped++; result.Notes.Add("original name taken: " + e.Path); continue; }
                    using (var tf = TagLib.File.Create(now))
                    {
                        var id3 = (TagLib.Id3v2.Tag)tf.GetTag(TagLib.TagTypes.Id3v2, true);
                        if (e.Fixes.Contains(FixTitleTrim) && tf.Tag.Title == e.NewTitle) tf.Tag.Title = e.OldTitle;
                        if (e.Fixes.Contains(FixTcmp)) id3.IsCompilation = e.OldCompilation;
                        tf.Save();
                    }
                    if (rename) File.Move(now, orig);
                    result.Done++;
                }
                catch (Exception ex)
                {
                    result.Failed++;
                    result.Notes.Add("failed: " + e.NewPath + ": " + ex.Message);
                }
            }
            result.Status = result.Failed > 0 || result.Skipped > 0 ? "partial" : "reverted";
            result.Message = $"{result.Done} reverted, {result.Skipped} skipped, {result.Failed} failed.";
            return result;
        }

        // ---- json ----

        private static string Rel(string root, string file) =>
            file.Substring(root.TrimEnd('\\', '/').Length).TrimStart('\\', '/');

        internal static string JournalFileName(DateTime now) =>
            "libtagfix-journal-" + now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".json";

        internal static void WriteJson(string path, string schema, Plan p)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path) ?? ".");
            var doc = new Dictionary<string, object>
            {
                ["schema"] = schema,
                ["libraryRoot"] = p.LibraryRoot,
                ["entries"] = p.Entries.Select(e => new Dictionary<string, object>
                {
                    ["path"] = e.Path, ["newPath"] = e.NewPath, ["fixes"] = e.Fixes,
                    ["oldTitle"] = e.OldTitle, ["newTitle"] = e.NewTitle, ["oldCompilation"] = e.OldCompilation
                }).ToList()
            };
            var ser = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            AtomicFile.WriteAllText(path, ser.Serialize(doc), new UTF8Encoding(false));
        }

        internal static Plan ReadJson(string path, string schema)
        {
            var ser = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            var root = ser.Deserialize<Dictionary<string, object>>(File.ReadAllText(path, Encoding.UTF8));
            if (!root.ContainsKey("schema") || (string)root["schema"] != schema)
                throw new InvalidDataException("not a " + schema + " file: " + path);
            var p = new Plan { LibraryRoot = root.ContainsKey("libraryRoot") ? (string)root["libraryRoot"] : "" };
            foreach (object o in (System.Collections.IEnumerable)root["entries"])
            {
                var d = (Dictionary<string, object>)o;
                var e = new Entry
                {
                    Path = (string)d["path"], NewPath = (string)d["newPath"],
                    OldTitle = (string)d["oldTitle"], NewTitle = (string)d["newTitle"], OldCompilation = (bool)d["oldCompilation"]
                };
                foreach (object f in (System.Collections.IEnumerable)d["fixes"]) e.Fixes.Add((string)f);
                p.Entries.Add(e);
            }
            return p;
        }
    }
}
