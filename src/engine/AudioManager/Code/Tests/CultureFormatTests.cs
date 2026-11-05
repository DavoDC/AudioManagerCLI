using AudioManager.Code.Modules;
using System;
using System.Globalization;
using System.IO;
using System.Threading;

namespace AudioManager
{
    /// <summary>
    /// Culture-invariance tests for the formatting calls fixed to InvariantCulture: TimeSpan seconds and
    /// Statistic percentages. The thread culture is switched to de-DE (decimal comma) and the output must
    /// match the invariant result byte for byte.
    /// </summary>
    internal static class CultureFormatTests
    {
        // Runs an action with the thread culture set to de-DE, restoring the previous culture in finally.
        private static T UnderGerman<T>(Func<T> action)
        {
            CultureInfo previous = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                return action();
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = previous;
            }
        }

        // Captures console output for an action, restoring Console.Out in finally.
        private static string Capture(Action action)
        {
            TextWriter original = Console.Out;
            var capture = new StringWriter();
            try
            {
                Console.SetOut(capture);
                action();
            }
            finally
            {
                Console.SetOut(original);
            }
            return capture.ToString();
        }

        public static void GermanCulture_IsEffective_ForDecimalFormatting()
        {
            // Precondition: the de-DE switch really changes double formatting, so the equality tests below mean something.
            Assert.Equal("1,5", UnderGerman(() => ((double)1.5).ToString()), "de-DE must format 1.5 with a decimal comma");
        }

        public static void UnderGerman_RestoresPreviousCulture()
        {
            CultureInfo before = Thread.CurrentThread.CurrentCulture;
            UnderGerman(() => 0);
            Assert.True(ReferenceEquals(before, Thread.CurrentThread.CurrentCulture), "culture must be restored");
        }

        public static void ConvertTimeSpanToString_IsSameUnderGermanCulture()
        {
            TimeSpan span = TimeSpan.FromSeconds(1.23456);
            string invariant = Doer.ConvertTimeSpanToString(span);
            string german = UnderGerman(() => Doer.ConvertTimeSpanToString(span));
            Assert.Equal(invariant, german, "ConvertTimeSpanToString must not depend on the thread culture");
            Assert.Equal("1.235 seconds", german, "1.23456 s rounds to 1.235 with a decimal point under de-DE");
        }

        public static void Statistic_Print_IsSameUnderGermanCulture()
        {
            // 1 of 3 items is 33.33%, which must keep its decimal point under de-DE.
            string invariant = Capture(() => new Statistic("Rock", 1, 3).Print(1, 10.0));
            string german = Capture(() => UnderGerman(() => { new Statistic("Rock", 1, 3).Print(1, 10.0); return 0; }));
            Assert.Equal(invariant, german, "Statistic.Print must not depend on the thread culture");
            Assert.Equal("| 1 | 33.33% | Rock | 1 |" + Environment.NewLine, german, "percentage must use a decimal point under de-DE");
        }
    }
}
