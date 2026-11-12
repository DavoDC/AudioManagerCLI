"""Verbatim move checker (design step A2).

Usage:
    python scripts/dev/verbatim_moves.py --base <git rev> Member1 Member2 ...

For each member name this extracts the member text (signature line through the
matching closing brace, or the terminating semicolon for fields and
expression bodies) from two places:
  - the old MusicIntegrator.cs, read with git show <rev>:<path>
  - the working tree: MusicIntegrator.cs plus every .cs file under
    src/AudioManager/Code/ that did not exist at the base revision (a moved
    member lands in a new file; pre-existing copies in other classes are ignored)
A member body of at most 3 non-blank lines whose single statement is a call or
return counts as a forwarder. A forwarder in MusicIntegrator.cs yields to a moved copy.
Leading whitespace is stripped from every line, then a unified diff and a
verdict are printed per member: SAME, CHANGED, MISSING or AMBIGUOUS.
Overloads are compared in order and labelled Name#1, Name#2.
Exit 0 only when every verdict is SAME.

Limitations: there is no C# parser. Declarations are found by line regex, so a
local variable with a type and the same name as a member can be taken for a
declaration. Braces, parens and semicolons are counted only at code level:
string, verbatim, interpolated, raw and char literals and comments are skipped.
"""
import argparse
import bisect
import difflib
import re
import subprocess
import sys
from dataclasses import dataclass
from pathlib import Path

MI_REL = "src/AudioManager/Code/Doer/MusicIntegrator.cs"
CODE_REL = "src/AudioManager/Code"
REPO_ROOT = Path(__file__).resolve().parents[2]

MODIFIERS = (
    "public|private|protected|internal|static|async|override|virtual|abstract|"
    "sealed|readonly|extern|unsafe|new|partial|const|volatile"
)
MODS = rf"(?:(?:{MODIFIERS})[ \t]+)*"
TYPE = r"(?:[\w.\[\]?]+(?:<[^=;{}\"\n]*>)?[ \t]+)"
# First words that start a statement, so a call or assignment is never a declaration.
STATEMENT_WORDS = {
    "return", "await", "throw", "else", "case", "yield", "goto", "new", "using",
    "lock", "var", "if", "while", "for", "foreach", "switch", "do", "in", "is",
    "as", "break", "continue",
}


@dataclass
class Member:
    line: int
    text: str


@dataclass
class Verdict:
    label: str
    verdict: str
    diff: list
    note: str = ""


# ---------- code-level scanner: skips strings, chars and comments ----------

def _skip_char(text, i):
    j = i + 1
    while j < len(text) and text[j] != "'":
        if text[j] == "\\":
            j += 2
            continue
        if text[j] == "\n":
            return j
        j += 1
    return j + 1


def _skip_string(text, start, q):
    """start is the first prefix char ($ or @, or the quote); q is the opening quote index."""
    prefix = text[start:q]
    verbatim = "@" in prefix
    interp = "$" in prefix
    n = len(text)
    if text.startswith('"""', q):
        close = text.find('"""', q + 3)
        return n if close < 0 else close + 3
    i = q + 1
    while i < n:
        c = text[i]
        if interp and c == "{":
            if text.startswith("{{", i):
                i += 2
                continue
            i = _scan_code(text, i + 1, [], True)
            continue
        if verbatim:
            if c == '"':
                if text.startswith('""', i):
                    i += 2
                    continue
                return i + 1
        else:
            if c == "\\":
                i += 2
                continue
            if c == '"':
                return i + 1
            if c == "\n":
                return i
        i += 1
    return n


def _scan_code(text, i, toks, in_hole):
    """Scan code from i. At top level record code-level ( ) { } ; in toks.
    Inside an interpolation hole, return the index just after the hole's closing brace."""
    n = len(text)
    depth = 0
    while i < n:
        c = text[i]
        nxt = text[i + 1] if i + 1 < n else ""
        if c == "/" and nxt == "/":
            end = text.find("\n", i)
            i = n if end < 0 else end
            continue
        if c == "/" and nxt == "*":
            end = text.find("*/", i + 2)
            i = n if end < 0 else end + 2
            continue
        if c == "'":
            i = _skip_char(text, i)
            continue
        if c in "$@\"":
            j = i
            while j < n and text[j] in "$@":
                j += 1
            if j < n and text[j] == '"':
                i = _skip_string(text, i, j)
                continue
        if in_hole:
            if c == "{":
                depth += 1
            elif c == "}":
                if depth == 0:
                    return i + 1
                depth -= 1
        elif c in "(){};":
            toks.append((i, c))
        i += 1
    return n


def code_tokens(text):
    toks = []
    _scan_code(text, 0, toks, False)
    return toks


# ---------- declaration finding and member extraction ----------

def _declaration_starts(text, name):
    """Offsets of line starts where `name` is declared as a method, constructor, field or property."""
    esc = re.escape(name)
    method = re.compile(
        rf"^[ \t]*(?P<head>{MODS}(?:{TYPE})?)" + esc + r"[ \t]*(?:<[^=;{}()\n]*>)?[ \t]*\(",
        re.MULTILINE)
    field = re.compile(
        rf"^[ \t]*(?P<head>{MODS}{TYPE})" + esc + r"[ \t]*(?=[=;{])",
        re.MULTILINE)
    starts = set()
    for pattern in (method, field):
        for m in pattern.finditer(text):
            words = m.group("head").split()
            if not words or words[0] in STATEMENT_WORDS:
                continue
            starts.add(m.start())
    return sorted(starts)


def _matching_brace(toks, k):
    depth = 0
    for j in range(k, len(toks)):
        ch = toks[j][1]
        if ch == "{":
            depth += 1
        elif ch == "}":
            depth -= 1
            if depth == 0:
                return j
    return None


def _member_end(text, toks, tok_pos, start):
    k = bisect.bisect_left(tok_pos, start)
    paren = 0
    while k < len(toks):
        pos, ch = toks[k]
        if ch == "(":
            paren += 1
        elif ch == ")":
            paren -= 1
        elif paren == 0 and ch == ";":
            return pos + 1
        elif paren == 0 and ch == "{":
            close = _matching_brace(toks, k)
            if close is None:
                return None
            end = toks[close][0] + 1
            tail = re.match(r"[ \t]*;", text[end:])
            if tail:
                end += tail.end()
            return end
        k += 1
    return None


def extract_members(source, name):
    toks = code_tokens(source)
    tok_pos = [p for p, _ in toks]
    members = []
    for start in _declaration_starts(source, name):
        end = _member_end(source, toks, tok_pos, start)
        if end is None:
            continue
        members.append(Member(line=source.count("\n", 0, start) + 1, text=source[start:end]))
    return members


def normalised(member_text):
    return [line.lstrip() for line in member_text.splitlines()]


def compare_pair(label, old, new):
    """Return (verdict, diff_lines) for two Member objects (or None)."""
    if old is None or new is None:
        return "MISSING", []
    a = normalised(old.text)
    b = normalised(new.text)
    diff = list(difflib.unified_diff(a, b, fromfile=f"old/{label}", tofile=f"new/{label}", lineterm=""))
    return ("SAME" if a == b else "CHANGED"), diff


def _is_forwarder(member):
    """A body of at most 3 non-blank lines whose single statement is a call or a return."""
    lines = [line for line in normalised(member.text) if line.strip()]
    if len(lines) > 3:
        return False
    text = " ".join(lines)
    if "=>" in text:
        body = text.split("=>", 1)[1]
    elif "{" in text:
        body = text.split("{", 1)[1]
    else:
        return False
    body = body.strip()
    if body.endswith("}"):
        body = body[:-1].strip()
    if body.count(";") != 1 or not body.endswith(";"):
        return False
    return body.startswith("return ") or re.match(r"[\w.<>]+\s*\(", body) is not None


def compare_name(name, old_src, current, mi_rel=MI_REL, existed_at_base=lambda rel: False):
    """Compare every declaration of `name` in the old MI source with the working tree.

    current maps repo-relative path to source text for every .cs file under the code tree.
    Only mi_rel and files for which existed_at_base(rel) is False are candidates: a moved
    member always lands in a new file, so other pre-existing classes' copies are ignored.
    """
    old_decls = extract_members(old_src, name)
    cur = []
    for rel in sorted(current):
        if rel != mi_rel and existed_at_base(rel):
            continue
        for member in extract_members(current[rel], name):
            cur.append((rel, member))
    mi_cur = [item for item in cur if item[0] == mi_rel]
    other = [item for item in cur if item[0] != mi_rel]
    if other and all(_is_forwarder(m) for _, m in mi_cur):
        chosen = other
    else:
        chosen = cur
    files = sorted({rel for rel, _ in chosen})
    if len(files) > 1:
        return [Verdict(name, "AMBIGUOUS", [], f"found in several files: {', '.join(files)}")]
    if not old_decls and not chosen:
        return [Verdict(name, "MISSING", [], "not found in the old file or the working tree")]

    count = max(len(old_decls), len(chosen))
    results = []
    for i in range(count):
        label = f"{name}#{i + 1}" if count > 1 else name
        old = old_decls[i] if i < len(old_decls) else None
        new = chosen[i][1] if i < len(chosen) else None
        if old is None:
            results.append(Verdict(label, "MISSING", [], "no such declaration in the old file"))
            continue
        if new is None:
            results.append(Verdict(label, "MISSING", [], "no such declaration in the working tree"))
            continue
        verdict, diff = compare_pair(label, old, new)
        note = f"working tree: {chosen[i][0]}:{new.line}"
        results.append(Verdict(label, verdict, diff, note))
    return results


# ---------- command line ----------

def _git_show(rev, rel):
    proc = subprocess.run(["git", "show", f"{rev}:{rel}"], cwd=REPO_ROOT, capture_output=True)
    if proc.returncode != 0:
        raise RuntimeError(proc.stderr.decode("utf-8", "replace").strip() or f"git show {rev}:{rel} failed")
    return proc.stdout.decode("utf-8-sig", "replace")


def _files_at_base(rev):
    proc = subprocess.run(["git", "ls-tree", "-r", "--name-only", rev, "--", CODE_REL],
                          cwd=REPO_ROOT, capture_output=True)
    if proc.returncode != 0:
        raise RuntimeError(proc.stderr.decode("utf-8", "replace").strip() or f"git ls-tree {rev} failed")
    return set(proc.stdout.decode("utf-8", "replace").split())


def _working_tree_sources():
    root = REPO_ROOT / CODE_REL
    sources = {}
    for path in sorted(root.rglob("*.cs")):
        rel = path.relative_to(REPO_ROOT).as_posix()
        sources[rel] = path.read_bytes().decode("utf-8-sig", "replace")
    return sources


def main(argv=None):
    parser = argparse.ArgumentParser(description="Check that moved members are verbatim copies.")
    parser.add_argument("--base", required=True, help="git revision holding the old MusicIntegrator.cs")
    parser.add_argument("names", nargs="+", help="member names to compare")
    args = parser.parse_args(argv)

    try:
        old_src = _git_show(args.base, MI_REL)
        at_base = _files_at_base(args.base)
    except RuntimeError as err:
        print(f"error: {err}", file=sys.stderr)
        return 1
    current = _working_tree_sources()

    verdicts = []
    for name in args.names:
        verdicts.extend(compare_name(name, old_src, current, existed_at_base=at_base.__contains__))

    for v in verdicts:
        if v.diff or v.note:
            print(f"== {v.label}" + (f" ({v.note})" if v.note else ""))
            for line in v.diff:
                print(line)
    print()
    for v in verdicts:
        print(f"{v.verdict} {v.label}")
    return 0 if verdicts and all(v.verdict == "SAME" for v in verdicts) else 1


if __name__ == "__main__":
    sys.exit(main())
