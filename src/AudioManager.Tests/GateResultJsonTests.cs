using System;
using System.Collections.Generic;

namespace AudioManager
{
    /// <summary>
    /// Tests for the integrate pre-integration gate result JSON (data/logs/integrate-gate.json).
    /// The pure builder is tested with synthetic gate outcomes; no git, parser or library is touched.
    /// </summary>
    internal static class GateResultJsonTests
    {
        private static readonly DateTime FixedNow = new DateTime(2026, 11, 10, 10, 30, 0);

        public static void Build_Passed_HasOkStatusAndNoReason()
        {
            string json = GateResultJson.Build(Program.PreIntegrationGateResult.Passed, new List<string>(), 0, FixedNow);
            Assert.True(json.Contains("\"schemaVersion\": 1"), "schema version");
            Assert.True(json.Contains("\"status\": \"passed\""), "status passed");
            Assert.True(json.Contains("\"generatedAt\": \"2026-11-10T10:30:00\""), "ISO time");
            Assert.True(json.Contains("\"mirrorDirty\": false"), "mirror clean");
        }

        public static void Build_MirrorStale_ListsDirtyFilesAndReasonCode()
        {
            var dirty = new List<string> { " M AUDIO_MIRROR/Artists/A/x.xml", "?? AUDIO_MIRROR/Artists/B/y.xml" };
            string json = GateResultJson.Build(Program.PreIntegrationGateResult.MirrorStale, dirty, 0, FixedNow);
            Assert.True(json.Contains("\"status\": \"mirror_stale\""), "status mirror_stale");
            Assert.True(json.Contains("\"mirrorDirty\": true"), "mirror dirty");
            Assert.True(json.Contains("\"mirrorDirtyCount\": 2"), "dirty count");
            Assert.True(json.Contains("AUDIO_MIRROR/Artists/A/x.xml"), "lists file");
            Assert.True(json.Contains("\"action\": \"commit_mirror\""), "action hint code");
        }

        public static void Build_MirrorStale_IgnoresLastRunInfoLine()
        {
            var dirty = new List<string> { " M LastRunInfo.txt", " M a.xml" };
            string json = GateResultJson.Build(Program.PreIntegrationGateResult.MirrorStale, dirty, 0, FixedNow);
            Assert.True(json.Contains("\"mirrorDirtyCount\": 1"), "LastRunInfo.txt not counted");
        }

        public static void Build_LibCheckerDirty_CarriesIssueCount()
        {
            string json = GateResultJson.Build(Program.PreIntegrationGateResult.LibCheckerDirty, new List<string>(), 7, FixedNow);
            Assert.True(json.Contains("\"status\": \"libchecker_dirty\""), "status libchecker_dirty");
            Assert.True(json.Contains("\"libCheckerIssues\": 7"), "issue count");
            Assert.True(json.Contains("\"mirrorDirty\": false"), "mirror clean");
        }

        public static void Build_MirrorRepoMissing_HasStatus()
        {
            string json = GateResultJson.Build(Program.PreIntegrationGateResult.MirrorRepoMissing, new List<string>(), 0, FixedNow);
            Assert.True(json.Contains("\"status\": \"mirror_repo_missing\""), "status mirror_repo_missing");
        }

        public static void Build_EscapesQuotesInPaths()
        {
            var dirty = new List<string> { " M a\"b.xml" };
            string json = GateResultJson.Build(Program.PreIntegrationGateResult.MirrorStale, dirty, 0, FixedNow);
            Assert.True(json.Contains("a\\\"b.xml"), "quote escaped");
        }
    }
}
