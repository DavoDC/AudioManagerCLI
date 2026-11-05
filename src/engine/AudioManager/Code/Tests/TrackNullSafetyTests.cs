using AudioManager.Code.Modules;

namespace AudioManager
{
    /// <summary>
    /// Null and empty Artists must not throw from PrimaryArtist or ToAllPropertiesString.
    /// </summary>
    internal static class TrackNullSafetyTests
    {
        public static void ProcessProperty_Null_ReturnsEmptyArray()
        {
            string[] result = Track.ProcessProperty(null);
            Assert.Equal("0", result.Length.ToString(), "null input -> empty array");
        }

        public static void PrimaryArtist_NullArtists_ReturnsNull()
        {
            var track = new Track { Artists = null };
            Assert.True(track.PrimaryArtist == null, "null Artists -> null PrimaryArtist");
        }

        public static void PrimaryArtist_EmptyArtists_ReturnsNull()
        {
            var track = new Track { Artists = "" };
            Assert.True(track.PrimaryArtist == null, "empty Artists -> null PrimaryArtist");
        }

        public static void PrimaryArtist_MultipleArtists_ReturnsFirst()
        {
            var track = new Track { Artists = "Artist A; Artist B" };
            Assert.Equal("Artist A", track.PrimaryArtist, "first artist of a semicolon list");
        }

        public static void ToAllPropertiesString_NullArtists_DoesNotThrow()
        {
            var track = new Track { Artists = null, Title = "T" };
            string text = track.ToAllPropertiesString();
            Assert.True(text.Contains("PrimaryArtist: NULL"), "null Artists renders PrimaryArtist as NULL");
        }
    }
}
