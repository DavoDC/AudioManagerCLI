using System;

namespace AudioManager
{
    /// <summary>
    /// ENG-13: when 'git status' itself fails (RunGitStatus returns the "error" sentinel), the pre-integration
    /// gate must report GitStatusFailed, not MirrorStale, so the console does not blame the mirror.
    /// Status and LibChecker are injected; no real git, parser or library is touched.
    /// </summary>
    internal static class GateGitErrorTests
    {
        public static void DecideGate_GitStatusError_ReturnsGitStatusFailed()
        {
            Program.PreIntegrationGateResult result = Program.DecidePreIntegrationGate(
                mirrorRepoExists: true,
                mirrorStatus: () => "error",
                libCheckerClean: () => true);
            Assert.True(result == Program.PreIntegrationGateResult.GitStatusFailed,
                "git status failure should be GitStatusFailed, not MirrorStale");
        }

        public static void DecideGate_RealStaleStatus_StillReturnsMirrorStale()
        {
            Program.PreIntegrationGateResult result = Program.DecidePreIntegrationGate(
                mirrorRepoExists: true,
                mirrorStatus: () => " M a.xml",
                libCheckerClean: () => true);
            Assert.True(result == Program.PreIntegrationGateResult.MirrorStale,
                "a real modified XML must still be MirrorStale");
        }

        public static void DecideGate_EmptyStatusAndCleanLibrary_ReturnsPassed()
        {
            Program.PreIntegrationGateResult result = Program.DecidePreIntegrationGate(
                mirrorRepoExists: true,
                mirrorStatus: () => "",
                libCheckerClean: () => true);
            Assert.True(result == Program.PreIntegrationGateResult.Passed,
                "empty status with a clean library should pass");
        }

        public static void DecideGate_GitStatusError_DoesNotInvokeLibChecker()
        {
            bool libCalled = false;
            Program.DecidePreIntegrationGate(
                mirrorRepoExists: true,
                mirrorStatus: () => "error",
                libCheckerClean: () => { libCalled = true; return true; });
            Assert.True(!libCalled, "git status failure must decide before LibChecker runs");
        }
    }
}
