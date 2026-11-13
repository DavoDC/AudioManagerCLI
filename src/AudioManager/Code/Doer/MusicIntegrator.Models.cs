using AudioManager.Code.Modules;
using System;
using System.Collections.Generic;

namespace AudioManager
{
    internal partial class MusicIntegrator
    {
        /// <summary>Per-file integration result for the log and JSON output.</summary>
        internal class LogEntry
        {
            public string Filename;
            // Path relative to NewMusic (null when the source is not under it); lets a client key rows per file.
            public string SourceRel;
            // The same path before Tag Fix renamed the file in place (equal to SourceRel when not renamed). A dry-run
            // manifest is built from the pre-rename name, so a client matches a renamed row by this one.
            public string OriginalSourceRel;
            public string Title;
            public string Artists;
            public string Album;
            public string Destination;
            public List<string> TagChanges = new List<string>();
            public string Status; // "moved", "would-move", "skipped", "would-skip", "error"
            public string Detail; // reason or error message
            public string Reason;
            public bool IsNewFolder;
            public bool InBatchDuplicate;
            // True when this file's album was detected as a compilation album by RunScanAhead
            // (3+ distinct primary artists on the same album within this batch). Independent of
            // where the file finally routes: an album can be a batch compilation and still land
            // under Artists/ when the primary artist already has a folder, so this flag records
            // the detection itself rather than the routing outcome.
            public bool CompilationAlbum;
            // Library-duplicate block (distinct from InBatchDuplicate - see class remarks below).
            // Populated once in PreScanFiles when a library duplicate is found; independent of
            // which D/L/K decision is later made, so it survives into the JSON for every outcome.
            public bool LibraryDuplicate;
            public string DupLibraryPath;   // RelMirrorPath - "In AudioMirror" (see CLAUDE.md Display Conventions)
            public string DupLibraryTrack;
            public string DupLibraryAlbum;
            public string DupNewAlbum;
            public string DupRecommendationKey; // "D", "L", or "K"
            public string DupRecommendation;    // "Delete library copy" / "Delete new file" / "Keep both"
            public string DupReason;
            // Full before/after tag and filename change for this track (GUI-54), from TagFixer.LogsByFile.
            public TagFixer.FixLog TagFix;
        }

        /// <summary>
        /// Structured post-run integrity data for a real run's execution record (section 2.2 of
        /// the execution record contract). Populated by PrintConfidenceReport
        /// from the exact same locals it already prints from, so the JSON and the console text can
        /// never disagree. Null on a dry run - the sanity check never runs there. Data only, no
        /// behaviour.
        /// </summary>
        internal class ConfidenceRecord
        {
            public int TotalFiles;
            public int Moved;
            public int Skipped;
            public int ExpectedMoved;
            public bool CountOk;
            public bool SanityRan;
            public int SanityChecked;
            public List<(string Destination, string Reason)> SanityFailures = new List<(string Destination, string Reason)>();
            public List<string> NewFolders = new List<string>();
            public List<(string Filename, string Detail, string SourceRel)> Errors = new List<(string Filename, string Detail, string SourceRel)>();
        }

        /// <summary>
        /// Batch-level scan-ahead context that is not derivable from any single LogEntry.
        /// Carried into BuildJson so a client can show the same batch picture the CLI prints:
        /// which artists get existing Misc songs auto-migrated (files ALREADY in the library
        /// that this run will move), and which albums were detected as batch compilations.
        /// Route distribution is NOT held here - BuildJson derives it from the entries.
        /// </summary>
        internal class BatchSummary
        {
            /// <summary>primary artist -> number of existing Misc songs that will be auto-migrated.</summary>
            public Dictionary<string, int> MiscAutoMigrations = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            /// <summary>Album names with 3+ distinct primary artists in this batch.</summary>
            public List<string> CompilationAlbums = new List<string>();
        }

        /// <summary>
        /// NewMusic as a real run left it, written as the execution record's "leftovers" block.
        /// EmptyFolders and OtherFiles are relative to NewMusic (backslashes); DeclinedCount is the
        /// number of mp3s a manifest declined (they stay in NewMusic and are not listed in OtherFiles).
        /// </summary>
        internal class LeftoversSnapshot
        {
            public List<string> EmptyFolders { get; } = new List<string>();
            public List<string> OtherFiles { get; } = new List<string>();
            public int DeclinedCount { get; set; }
        }

        /// <summary>Data about a duplicate found during pre-scan.</summary>
        internal class DupData
        {
            public string DuplicatePath;
            public string LibraryFilePath;
            public string RelLibraryPath;
            public string RelMirrorPath;
            public string RelNewPath;
            public string DisplayNewFilename;
            public string MirrorTrack;
            public string MirrorAlbum;
            public string DupReason;
            public char RecommendedKey; // 'D', 'L', or '\0'
            public string OptionsLine;
            public char Decision; // set by PresentDuplicateAndDecide: 'D', 'L', 'K', 'Q'
            public bool SkipRouting; // true for D-decided duplicates and L-decided ones whose replacement is blocked (3a); dry-run L routes in 3b
        }

        /// <summary>
        /// Set when the pre-move MAX_PATH gate stops a batch before any file moves. Written as the top-level
        /// "blocked" object of the routing JSON (see docs/Contracts/RoutingJSONFormat.md).
        /// </summary>
        internal class MirrorPathBlock
        {
            public string Status = "path_too_long";
            public string Action = "rename_or_shorten";
            public int Limit = Reflector.OverlongPathLimit;
            public List<(string file, int length)> Files = new List<(string file, int length)>();
        }

        /// <summary>A file pre-scanned from NewMusic with its cleaned tags and duplicate data.</summary>
        internal class ScannedFile
        {
            public string SourcePath;
            public Track Track;
            public bool IsReadable;
            public string ReadError;
            public Exception ReadException;
            public LogEntry LogEntry;
            public DupData Duplicate; // null if no duplicate found
            public bool InBatchDuplicate; // true if another file in the batch shares normalised artist+title and is not a distinct recording
            public double LengthSec; // 0 = unknown
        }

        /// <summary>
        /// One real Misc move as journaled. From and To are library paths. RetagFrom and RetagTo are set only when
        /// the ARTIST tag was retagged to the artist folder spelling after the move (BL-149).
        /// </summary>
        internal sealed class MiscMigrationMove
        {
            public string From, To, RetagFrom, RetagTo;
        }

        /// <summary>Counts from one RevertMiscMigration run: moves reverted, moves skipped (not at the migrated place, or old place taken), moves that failed.</summary>
        internal sealed class MiscRevertResult
        {
            public int Reverted, Skipped, Failed;
        }

        /// <summary>Facts the duplicate recommendation needs; a length of 0 or less means unknown.</summary>
        internal class DupFacts
        {
            public string NewAlbum, LibraryAlbum;
            public bool NewIsAlbum, SameAlbum, LibraryIsSingle, LibraryIsCompilation;
            public int IncomingAlbumTracks, LibraryAlbumTracks, IncomingArtists, LibraryArtists;
            public double IncomingLengthSec, LibraryLengthSec;
        }
    }
}
