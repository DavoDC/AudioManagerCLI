using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using File = System.IO.File;

namespace AudioManager
{
    /// <summary>
    /// Per-file exceptions loaded from config/tagfix-exceptions.xml.
    ///
    /// Two row kinds, both keyed by the ORIGINAL NewMusic filename (case-insensitive, no path):
    ///   &lt;Exception file="..." keep="title|artist|album|casing" /&gt;  read by TagFixer. Multiple rows for one file combine.
    ///   &lt;NotDuplicate file="..." /&gt;                         read by the integrate library-duplicate check.
    ///
    /// Loading always fails safe. A missing file yields no exceptions silently. A malformed file logs one
    /// [WARN] and yields no exceptions. An unusable row is skipped on its own with a [WARN].
    /// </summary>
    internal class TagFixExceptions
    {
        /// <summary>The keep tokens a row may list. Anything else is skipped with a warning.</summary>
        internal static readonly string[] ValidKeeps = { "title", "artist", "album", "casing" };

        private readonly Dictionary<string, HashSet<string>> keepsByFile = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> notDuplicateFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Loads exceptions from the given path (or Constants.TagFixExceptionsPath by default).
        /// Never throws.
        /// </summary>
        internal static TagFixExceptions Load(string path = null)
        {
            string effectivePath = path ?? Constants.TagFixExceptionsPath;
            var set = new TagFixExceptions();
            try
            {
                if (!File.Exists(effectivePath)) return set;

                var doc = new XmlDocument();
                doc.Load(effectivePath);

                foreach (XmlElement el in doc.SelectNodes("//Exception").OfType<XmlElement>())
                    set.AddKeepRow(el);
                foreach (XmlElement el in doc.SelectNodes("//NotDuplicate").OfType<XmlElement>())
                    set.AddNotDuplicateRow(el);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  [WARN] Could not load tag fix exceptions ({effectivePath}): {ex.Message}");
                return new TagFixExceptions();
            }
            return set;
        }

        /// <summary>True when the original filename has a keep token (title, artist, album or casing).</summary>
        internal bool Keeps(string originalFilename, string keep)
        {
            if (string.IsNullOrWhiteSpace(originalFilename) || string.IsNullOrWhiteSpace(keep)) return false;
            return keepsByFile.TryGetValue(originalFilename.Trim(), out var keeps) && keeps.Contains(keep.Trim().ToLowerInvariant());
        }

        /// <summary>True when the library-duplicate check must skip this original filename.</summary>
        internal bool IsNotDuplicate(string originalFilename)
        {
            if (string.IsNullOrWhiteSpace(originalFilename)) return false;
            return notDuplicateFiles.Contains(originalFilename.Trim());
        }

        private void AddKeepRow(XmlElement el)
        {
            string file = el.GetAttribute("file").Trim();
            if (string.IsNullOrEmpty(file))
            {
                Console.WriteLine("  [WARN] Tag fix exception skipped: missing file attribute");
                return;
            }

            var tokens = (el.GetAttribute("keep") ?? "").Split('|').Select(t => t.Trim().ToLowerInvariant()).Where(t => t.Length > 0).ToList();
            var valid = new List<string>();
            foreach (string token in tokens)
            {
                if (ValidKeeps.Contains(token)) valid.Add(token);
                else Console.WriteLine($"  [WARN] Tag fix exception for '{file}': unknown keep '{token}' ignored");
            }
            if (valid.Count == 0) return;

            if (!keepsByFile.TryGetValue(file, out var keeps))
            {
                keeps = new HashSet<string>();
                keepsByFile[file] = keeps;
            }
            foreach (string token in valid) keeps.Add(token);
        }

        private void AddNotDuplicateRow(XmlElement el)
        {
            string file = el.GetAttribute("file").Trim();
            if (string.IsNullOrEmpty(file))
            {
                Console.WriteLine("  [WARN] NotDuplicate row skipped: missing file attribute");
                return;
            }
            notDuplicateFiles.Add(file);
        }
    }
}
