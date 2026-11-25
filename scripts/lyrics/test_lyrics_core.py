"""Offline tests for lyrics_core (run: python -m pytest scripts/lyrics)."""
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))

import lyrics_core as lc


def test_normalise_strips_tags_punct_case():
    assert lc.normalise("[Chorus]\nHello, WORLD!\n\n(Yeah)") == "hello world yeah"


def test_normalise_handles_none_and_lrc_stamps():
    assert lc.normalise(None) == ""
    assert lc.normalise("[00:12.34] Hello there") == "hello there"


def test_agreement_identical_is_100():
    assert lc.agreement("Hello world today", "hello, world today!") == 100


def test_agreement_catches_repeat_trap():
    short = "love generation " * 3 + "we are the night"
    long = ("love generation " * 12 + "we are the night ") * 3
    verdict = lc.compare(short, long)
    assert verdict["verdict"] == "disputed"
    assert verdict["length_ratio"] < lc.LENGTH_RATIO_MIN


def test_compare_verified_when_close():
    a = "one two three four five six seven eight nine ten"
    b = "One two three four five six seven eight nine ten."
    assert lc.compare(a, b)["verdict"] == "verified"


def test_compare_disputed_when_different():
    a = "one two three four five six seven eight nine ten"
    b = "alpha beta gamma delta epsilon zeta eta theta iota kappa"
    assert lc.compare(a, b)["verdict"] == "disputed"


def test_duration_gate():
    assert lc.duration_ok(200.0, 201.5)
    assert not lc.duration_ok(200.0, 204.0)
    assert not lc.duration_ok(200.0, None)


def test_audio_match_fraction():
    lyric = "line one here\nline two there\nline three again\nline four more"
    transcript = "line one here line two there something else entirely line four more"
    frac = lc.audio_match(lyric, transcript)
    assert 0.74 <= frac <= 0.76  # 3 of 4 lines matched
