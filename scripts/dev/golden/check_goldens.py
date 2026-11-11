"""Drift check for the golden contract fixtures (the second half of the contract rule).

Runs the real engine on a fresh sandbox (make_goldens.build_all, nothing written to tests/golden), then compares the
KEY SETS of each fresh capture with the checked-in tests/golden/<name>.json: a renamed, added or dropped field fails.
Array rows are merged (files[].sourceRel). Values are not compared here; the consumer tests pin identity values.

Usage:  python scripts\\dev\\golden\\check_goldens.py [--exe PATH]
Exit 0 when the key sets match; exit 1 on drift, a missing golden or a golden with no fresh capture.
"""
import argparse
import json
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
import make_goldens  # noqa: E402  (same folder; shares the producer flow)


def key_paths(node, prefix: str = "") -> set:
    out = set()
    if isinstance(node, dict):
        for key, value in node.items():
            path = f"{prefix}.{key}" if prefix else key
            out.add(path)
            out |= key_paths(value, path)
    elif isinstance(node, list):
        for item in node:
            out |= key_paths(item, prefix + "[]")
    return out


def main(argv: list) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--exe", default=str(make_goldens.DEFAULT_EXE), help="engine exe (default: Release build)")
    parser.add_argument("--golden", default=str(make_goldens.DEFAULT_OUT), help="checked-in goldens folder")
    args = parser.parse_args(argv)
    golden_dir = Path(args.golden)

    fresh = make_goldens.build_all(Path(args.exe))
    fresh_names = {k for k in fresh if not k.endswith("#command")}
    checked_in = {p.stem for p in golden_dir.glob("*.json")}
    drift = False

    for name in sorted(fresh_names | checked_in):
        if name not in checked_in:
            print(f"[DRIFT] {name}.json: produced by the engine but not checked in (run make_goldens.py)")
            drift = True
            continue
        if name not in fresh_names:
            print(f"[DRIFT] {name}.json: checked in but the producer no longer captures it")
            drift = True
            continue
        golden = json.loads((golden_dir / f"{name}.json").read_text(encoding="utf-8"))
        now = key_paths(fresh[name])
        was = key_paths(golden)
        added, removed = sorted(now - was), sorted(was - now)
        if added or removed:
            drift = True
            print(f"[DRIFT] {name}.json")
            for path in added:
                print(f"    + {path}")
            for path in removed:
                print(f"    - {path}")

    if drift:
        print("check_goldens: FAILED (key sets differ from the real engine output)")
        return 1
    print(f"check_goldens: OK ({len(fresh_names)} goldens, key sets match a fresh real run)")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
