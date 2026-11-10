using System;
using System.IO;

namespace AudioManager
{
    /// <summary>
    /// A crashed pre-integration gate must never leave a stale integrate-gate.json from an earlier run
    /// (a client would read an old "passed"). The file is cleared at gate start and an error status is written on a throw.
    /// </summary>
    internal static class GateErrorJsonTests
    {
        public static void JsonText_Quote_EscapesControlCharsNeverDropsThem()
        {
            Assert.Equal("null", JsonText.Quote(null), "null stays the literal null");
            Assert.Equal("\"a\\tb\\r\\n\\u0001\\\\\\\"\"", JsonText.Quote("a\tb\r\n\u0001\\\""), "tab, CR, LF, control and quote escaped");
        }

        public static void GateJson_ErrorMessageWithTabAndCr_IsEscapedNotDropped()
        {
            string d = Path.Combine(Path.GetTempPath(), "am_gatejs_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                GateResultJson.WriteError(d, "a\tb\rc");
                string json = File.ReadAllText(Path.Combine(d, GateResultJson.OutputFileName));
                Assert.True(json.Contains("a\\tb\\rc"), "tab and CR are escaped in the gate json: " + json);
            }
            finally { if (Directory.Exists(d)) Directory.Delete(d, true); }
        }

        public static void GateJson_GitStatusFailed_HasGitErrorStatusAndFixGitAction()
        {
            string json = GateResultJson.Build(Program.PreIntegrationGateResult.GitStatusFailed, null, 0, DateTime.Now);
            Assert.True(json.Contains("\"status\": \"git_error\"") && json.Contains("\"action\": \"fix_git\""), "git failure maps to git_error / fix_git");
        }

        public static void WriteError_WritesGateErrorStatus_AndClearStaleRemovesOldFile()
        {
            string d = Path.Combine(Path.GetTempPath(), "am_gateerr_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(d);
            try
            {
                string f = Path.Combine(d, GateResultJson.OutputFileName);
                File.WriteAllText(f, "{ \"status\": \"passed\" }");
                GateResultJson.ClearStale(d);
                Assert.True(!File.Exists(f), "stale gate file removed at gate start");
                GateResultJson.WriteError(d, "boom \"x\"");
                string json = File.ReadAllText(f);
                Assert.True(json.Contains("\"status\": \"gate_error\""), "error status written");
                Assert.True(json.Contains("boom \\\"x\\\""), "message escaped into json");
                GateResultJson.ClearStale(Path.Combine(d, "missing"));
            }
            finally { Directory.Delete(d, true); }
        }
    }
}
