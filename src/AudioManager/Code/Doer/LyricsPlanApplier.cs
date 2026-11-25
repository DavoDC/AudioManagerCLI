using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace AudioManager
{
    /// <summary>
    /// Guarded apply of a verified lyrics plan (version 1), plus its revert. Writes the ID3 USLT frame only.
    ///
    /// Same safety shape as TagFixApplier: dry-run is the default (a real write needs apply=true and creates no
    /// file at all otherwise), one apply at a time (lock file), a journal is written BEFORE the first change and
    /// rewritten after every file, every file is byte-copied to the backup folder before it is written, and old
    /// backups are pruned at the START of the next real apply. Only action=write rows are acted on, a row whose
    /// size or mtime differs from the plan is skipped as stale, and a file that already has a USLT is never
    /// overwritten. Revert restores the journalled old USLT only while the current USLT still equals the
    /// journal's new value.
    /// Contract: docs/Contracts/LyricsPlanApplyFormat.md. Bump SchemaVersion on a breaking change.
    /// </summary>
    internal static class LyricsPlanApplier
    {
        public const int SchemaVersion = 1;
        public const int PlanVersion = 1;
        public const string ResultFileName = "lyrics-result.json";
        public const string LockFileName = "lyrics-apply.lock";
        public const int BackupRetentionDays = 7;

        internal class ApplyResult
        {
            public string Status;   // applied | partial | nothing_to_do | dry_run | bad_plan | apply_locked (no result file for dry_run or apply_locked)
            public int Written, SkippedStale, HasLyrics, SkippedOther, Failed, WouldWrite;
            public string JournalPath;
            public List<string> Failures = new List<string>();
            public List<string> Notes = new List<string>();
        }

        internal class RevertResult
        {
            public int Reverted, SkippedModified, Failed;
            public List<string> Notes = new List<string>();
        }

        private class PlanRow
        {
            public string Path, Action, Lyrics, Mtime;
            public long Size;
        }

        private class JournalEntry
        {
            public string File, OldLyrics, NewLyrics, Backup, Status, Detail;
        }

        /// <summary>
        /// Applies the plan at planPath to the library at libraryRoot. apply=false (the default of the CLI) only counts
        /// what would be written and touches nothing.
        /// </summary>
        public static ApplyResult Apply(string planPath, string libraryRoot, string logsDir, string backupRoot, DateTime now, bool apply)
        {
            if (!apply) return ApplyUnderLock(planPath, libraryRoot, logsDir, backupRoot, now, false);

            string lockPath = Path.Combine(logsDir, LockFileName);
            FileStream lockStream = TagFixApplier.TryAcquireLock(logsDir, LockFileName);
            if (lockStream == null)
            {
                var locked = new ApplyResult { Status = "apply_locked" };
                locked.Failures.Add("another lyrics apply is running, or a stale lock file is at " + lockPath + " that could not be removed; nothing was changed");
                return locked;
            }
            try { return ApplyUnderLock(planPath, libraryRoot, logsDir, backupRoot, now, true); }
            finally
            {
                lockStream.Dispose();
                try { File.Delete(lockPath); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        private static ApplyResult ApplyUnderLock(string planPath, string libraryRoot, string logsDir, string backupRoot, DateTime now, bool apply)
        {
            var result = new ApplyResult();
            List<PlanRow> rows;
            string planError = ReadPlan(planPath, out rows);
            if (planError != null)
            {
                result.Status = "bad_plan";
                result.Failures.Add(planError);
                if (apply) WriteResult(logsDir, result, planPath, now);
                return result;
            }

            // Classify every row first, with no side effects: nothing is journalled or copied for a row that is skipped.
            var toWrite = new List<KeyValuePair<PlanRow, string>>();
            foreach (var row in rows)
            {
                if (!string.Equals(row.Action, "write", StringComparison.Ordinal)) { result.SkippedOther++; continue; }
                if (string.IsNullOrWhiteSpace(row.Lyrics)) { result.SkippedOther++; result.Notes.Add(row.Path + ": write row has no lyrics"); continue; }
                string full = ResolveInside(libraryRoot, row.Path);
                if (full == null) { result.SkippedOther++; result.Notes.Add(row.Path + ": path is not inside the library, skipped"); continue; }
                if (!File.Exists(full)) { result.SkippedOther++; result.Notes.Add(row.Path + ": file missing, skipped"); continue; }
                if (IsStale(full, row)) { result.SkippedStale++; continue; }
                try
                {
                    string current = ReadUslt(full);
                    if (!string.IsNullOrWhiteSpace(current)) { result.HasLyrics++; continue; }
                }
                catch (Exception ex)
                {
                    result.Failed++; result.Failures.Add(row.Path + ": unreadable, not changed: " + ex.Message);
                    continue;
                }
                toWrite.Add(new KeyValuePair<PlanRow, string>(row, full));
            }

            if (!apply)
            {
                result.WouldWrite = toWrite.Count;
                result.Status = "dry_run";
                return result;
            }
            if (toWrite.Count == 0)
            {
                result.Status = result.Failed == 0 ? "nothing_to_do" : "partial";
                WriteResult(logsDir, result, planPath, now);
                return result;
            }

            PruneOldBackups(backupRoot, logsDir, now);

            string stamp = now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            string runBackup = Path.Combine(backupRoot, stamp);
            Directory.CreateDirectory(logsDir);
            result.JournalPath = Path.Combine(logsDir, "lyrics-journal-" + stamp + ".json");

            // Journal first: old and new USLT of every file, all pending, before the first byte changes.
            var entries = new List<JournalEntry>();
            foreach (var kv in toWrite)
            {
                string old = null;
                try { old = ReadUslt(kv.Value); } catch (Exception) { }
                entries.Add(new JournalEntry
                {
                    File = kv.Key.Path.Replace('\\', '/'), OldLyrics = old, NewLyrics = kv.Key.Lyrics,
                    Backup = Path.Combine(runBackup, kv.Key.Path.Replace('/', Path.DirectorySeparatorChar)), Status = "pending",
                });
            }
            WriteJournal(result.JournalPath, libraryRoot, entries, now);

            for (int i = 0; i < toWrite.Count; i++)
            {
                var e = entries[i];
                string full = toWrite[i].Value;
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(e.Backup));
                    // Never overwrite an existing backup: it holds the earlier original bytes.
                    if (!File.Exists(e.Backup)) File.Copy(full, e.Backup, false);
                    WriteUslt(full, e.NewLyrics);
                    e.Status = "done"; result.Written++;
                }
                catch (Exception ex)
                {
                    e.Status = "failed"; e.Detail = ex.Message;
                    result.Failed++; result.Failures.Add(e.File + ": " + ex.Message);
                }
                WriteJournal(result.JournalPath, libraryRoot, entries, now);
            }

            result.Status = result.Failed == 0 ? "applied" : "partial";
            WriteResult(logsDir, result, planPath, now);
            return result;
        }

        /// <summary>Reverts a journal written by Apply. Never touches a file whose USLT changed since the apply.</summary>
        public static RevertResult Revert(string journalPath, string libraryRoot)
        {
            var res = new RevertResult();
            var entries = ReadJournal(journalPath);
            for (int i = entries.Count - 1; i >= 0; i--)
            {
                var e = entries[i];
                try
                {
                    string full = ResolveInside(libraryRoot, e.File);
                    if (full == null) { res.Failed++; res.Notes.Add(e.File + ": journal path is outside the library, refused"); continue; }
                    if (e.Status == "pending")
                    {
                        // Crash mid-apply: unknown whether the file was changed. Never guess; report and point at the backup.
                        res.Failed++; res.Notes.Add(e.File + ": pending (apply did not finish), not reverted; byte backup: " + e.Backup);
                    }
                    else if (e.Status == "failed")
                    {
                        // The apply threw: only a file still identical to its backup is quietly left alone.
                        string ho = File.Exists(full) ? HashFile(full) : null, hb = File.Exists(e.Backup) ? HashFile(e.Backup) : null;
                        if (ho == null || hb == null || ho != hb)
                        {
                            res.Failed++; res.Notes.Add(e.File + ": apply failed, file state unknown, not reverted; byte backup: " + e.Backup);
                        }
                    }
                    else if (e.Status == "done")
                    {
                        if (!File.Exists(full)) { res.Failed++; res.Notes.Add(e.File + ": current file missing"); continue; }
                        if (!string.Equals(ReadUslt(full), e.NewLyrics, StringComparison.Ordinal))
                        { res.SkippedModified++; res.Notes.Add(e.File + ": lyrics changed since apply, left alone"); continue; }
                        // shortcut: restores the USLT frame only (not a whole-file byte copy), so edits to other frames made since survive; the byte backup stays for manual use.
                        WriteUslt(full, e.OldLyrics);
                        e.Status = "reverted"; res.Reverted++;
                    }
                }
                catch (Exception ex)
                {
                    res.Failed++; res.Notes.Add(e.File + ": " + ex.Message);
                }
            }
            RewriteJournal(journalPath, entries);
            return res;
        }

        // ---- plan ----

        // Returns null on success, else the refusal reason. Extra keys (evidence, source, verdict) are ignored.
        private static string ReadPlan(string planPath, out List<PlanRow> rows)
        {
            rows = new List<PlanRow>();
            Dictionary<string, object> root;
            try
            {
                string text = File.ReadAllText(planPath);
                root = Serializer().DeserializeObject(text) as Dictionary<string, object>;
            }
            catch (Exception ex) { return "plan unreadable: " + ex.Message; }
            if (root == null) return "plan is not a JSON object";
            object v;
            long version;
            if (!root.TryGetValue("version", out v) || v == null || !long.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture), out version))
                return "plan has no version; refused";
            if (version != PlanVersion) return "plan version " + version + " is not supported (only " + PlanVersion + "); refused";
            object tracks;
            if (!root.TryGetValue("tracks", out tracks) || !(tracks is System.Collections.IEnumerable))
                return "plan has no tracks array";
            foreach (object o in (System.Collections.IEnumerable)tracks)
            {
                var d = o as Dictionary<string, object>;
                if (d == null) continue;
                long size = 0;
                object sz;
                if (d.TryGetValue("size", out sz) && sz != null) long.TryParse(Convert.ToString(sz, CultureInfo.InvariantCulture), out size);
                rows.Add(new PlanRow { Path = Str(d, "path"), Action = Str(d, "action"), Lyrics = Str(d, "lyrics"), Mtime = Str(d, "mtime"), Size = size });
            }
            return null;
        }

        private static bool IsStale(string full, PlanRow row)
        {
            if (new FileInfo(full).Length != row.Size) return true;
            DateTime planned;
            if (!DateTime.TryParse(row.Mtime, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out planned)) return true;
            if (planned.Kind == DateTimeKind.Utc) planned = planned.ToLocalTime();
            DateTime actual = File.GetLastWriteTime(full);
            return Trunc(planned) != Trunc(actual);
        }

        private static DateTime Trunc(DateTime d) => new DateTime(d.Ticks - d.Ticks % TimeSpan.TicksPerSecond);

        // Library-relative path (forward slashes) to a full path, or null when it is empty, rooted or escapes the library.
        private static string ResolveInside(string libraryRoot, string rel)
        {
            if (string.IsNullOrWhiteSpace(rel)) return null;
            try
            {
                string norm = rel.Replace('/', Path.DirectorySeparatorChar);
                if (Path.IsPathRooted(norm)) return null;
                string full = Path.GetFullPath(Path.Combine(libraryRoot, norm));
                string root = Path.GetFullPath(libraryRoot).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
                return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full : null;
            }
            catch (Exception) { return null; }
        }

        // ---- USLT ----

        // The first USLT frame's text, or null when the file has no ID3v2 tag or no USLT frame.
        private static string ReadUslt(string path)
        {
            using (var f = TagLib.File.Create(path))
            {
                var id3 = (TagLib.Id3v2.Tag)f.GetTag(TagLib.TagTypes.Id3v2, false);
                if (id3 == null) return null;
                var fr = id3.GetFrames<TagLib.Id3v2.UnsynchronisedLyricsFrame>().FirstOrDefault();
                return fr == null ? null : fr.Text;
            }
        }

        // Replaces every USLT frame with one holding text; null removes them all.
        private static void WriteUslt(string path, string text)
        {
            using (var f = TagLib.File.Create(path))
            {
                var id3 = (TagLib.Id3v2.Tag)f.GetTag(TagLib.TagTypes.Id3v2, true);
                foreach (var fr in id3.GetFrames<TagLib.Id3v2.UnsynchronisedLyricsFrame>().ToList()) id3.RemoveFrame(fr);
                if (text != null)
                    id3.AddFrame(new TagLib.Id3v2.UnsynchronisedLyricsFrame("", "eng", TagLib.StringType.UTF16) { Text = text });
                f.Save();
            }
        }

        // ---- journal ----

        private static System.Web.Script.Serialization.JavaScriptSerializer Serializer() =>
            new System.Web.Script.Serialization.JavaScriptSerializer { MaxJsonLength = int.MaxValue };

        private static void WriteJournal(string path, string libraryRoot, List<JournalEntry> entries, DateTime now)
        {
            var sb = new StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine($"  \"schemaVersion\": {SchemaVersion},");
            sb.AppendLine($"  \"generatedAt\": {J(now.ToString("s", CultureInfo.InvariantCulture))},");
            sb.AppendLine($"  \"library\": {J(libraryRoot)},");
            sb.AppendLine("  \"entries\": [");
            for (int i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                sb.AppendLine("    {" + $"\"file\": {J(e.File)}, \"oldLyrics\": {J(e.OldLyrics)}, \"newLyrics\": {J(e.NewLyrics)}, \"backup\": {J(e.Backup)}, \"detail\": {J(e.Detail)}, \"status\": {J(e.Status)}" + "}" + (i < entries.Count - 1 ? "," : ""));
            }
            sb.AppendLine("  ]");
            sb.AppendLine("}");
            AtomicFile.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        }

        private static List<JournalEntry> ReadJournal(string path)
        {
            var list = new List<JournalEntry>();
            var root = Serializer().DeserializeObject(File.ReadAllText(path)) as Dictionary<string, object>;
            if (root == null || !root.ContainsKey("entries")) throw new InvalidDataException("journal has no entries: " + path);
            foreach (object o in (System.Collections.IEnumerable)root["entries"])
            {
                var d = o as Dictionary<string, object>;
                if (d == null) continue;
                list.Add(new JournalEntry
                {
                    File = Str(d, "file"), OldLyrics = Str(d, "oldLyrics"), NewLyrics = Str(d, "newLyrics"),
                    Backup = Str(d, "backup"), Detail = Str(d, "detail"), Status = Str(d, "status"),
                });
            }
            return list;
        }

        private static void RewriteJournal(string path, List<JournalEntry> entries)
        {
            var root = (Dictionary<string, object>)Serializer().DeserializeObject(File.ReadAllText(path));
            DateTime gen;
            if (!DateTime.TryParse(Str(root, "generatedAt"), CultureInfo.InvariantCulture, DateTimeStyles.None, out gen)) gen = DateTime.Now;
            WriteJournal(path, Str(root, "library"), entries, gen);
        }

        private static string Str(Dictionary<string, object> d, string key)
        {
            object v; return d.TryGetValue(key, out v) && v != null ? v.ToString() : null;
        }

        private static string HashFile(string path)
        {
            try
            {
                using (var sha = System.Security.Cryptography.SHA256.Create())
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    return BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", "");
            }
            catch (Exception) { return null; }
        }

        // ---- result and backups ----

        private static void WriteResult(string logsDir, ApplyResult r, string planPath, DateTime now)
        {
            Directory.CreateDirectory(logsDir);
            var sb = new StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine($"  \"schemaVersion\": {SchemaVersion},");
            sb.AppendLine($"  \"generatedAt\": {J(now.ToString("s", CultureInfo.InvariantCulture))},");
            sb.AppendLine($"  \"status\": {J(r.Status)},");
            sb.AppendLine($"  \"plan\": {J(planPath)},");
            sb.AppendLine($"  \"journal\": {J(r.JournalPath)},");
            sb.AppendLine("  \"counts\": {");
            sb.AppendLine($"    \"written\": {r.Written}, \"skipped_stale\": {r.SkippedStale}, \"has_lyrics\": {r.HasLyrics}, \"skipped_other\": {r.SkippedOther}, \"failed\": {r.Failed}");
            sb.AppendLine("  },");
            sb.AppendLine(r.Failures.Count == 0 ? "  \"failures\": []" : "  \"failures\": [");
            for (int i = 0; i < r.Failures.Count; i++)
                sb.AppendLine($"    {J(r.Failures[i])}{(i < r.Failures.Count - 1 ? "," : "")}");
            if (r.Failures.Count > 0) sb.AppendLine("  ]");
            sb.AppendLine("}");
            TagFixApplier.WriteWithTimestampedCopy(Path.Combine(logsDir, ResultFileName), sb.ToString(), now);
        }

        /// <summary>Deletes backup run folders older than BackupRetentionDays, keeping any whose journal has pending or failed entries. Called only at the start of a real apply.</summary>
        internal static void PruneOldBackups(string backupRoot, string logsDir, DateTime now)
        {
            if (!Directory.Exists(backupRoot)) return;
            foreach (string dir in Directory.GetDirectories(backupRoot))
            {
                if ((now - Directory.GetCreationTime(dir)).TotalDays <= BackupRetentionDays) continue;
                string journal = Path.Combine(logsDir, "lyrics-journal-" + Path.GetFileName(dir) + ".json");
                try
                {
                    if (File.Exists(journal))
                    {
                        string text = File.ReadAllText(journal);
                        if (text.Contains("\"status\": \"pending\"") || text.Contains("\"status\": \"failed\""))
                        {
                            Console.WriteLine($" - [WARN] Keeping old backup '{dir}': its journal has pending or failed entries.");
                            continue;
                        }
                    }
                }
                catch (IOException) { continue; }
                try { Directory.Delete(dir, true); }
                catch (Exception ex) { Console.WriteLine($" - [WARN] Could not prune old backup '{dir}': {ex.Message}"); }
            }
        }

        private static string J(string s) => JsonText.Quote(s);
    }
}
