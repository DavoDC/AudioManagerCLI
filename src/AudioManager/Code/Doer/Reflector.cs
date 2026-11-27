using AudioManager.Code.Modules;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace AudioManager
{
    /// <summary>
    /// Creates a lightweight 'mirror' of an Audio track folder.
    /// </summary>
    internal class Reflector : Doer
    {
        // Staging folder suffix for a regen build. Short on purpose: widens the MAX_PATH margin for staged temp files.
        internal const string StagingSuffix = ".stg";

        // Old staging suffix, still swept by RecoverLeftovers so a leftover from before the rename is cleared.
        internal const string LegacyStagingSuffix = ".staging";

        // Longest path (exclusive) the staged temp XML may reach: MAX_PATH is 260 including the terminator.
        internal const int OverlongPathLimit = 259;

        // Mirror path variable
        string mirrorPath;

        // Folder the XMLs are written into: mirrorPath.stg on a regen, else mirrorPath
        string buildPath;

        // Audio library root being mirrored
        string audioRoot;

        /// <summary>
        /// Construct an audio mirror
        /// </summary>
        /// <param name="mirrorPath">The audio mirror folder path</param>
        public Reflector(string mirrorPath)
            : this(Constants.AudioFolderPath, mirrorPath, Constants.MirrorRepoPath, null)
        {
        }

        /// <summary>
        /// Test seam: the same build with an injectable audio root, repo root and stamp path.
        /// </summary>
        internal Reflector(string audioRoot, string mirrorPath, string repoRoot, string lastRunInfoPath)
        {
            // Save parameters
            this.audioRoot = audioRoot;
            this.mirrorPath = mirrorPath;

            // Notify
            Console.WriteLine($"\nCreating mirror of '{audioRoot}'...");

            // If mirror path is outside of repo
            if (!IsInsideMirrorRepo(mirrorPath, repoRoot))
            {
                // Throw exception and notify
                string msg = $"\nMirror path was incorrect, outside the repo:\n{mirrorPath}\n";
                throw new ArgumentException(msg);
            }

            // One mirror build at a time: RecoverLeftovers deletes staging, so a second run would destroy a running build's work.
            using (AcquireBuildLock(mirrorPath))
            {
            // Heal any interrupted swap before touching anything (every run, incremental too)
            RecoverLeftovers(mirrorPath);

            bool regen = AgeChecker.RegenMirror;
            buildPath = regen ? mirrorPath + StagingSuffix : mirrorPath;
            DateTime builtAt = DateTime.Now;

            // Fresh first-seen hand-off for this build, keyed relative to the live mirror (also what the Parser reads after the swap)
            FirstSeenDates.Reset(mirrorPath);

            // Setup folder structure
            CreateFolders();

            // Populate with files
            var statisticsInfo = CreateFiles();

            // Regen: swap the finished staging folder in, then record the run only after success
            if (regen)
            {
                // An audio root with no mp3 (unmounted volume, wrong folder) must never replace the live mirror with an empty one.
                if (statisticsInfo.Item1 == 0)
                {
                    try { if (Directory.Exists(buildPath)) Directory.Delete(buildPath, true); } catch (Exception) { /* leftover is healed by RecoverLeftovers next run */ }
                    throw new InvalidOperationException($"No mp3 files found under '{audioRoot}'; refusing to replace the mirror with an empty one.");
                }
                SwapDirectory(buildPath, mirrorPath);
                AgeChecker.MarkRegenerated(builtAt, lastRunInfoPath);
            }

            // Print statistics
            PrintStats(statisticsInfo);
            }
        }

        /// <summary>
        /// Exclusive lock for one mirror build, kept in the temp folder (never inside the mirror repo, where it would show as
        /// an untracked file). Held by an open handle, so a crashed run releases it. Throws IOException when another build holds it.
        /// </summary>
        internal static FileStream AcquireBuildLock(string mirrorPath)
        {
            string full = Path.GetFullPath(mirrorPath).TrimEnd('\\', '/').ToLowerInvariant();
            string name;
            using (var sha = System.Security.Cryptography.SHA256.Create())
                name = BitConverter.ToString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(full))).Replace("-", "").Substring(0, 16);
            string path = Path.Combine(Path.GetTempPath(), "AudioManager-mirror-build-" + name + ".lock");
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
            }
            catch (IOException)
            {
                throw new IOException("Another mirror build (integrate, analysis or --mirror-retry) is already running for this mirror; wait for it to finish.");
            }
        }


        /// <summary>
        /// True only when the path, once normalised (no .. segments), sits strictly under the mirror repo.
        /// Guards the recursive delete of a stale staging folder.
        /// </summary>
        internal static bool IsInsideMirrorRepo(string path)
        {
            return IsInsideMirrorRepo(path, Constants.MirrorRepoPath);
        }

        internal static bool IsInsideMirrorRepo(string path, string repoRoot)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            string full;
            try { full = Path.GetFullPath(path); }
            catch (Exception) { return false; }
            if (!full.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)) full += Path.DirectorySeparatorChar;
            return full.Length > repoRoot.Length
                && full.StartsWith(repoRoot, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Set up folder structure mirroring the actual folder, inside buildPath
        /// </summary>
        public void CreateFolders()
        {
            // One walk of the audio tree lists the folders and the files (and each file's mtime); it throws for a
            // missing audio root, before anything is created
            ListAudioTree();
            string[] realDirs = listedDirs;

            // A stale staging folder (never the live mirror) is cleared before a fresh build
            if (AgeChecker.RegenMirror && Directory.Exists(buildPath))
            {
                Directory.Delete(buildPath, true);
            }
            Directory.CreateDirectory(buildPath);

            // For every actual folder
            foreach (var directoryPath in realDirs)
            {
                // Get real relative path
                string relativePath = GetRelativePath(audioRoot, directoryPath);

                // Create relative folder within the build path
                string newDirectoryPath = Path.Combine(buildPath, relativePath);
                Directory.CreateDirectory(newDirectoryPath);
            }
        }


        // The audio tree as listed once per build: folders, files and (fast path only) each file's last-write time.
        private string[] listedDirs;
        private string[] listedFiles;
        private DateTime[] listedFileTimesUtc;

        /// <summary>
        /// Lists the audio tree once. The fast path walks with DirectoryInfo, whose entries carry the mtime from the
        /// directory read (no stat per file); it yields the same paths in the same order as Directory.GetFiles and
        /// Directory.GetDirectories, but only for a root that is already in full-path form, so any other root keeps the
        /// original calls (and their path spelling) untouched.
        /// </summary>
        private void ListAudioTree()
        {
            if (!IsFullPathForm(audioRoot))
            {
                listedDirs = Directory.GetDirectories(audioRoot, "*", SearchOption.AllDirectories);
                listedFiles = Directory.GetFiles(audioRoot, "*", SearchOption.AllDirectories);
                listedFileTimesUtc = null;
                return;
            }

            var dirs = new List<string>();
            var files = new List<string>();
            var times = new List<DateTime>();
            foreach (FileSystemInfo entry in new DirectoryInfo(audioRoot).EnumerateFileSystemInfos("*", SearchOption.AllDirectories))
            {
                if (entry is DirectoryInfo)
                {
                    dirs.Add(entry.FullName);
                }
                else
                {
                    files.Add(entry.FullName);
                    times.Add(entry.LastWriteTimeUtc);
                }
            }
            listedDirs = dirs.ToArray();
            listedFiles = files.ToArray();
            listedFileTimesUtc = times.ToArray();
        }

        /// <summary>
        /// True when the path is already its own normalised full form, so FileSystemInfo.FullName spells it exactly as
        /// the string-based directory calls do.
        /// </summary>
        private static bool IsFullPathForm(string path)
        {
            try { return string.Equals(Path.GetFullPath(path), path, StringComparison.Ordinal); }
            catch (Exception) { return false; }
        }

        /// <summary>
        /// Populate folder structure with mirrored files. In incremental mode, also prunes XMLs
        /// whose source MP3 no longer exists (guard: only when Audio root enumerated successfully).
        /// </summary>
        /// <returns>Statistics tuple: (mp3Count, sanitised, refreshed, pruned, nonMP3Files)</returns>
        private Tuple<int, int, int, int, List<string>> CreateFiles()
        {
            // Holders
            int mp3FileCount = 0;
            int sanitisationCount = 0;
            int refreshedCount = 0;
            List<string> nonMP3Files = new List<string>();

            // For every actual file (listed once, with CreateFolders)
            string[] realFiles = listedFiles;

            // Track expected XML paths so orphan detection knows what to keep
            var expectedXmlPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Incremental mode: one walk of the existing mirror gives every XML's path and mtime. It serves the stale
            // check below (no stat per XML) and the orphan prune after the loop (no second walk): XMLs written during the
            // loop are all expected, so the prune outcome is the same as listing after the loop.
            List<string> existingXmls = null;
            Dictionary<string, DateTime> existingXmlTimes = null;
            if (!AgeChecker.RegenMirror && Directory.Exists(buildPath) && IsFullPathForm(buildPath))
            {
                existingXmls = new List<string>();
                existingXmlTimes = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
                foreach (FileInfo xml in new DirectoryInfo(buildPath).EnumerateFiles("*.xml", SearchOption.AllDirectories))
                {
                    existingXmls.Add(xml.FullName);
                    existingXmlTimes[xml.FullName] = xml.LastWriteTimeUtc;
                }
            }

            for (int fileIndex = 0; fileIndex < realFiles.Length; fileIndex++)
            {
                string realFilePath = realFiles[fileIndex];
                // For non-MP3 files, add the relative path to the list and skip
                if (Path.GetExtension(realFilePath)?.ToLowerInvariant() != ".mp3")
                {
                    nonMP3Files.Add(GetRelativePath(audioRoot, realFilePath));
                    continue;
                }

                // Compute the XML path once (the same helper as the pre-move gate), for orphan tracking and the write
                string fullMirrorPath = Path.Combine(buildPath, MirrorXmlRelativePath(audioRoot, realFilePath, out bool sanitised));
                bool firstUse = expectedXmlPaths.Add(fullMirrorPath);

                // The listed mtime is valid only for the first mp3 that maps to this XML: a second one (two names that
                // sanitise alike) must see the XML the first one just wrote, so it takes the live stat.
                DateTime xmlTime = default(DateTime);
                bool xmlTimeKnown = firstUse && existingXmlTimes != null && existingXmlTimes.TryGetValue(fullMirrorPath, out xmlTime);
                DateTime? mp3Time = listedFileTimesUtc != null ? listedFileTimesUtc[fileIndex] : (DateTime?)null;

                // Otherwise, process MP3 file
                if (CreateFile(realFilePath, fullMirrorPath, sanitised, xmlTimeKnown ? xmlTime : (DateTime?)null, mp3Time, out bool refreshed))
                    sanitisationCount++;
                if (refreshed)
                    refreshedCount++;

                mp3FileCount++;
            }

            // Incremental mode: prune XMLs whose MP3 was deleted since last run.
            // Guard: skip if Audio root returned 0 files (access failure or unexpected empty library).
            int prunedCount = 0;
            if (!AgeChecker.RegenMirror && realFiles.Length > 0)
                prunedCount = PruneOrphanedXmls(buildPath, expectedXmlPaths, existingXmls);

            // Return holders
            return Tuple.Create(mp3FileCount, sanitisationCount, refreshedCount, prunedCount, nonMP3Files);
        }


        /// <summary>
        /// Create a mirrored file (the file contents are just the real path at this stage).
        /// In incremental mode, also refreshes the XML when the MP3 has been modified since last analysis.
        /// </summary>
        /// <param name="realFilePath">The actual MP3 file path</param>
        /// <param name="refreshed">Output: true if an existing XML was overwritten because the MP3 was newer</param>
        /// <returns>True if the filename was sanitised</returns>
        /// <param name="fullMirrorPath">The XML path, already computed by the caller from MirrorXmlRelativePath</param>
        /// <param name="sanitised">True if the XML name differs from the MP3 name, from the same call</param>
        /// <param name="knownXmlTimeUtc">The XML's mtime from the mirror walk when it was listed as existing, else null (stat it)</param>
        /// <param name="knownMp3TimeUtc">The MP3's mtime from the audio walk, or null (stat it)</param>
        private bool CreateFile(string realFilePath, string fullMirrorPath, bool sanitised, DateTime? knownXmlTimeUtc, DateTime? knownMp3TimeUtc, out bool refreshed)
        {
            refreshed = false;

            // One stat of the XML answers both "exists" and "mtime" (File.Exists is false for a directory, as FileInfo.Exists is),
            // and none is needed when the mirror walk already listed it
            DateTime xmlTimeUtc = default(DateTime);
            bool xmlExists = knownXmlTimeUtc.HasValue;
            if (xmlExists)
            {
                xmlTimeUtc = knownXmlTimeUtc.Value;
            }
            else
            {
                var xmlInfo = new FileInfo(fullMirrorPath);
                xmlExists = xmlInfo.Exists;
                if (xmlExists) xmlTimeUtc = xmlInfo.LastWriteTimeUtc;
            }

            if (!xmlExists)
            {
                // Regen builds into a fresh staging folder, so the live XML still holds the first-seen date: keep it for the Parser
                RememberDateAdded(fullMirrorPath);
                // XML doesn't exist: create it containing the real path
                AtomicFile.WriteAllText(fullMirrorPath, realFilePath, new UTF8Encoding(false));
            }
            else if ((knownMp3TimeUtc ?? File.GetLastWriteTimeUtc(realFilePath)) > xmlTimeUtc)
            {
                // MP3 was modified after the XML was written (e.g., tag edit in Mp3tag):
                // overwrite with the real path so this run re-reads fresh tag data (the first-seen date is read before it goes)
                RememberDateAdded(fullMirrorPath);
                AtomicFile.WriteAllText(fullMirrorPath, realFilePath, new UTF8Encoding(false));
                refreshed = true;
            }

            // Return sanitised flag
            return sanitised;
        }

        /// <summary>
        /// Records the DateAdded held by the XML about to be replaced by a stub. The XML is read from the live mirror
        /// (a regen stub goes into the staging folder, so the live one is still intact); the key is relative to the mirror root.
        /// </summary>
        private void RememberDateAdded(string fullMirrorPath)
        {
            string live = buildPath == mirrorPath ? fullMirrorPath : mirrorPath + fullMirrorPath.Substring(buildPath.Length);
            string key = FirstSeenDates.KeyFor(buildPath, fullMirrorPath);
            if (key != null) FirstSeenDates.RecordFrom(live, key);
        }

        /// <summary>
        /// Returns true if the MP3 file has been modified more recently than its mirror XML,
        /// indicating that cached tag data in the XML is stale and should be refreshed.
        /// </summary>
        internal static bool IsStaleMirrorXml(string mp3Path, string xmlPath)
        {
            if (!File.Exists(xmlPath)) return false;
            return File.GetLastWriteTimeUtc(mp3Path) > File.GetLastWriteTimeUtc(xmlPath);
        }

        /// <summary>
        /// Deletes XMLs from mirrorPath that are not in expectedXmlPaths (orphaned entries
        /// left by deleted MP3s). Returns the number of XMLs pruned.
        /// </summary>
        internal static int PruneOrphanedXmls(string mirrorPath, HashSet<string> expectedXmlPaths)
        {
            return PruneOrphanedXmls(mirrorPath, expectedXmlPaths, null);
        }

        /// <summary>
        /// Same prune over an already-listed set of XML paths (spelled as Directory.GetFiles would), or a fresh
        /// walk of mirrorPath when none is given.
        /// </summary>
        internal static int PruneOrphanedXmls(string mirrorPath, HashSet<string> expectedXmlPaths, IEnumerable<string> listedXmlPaths)
        {
            if (!Directory.Exists(mirrorPath)) return 0;

            // Never delete everything on an empty expected set (e.g. a library with only non-MP3 files)
            if (expectedXmlPaths.Count == 0)
            {
                Console.WriteLine(" - XML prune skipped: nothing was expected");
                return 0;
            }
            int pruned = 0;
            // "*.xml" also matches longer extensions on .NET Framework (8.3 semantics), so filter exactly.
            foreach (string xmlFile in listedXmlPaths ?? Directory.GetFiles(mirrorPath, "*.xml", SearchOption.AllDirectories))
            {
                if (!xmlFile.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)) continue;
                if (expectedXmlPaths.Contains(xmlFile)) continue;
                try
                {
                    File.Delete(xmlFile);
                    pruned++;
                }
                catch (Exception ex)
                {
                    // One locked file must not abort the prune of the rest
                    Console.WriteLine($"  [WARN] Could not prune '{Path.GetFileName(xmlFile)}': {ex.Message}");
                }
            }
            return pruned;
        }


        /// <summary>
        /// Print info about completed mirroring process
        /// </summary>
        /// <param name="statisticsInfo"></param>
        private void PrintStats(Tuple<int, int, int, int, List<string>> statisticsInfo)
        {
            // Extract info items
            int mp3FileCount = statisticsInfo.Item1;
            int sanitisedFileNames = statisticsInfo.Item2;
            int refreshedCount = statisticsInfo.Item3;
            int prunedCount = statisticsInfo.Item4;
            List<string> nonMP3Files = statisticsInfo.Item5;

            // Print mirror path
            Console.WriteLine($" - Path: '{mirrorPath}'");

            // Print file count
            Console.WriteLine($" - MP3 file count: {mp3FileCount}");

            // Print non-MP3 file info
            var nonMP3info = ProcessNonMP3(nonMP3Files);
            Console.WriteLine($" - Non-MP3 files found: {nonMP3info.Item1}");
            if (nonMP3info.Item3 != null)
            {
                Console.WriteLine($"  - Extension list: {nonMP3info.Item3}");
            }
            if (nonMP3info.Item2 != null)
            {
                Console.Write(nonMP3info.Item2);
            }

            // Print sanitisation count
            Console.WriteLine($" - MP3 filenames sanitised: {sanitisedFileNames}");

            // Print refreshed count (only show when > 0 to avoid noise in normal runs)
            if (refreshedCount > 0)
                Console.WriteLine($" - XMLs refreshed (MP3 updated since last analysis): {refreshedCount}");

            // Print pruned count (only show when > 0 - indicates deleted MPs were cleaned up)
            if (prunedCount > 0)
                Console.WriteLine($" - XMLs pruned (MP3 deleted since last analysis): {prunedCount}");

            // Print recreation setting
            Console.WriteLine($" - Recreated: {AgeChecker.RegenMirror}");

            // Finish and print time taken
            FinishAndPrintTimeTaken();
        }

        /// <summary>
        /// Generate info strings from a list of non-MP3 filenames
        /// </summary>
        /// <param name="nonMP3Files"></param>
        /// <returns>Tuple of (count info, detailed unexpected files info, extension list)</returns>
        private Tuple<string, string, string> ProcessNonMP3(List<string> nonMP3Files)
        {
            // Extract list of extensions
            var extList = nonMP3Files.Select(fileName => Path.GetExtension(fileName)).ToList();

            // Check against expected types
            var expectedExt = new HashSet<string> { ".ini", ".txt", ".lnk", ".ffs_db" };
            bool allExpected = extList.TrueForAll(ext => expectedExt.Contains(ext));

            // Combine and format info
            string nonMP3infoStr = $"{extList.Count} ({(allExpected ? "all expected" : "UNEXPECTED!")})";

            // Format extension list (only if unexpected)
            string extListInfo = null;

            // If extensions were unexpected, build details string
            string detailedInfo = null;
            if (!allExpected)
            {
                // Only show extension list if there are unexpected ones
                extListInfo = string.Join(", ", extList.Distinct().OrderBy(e => e));

                // Group files by extension, filter to only unexpected ones
                var unexpectedByExt = nonMP3Files
                    .GroupBy(file => Path.GetExtension(file))
                    .Where(group => !expectedExt.Contains(group.Key))
                    .OrderBy(group => group.Key);

                var sb = new StringBuilder();
                sb.Append("  - Unexpected file types:\n");
                foreach (var extGroup in unexpectedByExt)
                {
                    sb.Append($"    {extGroup.Key} ({extGroup.Count()} files):\n");
                    foreach (var file in extGroup.OrderBy(f => f))
                    {
                        sb.Append($"      - {file}\n");
                    }
                }
                detailedInfo = sb.ToString();
            }

            // Return info
            return Tuple.Create<string, string, string>(nonMP3infoStr, detailedInfo, extListInfo);
        }


        /// <summary>
        /// Sanitise a given filename of special characters
        /// </summary>
        /// <param name="filename">Original filename.</param>
        /// <returns>Sanitised filename.</returns>
        public static string SanitiseFilename(string filename)
        {
            // Fast path: a name made only of ASCII characters the full pipeline provably leaves unchanged
            if (filename != null && IsUnchangedByFullSanitise(filename)) return filename;
            return SanitiseFilenameFull(filename);
        }

        // The single-byte code page used for the wide-character squash, fetched once instead of per call.
        // Declared before AsciiUnchanged: static initialisers run in textual order and the table build calls the full pipeline.
        private static readonly Encoding CyrillicEncoding = Encoding.GetEncoding("Cyrillic");

        // Per-ASCII-char answer from the full pipeline itself ("does a name of just this char come back unchanged"), built once.
        // The pipeline works per character (category filter, single-byte code page round trip, invalid-char map), so a name
        // whose every char is in the set is returned unchanged. Derived from SanitiseFilenameFull, never hand-listed.
        private static readonly bool[] AsciiUnchanged = BuildAsciiUnchanged();

        private static bool[] BuildAsciiUnchanged()
        {
            var table = new bool[128];
            for (int c = 0; c < 128; c++)
            {
                string one = ((char)c).ToString();
                table[c] = SanitiseFilenameFull(one) == one;
            }
            return table;
        }

        private static bool IsUnchangedByFullSanitise(string filename)
        {
            foreach (char c in filename)
            {
                if (c >= 128 || !AsciiUnchanged[c]) return false;
            }
            return true;
        }

        private static string SanitiseFilenameFull(string filename)
        {
            // Remove any non-ASCII characters and replace wide characters with their closest equivalent
            string sanitisedFilename = new string(filename
                .Where(c => Char.GetUnicodeCategory(c) != UnicodeCategory.Control &&
                            Char.GetUnicodeCategory(c) != UnicodeCategory.ModifierSymbol &&
                            Char.GetUnicodeCategory(c) != UnicodeCategory.OtherSymbol)
                .ToArray());
            sanitisedFilename = Encoding.ASCII.GetString(CyrillicEncoding.GetBytes(sanitisedFilename));

            // Replace invalid characters with an underscore if found
            if (sanitisedFilename.Any(c => Constants.InvalidChars.Contains(c)))
            {
                sanitisedFilename = new string(sanitisedFilename.Select(c => Constants.InvalidChars.Contains(c) ? '_' : c).ToArray());
            }

            // Return new file name
            return sanitisedFilename;
        }


        /// <summary>
        /// Calculates the relative path from a base path to a target path.
        /// </summary>
        /// <param name="basePath">The base path.</param>
        /// <param name="targetPath">The target path.</param>
        /// <returns>The relative path from the base path to the target path.</returns>
        private static volatile Tuple<string, Uri> lastBaseUri;

        private static string GetRelativePath(string basePath, string targetPath)
        {
            // Ensure basePath is treated as a directory (trailing separator) so Uri.MakeRelativeUri
            // returns paths relative to the directory rather than including the directory name.
            string basePathFixed = basePath;
            if (!basePathFixed.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal) && !basePathFixed.EndsWith(Path.AltDirectorySeparatorChar.ToString(), StringComparison.Ordinal))
            {
                basePathFixed += Path.DirectorySeparatorChar;
            }

            // Convert paths to URIs for accurate relative path calculation
            // The base is the same for every file of a run, so its parsed Uri is kept (one immutable pair, swapped whole, thread-safe)
            var cached = lastBaseUri;
            if (cached == null || !string.Equals(cached.Item1, basePathFixed, StringComparison.Ordinal))
            {
                cached = Tuple.Create(basePathFixed, new Uri(basePathFixed));
                lastBaseUri = cached;
            }
            Uri baseUri = cached.Item2;
            Uri targetUri = new Uri(targetPath);

            // Calculate relative URI
            Uri relativeUri = baseUri.MakeRelativeUri(targetUri);

            // Convert relative URI to a string, unescape special characters, and normalize to Windows
            // backslash paths. URI.MakeRelativeUri returns forward-slash paths; Path.Combine and
            // Directory.GetFiles both use backslashes, so a mismatch would cause PruneOrphanedXmls
            // to delete all XMLs (HashSet lookup fails because '/' != '\').
            return Uri.UnescapeDataString(relativeUri.ToString()).Replace('/', Path.DirectorySeparatorChar);
        }

        /// <summary>
        /// The XML path relative to the mirror root for one library MP3: the library-relative path with the
        /// file name sanitised and the extension changed to .xml. Single source for CreateFiles, CreateFile
        /// and the pre-move MAX_PATH gate, so all three agree on where an XML lands.
        /// </summary>
        internal static string MirrorXmlRelativePath(string audioRoot, string libraryFilePath)
        {
            return MirrorXmlRelativePath(audioRoot, libraryFilePath, out _);
        }

        /// <summary>
        /// Same path, also reporting whether the file name was sanitised (the XML name differs from the MP3 name),
        /// so a caller that needs both pays for one sanitise and one relative-path calculation.
        /// </summary>
        internal static string MirrorXmlRelativePath(string audioRoot, string libraryFilePath, out bool sanitised)
        {
            string relativePath = GetRelativePath(audioRoot, libraryFilePath);
            string fileName = Path.GetFileName(libraryFilePath);
            string sanitisedName = SanitiseFilename(fileName);
            sanitised = fileName != sanitisedName;
            string xmlRelPath = sanitised
                ? ReplaceFileNameSegment(relativePath, sanitisedName)
                : relativePath;
            return Path.ChangeExtension(xmlRelPath, ".xml");
        }

        /// <summary>
        /// The full staged temp path a regen gives one library MP3's XML: mirror.stg\rel.xml.tmp
        /// (the .stg folder from StagingSuffix, the .tmp from AtomicFile.WriteAllText).
        /// </summary>
        internal static string ProjectedStagingTempPath(string mirrorPath, string audioRoot, string libraryFilePath)
        {
            return Path.Combine(mirrorPath + StagingSuffix, MirrorXmlRelativePath(audioRoot, libraryFilePath)) + AtomicFile.TempSuffix;
        }

        /// <summary>
        /// Planned destinations whose projected staged temp path is at or over the limit. Each entry is the
        /// planned destination with its projected length. Empty when every destination fits.
        /// </summary>
        internal static List<(string file, int length)> FindOverlongMirrorPaths(string mirrorPath, string audioRoot,
            IEnumerable<string> plannedDestinations, int limit = OverlongPathLimit)
        {
            var overlong = new List<(string file, int length)>();
            foreach (string dest in plannedDestinations ?? Enumerable.Empty<string>())
            {
                int length = ProjectedStagingTempPath(mirrorPath, audioRoot, dest).Length;
                if (length >= limit) overlong.Add((dest, length));
            }
            return overlong;
        }

        /// <summary>
        /// Swaps the final path segment (the file name) for its sanitised form, leaving every
        /// directory segment untouched even when it contains the same text.
        /// </summary>
        internal static string ReplaceFileNameSegment(string relativePath, string sanitisedName)
        {
            string directory = Path.GetDirectoryName(relativePath);
            if (string.IsNullOrEmpty(directory)) return sanitisedName;
            return Path.Combine(directory, sanitisedName);
        }

        /// <summary>
        /// Replaces target with a fully built staging folder. The old target is parked at target + ".old"
        /// and moved back if the final move fails; the backup is deleted only after the swap succeeds.
        /// </summary>
        internal static void SwapDirectory(string staging, string target)
        {
            string old = target + ".old";
            bool parked = false;
            if (Directory.Exists(target))
            {
                Directory.Move(target, old);
                parked = true;
            }
            try
            {
                Directory.Move(staging, target);
            }
            catch (Exception)
            {
                if (parked && !Directory.Exists(target) && Directory.Exists(old))
                {
                    Directory.Move(old, target);
                }
                throw;
            }
            if (!parked) return;
            try
            {
                Directory.Delete(old, true);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  [WARN] Could not delete previous mirror '{old}': {ex.Message}");
            }
        }

        /// <summary>
        /// Clears leftovers from an interrupted swap: a staging folder (current or legacy suffix) is always
        /// disposable, and an orphaned .old is moved back to target when target is missing, otherwise deleted.
        /// </summary>
        internal static void RecoverLeftovers(string target)
        {
            string old = target + ".old";
            foreach (string suffix in new[] { StagingSuffix, LegacyStagingSuffix })
            {
                string staging = target + suffix;
                try
                {
                    if (Directory.Exists(staging)) Directory.Delete(staging, true);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  [WARN] Could not delete stale staging '{staging}': {ex.Message}");
                }
            }
            try
            {
                if (!Directory.Exists(old)) return;
                if (!Directory.Exists(target))
                {
                    Directory.Move(old, target);
                    Console.WriteLine("  [SWEEP] Restored previous mirror");
                }
                else
                {
                    Directory.Delete(old, true);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  [WARN] Could not recover previous mirror '{old}': {ex.Message}");
            }
        }
    }
}
