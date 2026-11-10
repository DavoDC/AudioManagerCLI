using System;
using System.IO;

namespace AudioManager
{
    internal static class Constants
    {
        /// <summary>
        /// Paths
        /// </summary>

        // Common user base path from %USERPROFILE% with fallback (e.g. <home>\)
        public static readonly string UserBasePath = GetUserBasePath();

        // Test-only override: env AUDIOMANAGER_TEST_ROOT redirects NewMusic, its quarantine, logs, backups,
        // the library (Audio) and the mirror repo (AudioMirror) into that folder. Unset means the real paths, unchanged.
        public static readonly string TestRoot = Environment.GetEnvironmentVariable("AUDIOMANAGER_TEST_ROOT");
        private static bool HasTestRoot => !string.IsNullOrWhiteSpace(TestRoot);

        // Absolute path to the repo root (discovered by walking up from exe directory)
        public static readonly string RepoRoot = FindRepoRoot();

        // Must come after TestRoot and RepoRoot: static initialisers run in textual order.
        private static readonly ResolvedRoots Roots = ResolveRoots(TestRoot, UserBasePath, RepoRoot);

        // The path to the actual audio library (e.g. <home>\Audio\; test root: <testRoot>\Audio)
        public static readonly string AudioFolderPath = Roots.Audio;

        // The path to the new music folder (e.g. <home>\Downloads\NewMusic\; test root: <testRoot>\NewMusic)
        public static readonly string NewMusicPath = Roots.NewMusic;

        // Quarantine sibling of NewMusic (e.g. <home>\Downloads\NewMusic-Quarantine\). Deliberately outside
        // NewMusicPath because TagFixer scans NewMusicPath recursively.
        public static readonly string QuarantinePath = Roots.Quarantine;

        // Absolute path to the mirror repo (assumed to sit next to this repo in GitHubRepos\; test root: <testRoot>\AudioMirror\)
        public static readonly string MirrorRepoPath = Roots.MirrorRepo;

        // Mirror folder name
        public const string MirrorFolderName = "AUDIO_MIRROR";

        // Absolute path to mirror folder
        public static readonly string MirrorFolderPath = Path.Combine(MirrorRepoPath, MirrorFolderName);

        // Path to the reports folder (data/reports/, tracked except data/reports/with-issues/)
        public static readonly string ReportsPath = Path.Combine(RepoRoot, "data", "reports");

        // Path to the LibChecker exceptions config file
        public static readonly string LibCheckerExceptionsPath = Path.Combine(RepoRoot, "config", "libchecker-exceptions.xml");

        // Path to the artist name casing overrides config file
        public static readonly string ArtistOverridesPath = Path.Combine(RepoRoot, "config", "artist-name-overrides.xml");

        // Path to the user-defined TagFixer custom rules config file (additive pass after built-in fixes)
        public static readonly string TagFixCustomRulesPath = Path.Combine(RepoRoot, "config", "tagfix-custom-rules.xml");

        // Path to the artist name aliases config file (maps old names to canonical names for duplicate detection)
        public static readonly string ArtistAliasesPath = Path.Combine(RepoRoot, "config", "artist-aliases.xml");

        // Path to the integration logs folder (gitignored, written by MusicIntegrator)
        /// <summary>Tag Fix byte backups, one folder per apply run (gitignored; pruned after 7 days at the next apply).</summary>
        public static readonly string BackupsPath = HasTestRoot ? Path.Combine(TestRoot, "backups") : Path.Combine(RepoRoot, "data", "backups");
        public static readonly string LogsPath = HasTestRoot ? Path.Combine(TestRoot, "logs") : Path.Combine(RepoRoot, "data", "logs");

        // Path to the parser result cache (gitignored, in data/logs/)
        public static readonly string ParseCachePath = Path.Combine(LogsPath, "parse-cache.txt");

        // Max time a git subprocess may run before it is killed (bounded waits in AudioMirrorCommitter and ReportWriter)
        public const int GitTimeoutMs = 30000;

        /// <summary>
        /// Library folder names
        /// </summary>

        public const string MiscDir          = "Miscellaneous Songs";
        public const string ArtistsDir      = "Artists";
        public const string CompilationsDir = "Compilations";
        public const string MusivDir        = "Musivation";
        public const string MotivDir        = "Motivation";
        public const string SourcesDir      = "Sources";
        public const string SinglesDir      = "Singles";

        /// <summary>Tag value sentinel: the tag is absent from the file.</summary>
        public const string MissingTag = "Missing";
        /// <summary>Cover size sentinel: album art is present but its dimensions could not be read.</summary>
        public const string UnknownCover = "Unknown";

        /// <summary>
        /// Mirror Management
        /// </summary>

        // Last run info file path
        public static readonly string LastRunInfoFilePath = MirrorRepoPath + "LastRunInfo.txt";

        // Age threshold for mirror regeneration
        public static readonly TimeSpan AgeThreshold = TimeSpan.FromDays(7);

        /// <summary>
        /// File System / Validation Utilities
        /// </summary>

        // Invalid file name characters
        public static readonly char[] InvalidChars = Path.GetInvalidFileNameChars();

        /// <summary>
        /// Tag validation
        /// </summary>

        /// Unwanted info in song titles that should be removed when validating tags
        public static readonly string[] UnwantedInfo = {
            "feat.", "ft.", "edit", "bonus", "original", "soundtrack", "version", "explicit"
        };

        /// <summary>
        /// Helpers
        /// </summary>

        /// <summary>
        /// Gets the user's base path from the USERPROFILE environment variable,
        /// falling back to the system UserProfile folder if unset. Ensures a trailing separator.
        /// </summary>
        /// <returns>The user base path ending with a directory separator.</returns>
        private static string GetUserBasePath()
        {
            string userProfile = Environment.GetEnvironmentVariable("USERPROFILE");
            if (string.IsNullOrEmpty(userProfile))
            {
                userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            }

            if (!userProfile.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal) &&
                !userProfile.EndsWith(Path.AltDirectorySeparatorChar.ToString(), StringComparison.Ordinal))
            {
                userProfile += Path.DirectorySeparatorChar;
            }

            return userProfile;
        }

        /// <summary>Resolved library, new music, quarantine and mirror roots.</summary>
        internal sealed class ResolvedRoots
        {
            public readonly string NewMusic;
            public readonly string Quarantine;
            public readonly string MirrorRepo;
            public readonly string Audio;

            public ResolvedRoots(string newMusic, string quarantine, string mirrorRepo, string audio)
            {
                NewMusic = newMusic;
                Quarantine = quarantine;
                MirrorRepo = mirrorRepo;
                Audio = audio;
            }
        }

        /// <summary>
        /// Resolves the library, new music, quarantine and mirror roots. A non-blank testRoot redirects all four
        /// under it (AUDIOMANAGER_TEST_ROOT); null or blank keeps the real locations under userBase and repoRoot.
        /// Pure: no filesystem access.
        /// </summary>
        internal static ResolvedRoots ResolveRoots(string testRoot, string userBase, string repoRoot)
        {
            if (!string.IsNullOrWhiteSpace(testRoot))
            {
                return new ResolvedRoots(
                    Path.Combine(testRoot, "NewMusic"),
                    Path.Combine(testRoot, "NewMusic-Quarantine"),
                    Path.Combine(testRoot, "AudioMirror") + Path.DirectorySeparatorChar,
                    Path.Combine(testRoot, "Audio"));
            }

            string newMusic = Path.Combine(userBase, "Downloads", "NewMusic");
            return new ResolvedRoots(
                newMusic,
                Path.Combine(Path.GetDirectoryName(newMusic), "NewMusic-Quarantine"),
                Path.GetFullPath(Path.Combine(repoRoot, "..", "AudioMirror")) + Path.DirectorySeparatorChar,
                Path.Combine(userBase, "Audio"));
        }

        /// <summary>
        /// Finds the repo root by walking up from the exe directory until a sentinel file is found.
        /// Sentinels: CLAUDE.md, README.md, or .git directory.
        /// Falls back to exe directory if no sentinel is found.
        /// Replaces hardcoded ".." chains with dynamic discovery.
        /// </summary>
        /// <returns>The repo root directory path.</returns>
        private static string FindRepoRoot()
        {
            string current = AppDomain.CurrentDomain.BaseDirectory;
            string[] sentinels = { "CLAUDE.md", "README.md", ".git" };

            while (!string.IsNullOrEmpty(current))
            {
                foreach (var sentinel in sentinels)
                {
                    string path = Path.Combine(current, sentinel);
                    if (File.Exists(path) || Directory.Exists(path))
                    {
                        return current;
                    }
                }

                DirectoryInfo parent = Directory.GetParent(current);
                if (parent == null) break;
                current = parent.FullName;
            }

            return AppDomain.CurrentDomain.BaseDirectory;
        }
    }
}