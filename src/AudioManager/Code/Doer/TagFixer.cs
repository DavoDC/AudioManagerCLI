using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using AudioManager.Code.Modules;
using TagLib;
using File = System.IO.File;

namespace AudioManager
{
    /// <summary>
    /// Cleans raw MP3 files in NewMusic folder before integration.
    /// Applies tag cleanup rules (removing parentheticals, fixing featured artists, setting TCMP, etc.)
    /// and renames files to match the library convention.
    /// </summary>
    internal class TagFixer : Doer
    {
        private bool dryRun;
        private int fixedCount = 0;
        private int skippedCount = 0;

        // shortcut: static test seams, restore the originals in a finally; upgrade to injection if a second caller needs different behaviour.
        internal static Action<TagLib.File> Saver = f => f.Save();
        internal static Action<string, string> Restorer = (bak, src) => File.Copy(bak, src, true);

        /// <summary>
        /// User-defined rules from config/tagfix-custom-rules.xml, applied as an additive pass
        /// AFTER the built-in hardcoded fixes. Empty when the file is missing or malformed.
        /// </summary>
        private readonly TagFixCustomRuleSet customRules;

        /// <summary>
        /// Per-file keep= flags from config/tagfix-exceptions.xml. Empty when the file is missing or malformed.
        /// </summary>
        private readonly TagFixExceptions exceptions;

        /// <summary>
        /// Per-file tag changes for display in MusicIntegrator's combined routing block.
        /// Key: filename MusicIntegrator will see (original in dry-run; post-rename in real mode).
        /// Value: list of change strings e.g. "Title: \"X\" -> \"Y\"". Excludes filename renames.
        /// </summary>
        public Dictionary<string, List<string>> FileChanges { get; } = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Full fix log per file (keyed by original and new name), for the structured tagFix object in routing JSON.</summary>
        public Dictionary<string, FixLog> LogsByFile { get; } = new Dictionary<string, FixLog>(StringComparer.OrdinalIgnoreCase);

        internal class FixLog
        {
            public string Filename;
            public string OriginalFilename;
            public string SourcePath;
            public List<string> Changes = new List<string>();
            public string Status; // "fixed", "would-fix", "skipped", "error"
            public string Detail;
            /// <summary>Tag values before the fix. Set for every file ProcessFile reads (dry run and real).</summary>
            public TagState OldTags;
            /// <summary>Tag values the fix writes. Set for would-fix rows; equal to OldTags for a would-quarantine row.</summary>
            public TagState NewTags;
            /// <summary>Dry run only: the instrumental would be moved to quarantine.</summary>
            public bool WouldQuarantine;
            /// <summary>Dry run only: the planned rename is blocked (empty artist or title, or the target name is taken).</summary>
            public bool NeedsManual;
            public string ManualReason;
            /// <summary>Dry run only: the rename is blocked, so the file keeps OriginalFilename as its planned name.</summary>
            public bool RenameBlocked;
        }

        /// <summary>Tag values captured for the dry-run plan JSON (tagfix-plan.json).</summary>
        internal class TagState
        {
            public string Title;
            public string Album;
            public string Artists;
            public string Genre;
            public bool Compilation;
            /// <summary>Count of embedded cover pictures. TagFixer never changes art, so old and new carry the same value.</summary>
            public int ArtCount;
        }

        /// <summary>One NewMusic mp3 as found by the scan, before any processing. Input to the plan fingerprint.</summary>
        internal class ScannedFile
        {
            public string Path;
            public long Size;
            public DateTime MtimeUtc;
        }

        /// <summary>Every mp3 the scan found, captured before processing. Empty when the folder is missing.</summary>
        public List<ScannedFile> Scanned { get; } = new List<ScannedFile>();

        /// <summary>One FixLog per scanned mp3, in scan order. Read by TagFixPlanJson.</summary>
        public List<FixLog> Results { get; } = new List<FixLog>();

        public TagFixer(bool dryRun = false, Func<string, bool> fileFilter = null, IDictionary<string, string> artistSpellingChoices = null)
        {
            this.dryRun = dryRun;
            this.customRules = TagFixCustomRuleSet.Load();
            this.exceptions = TagFixExceptions.Load();
            this.artistSpellingChoices = CopyChoices(artistSpellingChoices);
            string modeLabel = dryRun ? " [DRY RUN - no files will be modified]" : "";
            Console.WriteLine($"\nFixing music tags...{modeLabel}");

            ScanAndProcess(Constants.NewMusicPath, fileFilter);
        }

        /// <summary>
        /// Snapshots every mp3 under folder (before any processing), then runs ProcessFile on each into Results.
        /// The public constructor passes Constants.NewMusicPath; the test seam passes a fixture folder.
        /// </summary>
        /// <summary>Read-only: every tagfix backup left under folder, including moved-aside ".tagfix.bak.stale-N" copies.</summary>
        internal static List<string> ListLeftoverBackups(string folder)
        {
            var found = new List<string>();
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return found;
            try
            {
                foreach (string p in Directory.GetFiles(folder, "*.tagfix.bak*", SearchOption.AllDirectories))
                    if (p.EndsWith(".tagfix.bak", StringComparison.OrdinalIgnoreCase) || p.IndexOf(".tagfix.bak.stale-", StringComparison.OrdinalIgnoreCase) >= 0)
                        found.Add(p);
            }
            catch (Exception) { /* informational only */ }
            return found;
        }

        private void ScanAndProcess(string folder, Func<string, bool> fileFilter = null)
        {
            foreach (string leftover in ListLeftoverBackups(folder))
                Console.WriteLine($" - [WARN] Leftover tag backup from an earlier run (never swept, may be the only good copy): {leftover}");
            try
            {
                var files = Directory.Exists(folder)
                    ? Directory.GetFiles(folder, "*.mp3", SearchOption.AllDirectories)
                    : Array.Empty<string>();
                // Files the caller declines are never scanned or touched.
                if (fileFilter != null) files = files.Where(fileFilter).ToArray();

                foreach (var path in files)
                {
                    // shortcut: a file that vanishes between scan and stat is recorded with size -1, upgrade to a re-scan when the plan must be exact.
                    var info = new FileInfo(path);
                    long size = -1;
                    try { size = info.Length; } catch (IOException) { }
                    Scanned.Add(new ScannedFile { Path = path, Size = size, MtimeUtc = info.LastWriteTimeUtc });
                }

                if (files.Length == 0)
                {
                    Console.WriteLine(" - Nothing to fix!");
                    return;
                }

                foreach (var sourcePath in files)
                {
                    Results.Add(ProcessFile(sourcePath));
                }

                // Per-file tag changes shown in MusicIntegrator's combined routing block.
                // Only surface errors and the count here.
                var tagErrors = Results.Where(l => l.Status == "error").ToList();
                if (tagErrors.Count > 0)
                {
                    Console.WriteLine($"[ERRORS: {tagErrors.Count}]");
                    foreach (var e in tagErrors) Console.WriteLine($" {e.OriginalFilename}: {e.Detail}");
                }
                Console.WriteLine($"Fixed: {fixedCount}  |  Skipped: {skippedCount}");
            }
            finally
            {
                FinishAndPrintTimeTaken("Tag fixing");
            }
        }

        /// <summary>
        /// Test-only entry point. Skips the NewMusicPath directory scan so tests can call
        /// ProcessFile() directly against a fixture path without touching the real inbox.
        /// Pass customRulesPath to load custom rules from a fixture file instead of the repo config.
        /// </summary>
        internal static TagFixer ForTesting(bool dryRun, string customRulesPath = null, string quarantineRoot = null, string exceptionsPath = null, IDictionary<string, string> artistSpellingChoices = null)
        {
            return new TagFixer(dryRun, skipScan: true, customRulesPath: customRulesPath, quarantineRoot: quarantineRoot, exceptionsPath: exceptionsPath, artistSpellingChoices: artistSpellingChoices);
        }

        /// <summary>
        /// Test-only: runs the same scan and processing as the public constructor against folder instead of
        /// Constants.NewMusicPath, so the plan JSON can be exercised on a temp fixture folder.
        /// </summary>
        internal static TagFixer ScanFolderForTesting(string folder, bool dryRun, string customRulesPath = null, string quarantineRoot = null, Func<string, bool> fileFilter = null, IDictionary<string, string> artistSpellingChoices = null)
        {
            var tagFixer = new TagFixer(dryRun, skipScan: true, customRulesPath: customRulesPath, quarantineRoot: quarantineRoot, artistSpellingChoices: artistSpellingChoices);
            tagFixer.ScanAndProcess(folder, fileFilter);
            return tagFixer;
        }

        /// <summary>Scans and processes an arbitrary folder (used by TagFixApplier and tests); same logic as the public constructor.</summary>
        internal static TagFixer ForFolder(string folder, bool dryRun, string quarantineRoot = null)
        {
            return ScanFolderForTesting(folder, dryRun, quarantineRoot: quarantineRoot);
        }

        private TagFixer(bool dryRun, bool skipScan, string customRulesPath = null, string quarantineRoot = null, string exceptionsPath = null, IDictionary<string, string> artistSpellingChoices = null)
        {
            this.dryRun = dryRun;
            this.customRules = TagFixCustomRuleSet.Load(customRulesPath);
            this.exceptions = TagFixExceptions.Load(exceptionsPath);
            this.quarantineRoot = quarantineRoot ?? Constants.QuarantinePath;
            this.artistSpellingChoices = CopyChoices(artistSpellingChoices);
        }

        // Manifest-chosen artist spellings (object manifest form), keyed by ArtistSpellingCandidates.SpellingKey.
        // Empty means no choices, so tags are cleaned exactly as before.
        private readonly Dictionary<string, string> artistSpellingChoices = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private static Dictionary<string, string> CopyChoices(IDictionary<string, string> choices) =>
            choices == null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(choices, StringComparer.OrdinalIgnoreCase);

        /// <summary>Replaces each artist that has a manifest-chosen spelling. Applied after keep=, so keep=artist does not block it.</summary>
        internal List<string> ApplyChosenSpellings(List<string> names)
        {
            if (artistSpellingChoices.Count == 0) return names;
            return names.Select(ChosenSpellingOr).ToList();
        }

        /// <summary>Text form of ApplyChosenSpellings for the keep=artist path; returns the input unchanged when nothing matches.</summary>
        internal string ApplyChosenSpellingsToText(string text)
        {
            if (artistSpellingChoices.Count == 0 || string.IsNullOrEmpty(text)) return text;
            var names = text.Split(';').Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
            var mapped = names.Select(ChosenSpellingOr).ToList();
            return mapped.SequenceEqual(names) ? text : string.Join("; ", mapped);
        }

        private string ChosenSpellingOr(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return name;
            return artistSpellingChoices.TryGetValue(ArtistSpellingCandidates.SpellingKey(name), out string chosen) ? chosen : name;
        }

        /// <summary>
        /// Folder that Akira The Don instrumentals are moved into (never deleted). Constants.QuarantinePath unless a test overrides it.
        /// </summary>
        // BL-58: initialised here so the public constructor (which does not chain to the private one) also gets the default.
        private readonly string quarantineRoot = Constants.QuarantinePath;

        /// <summary>
        /// The cleaned field values ProcessFile would write for a file with these tags: built-in cleanup, then keep= flags
        /// (config/tagfix-exceptions.xml) and custom rules. Pure, no file access; the dry-run routing preview uses it so
        /// it agrees with the real run.
        /// </summary>
        internal (string title, string artists, string album, string genres) PreviewFields(string sourcePath, string title, string artists, string album, string genres)
        {
            string originalName = Path.GetFileName(sourcePath);
            string originalRel = MusicIntegrator.RelToNewMusic(sourcePath);
            bool keepTitle = exceptions.Keeps(originalName, "title", originalRel);
            bool keepAlbum = exceptions.Keeps(originalName, "album", originalRel);
            bool keepArtist = exceptions.Keeps(originalName, "artist", originalRel);
            bool keepCasing = exceptions.Keeps(originalName, "casing", originalRel);

            string cleanTitle = keepTitle ? title : StripTrailingCredit(RemoveParentheticals(title));
            string cleanAlbum = keepAlbum ? album : StripAlbumSuffixes(RemoveParentheticals(album));
            var artistList = keepArtist ? new List<string>() : ApplyChosenSpellings(ExtractAndFixArtists(title, artists, applyCasing: !keepCasing));
            string cleanArtists = keepArtist ? ApplyChosenSpellingsToText(artists) : string.Join("; ", artistList);
            string cleanGenres = ShouldFixGenre(cleanArtists, genres) ? DetermineGenre(cleanArtists) : genres;

            var values = new TagFieldValues { Title = cleanTitle ?? "", Album = cleanAlbum ?? "", Artists = cleanArtists ?? "", Genre = cleanGenres ?? "" };
            var protectedFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (keepTitle) protectedFields.Add("title");
            if (keepAlbum) protectedFields.Add("album");
            if (keepArtist) protectedFields.Add("artists");
            if (customRules != null) customRules.Apply(values, protectedFields);
            return (values.Title, values.Artists, values.Album, values.Genre);
        }

        /// <summary>
        /// Reads, cleans, and (if not dry-run) writes ID3 tags for a single MP3 file, renaming
        /// it to match library convention. Returns the FixLog describing what changed.
        /// </summary>
        internal FixLog ProcessFile(string sourcePath)
        {
            var log = new FixLog { OriginalFilename = Path.GetFileName(sourcePath), SourcePath = sourcePath };
            TagLib.File tagFile = null;
            try
            {
                // Read current tags
                tagFile = TagLib.File.Create(sourcePath);
                var tag = tagFile.Tag;
                var id3 = (TagLib.Id3v2.Tag)tagFile.GetTag(TagLib.TagTypes.Id3v2, true);

                string title = tag.Title ?? "";
                string artists = tag.JoinedPerformers ?? "";
                string album = tag.Album ?? "";
                string genres = tag.JoinedGenres ?? "";
                log.OldTags = new TagState { Title = title, Album = album, Artists = artists, Genre = genres, Compilation = id3.IsCompilation, ArtCount = tag.Pictures.Length };

                // QUARANTINE: Akira The Don instrumentals (Rule from STAGE_3B). Never deleted; moved to quarantineRoot.
                // Match if: artist = "Akira The Don" AND title contains/ends with "Instrumental"
                if (artists.IndexOf("Akira The Don", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    (title.IndexOf("Instrumental", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     title.EndsWith("(Instrumental)", StringComparison.OrdinalIgnoreCase)))
                {
                    string fileName = Path.GetFileName(sourcePath);
                    skippedCount++;
                    log.Status = "skipped";
                    if (dryRun)
                    {
                        Console.WriteLine($"  [WOULD QUARANTINE] {fileName} (Akira The Don instrumental)");
                        log.Detail = "would quarantine instrumental";
                        log.WouldQuarantine = true;
                        log.NewTags = log.OldTags;
                        return log;
                    }

                    // Release the TagLib handle before moving; nulled so the finally does not dispose it twice.
                    tagFile.Dispose();
                    tagFile = null;
                    try
                    {
                        Directory.CreateDirectory(quarantineRoot);
                        string dest = NextFreeQuarantinePath(fileName);
                        File.Move(sourcePath, dest);
                        Console.WriteLine($"  [QUARANTINED] {fileName} -> {dest} (Akira The Don instrumental)");
                        log.Detail = "quarantined instrumental: " + dest;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"  [WARN] Could not quarantine '{fileName}', left in place: {ex.Message}");
                        log.Status = "error";
                        log.Detail = ex.Message;
                    }
                    return log;
                }

                // Per-file keep= flags (config/tagfix-exceptions.xml), keyed by the original NewMusic filename.
                string originalName = Path.GetFileName(sourcePath);
                string originalRel = MusicIntegrator.RelToNewMusic(sourcePath);
                bool keepTitle = exceptions.Keeps(originalName, "title", originalRel);
                bool keepAlbum = exceptions.Keeps(originalName, "album", originalRel);
                bool keepArtist = exceptions.Keeps(originalName, "artist", originalRel);
                bool keepCasing = exceptions.Keeps(originalName, "casing", originalRel);

                // Apply built-in tag cleanup rules (unchanged - custom rules run after these)
                string cleanTitle = keepTitle ? title : StripTrailingCredit(RemoveParentheticals(title));
                string cleanAlbum = keepAlbum ? album : StripAlbumSuffixes(RemoveParentheticals(album));
                // keep=artist: artists stay exactly as read (no feat. split from the title); a manifest-chosen spelling still applies.
                var artistList = keepArtist ? new List<string>() : ApplyChosenSpellings(ExtractAndFixArtists(title, artists, applyCasing: !keepCasing));
                string cleanArtists = keepArtist ? ApplyChosenSpellingsToText(artists) : string.Join("; ", artistList);

                bool tcmpNeeded = !id3.IsCompilation;
                bool genreNeeded = ShouldFixGenre(cleanArtists, genres);
                string cleanGenres = genreNeeded ? DetermineGenre(cleanArtists) : genres;

                // Built-in deltas, recorded before the custom pass so each layer is separately visible
                bool builtInTitleChanged = cleanTitle != title;
                bool builtInAlbumChanged = cleanAlbum != album;
                bool builtInArtistsChanged = cleanArtists != artists && !string.IsNullOrEmpty(cleanArtists);

                // ADDITIVE PASS: user-defined rules from config/tagfix-custom-rules.xml.
                // Operates on the already-cleaned values; never alters built-in behaviour.
                var customValues = new TagFieldValues
                {
                    Title = cleanTitle ?? "",
                    Album = cleanAlbum ?? "",
                    Artists = cleanArtists ?? "",
                    Genre = cleanGenres ?? ""
                };
                // Fields the keep= flags protect are also out of reach of custom rules.
                var protectedFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (keepTitle) protectedFields.Add("title");
                if (keepAlbum) protectedFields.Add("album");
                if (keepArtist) protectedFields.Add("artists");
                var customChanges = customRules != null
                    ? customRules.Apply(customValues, protectedFields)
                    : new List<TagFixCustomRuleChange>();

                string finalTitle = customValues.Title;
                string finalAlbum = customValues.Album;
                string finalArtists = customValues.Artists;
                string finalGenres = customValues.Genre;
                if (customChanges.Any(c => c.Field == "artists"))
                {
                    artistList = finalArtists
                        .Split(';')
                        .Select(a => a.Trim())
                        .Where(a => !string.IsNullOrEmpty(a))
                        .ToList();
                    finalArtists = string.Join("; ", artistList);
                }

                // Tag values this fix would write (the plan JSON reads these; the real run writes the same values).
                log.NewTags = new TagState { Title = finalTitle, Album = finalAlbum, Artists = finalArtists, Genre = finalGenres, Compilation = true, ArtCount = log.OldTags.ArtCount };

                // Check if any changes needed (built-in plus custom)
                bool titleChanged = finalTitle != title;
                bool albumChanged = finalAlbum != album;
                bool artistsChanged = finalArtists != artists && !string.IsNullOrEmpty(finalArtists);
                bool genreChanged = finalGenres != genres;

                if (!titleChanged && !albumChanged && !artistsChanged && !tcmpNeeded && !genreChanged)
                {
                    log.Status = "skipped";
                    log.Detail = "no fixes needed";
                    skippedCount++;
                    return log;
                }

                // Record what will change - built-in fixes first, then each custom rule's own change
                if (builtInTitleChanged) log.Changes.Add($"Title: \"{title}\"  -> \"{cleanTitle}\"");
                if (builtInAlbumChanged) log.Changes.Add($"Album: \"{album}\"  -> \"{cleanAlbum}\"");
                if (builtInArtistsChanged) log.Changes.Add($"Artists: \"{artists}\"  -> \"{cleanArtists}\"");
                // TCMP fix is silent - almost always needed, adds noise to output
                if (genreNeeded) log.Changes.Add($"Genre: \"{genres}\"  -> \"{cleanGenres}\"");
                foreach (var change in customChanges) log.Changes.Add(change.Describe());

                // Apply changes (write tags + rename file)
                if (!dryRun)
                {
                    if (titleChanged) tag.Title = finalTitle;
                    if (albumChanged) tag.Album = finalAlbum;
                    if (artistsChanged) tag.Performers = artistList.ToArray();
                    if (tcmpNeeded) id3.IsCompilation = true;
                    if (genreChanged) tag.Genres = new[] { finalGenres };

                    // Keep a copy of the original bytes so a failed or interrupted write can be undone.
                    string backupPath = BackupBeforeWrite(sourcePath);
                    try
                    {
                        Saver(tagFile);
                    }
                    catch (Exception saveEx)
                    {
                        string name = Path.GetFileName(sourcePath);
                        tagFile.Dispose();
                        tagFile = null;
                        try
                        {
                            RestoreFromBackup(backupPath, sourcePath);
                            DeleteBackup(backupPath);
                            Console.WriteLine($"  [WARN] Tag write failed for '{name}', original restored: {saveEx.Message}");
                            log.Status = "error";
                            log.Detail = saveEx.Message;
                        }
                        catch (Exception restoreEx)
                        {
                            // The backup is the only intact copy now, so it is kept on disk.
                            Console.WriteLine($"  [WARN] Tag write failed for '{name}' and restore failed; original kept at '{backupPath}': {restoreEx.Message}");
                            log.Status = "error";
                            log.Detail = "restore failed: " + restoreEx.Message;
                        }
                        return log;
                    }

                    // Release the TagLib handle before renaming (an open handle can block File.Move on Windows).
                    // Nulled so the finally does not dispose it twice.
                    tagFile.Dispose();
                    tagFile = null;
                    DeleteBackup(backupPath);

                    // Rename file: {artists} - {title}.mp3
                    string sanitisedArtists = Reflector.SanitiseFilename(finalArtists);
                    string sanitisedTitle = Reflector.SanitiseFilename(finalTitle);
                    if (string.IsNullOrEmpty(sanitisedArtists) || string.IsNullOrEmpty(sanitisedTitle))
                    {
                        log.Changes.Add("[WARN] Rename skipped: empty artist or title after sanitisation");
                        log.Filename = log.OriginalFilename;
                    }
                    else
                    {
                        string newFilename = $"{sanitisedArtists} - {sanitisedTitle}.mp3";
                        string newPath = Path.Combine(Path.GetDirectoryName(sourcePath), newFilename);
                        // A case-only rename resolves to the source file on a case-insensitive volume, so File.Exists is true; it must still move.
                        bool caseOnlyRename = newPath.Equals(sourcePath, StringComparison.OrdinalIgnoreCase);
                        if (newPath != sourcePath && (caseOnlyRename || !File.Exists(newPath)))
                        {
                            // Tags are already saved, so a failed rename keeps the fixed status and the original name.
                            try
                            {
                                File.Move(sourcePath, newPath);
                                log.Changes.Add($"Filename: \"{Path.GetFileName(sourcePath)}\"  -> \"{newFilename}\"");
                                log.Filename = newFilename;
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"  [WARN] Could not rename '{Path.GetFileName(sourcePath)}' to '{newFilename}', kept original name: {ex.Message}");
                                log.Filename = log.OriginalFilename;
                                log.Detail = "rename failed: " + ex.Message;
                            }
                        }
                        else if (newPath.Equals(sourcePath, StringComparison.OrdinalIgnoreCase))
                        {
                            log.Filename = newFilename;
                        }
                        else
                        {
                            // A different file holds the target name: keep the original name so tag changes are not attributed to it.
                            log.Changes.Add($"[WARN] Rename skipped: '{newFilename}' already exists");
                            Console.WriteLine($"  [WARN] Rename skipped: '{newFilename}' already exists, kept '{log.OriginalFilename}'");
                            log.Filename = log.OriginalFilename;
                        }
                    }
                    log.Status = "fixed";
                    fixedCount++;
                }
                else
                {
                    // Dry run: show what would change
                    string sanitisedArtists = Reflector.SanitiseFilename(finalArtists);
                    string sanitisedTitle = Reflector.SanitiseFilename(finalTitle);
                    if (string.IsNullOrEmpty(sanitisedArtists) || string.IsNullOrEmpty(sanitisedTitle))
                    {
                        log.Changes.Add("[WARN] Rename skipped: empty artist or title after sanitisation");
                        log.Filename = log.OriginalFilename;
                        log.NeedsManual = true;
                        log.ManualReason = "empty artist or title after sanitisation";
                        log.RenameBlocked = true;
                    }
                    else
                    {
                        string newFilename = $"{sanitisedArtists} - {sanitisedTitle}.mp3";
                        log.Filename = newFilename;
                        // Read-only check, mirrors the real run: a different file already holds the target name.
                        string targetPath = Path.Combine(Path.GetDirectoryName(sourcePath), newFilename);
                        if (!newFilename.Equals(log.OriginalFilename, StringComparison.OrdinalIgnoreCase) && File.Exists(targetPath))
                        {
                            log.NeedsManual = true;
                            log.ManualReason = "rename target already exists";
                            log.RenameBlocked = true;
                        }
                    }
                    log.Status = "would-fix";
                    fixedCount++;
                }

                // Register changes for combined display in MusicIntegrator's routing block
                var nonFilenameChanges = log.Changes.Where(c => !c.StartsWith("Filename:", StringComparison.Ordinal)).ToList();
                if (nonFilenameChanges.Count > 0)
                {
                    FileChanges[log.OriginalFilename] = nonFilenameChanges;
                    if (!string.IsNullOrEmpty(log.Filename) && !log.Filename.Equals(log.OriginalFilename, StringComparison.OrdinalIgnoreCase))
                        FileChanges[log.Filename] = nonFilenameChanges;
                }

                // GUI-54: every file that reaches the apply stage (tag fix, TCMP-only, or a filename-only
                // rename that adds no change text in a dry run) gets its structured before/after registered.
                LogsByFile[log.OriginalFilename] = log;
                if (!string.IsNullOrEmpty(log.Filename) && !log.Filename.Equals(log.OriginalFilename, StringComparison.OrdinalIgnoreCase))
                    LogsByFile[log.Filename] = log;

                return log;
            }
            catch (Exception ex)
            {
                Console.WriteLine($" - Skipped '{Path.GetFileName(sourcePath)}': error reading file ({ex.Message})");
                log.Status = "error";
                log.Detail = ex.Message;
                skippedCount++;
                return log;
            }
            finally
            {
                // Release the TagLib file handle (TagLib keeps it open until disposed)
                tagFile?.Dispose();
            }
        }

        /// <summary>
        /// Copies sourcePath to sourcePath + ".tagfix.bak" before a tag write. Any stale backup from an
        /// earlier crashed run is moved aside (.stale-N), never deleted. The .bak suffix keeps it out of the *.mp3 scan.
        /// </summary>
        private static string BackupBeforeWrite(string sourcePath)
        {
            string backupPath = sourcePath + ".tagfix.bak";
            if (File.Exists(backupPath))
            {
                // A stale backup may be the only good copy (crash mid-save): move it aside, never delete.
                string aside;
                int n = 1;
                do { aside = backupPath + ".stale-" + n++; } while (File.Exists(aside));
                File.Move(backupPath, aside);
            }
            File.Copy(sourcePath, backupPath, false);
            return backupPath;
        }

        /// <summary>
        /// Puts the backed-up original bytes back over sourcePath after a failed tag write.
        /// </summary>
        private static void RestoreFromBackup(string backupPath, string sourcePath)
        {
            Restorer(backupPath, sourcePath);
        }

        /// <summary>
        /// Deletes the backup once the write is safe. A failure only warns: the file itself is not failed for it.
        /// </summary>
        private static void DeleteBackup(string backupPath)
        {
            try
            {
                if (File.Exists(backupPath)) File.Delete(backupPath);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  [WARN] Could not delete tag backup '{Path.GetFileName(backupPath)}': {ex.Message}");
            }
        }

        /// <summary>
        /// Returns a path in quarantineRoot that does not exist yet: the original name first, then a
        /// timestamp suffix, then a counter. Never returns an existing file, so nothing is overwritten.
        /// </summary>
        private string NextFreeQuarantinePath(string fileName)
        {
            string direct = Path.Combine(quarantineRoot, fileName);
            if (!File.Exists(direct)) return direct;

            string stem = Path.GetFileNameWithoutExtension(fileName);
            string ext = Path.GetExtension(fileName);
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
            string stamped = Path.Combine(quarantineRoot, $"{stem} ({stamp}){ext}");
            if (!File.Exists(stamped)) return stamped;

            for (int n = 2; ; n++)
            {
                string candidate = Path.Combine(quarantineRoot, $"{stem} ({stamp}) {n}{ext}");
                if (!File.Exists(candidate)) return candidate;
            }
        }

        /// <summary>
        /// Removes entire parenthetical phrases from a string.
        /// Patterns: (feat. X), (ft. X), (Album Version), (Explicit), (Edit), (Radio Edit), (Original), (Remix), (Version)
        /// </summary>
        internal static string RemoveParentheticals(string input)
        {
            if (string.IsNullOrEmpty(input)) return input;

            string result = input;
            // Patterns to remove (in order of priority)
            var patterns = new[]
            {
                @"\s*\(feat\.\s+[^)]+\)", // (feat. Artist)
                @"\s*\(ft\.\s+[^)]+\)",   // (ft. Artist)
                @"\s*\(with\s+[^)]+\)",   // (with Artist)
                @"\s*\[feat\.\s+[^\]]+\]", // [feat. Artist]
                @"\s*\[ft\.\s+[^\]]+\]",   // [ft. Artist]
                @"\s*\(Album\s+Version\)", // (Album Version)
                @"\s*\(Single\s+Version\)", // (Single Version)
                @"\s*\(Radio\s+Version\)", // (Radio Version) - title field; album field already handled by StripAlbumSuffixes
                @"\s*\(Explicit\)",       // (Explicit)
                @"\s*\(Radio\s+Edit\)",  // (Radio Edit)
                @"\s*\(Edit\)",           // (Edit)
                @"\s*\(Original\)",       // (Original)
                @"\s*\(Version\)",        // (Version)
            };

            foreach (var pattern in patterns)
            {
                result = Regex.Replace(result, pattern, "", RegexOptions.IgnoreCase);
            }

            // Clean up any double spaces left behind
            result = Regex.Replace(result, @"\s+", " ");
            return result.Trim();
        }

        /// <summary>
        /// Strips album-folder suffix patterns from an album tag value.
        /// Called after RemoveParentheticals - handles edition markers, remaster tags, version
        /// qualifiers, and year suffixes that folder names inject into album metadata.
        /// NOT applied to title field - only album.
        /// Test cases: "(International Version)" -> "", "(Deluxe Edition)" -> "",
        /// "(2011 Remaster)" -> "", "(Remastered)" -> "", "(Bonus Tracks)" -> "",
        /// "(2019)" -> "", plain album unchanged.
        /// </summary>
        internal static string StripAlbumSuffixes(string album)
        {
            if (string.IsNullOrEmpty(album)) return album;

            string result = album;
            var patterns = new[]
            {
                @"\s*\([^)]*\s+Version\)",                                                   // (International Version), (Radio Version), (Acoustic Version), etc.
                @"\s*\(Deluxe(?:\s+Edition)?\)",                                             // (Deluxe Edition) or (Deluxe)
                @"\s*\((?:Special|Limited|Collector'?s?|Anniversary|Expanded|Extended)\s+Edition\)", // edition markers
                @"\s*\((?:\d{4}\s+)?Remaster(?:ed)?(?:\s+Edition)?\)",                          // (Remastered), (Remaster), (2011 Remaster), (2011 Remastered), (2014 Remastered Edition)
                @"\s*\(Mono\)",                                                              // (Mono)
                @"\s*\(Stereo\)",                                                            // (Stereo)
                @"\s*\(Bonus\s+Tracks?\)",                                                   // (Bonus Track) or (Bonus Tracks)
                @"\s*\((?:19|20)\d{2}\)",                                                    // year suffixes: (2019), (1995), etc.
            };

            foreach (var pattern in patterns)
            {
                result = Regex.Replace(result, pattern, "", RegexOptions.IgnoreCase);
            }

            result = Regex.Replace(result, @"\s+", " ");
            return result.Trim();
        }

        // BL-65: a bare credit at the END of a title ("360 featuring A & B", "Song ft. A"). Never mid-title, never bare "with"
        // ("Stay With Me" is a real title; "(with X)" is already handled in parentheses).
        private static readonly Regex BareTrailingCredit = new Regex(@"\S\s+(?:featuring|feat\.?|ft\.?)\s+(\S.*)$", RegexOptions.IgnoreCase);
        private static readonly Regex BareTrailingCreditCut = new Regex(@"\s+(?:featuring|feat\.?|ft\.?)\s+\S.*$", RegexOptions.IgnoreCase);

        /// <summary>Title cleanup: the built-in parenthetical strip plus a trailing bare credit and "(featuring X)".</summary>
        internal static string StripTrailingCredit(string title)
        {
            if (string.IsNullOrEmpty(title)) return title;
            string result = Regex.Replace(title, @"\s*[\(\[]\s*featuring\s+[^)\]]+[\)\]]", "", RegexOptions.IgnoreCase);
            if (BareTrailingCredit.IsMatch(result)) result = BareTrailingCreditCut.Replace(result, "");
            return Regex.Replace(result, @"\s+", " ").Trim();
        }

        private static Dictionary<string, string> _artistOverrides;

        // shortcut: static cache plus reset seam for tests, upgrade to an injected provider if overrides ever need a per-run path.
        internal static int OverridesLoadCount;

        /// <summary>
        /// Loads artist name overrides from config/artist-name-overrides.xml (once per process, then cached).
        /// Keys and values are both the canonical form; lookup is case-insensitive.
        /// </summary>
        internal static Dictionary<string, string> GetArtistOverrides()
        {
            if (_artistOverrides == null)
            {
                OverridesLoadCount++;
                _artistOverrides = LoadArtistOverrides(Constants.ArtistOverridesPath);
            }
            return _artistOverrides;
        }

        /// <summary>Clears the overrides cache so the next GetArtistOverrides call reloads the file (tests only).</summary>
        internal static void ResetArtistOverridesCache()
        {
            _artistOverrides = null;
        }

        /// <summary>
        /// Parses an artist overrides file. A missing file yields an empty map silently; a malformed
        /// file logs a [WARN] with the path and returns whatever parsed before the failure.
        /// </summary>
        internal static Dictionary<string, string> LoadArtistOverrides(string path)
        {
            var overrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (!File.Exists(path)) return overrides;
                var doc = new XmlDocument();
                doc.Load(path);
                foreach (XmlElement el in doc.SelectNodes("//Artist").OfType<XmlElement>())
                {
                    string canonical = el.GetAttribute("canonical");
                    if (!string.IsNullOrEmpty(canonical))
                    {
                        overrides[canonical] = canonical;
                        // Optional variant attribute maps stylized/Unicode-variant names to the canonical form.
                        // Needed when Unicode diacritics (e.g. JAŸ-Z) differ from the canonical ASCII form (Jay-Z)
                        // and OrdinalIgnoreCase can't match them as equal.
                        string variant = el.GetAttribute("variant");
                        if (!string.IsNullOrEmpty(variant))
                            overrides[variant] = canonical;
                    }
                }
            }
            catch (Exception ex)
            {
                // Best-effort: names fall back to plain title-casing, but the user must see why overrides were skipped.
                Console.WriteLine($"  [WARN] Could not read artist overrides '{path}': {ex.Message}");
            }
            return overrides;
        }

        /// <summary>
        /// Dedupe key for an artist name: case-insensitive, and every apostrophe form
        /// (straight, curly, modifier letter, backtick) plus a missing apostrophe compares equal.
        /// Display strings are never changed; this only decides whether two spellings are one artist.
        /// </summary>
        internal static string ArtistKey(string name)
        {
            var sb = new StringBuilder(name.Length);
            foreach (char c in name)
            {
                switch (c)
                {
                    case '\'':
                    case '’':
                    case '‘':
                    case 'ʼ':
                    case '`':
                        break;
                    default:
                        sb.Append(char.ToUpperInvariant(c));
                        break;
                }
            }
            return sb.ToString();
        }

        /// <summary>Equality over <see cref="ArtistKey"/>; one definition used by every artist dedupe step.</summary>
        internal sealed class ArtistNameComparer : IEqualityComparer<string>
        {
            internal static readonly ArtistNameComparer Instance = new ArtistNameComparer();

            public bool Equals(string x, string y)
            {
                return string.Equals(ArtistKey(x), ArtistKey(y), StringComparison.Ordinal);
            }

            public int GetHashCode(string obj)
            {
                return ArtistKey(obj).GetHashCode();
            }
        }

        /// <summary>
        /// Extracts featured artists from title parentheticals and combines with existing artists.
        /// Returns a list of artists with primary artist first, others semicolon-separated.
        /// </summary>
        internal static List<string> ExtractAndFixArtists(string title, string currentArtists, bool applyCasing = true)
        {
            var artists = new List<string>();

            // Parse existing artists
            if (!string.IsNullOrEmpty(currentArtists))
            {
                var existing = Track.ProcessProperty(currentArtists).ToList();
                artists.AddRange(existing);
            }

            // Extract featured artists from title
            var featPatterns = new[]
            {
                @"\(feat\.\s+([^)]+)\)",  // (feat. Artist)
                @"\(ft\.\s+([^)]+)\)",    // (ft. Artist)
                @"\(with\s+([^)]+)\)",    // (with Artist)
                @"\[feat\.\s+([^\]]+)\]", // [feat. Artist]
                @"\[ft\.\s+([^\]]+)\]",   // [ft. Artist]
                @"[\(\[]\s*featuring\s+([^)\]]+)[\)\]]", // (featuring Artist)  BL-65
                @"\S\s+(?:featuring|feat\.?|ft\.?)\s+(\S.*)$", // trailing bare credit  BL-65
            };

            foreach (var pattern in featPatterns)
            {
                var matches = Regex.Matches(title, pattern, RegexOptions.IgnoreCase);
                foreach (Match match in matches)
                {
                    if (match.Groups.Count > 1)
                    {
                        // "&" in title parentheticals is always a separator, never part of a name.
                        // Split "A & B" into individual artists so compound forms don't duplicate
                        // their components (e.g. "X & Y" when "X" and "Y" are already present).
                        // " and " and commas separate too (BL-60): the combined string is never added as one artist.
                        var featParts = Regex.Split(match.Groups[1].Value.Trim(), @"\s+&\s+(?!the\s)|\s+and\s+(?!the\s)|\s*,\s*", RegexOptions.IgnoreCase)
                            .Select(p => p.Trim())
                            .Where(p => !string.IsNullOrEmpty(p));
                        foreach (var featArtist in featParts)
                        {
                            // Skip exact duplicates
                            if (artists.Any(a => ArtistNameComparer.Instance.Equals(a, featArtist)))
                                continue;
                            // Skip "X of BandName" clarifications where BandName is already present.
                            // e.g. "(feat. Macklemore & Patrick Stump of Fall Out Boy)" when "Fall Out Boy"
                            // is already in the artist field - "of BandName" is a descriptor, not a separator.
                            int ofIdx = featArtist.IndexOf(" of ", StringComparison.OrdinalIgnoreCase);
                            if (ofIdx >= 0 && artists.Any(a => ArtistNameComparer.Instance.Equals(a, featArtist.Substring(ofIdx + 4).Trim())))
                                continue;
                            artists.Add(featArtist);
                        }
                    }
                }
            }

            // Casing (BL-61): the source tag is the truth, never recased (PARTYNEXTDOOR, RZA, MF DOOM, SZA).
            // Only the explicit overrides list (config/artist-name-overrides.xml) maps a name to a canonical form.
            // keep=casing (TagFixExceptions) passes applyCasing false, so the source casing is kept as read.
            var overrides = applyCasing ? GetArtistOverrides() : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var cased = artists
                .Select(a => overrides.TryGetValue(a, out string canonical) ? canonical : a)
                .Distinct(ArtistNameComparer.Instance)
                .ToList();

            // A comma-joined entry whose every part is already its own entry is a combined duplicate (BL-60).
            return cased
                .Where(a =>
                {
                    if (a.IndexOf(',') < 0) return true;
                    var parts = a.Split(',').Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
                    return parts.Count < 2 || !parts.All(p => cased.Any(o => !ReferenceEquals(o, a) && ArtistNameComparer.Instance.Equals(o, p)));
                })
                .ToList();
        }

        /// <summary>
        /// Checks if genre needs to be fixed.
        /// Musivation artists (ATD, Loot Bryon Smith): genre must be "Musivation".
        /// Generic Motivation tracks: any genre containing "Motivation" is normalized to exactly "Motivation".
        /// </summary>
        internal static bool ShouldFixGenre(string artists, string currentGenres)
        {
            if (string.IsNullOrEmpty(artists)) return false;

            // Musivation artists must have Musivation genre
            if (artists.IndexOf("Akira The Don", StringComparison.OrdinalIgnoreCase) >= 0 ||
                artists.IndexOf("Loot Bryon Smith", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return currentGenres.IndexOf("Musivation", StringComparison.OrdinalIgnoreCase) < 0;
            }

            // Generic Motivation: normalize non-exact "Motivation" genre variants (e.g. "Motivational")
            if (currentGenres.IndexOf("Motivation", StringComparison.OrdinalIgnoreCase) >= 0 &&
                currentGenres.IndexOf("Musivation", StringComparison.OrdinalIgnoreCase) < 0)
            {
                return !currentGenres.Equals("Motivation", StringComparison.OrdinalIgnoreCase);
            }

            return false;
        }

        /// <summary>
        /// Determines what genre should be set. Only called when ShouldFixGenre returns true.
        /// Musivation artists -> "Musivation"; all other cases -> "Motivation" (only other fix case).
        /// </summary>
        internal static string DetermineGenre(string artists)
        {
            if (string.IsNullOrEmpty(artists)) return "";

            if (artists.IndexOf("Akira The Don", StringComparison.OrdinalIgnoreCase) >= 0 ||
                artists.IndexOf("Loot Bryon Smith", StringComparison.OrdinalIgnoreCase) >= 0)
                return Constants.MusivDir; // "Musivation"

            return Constants.MotivDir; // "Motivation" - the only other case ShouldFixGenre triggers for
        }
    }
}
