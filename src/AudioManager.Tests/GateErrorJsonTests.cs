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
