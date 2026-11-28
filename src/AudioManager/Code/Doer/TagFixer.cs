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

        // Integrate path only: journal plus 7-day byte backup under data/backups. Null for TagFixApplier's own runs (it keeps its journal) and dry runs.
        private TagFixApplier.RunJournal runJournal;

        internal void EnableRunJournal(TagFixApplier.RunJournal journal) { runJournal = journal; }

        /// <summary>
        /// User-defined rules from config/tagfix-custom-rules.xml, applied as an additive pass
        /// AFTER the built-in hardcoded fixes. Empty when the file is missing or malformed.
        /// </summary>
        private readonly TagFixCustomRuleSet customRules;

        /// <summary>
        /// Per-file keep= flags from config/tagfix-exceptions.xml. Empty when the file is missing or malformed.
        /// </summary>
        private readonly TagFixExceptions exceptions;

        /// <summary>Accept rule id: the user accepted the title-suffix strip for this file (apply_title).</summary>
        internal const string ApplyTitleRule = "apply_title";

        /// <summary>
        /// Library-mirror lookup for the apply_title collision guard: true when the mirror already holds artist + title.
        /// A test seam; the default reads the AudioMirror XMLs once per process and only when an accepted strip is checked.
        /// </summary>
        internal static Func<string, string, bool> MirrorHasTitle = DefaultMirrorHasTitle;
        private static Dictionary<string, string> mirrorKeys;

        // Artist + normalised title keys this run has already claimed for an accepted strip, so two files in one run cannot collide.
        private readonly HashSet<string> claimedStrippedTitles = new HashSet<string>(StringComparer.Ordinal);

        private static bool DefaultMirrorHasTitle(string artist, string title)
        {
            // No catch: an unreadable mirror fails the file (ProcessFile's outer catch), never a write that might duplicate.
            if (mirrorKeys == null) mirrorKeys = new MirrorIndex(IntegrationRoots.FromConstants()).BuildMirrorIndex();
            return mirrorKeys.ContainsKey(artist.ToLowerInvariant() + "\0" + MirrorIndex.NormaliseTitleForDedup(title));
        }

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
            /// <summary>Dry run only: the planned rename is blocked (empty title or artists, or the target name is taken).</summary>
            public bool NeedsManual;
            public string ManualReason;
            /// <summary>Stable rule id for a residual-tag needsManual row (unwanted_text, missing_tag); null for the rename reasons.</summary>
            public string ManualRule;
            /// <summary>Suggested title for an unwanted_text row whose flagged word is in the title; empty when none. Suggestion only, nothing applies it.</summary>
            public string SuggestedTitle = "";
            /// <summary>Suggested album for an unwanted_text row whose flagged word is in the album; empty when none. Suggestion only, nothing applies it.</summary>
            public string SuggestedAlbum = "";
            /// <summary>The album tag before any fix, set with SuggestedAlbum; empty when there is no album suggestion.</summary>
            public string OldAlbum = "";
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
            : this(Constants.NewMusicPath, dryRun, fileFilter, artistSpellingChoices)
        {
        }

        /// <summary>Scans and processes newMusicRoot. The public constructor passes Constants.NewMusicPath; the integrate pre-flight passes the folder it was given.</summary>
        internal TagFixer(string newMusicRoot, bool dryRun, Func<string, bool> fileFilter, IDictionary<string, string> artistSpellingChoices)
        {
            this.dryRun = dryRun;
            this.customRules = TagFixCustomRuleSet.Load();
            this.exceptions = TagFixExceptions.Load();
            this.artistSpellingChoices = CopyChoices(artistSpellingChoices);
            string modeLabel = dryRun ? " [DRY RUN - no files will be modified]" : "";
            Console.WriteLine($"\nFixing music tags...{modeLabel}");

            // The real integrate write is journaled and backed up like an apply (docs: tagfix journal, retention 7 days).
            if (!dryRun) runJournal = TagFixApplier.RunJournal.Begin(newMusicRoot, Constants.LogsPath, Constants.BackupsPath, quarantineRoot, DateTime.Now);
            ScanAndProcess(newMusicRoot, fileFilter);
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

        /// <summary>Read-only dry-run scan: files with a pending fix or quarantine, plus errored or manual-review files.</summary>
        internal static int CountPending(string folder, string quarantineRoot)
        {
            var scan = ForFolder(folder, true, quarantineRoot);
            return scan.Results.Count(l => l.Status == "would-fix" || l.WouldQuarantine || l.Status == "error" || l.NeedsManual);
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

                // Every file is journaled pending before the first write, so a crash shows which files never finished.
                if (runJournal != null && !dryRun) runJournal.RegisterPending(files);

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
        internal static TagFixer ScanFolderForTesting(string folder, bool dryRun, string customRulesPath = null, string quarantineRoot = null, Func<string, bool> fileFilter = null, IDictionary<string, string> artistSpellingChoices = null, string exceptionsPath = null, TagFixApplier.RunJournal runJournal = null)
        {
            var tagFixer = new TagFixer(dryRun, skipScan: true, customRulesPath: customRulesPath, quarantineRoot: quarantineRoot, exceptionsPath: exceptionsPath, artistSpellingChoices: artistSpellingChoices);
            tagFixer.runJournal = runJournal;
            tagFixer.ScanAndProcess(folder, fileFilter);
            return tagFixer;
        }

        /// <summary>Scans and processes an arbitrary folder (used by TagFixApplier and tests); same logic as the public constructor.</summary>
        internal static TagFixer ForFolder(string folder, bool dryRun, string quarantineRoot = null, string exceptionsPath = null)
        {
            return ScanFolderForTesting(folder, dryRun, quarantineRoot: quarantineRoot, exceptionsPath: exceptionsPath);
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

        /// <summary>True on real runs (set by Program): a quarantined instrumental goes to the Recycle Bin and the quarantine
        /// folder is only the fallback for a file the bin cannot take. Default false keeps the folder move for tests.</summary>
        internal static bool QuarantineToRecycleBin;

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
                string coverIssue = CoverCheck.Evaluate(tag.Pictures);
                (string coverWidth, string coverHeight) = CoverSize(tag.Pictures);
                string lengthValue = FormatLength(tagFile.Properties?.Duration ?? TimeSpan.Zero);
                // Same convention as TrackTag (Year 0 or an implausible year is "Missing", BL-316), so the residual check agrees with LibChecker's "has no Year set".
                string yearValue = YearRule.ToStoredYear(tag.Year, sourcePath);

                // Empty title AND artists: derive both from an "Artist - Title.mp3" file name so the file is a normal fix.
                string tagTitle = title, tagArtists = artists;
                bool derivedFromName = false;
                if (title.Length == 0 && artists.Length == 0)
                {
                    string stem = Path.GetFileNameWithoutExtension(sourcePath);
                    int sep = stem.IndexOf(" - ", StringComparison.Ordinal);
                    if (sep > 0)
                    {
                        string dArtist = stem.Substring(0, sep).Trim(), dTitle = stem.Substring(sep + 3).Trim();
                        if (dArtist.Length > 0 && dTitle.Length > 0) { artists = dArtist; title = dTitle; derivedFromName = true; }
                    }
                }

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
                        if (QuarantineToRecycleBin)
                        {
                            long binSize = RecycleBin.SizeOf(sourcePath);
                            try
                            {
                                RecycleBin.Send(sourcePath);
                                Console.WriteLine($"  [RECYCLE] {sourcePath} ({binSize} bytes): recycled");
                                Console.WriteLine($"  [QUARANTINED] {fileName} -> Recycle Bin (Akira The Don instrumental)");
                                log.Detail = "quarantined instrumental: " + RecycleBin.HeldMarker;
                                return log;
                            }
                            catch (RecycleBinTooLargeException)
                            {
                                Console.WriteLine($"  [RECYCLE] {sourcePath} ({binSize} bytes): too large for Recycle Bin, moving to the quarantine folder instead");
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"  [RECYCLE] {sourcePath} ({binSize} bytes): failed, {ex.GetType().Name}; moving to the quarantine folder instead");
                            }
                        }
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

                // apply_title (Accept row): the user accepted the title-suffix strip. Recomputed here, never trusted from a caller.
                if (exceptions.Accepts(originalName, ApplyTitleRule, originalRel))
                {
                    string flaggedHit = Constants.UnwantedInfo.FirstOrDefault(u => LibChecker.ContainsUnwanted(finalTitle, u));
                    string stripped = flaggedHit == null ? "" : TitleSuffixStripper.Suggest(finalTitle, flaggedHit);
                    if (stripped.Length > 0)
                    {
                        // Title collision is checked before the album strip, so a refused title leaves the album alone too.
                        string primary = finalArtists.Split(';')[0].Trim();
                        if (TitleWouldDuplicate(sourcePath, primary, stripped))
                        {
                            // Refused before any write: the file keeps its name and tags in both the dry run and the real run.
                            log.NeedsManual = true;
                            log.ManualRule = "unwanted_text";
                            log.SuggestedTitle = "";
                            log.Status = "skipped";
                            skippedCount++;
                            if (dryRun)
                            {
                                log.ManualReason = $"found '{flaggedHit}' in title (would duplicate)";
                                log.Detail = "accepted title strip collides";
                            }
                            else
                            {
                                log.ManualReason = "would duplicate: " + stripped;
                                log.Detail = "refused: accepted title strip collides";
                            }
                            log.NewTags = new TagState { Title = finalTitle, Album = finalAlbum, Artists = finalArtists, Genre = finalGenres, Compilation = true, ArtCount = log.OldTags.ArtCount };
                            return log;
                        }
                        log.Changes.Add($"Title: \"{finalTitle}\"  -> \"{stripped}\" (accepted suggestion)");
                        finalTitle = stripped;
                        claimedStrippedTitles.Add(StrippedTitleKey(primary, stripped));
                    }
                    // Album strip: same rule and same accept, no collision check (an album name is not a file name).
                    string albumHit = Constants.UnwantedInfo.FirstOrDefault(u => LibChecker.ContainsUnwanted(finalAlbum, u));
                    string strippedAlbum = albumHit == null ? "" : TitleSuffixStripper.Suggest(finalAlbum, albumHit);
                    if (strippedAlbum.Length > 0)
                    {
                        log.Changes.Add($"Album: \"{finalAlbum}\"  -> \"{strippedAlbum}\" (accepted suggestion)");
                        finalAlbum = strippedAlbum;
                    }
                }

                // Last line of defence: no path above may hand back padded or double-spaced text. A field the user protected
                // with keep= stays exactly as read.
                if (!keepTitle) finalTitle = TidyWhitespace(finalTitle);
                if (!keepAlbum) finalAlbum = TidyWhitespace(finalAlbum);
                if (!keepArtist) finalArtists = TidyWhitespace(finalArtists);

                // Tag values this fix would write (the plan JSON reads these; the real run writes the same values).
                log.NewTags =new TagState { Title = finalTitle, Album = finalAlbum, Artists = finalArtists, Genre = finalGenres, Compilation = true, ArtCount = log.OldTags.ArtCount };

                // Check if any changes needed (built-in plus custom)
                bool titleChanged = finalTitle != tagTitle;
                bool albumChanged = finalAlbum != album;
                bool artistsChanged = finalArtists != tagArtists && !string.IsNullOrEmpty(finalArtists);
                bool genreChanged = finalGenres != genres;
                // A file whose name no longer matches its tags (artists added in Mp3tag) needs the rename even when no tag changes.
                bool filenameChanged = FilenameNeedsRename(originalName, finalArtists, finalTitle);

                if (!titleChanged && !albumChanged && !artistsChanged && !tcmpNeeded && !genreChanged && !filenameChanged)
                {
                    log.Status = "skipped";
                    log.Detail = "no fixes needed";
                    MarkResidualIssue(log, finalTitle, finalArtists, finalAlbum, yearValue, originalName, originalRel, lengthValue, coverWidth, coverHeight, coverIssue);
                    skippedCount++;
                    return log;
                }

                // Record what will change - built-in fixes first, then each custom rule's own change
                if (builtInTitleChanged) log.Changes.Add($"Title: \"{title}\"  -> \"{cleanTitle}\"");
                if (builtInAlbumChanged) log.Changes.Add($"Album: \"{album}\"  -> \"{cleanAlbum}\"");
                if (builtInArtistsChanged) log.Changes.Add($"Artists: \"{artists}\"  -> \"{cleanArtists}\"");
                if (derivedFromName) log.Changes.Add($"Tags derived from file name: artists \"{finalArtists}\", title \"{finalTitle}\"");
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
                    // With a run journal the entry is written first, then the bytes go to data/backups and stay there.
                    string backupPath = runJournal != null
                        ? runJournal.BeforeWrite(sourcePath, PlannedFilename(finalArtists, finalTitle, log.OriginalFilename), log.OldTags, log.NewTags)
                        : BackupBeforeWrite(sourcePath);
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
                            if (runJournal == null) DeleteBackup(backupPath);
                            Console.WriteLine($"  [WARN] Tag write failed for '{name}', original restored: {saveEx.Message}");
                            log.Status = "error";
                            log.Detail = saveEx.Message;
                            runJournal?.Fail(sourcePath, saveEx.Message);
                        }
                        catch (Exception restoreEx)
                        {
                            // The backup is the only intact copy now, so it is kept on disk.
                            Console.WriteLine($"  [WARN] Tag write failed for '{name}' and restore failed; original kept at '{backupPath}': {restoreEx.Message}");
                            log.Status = "error";
                            log.Detail = "restore failed: " + restoreEx.Message;
                            runJournal?.Fail(sourcePath, log.Detail);
                        }
                        return log;
                    }

                    // Release the TagLib handle before renaming (an open handle can block File.Move on Windows).
                    // Nulled so the finally does not dispose it twice.
                    tagFile.Dispose();
                    tagFile = null;
                    // The journal run keeps its byte backup for the retention window; only the legacy sibling backup is deleted.
                    if (runJournal == null) DeleteBackup(backupPath);

                    // Rename file: {artists} - {title}.mp3
                    string sanitisedArtists = Reflector.SanitiseFilename(finalArtists);
                    string sanitisedTitle = Reflector.SanitiseFilename(finalTitle);
                    if (string.IsNullOrEmpty(sanitisedArtists) || string.IsNullOrEmpty(sanitisedTitle))
                    {
                        log.Changes.Add("[WARN] Rename skipped: " + EmptyFieldReason(sanitisedArtists, sanitisedTitle));
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
                    runJournal?.Done(sourcePath, log.Filename);
                    fixedCount++;
                }
                else
                {
                    // Dry run: show what would change
                    string sanitisedArtists = Reflector.SanitiseFilename(finalArtists);
                    string sanitisedTitle = Reflector.SanitiseFilename(finalTitle);
                    if (string.IsNullOrEmpty(sanitisedArtists) || string.IsNullOrEmpty(sanitisedTitle))
                    {
                        log.Changes.Add("[WARN] Rename skipped: " + EmptyFieldReason(sanitisedArtists, sanitisedTitle));
                        log.Filename = log.OriginalFilename;
                        log.NeedsManual = true;
                        log.ManualReason = EmptyFieldReason(sanitisedArtists, sanitisedTitle);
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
                    if (!log.NeedsManual) MarkResidualIssue(log, finalTitle, finalArtists, finalAlbum, yearValue, originalName, originalRel, lengthValue, coverWidth, coverHeight, coverIssue);
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

                // Every file that reaches the apply stage (tag fix, TCMP-only, or a filename-only
                // rename that adds no change text in a dry run) gets its structured before/after registered.
                LogsByFile[log.OriginalFilename] = log;
                if (!string.IsNullOrEmpty(log.Filename) && !log.Filename.Equals(log.OriginalFilename, StringComparison.OrdinalIgnoreCase))
                    LogsByFile[log.Filename] = log;

                return log;
            }
            catch (Exception ex)
            {
                Console.WriteLine($" - Skipped '{Path.GetFileName(sourcePath)}': error reading file ({ex.Message})");
                runJournal?.Fail(sourcePath, ex.Message);
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

        // The name the real run will rename to, for the journal; the original name when the rename is skipped for an empty part.
        private static string PlannedFilename(string artists, string title, string originalName)
        {
            string a = Reflector.SanitiseFilename(artists), t = Reflector.SanitiseFilename(title);
            return string.IsNullOrEmpty(a) || string.IsNullOrEmpty(t) ? originalName : $"{a} - {t}.mp3";
        }

        private static string StrippedTitleKey(string artist, string title) =>
            artist.ToLowerInvariant() + "\0" + MirrorIndex.NormaliseTitleForDedup(title);

        /// <summary>
        /// apply_title collision guard: true when an accepted strip would land on a title this run already claimed,
        /// a title the library mirror already holds for the same artist, or a sibling file already named "Artist - Title.mp3".
        /// </summary>
        private bool TitleWouldDuplicate(string sourcePath, string artist, string title)
        {
            if (claimedStrippedTitles.Contains(StrippedTitleKey(artist, title))) return true;
            if (MirrorHasTitle(artist, title)) return true;
            string target = Path.Combine(Path.GetDirectoryName(sourcePath),
                $"{Reflector.SanitiseFilename(artist)} - {Reflector.SanitiseFilename(title)}.mp3");
            return File.Exists(target) && !target.Equals(sourcePath, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Names the empty field(s) of a blocked rename. Rule ids (TagFixPlanJson.ManualRule): empty_title, empty_artists,
        /// empty_artists_and_title.
        /// </summary>
        internal static string EmptyFieldReason(string sanitisedArtists, string sanitisedTitle)
        {
            bool artistsEmpty = string.IsNullOrEmpty(sanitisedArtists);
            bool titleEmpty = string.IsNullOrEmpty(sanitisedTitle);
            if (artistsEmpty && titleEmpty) return "empty artists and title after sanitisation";
            return titleEmpty ? "empty title after sanitisation" : "empty artists after sanitisation";
        }

        /// <summary>
        /// ENG-83: dry run only. A tag that still holds unwanted text after the fix is a needsManual row (rule unwanted_text)
        /// unless the user accepted it for this file or kept the field. A file with no unwanted text but an empty Year is a
        /// needsManual row (rule missing_tag, reason "Missing tag: Year"). A missing title or artist already gives
        /// empty_title, empty_artists or empty_artists_and_title. A blank or padded album is bad_field (FieldRules Album).
        /// </summary>
        private void MarkResidualIssue(FixLog log, string title, string artists, string album, string year, string name, string rel, string length, string coverWidth, string coverHeight, string coverIssue = null)
        {
            if (!dryRun) return;
            string rule = null, reason = null, flagged = null, flaggedField = null;
            var fields = new[] { ("title", title), ("artists", artists), ("album", album) };
            foreach (var f in fields)
            {
                string hit = Constants.UnwantedInfo.FirstOrDefault(u => LibChecker.ContainsUnwanted(f.Item2, u));
                if (hit != null) { rule = "unwanted_text"; reason = $"found '{hit}' in {f.Item1}"; flagged = hit; flaggedField = f.Item1; break; }
            }
            if (rule == null && year.Equals(Constants.MissingTag)) { rule = "missing_tag"; reason = "Missing tag: Year"; }
            if (rule == null && coverIssue != null) { rule = "bad_cover"; reason = coverIssue; }
            if (rule == null)
            {
                string fieldReason = ResidualFieldProblem(title, artists, album, length, coverWidth, coverHeight);
                if (fieldReason != null) { rule = "bad_field"; reason = fieldReason; }
            }
            if (rule == null || exceptions.Accepts(name, rule, rel)) return;
            if (rule == "unwanted_text" && new[] { "title", "artist", "album", "casing" }.Any(k => exceptions.Keeps(name, k, rel))) return;
            log.NeedsManual = true;
            log.ManualRule = rule;
            log.ManualReason = reason;
            if (flaggedField == "title") log.SuggestedTitle = TitleSuffixStripper.Suggest(title, flagged);
            if (flaggedField == "album")
            {
                // No collision check for the album: only the title can duplicate another file's name.
                log.SuggestedAlbum = TitleSuffixStripper.Suggest(album, flagged);
                log.OldAlbum = log.OldTags?.Album ?? "";
            }
        }

        /// <summary>
        /// BL-319 bad_field: the first FieldRules BLOCK result over Title, Artists, Album, Length and the cover size, or null. Year
        /// is left to missing_tag (YearRule stores an implausible year as Missing), and Genre and TrackNumber have no rule
        /// (David 2026-11-27).
        /// </summary>
        internal static string ResidualFieldProblem(string title, string artists, string album, string length, string coverWidth, string coverHeight)
        {
            var checks = new[] { ("Title", title), ("Artists", artists), ("Album", album), ("Length", length), ("CoverWidth", coverWidth), ("CoverHeight", coverHeight) };
            foreach (var check in checks)
            {
                FieldProblem p = FieldRules.Check(check.Item1, check.Item2);
                if (p != null && p.Severity == FieldSeverity.Block) return check.Item1 + " " + p.Reason;
            }
            return null;
        }

        // The cover size as the mirror writes it (TrackTag): "0" with no art, the pixel size, or Unknown when the bytes do not parse.
        private static (string width, string height) CoverSize(IPicture[] pictures)
        {
            if (pictures == null || pictures.Length == 0) return ("0", "0");
            var (w, h) = TrackTag.GetCoverDimensions(pictures[0].Data.Data);
            return (w > 0 ? w.ToString() : Constants.UnknownCover, h > 0 ? h.ToString() : Constants.UnknownCover);
        }

        // hh:mm:ss.fffffff, the Length shape the mirror writes and FieldRules reads.
        private static string FormatLength(TimeSpan duration) => duration.ToString(@"hh\:mm\:ss\.fffffff", System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>
        /// Embedded-cover health check (rule bad_cover). Detection only: TagFixer never changes art. A cover that is not
        /// JPEG or PNG (webp is the real case) does not show in Windows or Apple Music, and a solid-black cover is a placeholder.
        /// An absent cover is not flagged here (LibChecker and the analysis report it).
        /// </summary>
        internal static class CoverCheck
        {
            /// <summary>Highest luminance (0-255) a sampled pixel may have for the cover to count as solid black.</summary>
            internal const int BlackMaxLuma = 48;

            /// <summary>Null when the first picture is fine or there is none; otherwise the reason text.</summary>
            internal static string Evaluate(IPicture[] pictures)
            {
                if (pictures == null || pictures.Length == 0) return null;
                var pic = pictures.FirstOrDefault(p => p.Type == PictureType.FrontCover) ?? pictures[0];
                string mime = (pic.MimeType ?? "").Trim().ToLowerInvariant();
                if (mime == "image/jpg") mime = "image/jpeg";
                if (mime != "image/jpeg" && mime != "image/png")
                    return $"Cover is {(mime.Length == 0 ? "of unknown type" : mime)} (needs JPEG or PNG for Windows and Apple Music)";
                if (pic.Data == null || pic.Data.Count == 0) return "Cover is empty";
                try
                {
                    using (var ms = new System.IO.MemoryStream(pic.Data.Data))
                    using (var img = new System.Drawing.Bitmap(ms))
                    {
                        int n = 16;
                        using (var small = new System.Drawing.Bitmap(img, new System.Drawing.Size(n, n)))
                        {
                            for (int y = 0; y < n; y++)
                                for (int x = 0; x < n; x++)
                                {
                                    var c = small.GetPixel(x, y);
                                    int luma = (299 * c.R + 587 * c.G + 114 * c.B) / 1000;
                                    if (luma > BlackMaxLuma) return null;
                                }
                        }
                    }
                    return "Cover is solid black";
                }
                catch (Exception) { return "Cover cannot be decoded"; }
            }
        }

        /// <summary>
        /// Suggestion only (never applied here): the title without its LAST trailing (...) or [...] group, when that
        /// group contains the flagged unwanted word. Empty when the title has no such trailing group, or when removing
        /// it would leave no title. Only the last group is considered, so "Song (Alternate Version) (Live)" gets none.
        /// </summary>
        internal static class TitleSuffixStripper
        {
            private static readonly Regex TrailingGroup = new Regex(@"\s*(\([^()\[\]]*\)|\[[^()\[\]]*\])\s*$");

            /// <summary>Returns the title minus its trailing group when the group holds flaggedWord (case-insensitive), else "".</summary>
            public static string Suggest(string title, string flaggedWord)
            {
                if (string.IsNullOrWhiteSpace(title) || string.IsNullOrEmpty(flaggedWord)) return "";
                Match m = TrailingGroup.Match(title);
                if (!m.Success || !LibChecker.ContainsUnwanted(m.Value, flaggedWord)) return "";
                return title.Substring(0, m.Index).Trim();
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

        /// <summary>Collapses runs of whitespace to one space and trims both ends. Null and empty come back unchanged.</summary>
        internal static string TidyWhitespace(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            return Regex.Replace(text, @"\s+", " ").Trim();
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

            cased = MovePrimaryFirst(cased, GetPrimaryFirst());

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

        /// <summary>True when "{artists} - {title}.mp3" differs from the current file name (exact case). Empty parts never need a rename.</summary>
        internal static bool FilenameNeedsRename(string currentName, string artists, string title)
        {
            string a = Reflector.SanitiseFilename(artists ?? "");
            string t = Reflector.SanitiseFilename(title ?? "");
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(t)) return false;
            return !string.Equals($"{a} - {t}.mp3", currentName, StringComparison.Ordinal);
        }

        private static List<string> _primaryFirst;

        /// <summary>Artists from config/artist-primary-first.xml (cached; a missing file means none).</summary>
        internal static List<string> GetPrimaryFirst()
        {
            if (_primaryFirst == null)
            {
                var list = new List<string>();
                try
                {
                    if (File.Exists(Constants.ArtistPrimaryFirstPath))
                    {
                        var doc = new XmlDocument();
                        doc.Load(Constants.ArtistPrimaryFirstPath);
                        foreach (XmlElement el in doc.SelectNodes("//Artist").OfType<XmlElement>())
                        {
                            string n = el.GetAttribute("name");
                            if (!string.IsNullOrEmpty(n)) list.Add(n);
                        }
                    }
                }
                catch (Exception ex) { Console.WriteLine("[WARN] artist-primary-first.xml unreadable: " + ex.Message); }
                _primaryFirst = list;
            }
            return _primaryFirst;
        }

        internal static void ResetPrimaryFirstCache() { _primaryFirst = null; }

        /// <summary>The first listed artist who is credited becomes the first (folder) artist; others keep their order.</summary>
        internal static List<string> MovePrimaryFirst(List<string> artists, List<string> primaries)
        {
            foreach (string p in primaries)
            {
                int i = artists.FindIndex(a => ArtistNameComparer.Instance.Equals(a, p));
                if (i > 0)
                {
                    var r = new List<string>(artists);
                    string hit = r[i];
                    r.RemoveAt(i);
                    r.Insert(0, hit);
                    return r;
                }
                if (i == 0) return artists;
            }
            return artists;
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
                return Constants.MusivationDir; // "Musivation"

            return Constants.MotivDir; // "Motivation" - the only other case ShouldFixGenre triggers for
        }
    }
}
