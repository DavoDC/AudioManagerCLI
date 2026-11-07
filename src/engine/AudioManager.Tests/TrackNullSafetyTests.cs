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

        public static void PrimaryArtist_NullArtists_ReturnsEmpty()
        {
            var track = new Track { Artists = null };
            Assert.True(track.PrimaryArtist == "", "null Artists -> empty PrimaryArtist");
        }

        public static void PrimaryArtist_EmptyArtists_ReturnsEmpty()
        {
            var track = new Track { Artists = "" };
            Assert.True(track.PrimaryArtist == "", "empty Artists -> empty PrimaryArtist");
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
            Assert.True(text.Contains("PrimaryArtist: "), "null Artists still renders a PrimaryArtist line");
        }
    }
}
