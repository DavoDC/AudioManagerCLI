using System;
using System.Collections.Generic;
using System.Linq;
using ScannedFile = AudioManager.MusicIntegrator.ScannedFile;

namespace AudioManager
{
    /// <summary>
    /// The L (replace library copy) state of one integration run. _lDeletedLibraryPaths holds the library paths that
    /// L-decisions deleted, so the Misc migration can tell a dup-resolution delete from a stale mirror. _lStaged maps
    /// each staged library path to its backup, and _lStagedBySource maps each replacement SourcePath to its library
    /// path: together they hold a replaced copy aside until its replacement commits or rolls back. _dryReplacedLibPaths
    /// is the dry-run set of library paths an L-decision would replace, so 3b treats their destination as free as the
    /// real run does. _lSlotOwners maps each L slot to the replacement that owns it, so no other file lands there.
    /// The duplicate executor writes this state and the file mover and the Misc migration read it. MusicIntegrator
    /// holds one instance and forwards to it.
    /// </summary>
    internal sealed class ReplacementLedger
    {
        // Library paths deleted by L-decisions during real integration; used in RunMiscMigration
        // to distinguish "deleted by dup resolution" from "genuinely stale mirror".
        internal readonly HashSet<string> _lDeletedLibraryPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // L-decision stage-then-commit: library copies moved aside (not deleted) until the replacement
        // lands. _lStaged: libPath -> bakPath. _lStagedBySource: replacement SourcePath -> libPath.
        // Commit deletes the bak on success; rollback restores it on any skip; leftovers roll back at end.
        internal readonly Dictionary<string, string> _lStaged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        internal readonly Dictionary<string, string> _lStagedBySource = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // Dry run only: library paths an L-decision would replace (not blocked in 3a). Their copy is staged
        // aside in the real run, so 3b treats their destination as free, as the real run does.
        internal readonly HashSet<string> _dryReplacedLibPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Both modes: library path of each L-decision slot -> SourcePath of the replacement that owns it (first owner wins).
        // While a slot is reserved, 3b blocks every other file routed to that path, so a K file cannot take the freed slot.
        internal readonly Dictionary<string, string> _lSlotOwners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Commits the L-staged library copy paired with this replacement once it has routed.</summary>
        internal void CommitIfStaged(ScannedFile sf)
        {
            if (!_lStagedBySource.TryGetValue(sf.SourcePath, out string libPath)) return;
            if (_lStaged.TryGetValue(libPath, out string bakPath)) ReplaceGuard.Commit(bakPath);
            _lStaged.Remove(libPath);
            _lStagedBySource.Remove(sf.SourcePath);
        }

        /// <summary>Restores the L-staged library copy paired with this replacement when its routing is skipped.</summary>
        internal void RollbackIfStaged(ScannedFile sf)
        {
            if (!_lStagedBySource.TryGetValue(sf.SourcePath, out string libPath)) return;
            if (_lStaged.TryGetValue(libPath, out string bakPath)) ReplaceGuard.Rollback(bakPath, libPath);
            _lStaged.Remove(libPath);
            _lStagedBySource.Remove(sf.SourcePath);
            _lDeletedLibraryPaths.Remove(libPath);
        }

        /// <summary>Rolls back every L-staged library copy still pending at the end of routing.</summary>
        internal void RollbackAllStaged()
        {
            foreach (var kv in _lStaged.ToList())
            {
                ReplaceGuard.Rollback(kv.Value, kv.Key);
                _lDeletedLibraryPaths.Remove(kv.Key);
            }
            _lStaged.Clear();
            _lStagedBySource.Clear();
        }

        /// <summary>Records sourcePath as the owner of the L slot at libPath. The first owner wins.</summary>
        internal void RegisterSlotOwner(string libPath, string sourcePath)
        {
            if (!_lSlotOwners.ContainsKey(libPath)) _lSlotOwners[libPath] = sourcePath;
        }

        /// <summary>
        /// True when destPath is an L slot owned by a different replacement. Such a slot is reserved for its
        /// owner even while its library copy is staged aside, so no other file may land there in this batch.
        /// </summary>
        internal bool IsReservedForOtherReplacement(string destPath, string sourcePath)
        {
            return _lSlotOwners.TryGetValue(destPath, out string owner)
                && !string.Equals(owner, sourcePath, StringComparison.OrdinalIgnoreCase);
        }
    }
}
