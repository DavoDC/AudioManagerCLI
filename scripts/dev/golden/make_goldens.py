"""Capture golden JSON fixtures from REAL AudioManager.exe runs on a synthetic sandbox.

Producer side of the contract rule: the engine publishes tests/golden/<name>.json from real runs; the consumer's
GoldenContractTests read them; scripts/dev/golden/check_goldens.py regenerates them and diffs the key sets.

Usage:  python scripts\\dev\\golden\\make_goldens.py [--exe PATH] [--out DIR]

The sandbox is a fresh temp folder (am_golden_*). The exe is pointed at it with AUDIOMANAGER_TEST_ROOT, so the real
library, NewMusic and mirror are never touched. Only --out (default tests/golden) is written. The sandbox holds the
cases that go wrong: a clean single, an album pair, a library duplicate, a badly tagged file that Tag Fix changes,
an album song in library Singles/ (libfix), an undecided track, a non-mp3 file and an empty folder.

Volatile values are normalised (timestamps, fingerprints, dates, age buckets, absolute sandbox paths); every key is
kept. Any path the engine reports outside the sandbox fails the run.
"""
import argparse
import hashlib
import json
import os
import re
import shutil
import stat
import subprocess
import sys
import tempfile
from contextlib import contextmanager
from pathlib import Path

REPO = Path(__file__).resolve().parents[3]
DEFAULT_EXE = REPO / "src" / "AudioManager" / "bin" / "Release" / "AudioManager.exe"
DEFAULT_OUT = REPO / "tests" / "golden"
SANDBOX_PREFIX = "am_golden_"

try:
    from mutagen.id3 import APIC, ID3, TALB, TCMP, TDRC, TIT2, TPE1
except ImportError:
    sys.exit("make_goldens: mutagen is missing (pip install mutagen). Nothing was run.")

MP3_FRAME = bytes([0xFF, 0xFB, 0x90, 0x00]) + bytes(413)
TINY_PNG = bytes.fromhex("89504e470d0a1a0a0000000d49484452000000010000000108060000001f15c4890000000d49444154789c6300010000000500010d0a2db40000000049454e44ae426082")

STAMP_KEYS = {"newMusicFingerprint", "previewFingerprint", "oldFingerprint", "newFingerprint"}
AGE_KEYS = {"averageYears", "medianYears", "newestYears", "oldestYears"}
DRIVE_PATH = re.compile(r"[A-Za-z]:[\\/][^\s\"'<>|*?]*")
ISO_TIME = re.compile(r"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?")
STAMP = re.compile(r"\d{8}-\d{6}")

# Golden name -> contract doc it follows (printed in the run report).
CONTRACT = {
    "routing-dry-run": "RoutingJSONFormat.md",
    "routing-blocked-path-too-long": "RoutingJSONFormat.md",
    "execution": "RoutingJSONFormat.md",
    "execution-replaced-originals": "RoutingJSONFormat.md",
    "execution-preview-stale": "RoutingJSONFormat.md",
    "fingerprint": "RoutingJSONFormat.md",
    "integrate-gate-mirror-stale": "GateResultJSONFormat.md",
    "integrate-gate-libchecker-dirty": "GateResultJSONFormat.md",
    "integrate-gate-passed": "GateResultJSONFormat.md",
    "integrate-gate-mirror-repo-missing": "GateResultJSONFormat.md",
    "mirror-result": "MirrorResultJSONFormat.md",
    "mirror-result-libchecker-issues": "MirrorResultJSONFormat.md",
    "mirror-result-skipped": "MirrorResultJSONFormat.md",
    "tagfix-plan": "TagFixJSONFormat.md",
    "tagfix-apply-result": "TagFixApplyJSONFormat.md",
    "tagfix-apply-result-stale-plan": "TagFixApplyJSONFormat.md",
    "tagfix-apply-result-files-in-use": "TagFixApplyJSONFormat.md",
    "tagfix-journal": "TagFixApplyJSONFormat.md",
    "libfix-plan": "LibFixJSONFormat.md",
    "libfix-journal": "LibFixJSONFormat.md",
    "libfix-apply-result": "LibFixJSONFormat.md",
    "libfix-revert-result": "LibFixJSONFormat.md",
    "analysis-stats": "AnalysisJSONFormat.md",
    "analysis-tracks": "AnalysisJSONFormat.md",
}


def song(path: Path, artist: str, title: str, album: str) -> Path:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(MP3_FRAME * 12)
    tags = ID3()
    # Year, cover and the compilation flag keep LibChecker quiet, as a real library is (this library marks every song
    # as a compilation, see the LibChecker not_compilation rule).
    tags.add(TDRC(encoding=3, text="2007"))
    tags.add(TCMP(encoding=3, text="1"))
    tags.add(APIC(encoding=3, mime="image/png", type=3, desc="", data=TINY_PNG))
    tags.add(TPE1(encoding=3, text=artist))
    tags.add(TIT2(encoding=3, text=title))
    tags.add(TALB(encoding=3, text=album))
    tags.save(path)
    return path


def seed(root: Path) -> None:
    """The sandbox library and inbox, with the cases that go wrong."""
    nm = root / "NewMusic"
    song(nm / "Kanye West - Stronger.mp3", "Kanye West", "Stronger", "Graduation")                # album pair, part 1
    song(nm / "Kanye West - Flashing Lights.mp3", "Kanye West", "Flashing Lights", "Graduation")  # album pair, part 2
    song(nm / "Drake - Hotline Bling.mp3", "Drake", "Hotline Bling", "Views")                   # clean single, left undecided
    song(nm / "03 - Some Song (Explicit).mp3", "Some Artist feat. Guest", "Some Song (Explicit)", "Some Album")  # badly tagged
    song(nm / "Jay-Z - Encore.mp3", "Jay-Z", "Encore", "The Black Album")                       # library duplicate
    (nm / "desktop.ini").write_text("[.ShellClassInfo]\n", encoding="utf-8")                    # non-mp3 leftover
    (nm / "Empty Folder").mkdir(parents=True, exist_ok=True)                                     # empty leftover folder
    au = root / "Audio" / "Artists"
    song(au / "Kanye West" / "Graduation" / "Kanye West - Gold Digger.mp3", "Kanye West", "Gold Digger", "Graduation")
    song(au / "Kanye West" / "Singles" / "Kanye West - Champion.mp3", "Kanye West", "Champion", "Graduation")  # album in Singles
    song(au / "Jay-Z" / "Singles" / "Jay-Z - Encore.mp3", "Jay-Z", "Encore", "The Black Album")


def git_env() -> dict:
    return {**os.environ, "GIT_AUTHOR_NAME": "t", "GIT_AUTHOR_EMAIL": "t@example.invalid",
            "GIT_COMMITTER_NAME": "t", "GIT_COMMITTER_EMAIL": "t@example.invalid"}


def init_mirror_repo(root: Path) -> None:
    mirror = root / "AudioMirror"
    mirror.mkdir(parents=True, exist_ok=True)
    for args in (["init", "-q", "-b", "main"], ["commit", "-q", "--allow-empty", "-m", "start"]):
        subprocess.run(["git", "-C", str(mirror), *args], check=True, env=git_env(), capture_output=True)


def commit_mirror(root: Path) -> None:
    mirror = str(root / "AudioMirror")
    for args in (["add", "-A"], ["commit", "-q", "--allow-empty", "-m", "mirror"]):
        subprocess.run(["git", "-C", mirror, *args], check=True, env=git_env(), capture_output=True)


def root_variants(root: Path) -> list:
    text = str(root)
    variants = {text, text.replace("\\", "/"), root.as_posix()}
    return sorted(variants, key=len, reverse=True)


def check_path_text(text: str, root: Path, where: str) -> None:
    """Every drive-letter path in the text must sit inside the sandbox."""
    base = str(root).replace("/", "\\").rstrip("\\").lower()
    for match in DRIVE_PATH.finditer(text):
        found = match.group(0).replace("/", "\\").rstrip("\\").lower()
        if found != base and not found.startswith(base + "\\"):
            raise RuntimeError(f"{where}: engine reported a path outside the sandbox: {match.group(0)}")


class Runner:
    def __init__(self, exe: Path, root: Path):
        self.exe = exe
        self.root = root
        self.commands: list = []

    def run(self, args: list, timeout: int = 300) -> subprocess.CompletedProcess:
        env = {**os.environ, "AUDIOMANAGER_TEST_ROOT": str(self.root)}
        self.commands.append("AudioManager.exe " + " ".join(args))
        proc = subprocess.run([str(self.exe), *args], env=env, capture_output=True, text=True,
                              encoding="utf-8", errors="replace", stdin=subprocess.DEVNULL, timeout=timeout)
        check_path_text(proc.stdout + proc.stderr, self.root, f"exe {' '.join(args)} (console)")
        return proc


def newest(folder: Path, pattern: str) -> Path:
    hits = sorted(folder.glob(pattern))
    if not hits:
        raise RuntimeError(f"no {pattern} in {folder} after the run")
    return hits[-1]


def normalise(node, root: Path, path: tuple = ()):
    if isinstance(node, dict):
        return {k: normalise(v, root, path + (k,)) for k, v in node.items()}
    if isinstance(node, list):
        return [normalise(v, root, path + ("[]",)) for v in node]
    leaf = path[-1] if path else ""
    if isinstance(node, str):
        check_path_text(node, root, "json " + ".".join(path))
        if leaf in STAMP_KEYS or path[-2:] == ("fingerprint", "value"):
            return "<SHA256>"
        if leaf == "addedDate":
            return "<DATE>"
        text = node
        for variant in root_variants(root):
            text = re.sub(re.escape(variant), "<ROOT>", text, flags=re.IGNORECASE)
        text = ISO_TIME.sub("<ISO>", text)
        return STAMP.sub("<STAMP>", text)
    if isinstance(node, (int, float)) and not isinstance(node, bool):
        if path[:1] == ("ageStats",) and leaf in AGE_KEYS:
            return "<YEARS>"
        if path[-3:] == ("ageDistribution", "[]", "count"):
            return "<COUNT>"
    return node


def capture(goldens: dict, runner: Runner, name: str, folder: Path, pattern: str, expect: dict = None,
            check=None) -> None:
    """Read the newest JSON the engine wrote, assert the status this case should produce, store it normalised."""
    path = newest(folder, pattern)
    doc = json.loads(path.read_text(encoding="utf-8-sig"))
    for key, want in (expect or {}).items():
        got = doc.get(key)
        if got != want:
            raise RuntimeError(f"{name}: expected {key}={want!r}, engine wrote {got!r}")
    if check is not None:
        check(doc)
    goldens[name] = normalise(doc, runner.root)
    goldens[name + "#command"] = runner.commands[-1]


def capture_stdout_json(goldens: dict, runner: Runner, name: str, args: list) -> None:
    proc = runner.run(args)
    if proc.returncode != 0:
        raise RuntimeError(f"{name}: exit {proc.returncode}: {proc.stdout[-400:]}")
    line = [ln for ln in proc.stdout.splitlines() if ln.strip().startswith("{")][-1]
    goldens[name] = normalise(json.loads(line), runner.root)
    goldens[name + "#command"] = runner.commands[-1]


def has_mirror_hit(kind: str):
    def check(doc: dict) -> None:
        kinds = [h.get("kind") for h in doc.get("hits", [])]
        if kind not in kinds:
            raise RuntimeError(f"expected a LibChecker hit of kind {kind}, got {kinds}")
    return check


def stale_plan_differs(doc: dict) -> None:
    if doc.get("oldFingerprint") == doc.get("newFingerprint"):
        raise RuntimeError("stale_plan golden has equal old and new fingerprints; the case did not go stale")


def routing_manifest(doc: dict) -> list:
    rows = []
    for row in doc["files"]:
        if row["filename"] == "Drake - Hotline Bling.mp3":
            continue  # left undecided: stays in NewMusic, counted as declined
        entry = {"filename": row["filename"], "sourceRel": row["sourceRel"], "artist": row["artist"],
                 "title": row["title"]}
        if row.get("libraryDuplicate"):
            entry["dupResolution"] = "D"
        rows.append(entry)
    return rows


def build(exe: Path, root: Path, goldens: dict) -> None:
    """Run the producer flow on one sandbox. Every capture asserts the status the case should produce."""
    r = Runner(exe, root)
    logs = root / "logs"
    seed(root)
    init_mirror_repo(root)
    gate = ["integrate", "--dry-run", "--json-output", "--no-input"]

    # Gate states: the first run regenerates the mirror, so the gate sees a stale mirror; commit it, then LibChecker blocks.
    r.run(["integrate", "--dry-run", "--no-input"])
    r.run(gate)
    capture(goldens, r, "integrate-gate-mirror-stale", logs, "integrate-gate.json",
            {"status": "mirror_stale", "action": "commit_mirror"})
    commit_mirror(root)
    r.run(gate)
    capture(goldens, r, "integrate-gate-libchecker-dirty", logs, "integrate-gate.json",
            {"status": "libchecker_dirty", "action": "fix_library_issues"})

    # Mirror retry while the album-in-Singles hit stands: LibChecker dirty, nothing committed.
    r.run(["--mirror-retry"])
    capture(goldens, r, "mirror-result-libchecker-issues", logs, "mirror-result.json",
            {"status": "libchecker_issues", "mirrorCommitted": False}, check=has_mirror_hit("album_in_singles"))

    # Library fix: plan, apply, revert, then apply again so the library is left clean.
    r.run(["--libfix-plan"])
    capture(goldens, r, "libfix-plan", logs, "libfix-plan.json",
            {"schema": "libfix-plan-1", "kind": "album_in_singles"})
    plan = logs / "libfix-plan.json"
    r.run(["--libfix-apply", str(plan)])
    capture(goldens, r, "libfix-apply-result", logs, "libfix-result.json",
            {"schema": "libfix-result-1", "operation": "apply", "status": "applied"})
    capture(goldens, r, "libfix-journal", logs, "libfix-journal-*.json", {"schema": "libfix-journal-1"})
    journal = newest(logs, "libfix-journal-*.json")
    r.run(["--libfix-revert", str(journal)])
    capture(goldens, r, "libfix-revert-result", logs, "libfix-result.json",
            {"schema": "libfix-result-1", "operation": "revert", "status": "reverted"})
    r.run(["--libfix-plan"])
    r.run(["--libfix-apply", str(plan)])

    # Mirror: the skipped step first (nothing committed), then the retry that commits.
    r.run(["--mirror-retry", "--no-auto-commit"])
    capture(goldens, r, "mirror-result-skipped", logs, "mirror-result.json",
            {"status": "skipped", "mirrorCommitted": False})
    r.run(["--mirror-retry"])
    capture(goldens, r, "mirror-result", logs, "mirror-result.json", {"status": "ok", "mirrorCommitted": True})

    # Gate passes; the dry run routes the inbox before Tag Fix has run (the badly tagged file carries a tagFix change).
    r.run(gate)
    capture(goldens, r, "integrate-gate-passed", logs, "integrate-gate.json", {"status": "passed"})
    capture(goldens, r, "routing-dry-run", logs, "routing-*.json", {"recordType": "routing", "schemaVersion": 1})
    capture_stdout_json(goldens, r, "fingerprint", ["--fingerprint"])

    # Tag Fix: a stray file added after the plan makes it stale, so apply refuses; then a fresh plan applies.
    r.run(["tagfix", "--dry-run", "--json-output", "--no-input"])
    stray = song(root / "NewMusic" / "Stray Artist - Stray Track.mp3", "Stray Artist", "Stray Track", "Stray Album")
    r.run(["tagfix", "--apply-plan"])
    capture(goldens, r, "tagfix-apply-result-stale-plan", logs, "tagfix-result.json",
            {"status": "stale_plan"}, check=stale_plan_differs)
    stray.unlink()
    r.run(["tagfix", "--dry-run", "--json-output", "--no-input"])
    capture(goldens, r, "tagfix-plan", logs, "tagfix-plan.json", {"schemaVersion": 1})
    r.run(["tagfix", "--apply-plan"])
    capture(goldens, r, "tagfix-apply-result", logs, "tagfix-result.json", {"status": "applied"})
    capture(goldens, r, "tagfix-journal", logs, "tagfix-journal-*.json")

    # A fresh dry run stamps the preview after Tag Fix; the real integrate takes its accepted rows as the manifest.
    r.run(gate)
    fresh = json.loads(newest(logs, "routing-*.json").read_text(encoding="utf-8-sig"))
    manifest = root / "manifest.json"
    manifest.write_text(json.dumps(routing_manifest(fresh)), encoding="utf-8")
    proc = r.run(["integrate", "--manifest", str(manifest), "--json-output", "--no-input"])
    if proc.returncode != 0:
        raise RuntimeError(f"integrate --manifest exit {proc.returncode}: {proc.stdout[-600:]}")
    capture(goldens, r, "execution", logs, "execution-*.json", {"recordType": "execution", "schemaVersion": 1})

    # Analysis last: it reads the library the integrate just wrote.
    r.run(["analysis", "--json-output", "--no-input"])
    capture(goldens, r, "analysis-stats", logs, "analysis-stats.json", {"schemaVersion": 1})
    capture(goldens, r, "analysis-tracks", logs, "tracks.json", {"schemaVersion": 1})


def build_missing_mirror(exe: Path, root: Path, goldens: dict) -> None:
    """Second sandbox with no AudioMirror repo: the gate must say mirror_repo_missing."""
    r = Runner(exe, root)
    seed(root)
    r.run(["integrate", "--dry-run", "--json-output", "--no-input"])
    capture(goldens, r, "integrate-gate-mirror-repo-missing", root / "logs", "integrate-gate.json",
            {"status": "mirror_repo_missing", "action": "restore_mirror_repo"})


GATE = ["integrate", "--dry-run", "--json-output", "--no-input"]
GENERIC_READ = 0x80000000
FILE_SHARE_READ, FILE_SHARE_WRITE = 0x1, 0x2
OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL = 3, 0x80


@contextmanager
def held_open(path: Path):
    """Hold path open for reading, as a player does, with read/write sharing so the engine can still read it to plan.
    The engine's write probe (ReadWrite, FileShare.None) then fails, which is how a file open in another program looks.
    A fully exclusive handle (FileShare.None) also blocks the plan scan, so the file is never planned. Closed on exit."""
    import ctypes
    from ctypes import wintypes
    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel32.CreateFileW.restype = wintypes.HANDLE
    kernel32.CreateFileW.argtypes = [wintypes.LPCWSTR, wintypes.DWORD, wintypes.DWORD, wintypes.LPVOID,
                                     wintypes.DWORD, wintypes.DWORD, wintypes.HANDLE]
    kernel32.CloseHandle.argtypes = [wintypes.HANDLE]
    handle = kernel32.CreateFileW(str(path), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, None, OPEN_EXISTING,
                                  FILE_ATTRIBUTE_NORMAL, None)
    if handle in (None, 0, ctypes.c_void_p(-1).value):
        raise RuntimeError(f"could not hold {path.name} open (WinError {ctypes.get_last_error()})")
    try:
        yield handle
    finally:
        kernel32.CloseHandle(handle)


def lib_path(root: Path, rel: str) -> Path:
    return root.joinpath("Audio", *re.split(r"[\\/]", rel))


def file_sha(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def gate_passes(r: Runner, root: Path) -> None:
    r.run(GATE)
    gate_doc = json.loads((root / "logs" / "integrate-gate.json").read_text(encoding="utf-8-sig"))
    if gate_doc.get("status") != "passed":
        raise RuntimeError(f"gate expected to pass, engine wrote {gate_doc.get('status')!r}: "
                           f"dirty={gate_doc.get('mirrorDirtyFiles')} libchecker={gate_doc.get('libCheckerIssues')}")


def mirror_status(logs: Path) -> str:
    return json.loads(newest(logs, "mirror-result.json").read_text(encoding="utf-8-sig")).get("status")


def manifest_row(row: dict, dup: str = None) -> dict:
    entry = {"filename": row["filename"], "sourceRel": row["sourceRel"], "artist": row["artist"], "title": row["title"]}
    if dup:
        entry["dupResolution"] = dup
    return entry


def requires_key(key: str):
    def check(doc: dict) -> None:
        if key not in doc:
            raise RuntimeError(f"engine record has no top-level {key}")
    return check


def ready_gate(r: Runner, root: Path, rehearse: bool) -> None:
    """Seed a sandbox and reach a passing gate (mirror committed, LibChecker clean), the state each edge case starts from.
    Same steps as build(), without the golden captures. With rehearse, the libfix apply and revert are hash-compared."""
    logs = root / "logs"
    seed(root)
    init_mirror_repo(root)
    r.run(["integrate", "--dry-run", "--no-input"])
    r.run(GATE)
    commit_mirror(root)
    r.run(GATE)
    r.run(["--mirror-retry"])
    r.run(["--libfix-plan"])
    plan = logs / "libfix-plan.json"
    moves = json.loads(plan.read_text(encoding="utf-8-sig"))["moves"]
    before = {m["from"]: file_sha(lib_path(root, m["from"])) for m in moves} if rehearse else {}
    r.run(["--libfix-apply", str(plan)])
    if rehearse:
        for m in moves:
            if lib_path(root, m["from"]).exists() or not lib_path(root, m["to"]).exists():
                raise RuntimeError(f"libfix apply did not move {m['from']} to {m['to']}")
            if file_sha(lib_path(root, m["to"])) != before[m["from"]]:
                raise RuntimeError(f"libfix apply changed the bytes of {m['from']}")
    r.run(["--libfix-revert", str(newest(logs, "libfix-journal-*.json"))])
    if rehearse:
        for m in moves:
            if not lib_path(root, m["from"]).exists() or lib_path(root, m["to"]).exists():
                raise RuntimeError(f"libfix revert did not move {m['to']} back to {m['from']}")
            if file_sha(lib_path(root, m["from"])) != before[m["from"]]:
                raise RuntimeError(f"libfix revert did not restore the original bytes of {m['from']}")
        result = json.loads((logs / "libfix-result.json").read_text(encoding="utf-8-sig"))
        if result.get("status") != "reverted":
            raise RuntimeError(f"libfix revert wrote status {result.get('status')!r}")
        print(f"  libfix revert rehearsal: {len(moves)} move(s) applied and reverted, original bytes restored (sha256 equal)")
    r.run(["--libfix-plan"])
    r.run(["--libfix-apply", str(plan)])
    r.run(["--mirror-retry"])
    gate_passes(r, root)


def build_edges(exe: Path, root: Path, goldens: dict) -> None:
    """Edge cases on one sandbox that passes the gate, in order: a planned file held open by another program
    (files_in_use), a real run after a file lands in NewMusic (previewStale true), then an L replacement of a
    library duplicate (replacedOriginals)."""
    r = Runner(exe, root)
    logs = root / "logs"
    ready_gate(r, root, rehearse=True)

    # Tag Fix refuses while a planned file is open in another program: nothing changes, the result names the file.
    r.run(["tagfix", "--dry-run", "--json-output", "--no-input"])
    with held_open(root / "NewMusic" / "03 - Some Song (Explicit).mp3"):
        r.run(["tagfix", "--apply-plan"])
    capture(goldens, r, "tagfix-apply-result-files-in-use", logs, "tagfix-result.json", {"status": "files_in_use"})

    # Plan again (nothing changed) and apply, so routing sees the Tag Fix output as the main flow does.
    r.run(["tagfix", "--dry-run", "--json-output", "--no-input"])
    r.run(["tagfix", "--apply-plan"])

    # Stale preview: the dry run stamps NewMusic, then a file lands in NewMusic before the real run.
    gate_passes(r, root)
    manifest = root / "manifest.json"
    fresh = json.loads(newest(logs, "routing-*.json").read_text(encoding="utf-8-sig"))
    # Declined: the duplicate Jay-Z (kept for the L run) and the badly tagged Misc song, whose "feat." artist would
    # leave a LibChecker unwanted_text hit in the library (Misc songs are not retagged after migration).
    keep = [e for e in routing_manifest(fresh) if e["filename"] != "Jay-Z - Encore.mp3" and "Some Song" not in e["filename"]]
    manifest.write_text(json.dumps(keep), encoding="utf-8")
    stray = song(root / "NewMusic" / "Stray Artist - Stray Track.mp3", "Stray Artist", "Stray Track", "Stray Album")
    proc = r.run(["integrate", "--manifest", str(manifest), "--json-output", "--no-input"])
    if proc.returncode != 0:
        raise RuntimeError(f"integrate --manifest (stale preview) exit {proc.returncode}: {proc.stdout[-600:]}")
    capture(goldens, r, "execution-preview-stale", logs, "execution-*.json",
            {"recordType": "execution", "previewStale": True})

    # The real run moved files into the library; its post-integration mirror step did not commit them (the engine
    # prints the retry command for this case), so retry the mirror step before the gate is checked again.
    print(f"  stale-preview run: post-integration mirror status {mirror_status(logs)!r}; retrying the mirror step")
    r.run(["--mirror-retry"])

    # Replacement: the stray leaves NewMusic, a fresh dry run stamps the folder, and the manifest accepts the
    # library duplicate Jay-Z - Encore with L (replace the library copy).
    stray.unlink()
    gate_passes(r, root)
    fresh = json.loads(newest(logs, "routing-*.json").read_text(encoding="utf-8-sig"))
    dup = [row for row in fresh["files"] if row["filename"] == "Jay-Z - Encore.mp3"]
    if not dup or not dup[0].get("libraryDuplicate"):
        raise RuntimeError("Jay-Z - Encore.mp3 is not a library duplicate in the fresh preview")
    manifest.write_text(json.dumps([manifest_row(dup[0], "L")]), encoding="utf-8")
    proc = r.run(["integrate", "--manifest", str(manifest), "--json-output", "--no-input"])
    if proc.returncode != 0:
        raise RuntimeError(f"integrate --manifest (L replace) exit {proc.returncode}: {proc.stdout[-600:]}")
    capture(goldens, r, "execution-replaced-originals", logs, "execution-*.json",
            {"recordType": "execution", "previewStale": False})


def build_blocked(exe: Path, root: Path, goldens: dict) -> None:
    """A planned destination whose staged mirror path passes the Windows limit: the gate blocks the batch and the
    routing record carries the top-level blocked object. The long title is the only long name in the sandbox."""
    r = Runner(exe, root)
    ready_gate(r, root, rehearse=False)
    song(root / "NewMusic" / "Long Artist - Long Title.mp3", "Long Artist", "Long Title " + "x" * 170, "Long Album")
    r.run(GATE)
    capture(goldens, r, "routing-blocked-path-too-long", root / "logs", "routing-*.json",
            {"recordType": "routing", "schemaVersion": 1}, check=requires_key("blocked"))


def make_sandbox() -> Path:
    return Path(tempfile.mkdtemp(prefix=SANDBOX_PREFIX)).resolve()


def remove_sandbox(root: Path) -> None:
    assert root.name.startswith(SANDBOX_PREFIX), "refusing to delete a folder this script did not make"

    def clear_readonly(func, path, _exc):
        os.chmod(path, stat.S_IWRITE)
        func(path)

    shutil.rmtree(root, onerror=clear_readonly)


def data_snapshot() -> set:
    data = REPO / "data"
    out = set()
    if data.exists():
        for dirpath, _dirs, files in os.walk(data):
            for name in files:
                full = Path(dirpath) / name
                out.add((str(full), full.stat().st_size))
    return out


def build_all(exe: Path) -> dict:
    """Run the whole producer flow on fresh sandboxes; returns {golden name: normalised doc, name#command: str}."""
    if not exe.exists():
        raise SystemExit(f"make_goldens: engine exe missing: {exe}. Build it first (scripts\\dev\\build.bat --no-pause).")
    before = data_snapshot()
    goldens: dict = {}
    roots = [make_sandbox() for _ in range(4)]  # main, missing mirror, edge cases, blocked path
    main_root, missing_root, edge_root, blocked_root = roots
    try:
        build(exe, main_root, goldens)
        build_missing_mirror(exe, missing_root, goldens)
        build_edges(exe, edge_root, goldens)
        build_blocked(exe, blocked_root, goldens)
    finally:
        for root in roots:
            remove_sandbox(root)
    if data_snapshot() != before:
        raise RuntimeError("the engine wrote under the real repo data folder; the sandbox guard failed")
    return goldens


def dump(doc) -> str:
    return json.dumps(doc, indent=2, ensure_ascii=False) + "\n"


def main(argv: list) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--exe", default=str(DEFAULT_EXE), help="engine exe (default: Release build)")
    parser.add_argument("--out", default=str(DEFAULT_OUT), help="folder for the goldens (default tests/golden)")
    args = parser.parse_args(argv)
    out_dir = Path(args.out)
    out_dir.mkdir(parents=True, exist_ok=True)
    goldens = build_all(Path(args.exe))
    names = sorted(k for k in goldens if not k.endswith("#command"))
    for name in names:
        (out_dir / f"{name}.json").write_bytes(dump(goldens[name]).encode("utf-8"))
        print(f"  {name}.json  <-  {goldens[name + '#command']}  [{CONTRACT.get(name, '?')}]")
    print(f"make_goldens: wrote {len(names)} goldens to {out_dir}")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
