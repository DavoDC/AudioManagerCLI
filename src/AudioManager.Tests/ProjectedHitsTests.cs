using System.Collections.Generic;
using System.IO;
using System.Text;

namespace AudioManager.Tests
{
    /// <summary>ENG-83: projected-LibChecker hits as client data, honouring Accept and keep rows.</summary>
    internal static class ProjectedHitsTests
    {
        private static LibChecker.LibCheckerHit H(string kind, string path, params string[] related)
        {
            var h = new LibChecker.LibCheckerHit { Kind = kind, Path = path, Detail = "msg" };
            h.Related.AddRange(related);
            return h;
        }

        private static TagFixExceptions Ex(string xml)
        {
            string dir = Path.Combine(Path.GetTempPath(), "AudioManagerTest_" + System.Guid.NewGuid());
            Directory.CreateDirectory(dir);
            try
            {
                string p = Path.Combine(dir, "e.xml");
                File.WriteAllText(p, "<Exceptions>" + xml + "</Exceptions>");
                return TagFixExceptions.Load(p);
            }
            finally { Directory.Delete(dir, true); }
        }

        public static void Build_MapsSourceAndRelated_LibraryHitHasNullSource()
        {
            var map = new Dictionary<string, string> { { "\\A\\x.xml", "Inbox\\x.mp3" } };
            var hits = new List<LibChecker.LibCheckerHit> { H("duplicate", "\\A\\x.xml", "\\Lib\\y.xml"), H("genre_missing", "\\Lib\\y.xml") };
            var r = ProjectedHits.Build(hits, map, null);
            Assert.True(r.Count == 2, "two hits");
            Assert.True(r[0].SourceFile == "Inbox\\x.mp3", "source mapped");
            Assert.True(r[0].RelatedFiles[0] == "\\Lib\\y.xml", "library partner stays a mirror path");
            Assert.True(r[1].SourceFile == null, "library hit has no source file");
        }

        public static void Build_AcceptAndKeepSuppress_OnlyThatRule()
        {
            var map = new Dictionary<string, string> { { "\\A\\x.xml", "x.mp3" }, { "\\A\\z.xml", "z.mp3" } };
            var ex = Ex("<Accept file=\"x.mp3\" rule=\"unwanted_text\" /><Exception file=\"z.mp3\" keep=\"title\" />");
            var hits = new List<LibChecker.LibCheckerHit> { H("unwanted_text", "\\A\\x.xml"), H("missing_tag", "\\A\\x.xml"), H("unwanted_text", "\\A\\z.xml") };
            var r = ProjectedHits.Build(hits, map, ex);
            Assert.True(r.Count == 1, "only missing_tag survives");
            Assert.True(r[0].Rule == "missing_tag", "rule kept");
        }

        public static void AppendBlock_EmptyAndPopulated()
        {
            var sb = new StringBuilder();
            ProjectedHits.AppendBlock(sb, new List<ProjectedHits.Item>());
            Assert.True(sb.ToString().Contains("\"projectedHits\": [],"), "empty array");
            sb.Clear();
            ProjectedHits.AppendBlock(sb, new List<ProjectedHits.Item> { new ProjectedHits.Item { Rule = "duplicate", SourceFile = null, MirrorPath = "\\a.xml", Message = "m" } });
            Assert.True(sb.ToString().Contains("\"sourceFile\": null") && sb.ToString().Contains("\"rule\": \"duplicate\""), "populated");
        }
    }
}
