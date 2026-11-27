using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace AudioManager
{
    /// <summary>
    /// Characterisation of the incremental (no regen) mirror build: every console line, every mirror file's bytes and
    /// the per-file XML path helper, for a no-change pass, a changed-track pass and an orphan-prune pass. Pinned before
    /// the speed work so the speed work can prove it changed nothing observable.
    /// </summary>
    internal static class MirrorRegenParityTests
    {
        private static readonly string[] Mp3s =
        {
            @"Artists\Alpha\Album One\Alpha - One.mp3",
            @"Artists\Alpha\Album One\Alpha - Two (feat. B).mp3",
            @"Artists\Beyoncé\Lemonade\Beyoncé - Sorry.mp3",
            @"Artists\Wide　Name\Wide： Title.mp3",
            @"Artists\Pct\100% Real\100% - Hash #1.mp3",
            @"Artists\Pct\Brackets [x]\A & B - [x] Song.mp3",
            @"Miscellaneous Songs\Solo - Track.mp3",
            // Two names that sanitise to the same XML name: the second must not rewrite the XML the first just made
            @"Miscellaneous Songs\Twin - Café.mp3",
            @"Miscellaneous Songs\Twin - Cafe.mp3",
            @"Miscellaneous Songs\Café del Mar - Да.mp3",
        };

        private static string Unescape(string s)
        {
            // Test data uses backslash-u hex markers inside verbatim strings so the source stays ASCII.
            var sb = new StringBuilder();
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == '\\' && i + 5 < s.Length && s[i + 1] == 'u')
                {
                    sb.Append((char)Convert.ToInt32(s.Substring(i + 2, 4), 16));
                    i += 5;
                }
                else sb.Append(s[i]);
            }
            return sb.ToString();
        }

        private static string Snapshot(string audio, string mirror, string repoRoot, string console, string root)
        {
            var sb = new StringBuilder();
            sb.AppendLine("--- console (time line removed) ---");
            foreach (string line in console.Replace("\r\n", "\n").Split('\n'))
            {
                if (line.StartsWith(" - Time taken:", StringComparison.Ordinal)) continue;
                sb.AppendLine(line.Replace(root, "<ROOT>"));
            }
            sb.AppendLine("--- helper per mp3 ---");
            foreach (string mp3 in Directory.GetFiles(audio, "*.mp3", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal))
            {
                sb.AppendLine(Reflector.MirrorXmlRelativePath(audio, mp3) + " | " + Reflector.SanitiseFilename(Path.GetFileName(mp3)));
            }
            sb.AppendLine("--- mirror files (relative | length | bytes hex) ---");
            foreach (string f in Directory.GetFiles(mirror, "*", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal))
            {
                byte[] b = File.ReadAllBytes(f);
                sb.AppendLine(f.Substring(mirror.Length).Replace(root, "<ROOT>") + " | " + b.Length + " | " + BitConverter.ToString(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(b).Replace(root, "<ROOT>"))));
            }
            sb.AppendLine("--- mirror dirs ---");
            foreach (string d in Directory.GetDirectories(mirror, "*", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal))
                sb.AppendLine(d.Substring(mirror.Length));
            sb.AppendLine("--- stamp / staging ---");
            sb.AppendLine("staging exists: " + Directory.Exists(mirror + Reflector.StagingSuffix));
            sb.AppendLine("old exists: " + Directory.Exists(mirror + ".old"));
            return sb.ToString();
        }

        private static string RunReflector(string audio, string mirror, string repoRoot, string stamp)
        {
            var original = Console.Out;
            var writer = new StringWriter();
            Console.SetOut(writer);
            try { new Reflector(audio, mirror, repoRoot, stamp); }
            finally { Console.SetOut(original); }
            return writer.ToString();
        }

        /// <summary>Builds the fixture and runs the three passes; returns the three snapshots joined.</summary>
        internal static string RunScenario(bool oddAudioRoot)
        {
            string root = Path.Combine(Path.GetTempPath(), "am_regenparity_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            bool saved = AgeChecker.RegenMirror;
            try
            {
                string audio = Path.Combine(root, "Audio");
                // An audio root that is not in full-path form takes the original string-based listing; pinned too
                if (oddAudioRoot) audio = Path.Combine(root, "Audio", "..", "Audio");
                string mirror = Path.Combine(root, "AUDIO_MIRROR");
                string repoRoot = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
                string stamp = Path.Combine(root, "LastRunInfo.txt");
                Directory.CreateDirectory(audio);
                Directory.CreateDirectory(mirror);
                Directory.CreateDirectory(Path.Combine(audio, "Empty Folder"));

                DateTime baseTime = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
                foreach (string rel in Mp3s)
                {
                    string full = Path.Combine(audio, Unescape(rel));
                    Directory.CreateDirectory(Path.GetDirectoryName(full));
                    File.WriteAllText(full, "x");
                    File.SetLastWriteTimeUtc(full, baseTime);
                }
                File.WriteAllText(Path.Combine(audio, "desktop.ini"), "i");
                File.WriteAllText(Path.Combine(audio, "Artists", "notes.txt"), "n");
                File.WriteAllText(Path.Combine(audio, "Artists", "cover.jpg"), "j");

                AgeChecker.RegenMirror = false;
                var all = new StringBuilder();

                // Pass 1: empty mirror, every XML is created
                string c1 = RunReflector(audio, mirror, repoRoot, stamp);
                // Age every XML so the next pass sees the mp3s as unchanged
                foreach (string xml in Directory.GetFiles(mirror, "*.xml", SearchOption.AllDirectories))
                    File.SetLastWriteTimeUtc(xml, baseTime.AddDays(1));
                all.AppendLine("=== pass 1: create ===").Append(Snapshot(audio, mirror, repoRoot, c1, root));

                // Pass 2: no change
                var before = Directory.GetFiles(mirror, "*", SearchOption.AllDirectories)
                    .ToDictionary(p => p, p => File.GetLastWriteTimeUtc(p), StringComparer.Ordinal);
                string c2 = RunReflector(audio, mirror, repoRoot, stamp);
                all.AppendLine("=== pass 2: no change ===").Append(Snapshot(audio, mirror, repoRoot, c2, root));
                foreach (var kv in before)
                    Assert.True(File.GetLastWriteTimeUtc(kv.Key) == kv.Value, "no-change pass must not rewrite " + kv.Key);
                all.AppendLine("stamp written: " + File.Exists(stamp));

                // Pass 3: one track changed (newer than its XML)
                string changed = Path.Combine(audio, Unescape(Mp3s[2]));
                File.SetLastWriteTimeUtc(changed, baseTime.AddDays(2));
                string c3 = RunReflector(audio, mirror, repoRoot, stamp);
                all.AppendLine("=== pass 3: one changed ===").Append(Snapshot(audio, mirror, repoRoot, c3, root));

                // Pass 4: one mp3 deleted (orphan XML pruned), one new mp3 added
                File.Delete(Path.Combine(audio, Unescape(Mp3s[0])));
                string added = Path.Combine(audio, "Artists", "Alpha", "Album One", "Alpha - Three.mp3");
                File.WriteAllText(added, "x");
                File.SetLastWriteTimeUtc(added, baseTime);
                string c4 = RunReflector(audio, mirror, repoRoot, stamp);
                all.AppendLine("=== pass 4: delete + add ===").Append(Snapshot(audio, mirror, repoRoot, c4, root));

                return all.ToString();
            }
            finally
            {
                AgeChecker.RegenMirror = saved;
                try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch (Exception) { }
            }
        }

        public static void IncrementalMirror_NoChangeAndChangedPasses_MatchPinnedSnapshot()
        {
            string actual = "##### normal root #####\n" + RunScenario(false) + "##### odd root #####\n" + RunScenario(true);
            string path = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "MirrorRegenParity.expected.txt"));
            string expected = File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8).Replace("\r\n", "\n") : "";
            actual = actual.Replace("\r\n", "\n");
            if (Environment.GetEnvironmentVariable("MIRROR_PARITY_WRITE") == "1") File.WriteAllText(path, actual, new UTF8Encoding(false));
            Assert.True(expected == actual, "incremental mirror output drifted from the pinned snapshot. ACTUAL:\n" + actual);
        }
    }
}
