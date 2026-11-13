using MiscMigrationMove = AudioManager.MusicIntegrator.MiscMigrationMove;
using MiscRevertResult = AudioManager.MusicIntegrator.MiscRevertResult;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using File = System.IO.File;

namespace AudioManager
{
    /// <summary>
    /// Moves Misc tracks whose artist now has a library folder into that folder (Artists/{artist}/Singles/), retags
    /// case-only artist mismatches to the folder spelling, and journals every move before it happens (misc-migration-*.json).
    /// MusicIntegrator forwards to this class; the class also reverts a migration from its journal.
    /// </summary>
    internal sealed class MiscMigration
    {
        private readonly Func<string> _libraryPathSource;
        private readonly Func<IntegrationRoots> _rootsSource;
        private readonly Func<bool> _dryRunSource;
        private readonly Func<string> _journalDirSource;
        private readonly Func<Dictionary<string, List<string>>> _miscCandidatesSource;
        private readonly ReplacementLedger _ledger;

        /// <summary>Library path, roots, dry-run flag, journal folder and Misc candidates are read live on every use, never copied.</summary>
        internal MiscMigration(Func<string> libraryPath, Func<IntegrationRoots> roots, Func<bool> dryRun, Func<string> journalDir, Func<Dictionary<string, List<string>>> miscCandidates, ReplacementLedger ledger)
        {
            _libraryPathSource = libraryPath;
            _rootsSource = roots;
            _dryRunSource = dryRun;
            _journalDirSource = journalDir;
            _miscCandidatesSource = miscCandidates;
            _ledger = ledger;
        }

        private string _libraryPath => _libraryPathSource();
        private IntegrationRoots _roots => _rootsSource();
        private bool dryRun => _dryRunSource();
        private string MigrationJournalDir => _journalDirSource();
        private Dictionary<string, List<string>> _miscMigrationCandidates => _miscCandidatesSource();
        private HashSet<string> _lDeletedLibraryPaths => _ledger._lDeletedLibraryPaths;
        private MirrorIndex _mirror => new MirrorIndex(_roots);

        /// <summary>
        /// Rewrites the migration journal atomically (schemaVersion 1). Each move is {"from", "to"} plus an optional
        /// "retag": {"field": "artist", "from": old tag entry, "to": folder spelling}, present only when the tag was retagged.
        /// </summary>
        internal static void WriteMiscMigrationJournal(string dir, string path, List<MiscMigrationMove> moves)
        {
            try
            {
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("{\"recordType\": \"miscMigration\", \"schemaVersion\": 1, \"moves\": [");
                for (int i = 0; i < moves.Count; i++)
                {
                    var m = moves[i];
                    string line = "  {\"from\": " + JsonText.Quote(m.From) + ", \"to\": " + JsonText.Quote(m.To);
                    if (m.RetagFrom != null)
                        line += ", \"retag\": {\"field\": \"artist\", \"from\": " + JsonText.Quote(m.RetagFrom) + ", \"to\": " + JsonText.Quote(m.RetagTo) + "}";
                    sb.AppendLine(line + "}" + (i < moves.Count - 1 ? "," : ""));
                }
                sb.AppendLine("]}");
                AtomicFile.WriteAllText(path, sb.ToString(), new System.Text.UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  [WARN] Could not write the Misc migration journal: {ex.Message}");
            }
        }

        /// <summary>
        /// Migrates existing Misc songs to Artists/{artist}/Singles/ for artists promoted this batch.
        /// Runs in both dry-run (shows what would happen) and real mode (moves files).
        /// No-op if no artists were promoted with existing Misc songs.
        /// </summary>
        internal int RunMiscMigration()
        {
            if (_miscMigrationCandidates.Count == 0) return 0;
            var journaledMoves = new List<MiscMigrationMove>();
            string journalDir = MigrationJournalDir ?? _roots.Logs;
            string journalPath = Path.Combine(journalDir, $"misc-migration-{DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.json");

            Console.WriteLine("\n===========================================================================");
            Console.WriteLine(dryRun ? "Misc Migration (Dry Run)" : "Misc Migration");
            Console.WriteLine("===========================================================================");

            int totalCount = 0;
            int failedCount = 0;
            foreach (var kvp in _miscMigrationCandidates)
            {
                string artist = kvp.Key;
                var xmlPaths = kvp.Value;
                string destFolder = Path.Combine(_libraryPath, Constants.ArtistsDir, MusicIntegrator.SanitiseFolderName(artist), Constants.SinglesDir);
                string relDest = $"Artists\\{MusicIntegrator.SanitiseFolderName(artist)}\\{Constants.SinglesDir}";

                Console.WriteLine($"\n  {artist}: {xmlPaths.Count} song(s) -> {relDest}");

                foreach (var xmlPath in xmlPaths)
                {
                    string libPath = _mirror.DeriveLibraryPathFromMirrorPath(xmlPath);
                    string fileName = Path.GetFileName(libPath);

                    if (!File.Exists(libPath))
                    {
                        if (_lDeletedLibraryPaths.Contains(libPath))
                            Console.WriteLine($"  [INFO] Already deleted by duplicate resolution: {fileName}");
                        else
                            Console.WriteLine($"  [WARN] Not found in library (stale mirror?): {fileName}");
                        continue;
                    }

                    if (dryRun)
                    {
                        // Same outcome as the real File.Move below, which throws IOException when the destination exists
                        if (File.Exists(Path.Combine(destFolder, fileName)))
                        {
                            Console.WriteLine($"  [ERROR] Misc migration would fail for {fileName}: destination already exists");
                            failedCount++;
                        }
                        else
                        {
                            Console.WriteLine($"[DRY RUN] Would move: {fileName}");
                            string oldArtist = ReadCaseOnlyArtistEntry(libPath, artist);
                            if (oldArtist != null)
                            {
                                Console.WriteLine($"[DRY RUN] Would retag artist '{oldArtist}' -> '{artist}': {fileName}");
                                string plannedRename = RetagRenameTarget(Path.Combine(destFolder, fileName), oldArtist, artist);
                                if (plannedRename != null)
                                    Console.WriteLine($"[DRY RUN] Would rename '{fileName}' -> '{Path.GetFileName(plannedRename)}'");
                            }
                            totalCount++;
                        }
                    }
                    else
                    {
                        // Per-file guard: one failed move must not abort the rest of the migration
                        List<string> createdDirs = null;
                        try
                        {
                            createdDirs = FileMover.CreateDirectoryTracked(destFolder);
                            string destPath = Path.Combine(destFolder, fileName);
                            File.Move(libPath, destPath);
                            var move = new MiscMigrationMove { From = libPath, To = destPath };
                            journaledMoves.Add(move);
                            WriteMiscMigrationJournal(journalDir, journalPath, journaledMoves);
                            Console.WriteLine($"  Moved: {fileName}");
                            totalCount++;

                            // BL-149: the move is journaled first, so a failed retag keeps the move and its journal entry
                            string oldArtist = ReadCaseOnlyArtistEntry(destPath, artist);
                            if (oldArtist != null)
                            {
                                try
                                {
                                    ReplaceArtistTagEntry(destPath, oldArtist, artist);
                                    move.RetagFrom = oldArtist;
                                    move.RetagTo = artist;
                                    WriteMiscMigrationJournal(journalDir, journalPath, journaledMoves);
                                    Console.WriteLine($"  Retagged artist '{oldArtist}' -> '{artist}': {fileName}");

                                    // BL-151: the file name takes the folder spelling too; a failed rename keeps the name and the move
                                    string renamedPath = RetagRenameTarget(destPath, oldArtist, artist);
                                    if (renamedPath != null)
                                    {
                                        try
                                        {
                                            File.Move(destPath, renamedPath);
                                            move.To = renamedPath;
                                            WriteMiscMigrationJournal(journalDir, journalPath, journaledMoves);
                                            Console.WriteLine($"  Renamed: {fileName} -> {Path.GetFileName(renamedPath)}");
                                        }
                                        catch (Exception nex)
                                        {
                                            Console.WriteLine($"  [WARN] Rename failed for {fileName} (the name is kept): {nex.Message}");
                                        }
                                    }
                                }
                                catch (Exception rex)
                                {
                                    failedCount++;
                                    Console.WriteLine($"  [ERROR] Artist retag failed for {fileName} (the move is kept): {rex.Message}");
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            failedCount++;
                            FileMover.RemoveEmptyCreatedDirs(createdDirs);
                            Console.WriteLine($"  [ERROR] Misc migration failed for {fileName}: {ex.Message}");
                        }
                    }
                }
            }

            Console.WriteLine();
            Console.WriteLine(dryRun
                ? $"Would migrate: {totalCount} Misc song(s), {failedCount} would fail"
                : $"Migrated: {totalCount} Misc song(s), {failedCount} failed");
            return failedCount;
        }

        /// <summary>
        /// BL-149: the old ARTIST tag entry that equals artist ignoring case but not exactly (Performers first, then
        /// AlbumArtists), or null when no retag is due. Read-only: the file is opened and never saved. A read failure
        /// prints a WARN and gives null, so it never aborts the migration.
        /// </summary>
        internal static string ReadCaseOnlyArtistEntry(string path, string artist)
        {
            try
            {
                using (var tf = TagLib.File.Create(path))
                    return CaseOnlyArtistEntry(tf.Tag.Performers, artist) ?? CaseOnlyArtistEntry(tf.Tag.AlbumArtists, artist);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  [WARN] Could not read tags for the artist retag check: {Path.GetFileName(path)}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// BL-151: the file name with its artist entry equal to oldEntry (ignoring case) replaced by newEntry, or null when
        /// no entry matches. Only the artist part before the first " - " is read; entries there are separated by "; ".
        /// </summary>
        internal static string RetaggedFileName(string fileName, string oldEntry, string newEntry)
        {
            int sep = fileName.IndexOf(" - ", StringComparison.Ordinal);
            if (sep <= 0) return null;
            string[] entries = fileName.Substring(0, sep).Split(new[] { "; " }, StringSplitOptions.None);
            bool changed = false;
            for (int i = 0; i < entries.Length; i++)
            {
                if (string.Equals(entries[i], oldEntry, StringComparison.OrdinalIgnoreCase) && !string.Equals(entries[i], newEntry, StringComparison.Ordinal))
                {
                    entries[i] = newEntry;
                    changed = true;
                }
            }
            return changed ? string.Join("; ", entries) + fileName.Substring(sep) : null;
        }

        /// <summary>
        /// BL-151: the renamed path for a retagged file at currentPath, or null when no rename is due. A name held by
        /// another file gives a WARN and null, so the old name is kept. A name differing from the current one only by case
        /// is the same file on Windows, so it is not a clash.
        /// </summary>
        internal static string RetagRenameTarget(string currentPath, string oldEntry, string newEntry)
        {
            string newName = RetaggedFileName(Path.GetFileName(currentPath), oldEntry, newEntry);
            if (newName == null) return null;
            string target = Path.Combine(Path.GetDirectoryName(currentPath), newName);
            if (File.Exists(target) && !string.Equals(target, currentPath, StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"  [WARN] Rename skipped, '{newName}' already exists: {Path.GetFileName(currentPath)}");
                return null;
            }
            return target;
        }

        internal static string CaseOnlyArtistEntry(string[] entries, string artist)
        {
            if (entries == null) return null;
            foreach (string e in entries)
                if (e != null && string.Equals(e, artist, StringComparison.OrdinalIgnoreCase) && !string.Equals(e, artist, StringComparison.Ordinal))
                    return e;
            return null;
        }

        /// <summary>
        /// Replaces every Performers and AlbumArtists entry equal to oldEntry (exact match) with newEntry, then saves.
        /// Other entries are kept as they are. A tag field with no matching entry is not written.
        /// </summary>
        internal static void ReplaceArtistTagEntry(string path, string oldEntry, string newEntry)
        {
            using (var tf = TagLib.File.Create(path))
            {
                if (tf.Tag.Performers != null && tf.Tag.Performers.Contains(oldEntry))
                    tf.Tag.Performers = tf.Tag.Performers.Select(e => e == oldEntry ? newEntry : e).ToArray();
                if (tf.Tag.AlbumArtists != null && tf.Tag.AlbumArtists.Contains(oldEntry))
                    tf.Tag.AlbumArtists = tf.Tag.AlbumArtists.Select(e => e == oldEntry ? newEntry : e).ToArray();
                tf.Save();
            }
        }

        /// <summary>
        /// Undoes a Misc migration from its journal (misc-migration-*.json), newest move first. A song moves back only when
        /// its migrated copy exists and its old path is free, so nothing is overwritten. A move carrying a "retag" gets its
        /// old ARTIST tag entry restored BEFORE the move back, so a failed retag leaves the file unmoved (Failed). A failed
        /// move after a retag puts the migrated tag back (best effort). Every from and to must resolve under the library root
        /// (_libraryPath), else that entry is refused (Failed) and nothing is touched. Dry run prints what would be reverted and changes nothing;
        /// its counts are the moves that would be reverted. Throws when the journal is not a Misc migration journal.
        /// </summary>
        internal MiscRevertResult RevertMiscMigration(string journalPath)
        {
            var result = new MiscRevertResult();
            var ser = new System.Web.Script.Serialization.JavaScriptSerializer();
            var root = ser.DeserializeObject(File.ReadAllText(journalPath)) as Dictionary<string, object>;
            if (root == null || !"miscMigration".Equals(root.TryGetValue("recordType", out object rt) ? rt as string : null))
                throw new InvalidDataException("not a Misc migration journal: " + journalPath);
            var moves = root.TryGetValue("moves", out object mv) ? mv as object[] : null;
            if (moves == null) return result;
            // Every journal path must resolve under the library root: the Misc migration only ever moves files inside it
            string libRoot = Path.GetFullPath(_libraryPath).TrimEnd('\\', '/') + "\\";
            for (int i = moves.Length - 1; i >= 0; i--)
            {
                string fileName = null;
                try
                {
                    var m = (Dictionary<string, object>)moves[i];
                    string from = Path.GetFullPath((string)m["from"]);
                    string to = Path.GetFullPath((string)m["to"]);
                    fileName = Path.GetFileName(from);
                    if (!from.StartsWith(libRoot, StringComparison.OrdinalIgnoreCase) || !to.StartsWith(libRoot, StringComparison.OrdinalIgnoreCase))
                    {
                        Console.WriteLine($"  [ERROR] Revert refused for {fileName}: a journal path is outside the library");
                        result.Failed++;
                        continue;
                    }
                    if (!File.Exists(to) || File.Exists(from))
                    {
                        Console.WriteLine($"  [SKIP] Revert: {fileName} is not at its migrated place, or its old place is taken");
                        result.Skipped++;
                        continue;
                    }
                    if (dryRun)
                    {
                        Console.WriteLine($"[DRY RUN] Would revert: {fileName}");
                        result.Reverted++;
                        continue;
                    }

                    // Retag where the file is now, before the move: a failed retag leaves the file in place and counted as failed,
                    // so a re-run still sees it at its migrated place
                    Dictionary<string, object> retag = m.ContainsKey("retag") ? (Dictionary<string, object>)m["retag"] : null;
                    if (retag != null)
                        ReplaceArtistTagEntry(to, (string)retag["to"], (string)retag["from"]);
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(from));
                        File.Move(to, from);
                    }
                    catch
                    {
                        // The move failed after a successful retag: put the migrated tag back so the file matches its journal state
                        if (retag != null) RestoreMigratedTagBestEffort(to, (string)retag["from"], (string)retag["to"]);
                        throw;
                    }
                    Console.WriteLine($"  Reverted: {fileName}");
                    result.Reverted++;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  [ERROR] Revert failed for {fileName ?? "a journal entry"}: {ex.Message}");
                    result.Failed++;
                }
            }
            return result;
        }

        /// <summary>Best-effort undo of a retag whose move failed. A failure here is printed, never thrown.</summary>
        internal static void RestoreMigratedTagBestEffort(string path, string fromEntry, string toEntry)
        {
            try
            {
                ReplaceArtistTagEntry(path, fromEntry, toEntry);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  [WARN] Could not restore the migrated artist tag on {Path.GetFileName(path)}: {ex.Message}");
            }
        }
    }
}
