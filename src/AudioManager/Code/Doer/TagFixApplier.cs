using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace AudioManager
{
    /// <summary>
    /// Guarded real apply of a reviewed Tag Fix plan, plus its revert.
    ///
    /// Why this exists: NewMusic has no backup, so a real Tag Fix must be refusable and undoable.
    /// Apply refuses a stale plan (the NewMusic fingerprint no longer matches the reviewed plan),
    /// refuses when the backup drive is too full, writes a journal BEFORE the first change and updates
    /// it after every file (a crash leaves an exact record), and byte-copies every file it will touch
    /// to data/backups/&lt;stamp&gt;/. Backups older than 7 days are pruned at the START of the next
    /// apply, never on a clean run and never by a background job. Revert restores a file only when its
    /// current tags still equal the journal's newTags, and moves quarantined files back.
    /// Contract: docs/Contracts/TagFixApplyJSONFormat.md. Bump SchemaVersion on a breaking change.
    /// </summary>
    internal static class TagFixApplier
    {
        public const int SchemaVersion = 1;
        public const string ResultFileName = "tagfix-result.json";
        public const int BackupRetentionDays = 7;
        public const string LockFileName = "tagfix-apply.lock";

        internal class ApplyResult
        {
            public string Status;            // applied | partial | nothing_to_do | stale_plan | insufficient_disk | files_in_use | apply_locked (no tagfix-result.json written)
            public string PlanFingerprint;   // fingerprint stored in the reviewed plan
            public string CurrentFingerprint;// fingerprint of NewMusic when the apply started
            public string NewFingerprint;    // fingerprint of NewMusic after the apply (null when nothing was applied)
            public int Applied, AlreadyDone, Failed, Quarantined, NeedsManual, PendingAfter;
            public string JournalPath;
            public long BytesNeeded, BytesFree;
            public List<string> Failures = new List<string>();
        }

        internal class RevertResult
        {
            public int Reverted, SkippedModified, Failed;
            public List<string> Notes = new List<string>();
        }

        /// <summary>Reads the fingerprint value out of a tagfix-plan.json file; null when absent or unreadable.</summary>
        public static string ReadPlanFingerprint(string planPath)
        {
            if (!File.Exists(planPath)) return null;
            var m = Regex.Match(File.ReadAllText(planPath), "\"fingerprint\"\\s*:\\s*\\{[^}]*?\"value\"\\s*:\\s*\"([0-9a-f]{64})\"", RegexOptions.Singleline);
            return m.Success ? m.Groups[1].Value : null;
        }

        /// <summary>
        /// Applies the plan whose fingerprint is planFingerprint to folder. freeSpace is injectable for tests
        /// (returns free bytes for a path); null uses the real drive.
        /// </summary>
        public static ApplyResult Apply(string folder, string planFingerprint, string logsDir, string backupRoot,
            string quarantineRoot, DateTime now, Func<string, long> freeSpace = null)
        {
            // One apply at a time: a second run would share the stamp, backups and journal. The lock is held for the whole call.
            string lockPath = Path.Combine(logsDir, LockFileName);
            FileStream lockStream = TryAcquireLock(logsDir);
            if (lockStream == null)
            {
                var locked = new ApplyResult { PlanFingerprint = planFingerprint, Status = "apply_locked" };
                locked.Failures.Add("another tagfix apply is running, or a stale lock file is at " + lockPath + " that could not be removed; nothing was changed");
                return locked;
            }
            try
            {
                return ApplyUnderLock(folder, planFingerprint, logsDir, backupRoot, quarantineRoot, now, freeSpace);
            }
            finally
            {
                lockStream.Dispose();
                try { File.Delete(lockPath); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        // Returns the open lock on success, or null when a live apply holds it. A lock file nobody holds is stale
        // (its process crashed): it is deleted and the lock taken again.
        private static FileStream TryAcquireLock(string logsDir)
        {
            Directory.CreateDirectory(logsDir);
            string lockPath = Path.Combine(logsDir, LockFileName);
            try { return new FileStream(lockPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { return null; }
            try
            {
                using (new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
                File.Delete(lockPath);
                return new FileStream(lockPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        private static ApplyResult ApplyUnderLock(string folder, string planFingerprint, string logsDir, string backupRoot,
            string quarantineRoot, DateTime now, Func<string, long> freeSpace)
        {
            var result = new ApplyResult { PlanFingerprint = planFingerprint };
            var scan = TagFixer.ForFolder(folder, dryRun: true, quarantineRoot: quarantineRoot);
            result.CurrentFingerprint = TagFixPlanJson.Fingerprint(folder, scan.Scanned);

            if (planFingerprint == null || !string.Equals(planFingerprint, result.CurrentFingerprint, StringComparison.Ordinal))
            {
                result.Status = "stale_plan";
                WriteResult(logsDir, result, now);
                return result;
            }

            var planned = scan.Results.Where(l => l.Status == "would-fix" || l.WouldQuarantine).ToList();
            result.NeedsManual = scan.Results.Count(l => l.Status == "error" || l.NeedsManual);
            if (planned.Count == 0)
            {
                result.Status = "nothing_to_do";
                result.PendingAfter = 0;
                WriteResult(logsDir, result, now);
                return result;
            }

            // Locked-file preflight: a planned file another program holds open (a player, a sync tool) would fail mid-run.
            // Refuse up front, naming every such file, so the user closes them and re-runs; nothing is changed.
            var inUse = planned.Where(l => IsLockedForWrite(l.SourcePath)).Select(l => Rel(folder, l.SourcePath)).ToList();
            if (inUse.Count > 0)
            {
                result.Status = "files_in_use";
                foreach (string name in inUse) result.Failures.Add(name + ": open in another program; close it and run again");
                WriteResult(logsDir, result, now);
                return result;
            }

            // Disk check before anything is touched: backups need the planned files' bytes, plus 10% headroom.
            result.BytesNeeded = planned.Sum(l => SafeLength(l.SourcePath));
            Directory.CreateDirectory(backupRoot);
            result.BytesFree = (freeSpace ?? RealFreeSpace)(backupRoot);
            if (result.BytesFree < result.BytesNeeded + result.BytesNeeded / 10)
            {
                result.Status = "insufficient_disk";
                WriteResult(logsDir, result, now);
                return result;
            }

            PruneOldBackups(backupRoot, now, logsDir);

            string stamp = now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            string runBackup = Path.Combine(backupRoot, stamp);
            Directory.CreateDirectory(logsDir);
            result.JournalPath = Path.Combine(logsDir, $"tagfix-journal-{stamp}.json");

            var entries = planned.Select(l => new JournalEntry
            {
                File = Rel(folder, l.SourcePath),
                OldName = l.OriginalFilename,
                NewName = l.RenameBlocked || l.WouldQuarantine ? l.OriginalFilename : (l.Filename ?? l.OriginalFilename),
                Old = l.OldTags, New = l.NewTags,
                Backup = Path.Combine(runBackup, Rel(folder, l.SourcePath)),
                Status = "pending",
                WasQuarantine = l.WouldQuarantine,
            }).ToList();
            WriteJournal(result.JournalPath, folder, quarantineRoot, entries, now);

            var fixer = TagFixer.ForTesting(dryRun: false, quarantineRoot: quarantineRoot);
            for (int i = 0; i < planned.Count; i++)
            {
                var e = entries[i];
                string src = planned[i].SourcePath;
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(e.Backup));
                    // Never overwrite an existing backup: it holds the earlier original bytes.
                    if (!File.Exists(e.Backup)) File.Copy(src, e.Backup, false);
                    var log = fixer.ProcessFile(src);
                    if (log.Status == "error")
                    {
                        e.Status = "failed"; e.Detail = log.Detail;
                        result.Failed++; result.Failures.Add(e.File + ": " + log.Detail);
                    }
                    else if (log.Status == "fixed")
                    {
                        e.Status = "done"; e.NewName = log.Filename ?? e.OldName; result.Applied++;
                        e.NewHash = HashFile(Path.Combine(Path.GetDirectoryName(src), e.NewName));
                    }
                    else if (log.Detail != null && log.Detail.StartsWith("quarantined", StringComparison.Ordinal))
                    {
                        e.Status = "quarantined";
                        e.QuarantinedTo = log.Detail.Substring(log.Detail.IndexOf(':') + 1).Trim();
                        result.Quarantined++;
                    }
                    else
                    {
                        e.Status = "already_done"; result.AlreadyDone++;
                    }
                }
                catch (Exception ex)
                {
                    e.Status = "failed"; e.Detail = ex.Message;
                    result.Failed++; result.Failures.Add(e.File + ": " + ex.Message);
                }
                WriteJournal(result.JournalPath, folder, quarantineRoot, entries, now);
            }

            // Files the scan could not read (locked, corrupt) were never planned; they are failures, not silent skips.
            foreach (var u in scan.Results.Where(l => l.Status == "error"))
            {
                result.Failed++; result.Failures.Add(Rel(folder, u.SourcePath) + ": unreadable, not changed: " + u.Detail);
            }

            var after = TagFixer.ForFolder(folder, dryRun: true, quarantineRoot: quarantineRoot);
            result.PendingAfter = after.Results.Count(l => l.Status == "would-fix" || l.WouldQuarantine);
            result.NewFingerprint = TagFixPlanJson.Fingerprint(folder, after.Scanned);
            result.Status = result.Failed == 0 ? "applied" : "partial";
            WriteResult(logsDir, result, now);
            return result;
        }

        // True when the file cannot be opened exclusively for write right now (IO sharing violation or access refused).
        private static bool IsLockedForWrite(string path)
        {
            try { using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { } return false; }
            catch (IOException) { return true; }
            catch (UnauthorizedAccessException) { return true; }
        }

        /// <summary>Reverts a journal written by Apply. Never touches a file whose tags changed since the apply.</summary>
        public static RevertResult Revert(string journalPath, string folder, string quarantineRoot)
        {
            var res = new RevertResult();
            var entries = ReadJournal(journalPath);
            for (int i = entries.Count - 1; i >= 0; i--)
            {
                var e = entries[i];
                try
                {
                    string original = Path.Combine(folder, e.File);
                    string escape = FirstEscapingPath(e, original, folder, quarantineRoot);
                    if (escape != null)
                    {
                        res.Failed++; res.Notes.Add(e.File + ": journal path is outside the allowed folder, refused: " + escape);
                        continue;
                    }
                    if (e.Status == "quarantined")
                    {
                        if (!File.Exists(e.QuarantinedTo)) { res.Failed++; res.Notes.Add(e.File + ": quarantined file missing"); continue; }
                        if (File.Exists(original)) { res.Failed++; res.Notes.Add(e.File + ": original path occupied"); continue; }
                        File.Move(e.QuarantinedTo, original);
                        e.Status = "reverted"; res.Reverted++;
                    }
                    else if (e.Status == "pending")
                    {
                        // Crash mid-apply: unknown whether the file was changed. Never guess; report and point at the backup.
                        res.Failed++;
                        res.Notes.Add(e.File + ": pending (apply did not finish), not reverted; byte backup: " + e.Backup);
                    }
                    else if (e.Status == "done")
                    {
                        string current = Path.Combine(Path.GetDirectoryName(original), e.NewName);
                        if (!File.Exists(current)) { res.Failed++; res.Notes.Add(e.File + ": current file missing"); continue; }
                        // Bytes decide when the journal recorded a hash (any edit, even outside the five fields, counts); older journals fall back to tags.
                        bool unchanged = !string.IsNullOrEmpty(e.NewHash) ? HashFile(current) == e.NewHash : TagsMatch(current, e.New);
                        if (!unchanged) { res.SkippedModified++; res.Notes.Add(e.File + ": modified since apply, left alone"); continue; }
                        if (!current.Equals(original, StringComparison.OrdinalIgnoreCase) && File.Exists(original))
                        { res.Failed++; res.Notes.Add(e.File + ": original name occupied"); continue; }
                        if (File.Exists(e.Backup)) File.Copy(e.Backup, current, true);
                        else WriteTags(current, e.Old);
                        if (!current.Equals(original, StringComparison.OrdinalIgnoreCase)) File.Move(current, original);
                        e.Status = "reverted"; res.Reverted++;
                    }
                }
                catch (Exception ex)
                {
                    res.Failed++; res.Notes.Add(e.File + ": " + ex.Message);
                }
            }
            RewriteJournalStatuses(journalPath, entries);
            return res;
        }

        // A journal is a plain file anyone can edit; revert must never write or move outside the folder it was applied to.
        private static string FirstEscapingPath(JournalEntry e, string original, string folder, string quarantineRoot)
        {
            if (!IsUnder(original, folder)) return original;
            if (!string.IsNullOrEmpty(e.NewName))
            {
                string current = Path.Combine(Path.GetDirectoryName(original) ?? folder, e.NewName);
                if (!IsUnder(current, folder)) return current;
            }
            if (!string.IsNullOrEmpty(e.QuarantinedTo) && !IsUnder(e.QuarantinedTo, quarantineRoot)) return e.QuarantinedTo;
            return null;
        }

        private static bool IsUnder(string path, string root)
        {
            try
            {
                string full = Path.GetFullPath(path);
                string r = Path.GetFullPath(root).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
                return full.StartsWith(r, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception) { return false; }
        }

        // ---- journal ----

        private class JournalEntry
        {
            public string File, OldName, NewName, Backup, Status, Detail, QuarantinedTo, NewHash;
            public TagFixer.TagState Old, New;
            public bool WasQuarantine;
        }

        private static void WriteJournal(string path, string folder, string quarantineRoot, List<JournalEntry> entries, DateTime now)
        {
            var sb = new StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine($"  \"schemaVersion\": {SchemaVersion},");
            sb.AppendLine($"  \"generatedAt\": {J(now.ToString("s", CultureInfo.InvariantCulture))},");
            sb.AppendLine($"  \"folder\": {J(folder)},");
            sb.AppendLine($"  \"quarantineRoot\": {J(quarantineRoot)},");
            sb.AppendLine("  \"entries\": [");
            for (int i = 0; i < entries.Count; i++)
                sb.AppendLine("    " + EntryLine(entries[i]) + (i < entries.Count - 1 ? "," : ""));
            sb.AppendLine("  ]");
            sb.AppendLine("}");
            AtomicFile.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        }

        // One flat object per line so a revert can read it back without a JSON library.
        private static string EntryLine(JournalEntry e)
        {
            Func<TagFixer.TagState, string, string> tags = (t, p) => t == null ? "" :
                $"\"{p}Title\": {J(t.Title)}, \"{p}Album\": {J(t.Album)}, \"{p}Artists\": {J(t.Artists)}, \"{p}Genre\": {J(t.Genre)}, \"{p}Compilation\": {(t.Compilation ? "true" : "false")}, ";
            return "{" + $"\"file\": {J(e.File)}, \"oldName\": {J(e.OldName)}, \"newName\": {J(e.NewName)}, " +
                tags(e.Old, "old") + tags(e.New, "new") +
                $"\"backup\": {J(e.Backup)}, \"newHash\": {J(e.NewHash)}, \"quarantinedTo\": {J(e.QuarantinedTo)}, \"detail\": {J(e.Detail)}, \"status\": {J(e.Status)}" + "}";
        }

        private static List<JournalEntry> ReadJournal(string path)
        {
            var list = new List<JournalEntry>();
            // A real JSON parser: a hand-edited or reformatted journal reads the same as one we wrote.
            var root = new System.Web.Script.Serialization.JavaScriptSerializer().DeserializeObject(File.ReadAllText(path)) as Dictionary<string, object>;
            if (root == null || !root.ContainsKey("entries")) throw new InvalidDataException("journal has no entries: " + path);
            foreach (object o in (System.Collections.IEnumerable)root["entries"])
            {
                var d = o as Dictionary<string, object>;
                if (d == null) continue;
                list.Add(new JournalEntry
                {
                    File = JStrVal(d, "file"), OldName = JStrVal(d, "oldName"), NewName = JStrVal(d, "newName"),
                    Backup = JStrVal(d, "backup"), QuarantinedTo = JStrVal(d, "quarantinedTo"), Detail = JStrVal(d, "detail"),
                    Status = JStrVal(d, "status"), NewHash = JStrVal(d, "newHash"),
                    Old = ReadTagsDict(d, "old"), New = ReadTagsDict(d, "new"),
                });
            }
            return list;
        }

        private static string JStrVal(Dictionary<string, object> d, string key)
        {
            object v; return d.TryGetValue(key, out v) && v != null ? v.ToString() : null;
        }

        private static TagFixer.TagState ReadTagsDict(Dictionary<string, object> d, string p)
        {
            if (!d.ContainsKey(p + "Title")) return null;
            object c;
            return new TagFixer.TagState
            {
                Title = JStrVal(d, p + "Title"), Album = JStrVal(d, p + "Album"), Artists = JStrVal(d, p + "Artists"),
                Genre = JStrVal(d, p + "Genre"), Compilation = d.TryGetValue(p + "Compilation", out c) && c is bool && (bool)c,
            };
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

        private static void RewriteJournalStatuses(string path, List<JournalEntry> entries)
        {
            // Regenerate the whole file from the parsed header and entries, so any layout the parser accepted is saved back.
            var root = (Dictionary<string, object>)new System.Web.Script.Serialization.JavaScriptSerializer().DeserializeObject(File.ReadAllText(path));
            DateTime gen;
            if (!DateTime.TryParse(JStrVal(root, "generatedAt"), CultureInfo.InvariantCulture, DateTimeStyles.None, out gen)) gen = DateTime.Now;
            WriteJournal(path, JStrVal(root, "folder"), JStrVal(root, "quarantineRoot"), entries, gen);
        }

        private static TagFixer.TagState ReadTags(string line, string p)
        {
            if (!line.Contains($"\"{p}Title\"")) return null;
            return new TagFixer.TagState
            {
                Title = Str(line, p + "Title"), Album = Str(line, p + "Album"), Artists = Str(line, p + "Artists"),
                Genre = Str(line, p + "Genre"), Compilation = Regex.IsMatch(line, $"\"{p}Compilation\": true"),
            };
        }

        private static string Str(string line, string key)
        {
            var m = Regex.Match(line, "\"" + key + "\": (null|\"((?:[^\"\\\\]|\\\\.)*)\")");
            if (!m.Success || m.Groups[1].Value == "null") return null;
            return Regex.Replace(m.Groups[2].Value, "\\\\(.)", x => x.Groups[1].Value == "n" ? "\n" : x.Groups[1].Value);
        }

        // ---- tag helpers ----

        private static bool TagsMatch(string path, TagFixer.TagState expected)
        {
            if (expected == null) return false;
            using (var f = TagLib.File.Create(path))
            {
                var id3 = (TagLib.Id3v2.Tag)f.GetTag(TagLib.TagTypes.Id3v2, true);
                return (f.Tag.Title ?? "") == (expected.Title ?? "")
                    && (f.Tag.Album ?? "") == (expected.Album ?? "")
                    && (f.Tag.JoinedPerformers ?? "") == (expected.Artists ?? "")
                    && (f.Tag.JoinedGenres ?? "") == (expected.Genre ?? "")
                    && id3.IsCompilation == expected.Compilation;
            }
        }

        // shortcut: used only when the byte backup was pruned; restores the five planned fields, not other frames.
        private static void WriteTags(string path, TagFixer.TagState t)
        {
            using (var f = TagLib.File.Create(path))
            {
                var id3 = (TagLib.Id3v2.Tag)f.GetTag(TagLib.TagTypes.Id3v2, true);
                f.Tag.Title = t.Title; f.Tag.Album = t.Album;
                f.Tag.Performers = (t.Artists ?? "").Split(new[] { "; " }, StringSplitOptions.RemoveEmptyEntries);
                f.Tag.Genres = string.IsNullOrEmpty(t.Genre) ? new string[0] : new[] { t.Genre };
                id3.IsCompilation = t.Compilation;
                f.Save();
            }
        }

        // ---- result and small helpers ----

        private static void WriteResult(string logsDir, ApplyResult r, DateTime now)
        {
            Directory.CreateDirectory(logsDir);
            var sb = new StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine($"  \"schemaVersion\": {SchemaVersion},");
            sb.AppendLine($"  \"generatedAt\": {J(now.ToString("s", CultureInfo.InvariantCulture))},");
            sb.AppendLine($"  \"status\": {J(r.Status)},");
            sb.AppendLine($"  \"oldFingerprint\": {J(r.PlanFingerprint)},");
            sb.AppendLine($"  \"newFingerprint\": {J(r.NewFingerprint ?? r.CurrentFingerprint)},");
            sb.AppendLine($"  \"journal\": {J(r.JournalPath)},");
            sb.AppendLine($"  \"bytesNeeded\": {r.BytesNeeded},");
            sb.AppendLine($"  \"bytesFree\": {r.BytesFree},");
            sb.AppendLine("  \"counts\": {");
            sb.AppendLine($"    \"applied\": {r.Applied}, \"alreadyDone\": {r.AlreadyDone}, \"failed\": {r.Failed}, \"quarantined\": {r.Quarantined},");
            sb.AppendLine($"    \"needsManual\": {r.NeedsManual}, \"pendingAfter\": {r.PendingAfter}");
            sb.AppendLine("  },");
            sb.AppendLine(r.Failures.Count == 0 ? "  \"failures\": []" : "  \"failures\": [");
            for (int i = 0; i < r.Failures.Count; i++)
                sb.AppendLine($"    {J(r.Failures[i])}{(i < r.Failures.Count - 1 ? "," : "")}");
            if (r.Failures.Count > 0) sb.AppendLine("  ]");
            sb.AppendLine("}");
            AtomicFile.WriteAllText(Path.Combine(logsDir, ResultFileName), sb.ToString(), new UTF8Encoding(false));
        }

        /// <summary>Deletes backup run folders older than BackupRetentionDays. Called only at the start of an apply.</summary>
        internal static void PruneOldBackups(string backupRoot, DateTime now, string logsDir = null)
        {
            if (!Directory.Exists(backupRoot)) return;
            foreach (string dir in Directory.GetDirectories(backupRoot))
            {
                DateTime created = Directory.GetCreationTime(dir);
                if ((now - created).TotalDays > BackupRetentionDays)
                {
                    // A run whose journal still has pending entries (crash mid-apply) is the only copy of the original bytes.
                    if (logsDir != null)
                    {
                        string journal = Path.Combine(logsDir, "tagfix-journal-" + Path.GetFileName(dir) + ".json");
                        try
                        {
                            if (File.Exists(journal) && File.ReadAllText(journal).Contains("\"status\": \"pending\""))
                            {
                                Console.WriteLine($" - [WARN] Keeping old backup '{dir}': its journal has pending entries.");
                                continue;
                            }
                        }
                        catch (IOException) { continue; }
                    }
                    try { Directory.Delete(dir, true); }
                    catch (Exception ex) { Console.WriteLine($" - [WARN] Could not prune old backup '{dir}': {ex.Message}"); }
                }
            }
        }

        private static long RealFreeSpace(string path) => new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))).AvailableFreeSpace;

        private static long SafeLength(string p) { try { return new FileInfo(p).Length; } catch (IOException) { return 0; } }

        private static string Rel(string folder, string path)
        {
            string root = folder.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            return path.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? path.Substring(root.Length) : Path.GetFileName(path);
        }

        private static string J(string s)
        {
            return JsonText.Quote(s);
        }
    }
}
