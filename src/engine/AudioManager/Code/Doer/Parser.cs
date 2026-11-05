using AudioManager.Code.Modules;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace AudioManager
{
    /// <summary>
    /// Parses audio track metadata into XML files.
    /// </summary>
    internal class Parser : Doer
    {
        // Properties
        public List<TrackTag> audioTags { get; }

        /// <summary>
        /// Construct a parser
        /// </summary>
        /// <param name="mirrorPath">The audio mirror folder path</param>
        /// <param name="cachePath">Override cache file path. Null uses Constants.ParseCachePath (production default).</param>
        public Parser(string mirrorPath, string cachePath = null)
        {
            string effectiveCachePath = cachePath ?? Constants.ParseCachePath;

            if (!Directory.Exists(mirrorPath))
                throw new DirectoryNotFoundException($"Audio mirror folder not found: {mirrorPath}");

            // Notify
            Console.Write("\nParsing audio metadata...");

            // Initialise tag list
            audioTags = new List<TrackTag>();

            // Cache hit: skip all XML reads when nothing changed since last parse
            if (ParseCache.TryLoad(effectiveCachePath, mirrorPath, out var cached))
            {
                audioTags = cached;
                Console.WriteLine($" - Tags parsed: {audioTags.Count} (cache)");
                FinishAndPrintTimeTaken();
                return;
            }

            // Cache miss: parse all XMLs in parallel (each file read is independent)
            string[] mirrorFiles = Directory.GetFiles(mirrorPath, "*", SearchOption.AllDirectories);
            var bag = new ConcurrentBag<TrackTag>();
            var badFiles = new ConcurrentBag<string>(); // non-XML files found (mirror corruption)
            var failures = new ConcurrentBag<string>(); // XML files that could not be parsed

            Parallel.ForEach(mirrorFiles, mirrorFilePath =>
            {
                string name = Path.GetFileName(mirrorFilePath);
                if (name.Equals("README.md", StringComparison.OrdinalIgnoreCase)) return;

                if (!string.Equals(Path.GetExtension(mirrorFilePath), ".xml", StringComparison.OrdinalIgnoreCase))
                {
                    badFiles.Add(mirrorFilePath);
                    return;
                }

                try
                {
                    bag.Add(new TrackTag(mirrorFilePath));
                }
                catch (Exception ex)
                {
                    failures.Add($"{mirrorFilePath}: {ex.Message}");
                }
            });

            // Surface mirror corruption after parallel phase completes
            if (!badFiles.IsEmpty)
                throw new ArgumentException($"Non-XML file(s) found in mirror folder: {string.Join(", ", badFiles)}");

            // Report every failed file in one exception, not just the first
            if (!failures.IsEmpty)
            {
                string[] failureList = failures.ToArray();
                Array.Sort(failureList, StringComparer.Ordinal);
                throw new InvalidDataException(
                    $"Failed to parse {failureList.Length} file(s) in mirror folder:\n" + string.Join("\n", failureList));
            }

            audioTags = new List<TrackTag>(bag);
            // Ordinal sort so the result, and the cache written from it, is deterministic
            audioTags.Sort((a, b) => string.CompareOrdinal(a.RelPath, b.RelPath));
            Console.WriteLine();

            // Save cache so next run can skip these reads
            ParseCache.Save(effectiveCachePath, audioTags);

            // Print number of tags parsed
            Console.WriteLine($" - Tags parsed: {audioTags.Count}");

            // Finish and print time taken
            FinishAndPrintTimeTaken();
        }
    }
}
