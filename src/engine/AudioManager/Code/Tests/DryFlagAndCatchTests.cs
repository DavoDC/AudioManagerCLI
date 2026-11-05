using System;

namespace AudioManager
{
    /// <summary>
    /// Covers the analysis-mode --dry-run rejection (Program.cs). Analysis has no dry path, so
    /// --dry-run there would regen the mirror and auto-commit; the helper makes that rule testable.
    /// </summary>
    internal static class DryFlagAndCatchTests
    {
        public static void AnalysisRejectsDryRun_AnalysisWithDryRun_IsRejected()
        {
            Assert.True(Program.AnalysisRejectsDryRun(1, true),
                "analysis mode (1) with --dry-run must be rejected, it would otherwise regen and auto-commit");
        }

        public static void AnalysisRejectsDryRun_AnalysisWithoutDryRun_IsAccepted()
        {
            Assert.True(!Program.AnalysisRejectsDryRun(1, false),
                "analysis mode without --dry-run must still run");
        }

        public static void AnalysisRejectsDryRun_IntegrateAndTagfixWithDryRun_AreAccepted()
        {
            Assert.True(!Program.AnalysisRejectsDryRun(2, true), "integrate --dry-run must not be affected");
            Assert.True(!Program.AnalysisRejectsDryRun(3, true), "tagfix --dry-run must not be affected");
        }
    }
}
