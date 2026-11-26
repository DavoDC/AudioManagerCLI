using System;
using System.Globalization;

namespace AudioManager.Code.Modules
{
    /// <summary>
    /// The one plausibility rule for a Year tag (BL-316). A year is valid only when it is exactly four digits
    /// and falls between 1900 and next calendar year. Empty, "Missing" and 0 are not valid years; callers
    /// handle them as Missing (see ToStoredYear).
    /// </summary>
    internal static class YearRule
    {
        /// <summary>True only for exactly four ASCII digits whose value is in 1900..(this year + 1).</summary>
        internal static bool IsValid(string year)
        {
            if (string.IsNullOrEmpty(year) || year.Length != 4) return false;
            foreach (char c in year)
            {
                if (c < '0' || c > '9') return false;
            }
            return IsValid(int.Parse(year, CultureInfo.InvariantCulture));
        }

        /// <summary>True only for a year in 1900..(this year + 1).</summary>
        internal static bool IsValid(int year)
        {
            return year >= 1900 && year <= DateTime.Now.Year + 1;
        }

        /// <summary>
        /// The text to store for a raw tag year. 0 is Missing. A non-zero implausible year is printed as a
        /// [WARN] line naming the file and becomes Missing, so it never reaches the cache or the XML.
        /// </summary>
        internal static string ToStoredYear(uint raw, string relPath)
        {
            if (raw == 0) return Constants.MissingTag;
            if (IsValid((int)raw)) return raw.ToString(CultureInfo.InvariantCulture);
            Console.WriteLine("[WARN] invalid year " + raw.ToString(CultureInfo.InvariantCulture) + " in " + relPath);
            return Constants.MissingTag;
        }

        /// <summary>Keeps a stored year text when it is plausible, otherwise returns Missing.</summary>
        internal static string OrMissing(string year)
        {
            return IsValid(year) ? year : Constants.MissingTag;
        }
    }
}
