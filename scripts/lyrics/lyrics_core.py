"""Pure lyrics logic: normalise, compare two texts, duration gate, audio match.

No network, no file writes. Thresholds live in the constants block below and are
calibrated against hand-labelled tracks (see docs/Architecture/Lyrics.md once written).
"""
import re

from rapidfuzz import fuzz

DURATION_TOL_S = 2.0
AGREE_VERIFIED_MIN = 90
LENGTH_RATIO_MIN = 0.7
AUDIO_LINE_MATCH_MIN = 80  # per-line partial ratio to count a line as heard

_LRC_STAMP = re.compile(r"\[\d{1,3}:\d{2}(?:[.:]\d{1,3})?\]")
_SECTION_TAG = re.compile(r"\[[^\]]*\]")
_NON_WORD = re.compile(r"[^\w\s]", re.UNICODE)


def normalise(text):
    if not text:
        return ""
    text = _LRC_STAMP.sub(" ", text)
    text = _SECTION_TAG.sub(" ", text)
    text = _NON_WORD.sub("", text.lower())
    return " ".join(text.split())


def agreement(a, b):
    na, nb = normalise(a), normalise(b)
    if not na or not nb:
        return 0
    return min(fuzz.token_set_ratio(na, nb), fuzz.token_sort_ratio(na, nb))


def length_ratio(a, b):
    la, lb = len(normalise(a)), len(normalise(b))
    if not la or not lb:
        return 0.0
    return min(la, lb) / max(la, lb)


def compare(a, b):
    score = agreement(a, b)
    ratio = length_ratio(a, b)
    ok = score >= AGREE_VERIFIED_MIN and ratio >= LENGTH_RATIO_MIN
    return {
        "verdict": "verified" if ok else "disputed",
        "agreement": score,
        "length_ratio": round(ratio, 3),
    }


def duration_ok(file_s, candidate_s):
    if file_s is None or candidate_s is None:
        return False
    return abs(file_s - candidate_s) <= DURATION_TOL_S


def audio_match(lyric_text, transcript):
    """Fraction of non-empty lyric lines found in the Whisper transcript."""
    nt = normalise(transcript)
    lines = [normalise(ln) for ln in (lyric_text or "").splitlines()]
    lines = [ln for ln in lines if ln]
    if not lines or not nt:
        return 0.0
    hit = sum(1 for ln in lines if fuzz.partial_ratio(ln, nt) >= AUDIO_LINE_MATCH_MIN)
    return hit / len(lines)
