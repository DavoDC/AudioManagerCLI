using AudioManager.Code.Modules;
using System;

namespace AudioManager
{
    /// <summary>Date Added / Date Modified in tracks.json: never the mirror XML mtime (force-regen resets it).</summary>
    internal static class TracksJsonDatesTests
    {
        public static void AddedDate_PrefersTheEarlierOfXmlAndMp3Created()
        {
            Assert.Equal("2021-05-04", TracksJson.PickAddedDate("2021-05-04", new DateTime(2023, 3, 1)), "xml earlier");
            Assert.Equal("2019-01-02", TracksJson.PickAddedDate("2021-05-04", new DateTime(2019, 1, 2)), "mp3 earlier");
        }

        public static void AddedDate_FallsBackToWhicheverExists()
        {
            Assert.Equal("2021-05-04", TracksJson.PickAddedDate("2021-05-04", null), "xml only");
            Assert.Equal("2019-01-02", TracksJson.PickAddedDate(null, new DateTime(2019, 1, 2)), "mp3 only");
            Assert.Equal("2019-01-02", TracksJson.PickAddedDate("garbage", new DateTime(2019, 1, 2)), "bad xml ignored");
        }

        public static void AddedDate_IsNullWhenNothingKnown()
        {
            Assert.True(TracksJson.PickAddedDate(null, null) == null, "no xml, no mp3 -> null, never the XML mtime");
        }

        public static void Build_EmitsModifiedDateField()
        {
            var t = new TrackTag("\\Artists\\A\\Singles\\A - S.xml", "S", "A", "Al", "2020", "1", "Rock",
                "00:03:00.0000000", "1", "False", "1000", "1000");
            string json = TracksJson.Build(new System.Collections.Generic.List<TrackTag> { t });
            Assert.True(json.Contains("\"modifiedDate\": null"), "modifiedDate present (null when the mp3 is absent)");
        }
    }
}
