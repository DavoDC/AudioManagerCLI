"""Tests for verbatim_moves.py (design step A2).

Run: python -m pytest scripts/dev/test_verbatim_moves.py -q
"""
import textwrap

import verbatim_moves as vm

MI = vm.MI_REL
MOVED = "src/AudioManager/Code/Doer/Moved.cs"

HOLDER = textwrap.dedent("""\
    internal class Holder
    {
        internal int Total(int a, int b)
        {
            int sum = a + b;
            return sum;
        }
    }
    """)

HOLDER_CHANGED_LINE = HOLDER.replace("int sum = a + b;", "int sum = a - b;")

HOLDER_REINDENTED = textwrap.indent(HOLDER, "            ")

BRACE_IN_STRING = textwrap.dedent("""\
    internal class Holder
    {
        internal string Open()
        {
            var s = "{";
            return s + "}";
        }

        internal int Next() { return 1; }
    }
    """)

BRACE_IN_OTHER_LITERALS = textwrap.dedent("""\
    internal class Holder
    {
        internal string Forms(int x)
        {
            var a = $"{{ {x} }}";
            var b = @"{";
            char c = '{';
            // a stray { in a comment
            /* and a } in a block comment */
            return a + b + c;
        }

        internal int Next() { return 1; }
    }
    """)

FIELD_AND_PROPERTY = textwrap.dedent("""\
    internal class Holder
    {
        private Dictionary<string, List<string>> _pending = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        public bool Blocked { get; private set; }
        private static readonly string Sep = ";";
    }
    """)

CALL_SITES = textwrap.dedent("""\
    internal class Holder
    {
        internal int Total(int a, int b)
        {
            return Total(a, b);
        }

        internal int Caller()
        {
            var x = Total(1, 2);
            return x;
        }
    }
    """)

TWO_OVERLOADS = textwrap.dedent("""\
    internal class Holder
    {
        internal int Total(int a) { return a; }
        internal int Total(int a, int b) { return a + b; }
    }
    """)


def _one(src, name):
    members = vm.extract_members(src, name)
    assert len(members) == 1, members
    return members[0]


def test_identical_body_is_same():
    verdict, diff = vm.compare_pair("Total", _one(HOLDER, "Total"), _one(HOLDER, "Total"))
    assert verdict == "SAME"
    assert diff == []


def test_one_changed_line_is_changed():
    verdict, diff = vm.compare_pair(
        "Total", _one(HOLDER, "Total"), _one(HOLDER_CHANGED_LINE, "Total"))
    assert verdict == "CHANGED"
    assert "-int sum = a + b;" in diff
    assert "+int sum = a - b;" in diff


def test_reindented_only_is_same():
    verdict, diff = vm.compare_pair(
        "Total", _one(HOLDER, "Total"), _one(HOLDER_REINDENTED, "Total"))
    assert verdict == "SAME"
    assert diff == []


def test_brace_inside_string_literal_does_not_end_member():
    member = _one(BRACE_IN_STRING, "Open")
    assert vm.normalised(member.text) == [
        "internal string Open()",
        "{",
        'var s = "{";',
        'return s + "}";',
        "}",
    ]


def test_brace_inside_interpolated_verbatim_char_and_comments():
    member = _one(BRACE_IN_OTHER_LITERALS, "Forms")
    assert vm.normalised(member.text) == [
        "internal string Forms(int x)",
        "{",
        'var a = $"{{ {x} }}";',
        'var b = @"{";',
        "char c = '{';",
        "// a stray { in a comment",
        "/* and a } in a block comment */",
        "return a + b + c;",
        "}",
    ]


def test_field_and_property_extraction():
    pending = _one(FIELD_AND_PROPERTY, "_pending")
    assert len(vm.normalised(pending.text)) == 1
    blocked = _one(FIELD_AND_PROPERTY, "Blocked")
    assert vm.normalised(blocked.text) == ["public bool Blocked { get; private set; }"]


def test_call_sites_are_not_declarations():
    members = vm.extract_members(CALL_SITES, "Total")
    assert len(members) == 1
    assert vm.normalised(members[0].text)[0] == "internal int Total(int a, int b)"


def test_absent_member_is_missing():
    verdicts = vm.compare_name("Nope", HOLDER, {MI: HOLDER})
    assert [(v.label, v.verdict) for v in verdicts] == [("Nope", "MISSING")]


def test_overloads_are_numbered_and_compared_in_order():
    verdicts = vm.compare_name("Total", TWO_OVERLOADS, {MI: TWO_OVERLOADS})
    assert [(v.label, v.verdict) for v in verdicts] == [
        ("Total#1", "SAME"),
        ("Total#2", "SAME"),
    ]


def test_forwarder_in_mi_prefers_the_moved_copy():
    forwarder = textwrap.dedent("""\
        internal class Holder
        {
            internal int Total(int a, int b) => Moved.Total(a, b);
        }
        """)
    moved = textwrap.dedent("""\
        internal static class Moved
        {
            internal int Total(int a, int b)
            {
                int sum = a + b;
                return sum;
            }
        }
        """)
    verdicts = vm.compare_name("Total", HOLDER, {MI: forwarder, MOVED: moved})
    assert [(v.label, v.verdict) for v in verdicts] == [("Total", "SAME")]


def test_two_full_copies_are_ambiguous():
    verdicts = vm.compare_name("Total", HOLDER, {MI: HOLDER, MOVED: HOLDER})
    assert [v.verdict for v in verdicts] == ["AMBIGUOUS"]


def test_same_named_member_in_file_existing_at_base_is_ignored():
    # Same-named copy that already existed at base (another class's private copy): not a move target.
    unrelated = textwrap.dedent("""\
        internal static class Other
        {
            internal int Total(int a, int b)
            {
                return 0;
            }
        }
        """)
    current = {MI: HOLDER, MOVED: unrelated}
    # Without the filter both copies are candidates and the name is AMBIGUOUS.
    assert [v.verdict for v in vm.compare_name("Total", HOLDER, current)] == ["AMBIGUOUS"]
    # With the filter the existing file is ignored and the MI copy compares SAME.
    verdicts = vm.compare_name("Total", HOLDER, current, existed_at_base=lambda rel: rel == MOVED)
    assert [(v.label, v.verdict) for v in verdicts] == [("Total", "SAME")]


def test_two_line_forwarder_is_a_forwarder():
    wrapped = textwrap.dedent("""\
        internal class Holder
        {
            internal int Total(int a, int b)
                => Moved.Total(a, b);
        }
        """)
    moved = textwrap.dedent("""\
        internal static class Moved
        {
            internal int Total(int a, int b)
            {
                int sum = a + b;
                return sum;
            }
        }
        """)
    verdicts = vm.compare_name("Total", HOLDER, {MI: wrapped, MOVED: moved})
    assert [(v.label, v.verdict) for v in verdicts] == [("Total", "SAME")]
    assert vm._is_forwarder(_one(wrapped, "Total"))
    assert not vm._is_forwarder(_one(HOLDER, "Total"))
