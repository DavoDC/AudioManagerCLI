"""Dry-run parity harness for the MusicIntegrator decomposition: a baseline exe against a candidate exe.

Usage:  python scripts\\dev\\golden\\integrate_parity.py --baseline-exe PATH --exe PATH
        python scripts\\dev\\golden\\integrate_parity.py --self-test

Each exe is run on its own fresh, identically seeded sandbox (AUDIOMANAGER_TEST_ROOT, the same redirect make_goldens
uses, so the real library, NewMusic and mirror are never read or written). Only two command lines ever run, both
dry runs: `integrate --dry-run --no-input` and `integrate --dry-run --no-input --json-output`. The script asserts that
every command list contains --dry-run before it launches anything and refuses to start otherwise. make_goldens and
check_goldens are imported for their helpers only; their own real-integrate flows are never called.

For every scenario the console output, the newest routing-*.json and the exit codes of both runs are normalised
(sandbox root, timestamps, durations) and compared. Before comparing, each seeded case must have left its marker text
in the BASELINE console output; a missing marker prints FIXTURE NOT EXERCISED and exits 2 (a parity run that never
reached a branch proves nothing). Exit 0 prints IDENTICAL; exit 1 prints DIFFERENT with a diff.

--self-test needs no exe: it proves that a changed JSON file and a missing marker are both detected.

Cases (marker text in the baseline console output):
  scan_ahead_misc    an artist promoted by scan-ahead who also has Misc mirror entries
  compilation        a compilation album (3 distinct artists)
  album_subfolder    an album that routes to an album subfolder
  library_duplicate  a library duplicate found through the mirror
  in_batch_duplicate the same artist and title twice in the batch
  akira_sampled      an Akira The Don track with a sampled person
  folder_case        an artist folder whose case differs from the tag
  max_path           (second sandbox) a destination that projects past MAX_PATH
"""
import argparse
import difflib
import json
import re
import sys
import tempfile
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
import make_goldens  # noqa: E402  (same folder; shares the sandbox helpers)

song = make_goldens.song

# The only command lines this script may launch. Hard-coded, every one a dry run.
PREP_CMD = ["integrate", "--dry-run", "--no-input"]            # regenerates the sandbox mirror; output not compared
STDOUT_CMD = ["integrate", "--dry-run", "--no-input"]
JSON_CMD = ["integrate", "--dry-run", "--no-input", "--json-output"]
COMMANDS = {"prep": PREP_CMD, "stdout": STDOUT_CMD, "json": JSON_CMD}

# Marker text each seeded case must leave in the baseline console output. Order is report order.
GATE_MARKER = ("gate_passed", "Pre-integration validation passed.")
MAIN_MARKERS = [
    GATE_MARKER,
    ("scan_ahead_misc", "existing Misc song(s) will be auto-migrated"),
    ("compilation", "compilation album(s) detected"),
    ("album_subfolder", "Artist folder; 3 songs from album -> album subfolder"),
    ("library_duplicate", "In AudioMirror:"),
    ("in_batch_duplicate", "[WARN: IN-BATCH DUPLICATE]"),
    ("akira_sampled", "below People threshold"),
    ("folder_case", "(case only, matches the tag)"),
]
LONG_MARKERS = [
    GATE_MARKER,
    ("max_path", "[BLOCKED]"),
]
# Cases the plan lists that dry run cannot reach would be named here, never given a fake marker.
UNREACHABLE: list = []

DURATION = re.compile(r"\b\d+(?:\.\d+)?\s?(?:ms|s|sec|secs|seconds|minutes)\b")
CLOCK = re.compile(r"\b\d{1,2}:\d{2}:\d{2}(?:\.\d+)?\b")
LOG_STAMP = re.compile(r"\b\d{4}-\d{2}-\d{2}_\d{6}\b")
DATE = re.compile(r"\b\d{4}-\d{2}-\d{2}\b")
HEX_HASH = re.compile(r"\b[0-9a-f]{40}\b|\b[0-9a-f]{64}\b")


# ---- safety -------------------------------------------------------------------------------------------------------

def assert_command_lists() -> None:
    """Refuse to start unless every command this script can launch is a dry run."""
    for name, args in COMMANDS.items():
        if "--dry-run" not in args:
            raise SystemExit(f"integrate_parity: REFUSED, command list {name!r} lacks --dry-run: {args}. Nothing was run.")
        if args[0] != "integrate":
            raise SystemExit(f"integrate_parity: REFUSED, command list {name!r} is not an integrate dry run: {args}.")


class DryRunner(make_goldens.Runner):
    """make_goldens.Runner that re-checks, at launch time, that the command is a dry run and the root is a sandbox."""

    def run(self, args: list, timeout: int = 300):
        if "--dry-run" not in args:
            raise SystemExit(f"integrate_parity: REFUSED to launch without --dry-run: {args}")
        if not self.root.name.startswith(make_goldens.SANDBOX_PREFIX):
            raise SystemExit(f"integrate_parity: REFUSED, root is not a sandbox: {self.root}")
        if not str(self.root).lower().startswith(str(Path(tempfile.gettempdir()).resolve()).lower()):
            raise SystemExit(f"integrate_parity: REFUSED, sandbox is outside the temp folder: {self.root}")
        return super().run(args, timeout)


def repo_root_of(exe: Path) -> Path:
    """Where the exe would look for its repo root (walk up from its folder to CLAUDE.md, README.md or .git)."""
    current = exe.resolve().parent
    for folder in [current, *current.parents]:
        if any((folder / s).exists() for s in ("CLAUDE.md", "README.md", ".git")):
            return folder
    return exe.resolve().parent


def config_digest(folder: Path) -> dict:
    """File name -> sha256 for the config folder an exe reads (LibChecker exceptions, aliases, custom rules)."""
    cfg = folder / "config"
    return {p.name: make_goldens.file_sha(p) for p in sorted(cfg.glob("*")) if p.is_file()} if cfg.exists() else {}


def data_snapshot_of(folder: Path) -> set:
    out = set()
    data = folder / "data"
    if data.exists():
        for path in data.rglob("*"):
            if path.is_file():
                out.add((str(path), path.stat().st_size))
    return out


# ---- fixtures -----------------------------------------------------------------------------------------------------

def seed_main(root: Path) -> None:
    """Library and inbox that reach every branch listed in the module docstring, with a LibChecker-clean library."""
    nm = root / "NewMusic"
    au = root / "Audio"
    art = au / "Artists"

    # scan_ahead_misc: two Misc songs by Pat Misc in the library plus one in the batch = 3, so Pat Misc is promoted
    song(au / "Miscellaneous Songs" / "Pat Misc - One.mp3", "Pat Misc", "One", "Misc One")
    song(au / "Miscellaneous Songs" / "Pat Misc - Two.mp3", "Pat Misc", "Two", "Misc Two")
    song(nm / "Pat Misc - Three.mp3", "Pat Misc", "Three", "Misc Three")

    # compilation: three distinct artists on one album in the batch
    song(nm / "Comp One - Alpha.mp3", "Comp One", "Alpha", "Various Hits")
    song(nm / "Comp Two - Bravo.mp3", "Comp Two", "Bravo", "Various Hits")
    song(nm / "Comp Three - Charlie.mp3", "Comp Three", "Charlie", "Various Hits")

    # album_subfolder: the library already has two songs of the album in its subfolder, the batch adds a third
    song(art / "Ray Album" / "Record" / "Ray Album - Old Track.mp3", "Ray Album", "Old Track", "Record")
    song(art / "Ray Album" / "Record" / "Ray Album - Older Track.mp3", "Ray Album", "Older Track", "Record")
    song(nm / "Ray Album - New Track.mp3", "Ray Album", "New Track", "Record")

    # library_duplicate: same artist and title already in the library
    song(art / "Jay-Z" / "Singles" / "Jay-Z - Encore.mp3", "Jay-Z", "Encore", "The Black Album")
    song(nm / "Jay-Z - Encore.mp3", "Jay-Z", "Encore", "The Black Album")

    # in_batch_duplicate: the same artist and title twice in the inbox
    song(nm / "Twice Over - Same Song.mp3", "Twice Over", "Same Song", "Twice Album")
    song(nm / "Twice Over - Same Song (copy).mp3", "Twice Over", "Same Song", "Twice Album")

    # akira_sampled: an Akira The Don track naming a sampled person, no People folder yet
    song(nm / "Akira The Don - Sample Track.mp3", "Akira The Don;Scott Adams", "Sample Track", "Sample Album")

    # folder_case: the library folder and its songs' tags are lower case, the incoming tag is not
    song(art / "case check" / "Singles" / "case check - Old Single.mp3", "case check", "Old Single", "Case Album")
    song(nm / "Case Check - New Single.mp3", "Case Check", "New Single", "Case Album")


def seed_long(root: Path) -> None:
    """A destination that projects past MAX_PATH (the title carries the length)."""
    song(root / "Audio" / "Artists" / "Short Artist" / "Singles" / "Short Artist - Short Song.mp3",
         "Short Artist", "Short Song", "Short Album")
    song(root / "NewMusic" / "Long Artist - Long Title.mp3", "Long Artist", "Long Title " + "x" * 170, "Long Album")


SCENARIOS = {"main": (seed_main, MAIN_MARKERS), "long": (seed_long, LONG_MARKERS)}


# ---- run one exe on one sandbox -----------------------------------------------------------------------------------

def run_scenario(exe: Path, name: str) -> dict:
    """Seed a fresh sandbox, prepare the mirror, run the two compared dry runs, return raw results. Always removes the sandbox."""
    seed_fn, _markers = SCENARIOS[name]
    root = make_goldens.make_sandbox()
    try:
        runner = DryRunner(exe, root)
        seed_fn(root)
        make_goldens.init_mirror_repo(root)
        runner.run(PREP_CMD)                      # regenerates the mirror (dry run; sandbox mirror only)
        make_goldens.commit_mirror(root)
        out_proc = runner.run(STDOUT_CMD)
        json_proc = runner.run(JSON_CMD)
        logs = root / "logs"
        hits = sorted(logs.glob("routing-*.json"))
        doc = json.loads(hits[-1].read_text(encoding="utf-8-sig")) if hits else None
        return {
            "root": root,
            "stdout": out_proc.stdout + ("\n[stderr]\n" + out_proc.stderr if out_proc.stderr.strip() else ""),
            "json_stdout": json_proc.stdout,
            "doc": doc,
            "exit_stdout": out_proc.returncode,
            "exit_json": json_proc.returncode,
            "commands": list(runner.commands),
        }
    finally:
        make_goldens.remove_sandbox(root)


def normalise_text(text: str, root: Path) -> str:
    for variant in make_goldens.root_variants(root):
        text = re.sub(re.escape(variant), "<ROOT>", text, flags=re.IGNORECASE)
    text = make_goldens.ISO_TIME.sub("<ISO>", text)
    text = make_goldens.STAMP.sub("<STAMP>", text)
    text = LOG_STAMP.sub("<LOGSTAMP>", text)
    text = DATE.sub("<DATE>", text)
    text = CLOCK.sub("<CLOCK>", text)
    text = DURATION.sub("<DURATION>", text)
    text = HEX_HASH.sub("<HASH>", text)
    return text


def normalised(result: dict) -> dict:
    root = result["root"]
    doc = make_goldens.normalise(result["doc"], root) if result["doc"] is not None else None
    return {
        "stdout": normalise_text(result["stdout"], root),
        "json_stdout": normalise_text(result["json_stdout"], root),
        "routing_json": doc,
        "exit_stdout": result["exit_stdout"],
        "exit_json": result["exit_json"],
    }


def missing_markers(stdout: str, markers: list) -> list:
    return [(case, text) for case, text in markers if text not in stdout]


def diff_parts(base: dict, cand: dict) -> list:
    """Return human-readable difference lines between two normalised result dicts; empty means identical."""
    lines = []
    for key in ("exit_stdout", "exit_json"):
        if base[key] != cand[key]:
            lines.append(f"{key}: baseline {base[key]} != candidate {cand[key]}")
    for key in ("stdout", "json_stdout"):
        if base[key] != cand[key]:
            lines.append(f"{key} differs:")
            lines.extend(difflib.unified_diff(base[key].splitlines(), cand[key].splitlines(),
                                              "baseline", "candidate", lineterm="", n=2))
    base_json = json.dumps(base["routing_json"], indent=1, sort_keys=True)
    cand_json = json.dumps(cand["routing_json"], indent=1, sort_keys=True)
    if base_json != cand_json:
        lines.append("routing json differs:")
        lines.extend(difflib.unified_diff(base_json.splitlines(), cand_json.splitlines(),
                                          "baseline", "candidate", lineterm="", n=2))
    return lines


# ---- self test ----------------------------------------------------------------------------------------------------

def self_test() -> int:
    """No exe needed: a changed JSON file and a missing marker must both be detected, identical inputs must not."""
    assert_command_lists()
    root = Path(tempfile.gettempdir()) / (make_goldens.SANDBOX_PREFIX + "selftest")
    stdout = "\n".join(text for _case, text in MAIN_MARKERS) + "\nline\n"
    doc = {"recordType": "routing", "files": [{"filename": "a.mp3", "destination": str(root / "Audio" / "a.mp3")}]}
    with tempfile.TemporaryDirectory(prefix="am_parity_selftest_") as tmp:
        same = Path(tmp) / "same.json"
        other = Path(tmp) / "other.json"
        same.write_text(json.dumps(doc), encoding="utf-8")
        changed = json.loads(json.dumps(doc))
        changed["files"][0]["filename"] = "b.mp3"
        other.write_text(json.dumps(changed), encoding="utf-8")

        def make(path: Path, text: str, exit_code: int = 0) -> dict:
            return normalised({"root": root, "stdout": text, "json_stdout": "", "exit_stdout": exit_code, "exit_json": 0,
                               "doc": json.loads(path.read_text(encoding="utf-8"))})

        base = make(same, stdout)
        ok = True
        if diff_parts(base, make(same, stdout)):
            print("self-test FAILED: identical inputs reported a difference")
            ok = False
        if not diff_parts(base, make(other, stdout)):
            print("self-test FAILED: a changed JSON file was not detected")
            ok = False
        else:
            print("self-test: DIFFERENT detected for a changed routing JSON")
        if not diff_parts(base, make(same, stdout + "extra line\n")):
            print("self-test FAILED: a changed console line was not detected")
            ok = False
        else:
            print("self-test: DIFFERENT detected for a changed console line")
        if not diff_parts(base, make(same, stdout, exit_code=1)):
            print("self-test FAILED: a changed exit code was not detected")
            ok = False
        else:
            print("self-test: DIFFERENT detected for a changed exit code")
        if not missing_markers(stdout.replace(MAIN_MARKERS[1][1], ""), MAIN_MARKERS):
            print("self-test FAILED: a missing marker was not detected")
            ok = False
        else:
            print("self-test: FIXTURE NOT EXERCISED detected for a missing marker")
        if missing_markers(stdout, MAIN_MARKERS):
            print("self-test FAILED: a complete output reported a missing marker")
            ok = False
    print("self-test: PASSED" if ok else "self-test: FAILED")
    return 0 if ok else 1


# ---- main ---------------------------------------------------------------------------------------------------------

def main(argv: list) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--baseline-exe", help="engine exe from the last green commit (a copy outside the repo)")
    parser.add_argument("--exe", help="candidate engine exe")
    parser.add_argument("--self-test", action="store_true", help="prove DIFFERENT and FIXTURE NOT EXERCISED are detected")
    parser.add_argument("--dump-dir", help="also write each raw baseline console output to this folder (for fixture tuning)")
    args = parser.parse_args(argv)
    assert_command_lists()
    if args.self_test:
        return self_test()
    if not args.baseline_exe or not args.exe:
        parser.error("--baseline-exe and --exe are required (or --self-test)")
    baseline_exe, candidate_exe = Path(args.baseline_exe), Path(args.exe)
    for exe in (baseline_exe, candidate_exe):
        if not exe.exists():
            raise SystemExit(f"integrate_parity: exe missing: {exe}. Nothing was run.")

    # Each exe reads config/ under the repo root it discovers by walking up from its own folder. A baseline copy needs a
    # config/ folder copied from the repo, and both must match, or a difference could be config and not code.
    base_cfg, cand_cfg = config_digest(repo_root_of(baseline_exe)), config_digest(repo_root_of(candidate_exe))
    if base_cfg != cand_cfg:
        raise SystemExit("integrate_parity: REFUSED, the config/ folder found for the baseline exe "
                         f"({repo_root_of(baseline_exe) / 'config'}) differs from the candidate's "
                         f"({repo_root_of(candidate_exe) / 'config'}). Copy the repo config/ folder next to the baseline. Nothing was run.")

    sandboxes_before = set(Path(tempfile.gettempdir()).glob(make_goldens.SANDBOX_PREFIX + "*"))
    watch = {make_goldens.REPO, repo_root_of(baseline_exe), repo_root_of(candidate_exe)}
    before = {str(w): data_snapshot_of(w) for w in watch}
    exit_code = 0
    try:
        for name, (_seed, markers) in SCENARIOS.items():
            print(f"scenario {name}: baseline run")
            base_raw = run_scenario(baseline_exe, name)
            print(f"scenario {name}: candidate run")
            cand_raw = run_scenario(candidate_exe, name)
            for command in base_raw["commands"]:
                print(f"  ran: {command}")
            if args.dump_dir:
                dump = Path(args.dump_dir)
                dump.mkdir(parents=True, exist_ok=True)
                (dump / f"baseline_{name}_stdout.txt").write_text(base_raw["stdout"], encoding="utf-8")
                (dump / f"baseline_{name}_json_stdout.txt").write_text(base_raw["json_stdout"], encoding="utf-8")
            missing = missing_markers(base_raw["stdout"], markers)
            if missing:
                print(f"FIXTURE NOT EXERCISED in scenario {name}: no marker in the baseline output for:")
                for case, text in missing:
                    print(f"  - {case}: {text!r}")
                exit_code = max(exit_code, 2)
                continue
            if base_raw["doc"] is None:
                print(f"FIXTURE NOT EXERCISED in scenario {name}: the baseline run wrote no routing-*.json")
                exit_code = max(exit_code, 2)
                continue
            print(f"  markers present in the baseline output: {', '.join(case for case, _ in markers)}")
            parts = diff_parts(normalised(base_raw), normalised(cand_raw))
            if parts:
                print(f"DIFFERENT in scenario {name}:")
                for line in parts[:200]:
                    print("  " + line)
                exit_code = max(exit_code, 1)
    finally:
        after = {str(w): data_snapshot_of(w) for w in watch}
        if after != before:
            print("integrate_parity: an exe wrote under a repo data folder; the sandbox redirect failed")
            exit_code = max(exit_code, 3)
    leftovers = sorted(set(Path(tempfile.gettempdir()).glob(make_goldens.SANDBOX_PREFIX + "*")) - sandboxes_before)
    if leftovers:
        print(f"integrate_parity: sandbox folders from this run were not removed: {[str(p) for p in leftovers]}")
        exit_code = max(exit_code, 3)
    else:
        print("integrate_parity: all sandbox folders from this run were removed")
    if exit_code == 0:
        print("IDENTICAL")
    elif exit_code == 1:
        print("DIFFERENT")
    return exit_code


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
