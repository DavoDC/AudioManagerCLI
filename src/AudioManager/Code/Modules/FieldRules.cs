using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace AudioManager.Code.Modules
{
    /// <summary>How a caller treats a field problem. Block refuses the write or commit; Warn is reported only.</summary>
    internal enum FieldSeverity { Block, Warn }

    /// <summary>One field problem: its severity and a short reason that reads after the field name, e.g. "Title is missing or blank".</summary>
    internal sealed class FieldProblem
    {
        public FieldSeverity Severity;
        public string Reason;
    }

    /// <summary>
    /// The one per-field validator for track metadata (BL-319). Field names are the TrackXML element names: Title, Artists,
    /// Album, Year, Genres, TrackNumber, Length, CoverWidth, CoverHeight. Year is delegated to YearRule (BL-316), never
    /// duplicated. Scope is the caller's choice: MirrorGate.CheckXml runs on changed or untracked XML only, so the Genres
    /// rule never refuses the existing library. An unknown field name throws, so a typo cannot pass silently.
    /// </summary>
    internal static class FieldRules
    {
        private static readonly Regex LengthShape = new Regex(@"^([0-9]{2,6}):([0-5][0-9]):([0-5][0-9](?:\.[0-9]+)?)$", RegexOptions.CultureInvariant);
        private const double MinLengthSeconds = 20;
        private const double WarnLengthSeconds = 30 * 60;
        private const long WarnTrackNumber = 99;

        /// <summary>Null when the value is acceptable, otherwise the problem with its severity. Throws for an unknown field.</summary>
        internal static FieldProblem Check(string field, string value)
        {
            switch (field)
            {
                case "Title":
                case "Artists":
                case "Album":
                    return TextProblem(value);
                case "Genres":
                    return IsBlankOrMissing(value) ? Block("is missing or blank") : null;
                case "Year":
                    if (value == Constants.MissingTag || YearRule.IsValid(value ?? string.Empty)) return null;
                    return Block("'" + (value ?? string.Empty) + "' is not a valid year (1900 to next year)");
                case "TrackNumber":
                    return TrackNumberProblem(value);
                case "Length":
                    return LengthProblem(value);
                case "CoverWidth":
                case "CoverHeight":
                    return IsDigits(value) ? null : Block("is not a pixel size");
                default:
                    throw new ArgumentException("No FieldRules rule for field: " + field, nameof(field));
            }
        }

        /// <summary>Null when the value is acceptable, otherwise the short reason (any severity).</summary>
        internal static string Problem(string field, string value)
        {
            FieldProblem p = Check(field, value);
            return p == null ? null : p.Reason;
        }

        /// <summary>
        /// Cover shape (WARN only): a nonsquare cover. Sizes that are not digits are left to the CoverWidth and CoverHeight
        /// block rule, so this returns null for them.
        /// </summary>
        internal static FieldProblem CheckCoverShape(string width, string height)
        {
            long w, h;
            if (!IsDigits(width) || !IsDigits(height)) return null;
            if (!long.TryParse(width, NumberStyles.None, CultureInfo.InvariantCulture, out w)) return null;
            if (!long.TryParse(height, NumberStyles.None, CultureInfo.InvariantCulture, out h)) return null;
            if (w == h) return null;
            return Warn("cover is not square (" + width + "x" + height + ")");
        }

        private static FieldProblem TextProblem(string value)
        {
            if (IsBlankOrMissing(value)) return Block("is missing or blank");
            if (char.IsWhiteSpace(value[0]) || char.IsWhiteSpace(value[value.Length - 1]))
                return Block("has leading or trailing whitespace");
            if (value.IndexOf("  ", StringComparison.Ordinal) >= 0) return Block("has a double space");
            foreach (char c in value)
            {
                if (c <= '\u001f') return Block("has a control character");
                if (c == '�') return Block("has a replacement character (U+FFFD)");
            }
            return null;
        }

        private static FieldProblem TrackNumberProblem(string value)
        {
            if (!IsDigits(value)) return Block("is not a track number (digits only)");
            long n;
            if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out n) || n > WarnTrackNumber)
                return Warn("is over " + WarnTrackNumber.ToString(CultureInfo.InvariantCulture));
            if (n == 0) return Warn("is 0 (no track number)");
            return null;
        }

        private static FieldProblem LengthProblem(string value)
        {
            Match m = LengthShape.Match(value ?? string.Empty);
            if (!m.Success) return Block("is not hh:mm:ss");
            double seconds = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) * 3600
                + double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) * 60
                + double.Parse(m.Groups[3].Value, NumberStyles.Float, CultureInfo.InvariantCulture);
            if (seconds < MinLengthSeconds) return Block("is under 20 seconds");
            if (seconds > WarnLengthSeconds) return Warn("is over 30 minutes");
            return null;
        }

        private static bool IsBlankOrMissing(string value)
        {
            return string.IsNullOrWhiteSpace(value) || value == Constants.MissingTag;
        }

        // ASCII digits only; an empty string is not digits.
        private static bool IsDigits(string value)
        {
            if (string.IsNullOrEmpty(value)) return false;
            foreach (char c in value)
            {
                if (c < '0' || c > '9') return false;
            }
            return true;
        }

        private static FieldProblem Block(string reason)
        {
            return new FieldProblem { Severity = FieldSeverity.Block, Reason = reason };
        }

        private static FieldProblem Warn(string reason)
        {
            return new FieldProblem { Severity = FieldSeverity.Warn, Reason = reason };
        }
    }
}
