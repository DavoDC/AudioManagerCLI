using System;
using System.Globalization;

namespace AudioManager
{
    /// <summary>
    /// Pins JsonText.Quote, the one shared JSON string escaper, to the escape spec for every UTF-16 code unit.
    /// Every hand-built JSON writer routes through it, so a change here changes every record at once.
    /// </summary>
    internal static class JsonTextQuoteParityTests
    {
        public static void JsonTextQuote_EscapesEveryCharLikeTheSpec()
        {
            Assert.Equal("null", JsonText.Quote(null), "null gives the literal null");

            for (int i = 0; i <= 0xFFFF; i++)
            {
                char c = (char)i;
                string body;
                switch (c)
                {
                    case '"': body = "\\\""; break;
                    case '\\': body = "\\\\"; break;
                    case '\b': body = "\\b"; break;
                    case '\f': body = "\\f"; break;
                    case '\n': body = "\\n"; break;
                    case '\r': body = "\\r"; break;
                    case '\t': body = "\\t"; break;
                    default:
                        body = i < 0x20
                            ? "\\u" + i.ToString("x4", CultureInfo.InvariantCulture)
                            : c.ToString();
                        break;
                }
                Assert.Equal("\"" + body + "\"", JsonText.Quote(c.ToString()), "single char U+" + i.ToString("x4", CultureInfo.InvariantCulture));
            }

            string pair = "🎵";
            Assert.Equal("\"" + pair + "\"", JsonText.Quote(pair), "surrogate pair is copied unchanged");
        }
    }
}
