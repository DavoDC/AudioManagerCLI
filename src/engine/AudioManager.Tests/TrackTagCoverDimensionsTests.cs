using AudioManager.Code.Modules;
using System;

namespace AudioManager
{
    /// <summary>
    /// Byte-fixture tests for TrackTag.GetCoverDimensions: valid PNG and JPEG headers return their
    /// pixel size; truncated, unknown, null and too-short input returns (0, 0).
    /// </summary>
    internal static class TrackTagCoverDimensionsTests
    {
        private static byte[] PngHeader(int width, int height)
        {
            return new byte[]
            {
                0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,       // PNG signature
                0x00, 0x00, 0x00, 0x0D,                               // IHDR length
                0x49, 0x48, 0x44, 0x52,                               // 'IHDR'
                (byte)(width >> 24), (byte)(width >> 16), (byte)(width >> 8), (byte)width,
                (byte)(height >> 24), (byte)(height >> 16), (byte)(height >> 8), (byte)height
            };
        }

        public static void Cover_ValidPng_ReturnsWidthAndHeight()
        {
            var r = TrackTag.GetCoverDimensions(PngHeader(500, 300));
            Assert.True(r.width == 500 && r.height == 300,
                "PNG 500x300 should read as (500,300), got (" + r.width + "," + r.height + ")");
        }

        public static void Cover_ValidJpeg_ReturnsWidthAndHeight()
        {
            byte[] data = new byte[]
            {
                0xFF, 0xD8,                                           // SOI
                0xFF, 0xE0, 0x00, 0x10,                               // APP0, length 16 (includes the 2 length bytes)
                0x4A, 0x46, 0x49, 0x46, 0x00, 0x01, 0x01, 0x00,       // 14 filler payload bytes
                0x00, 0x01, 0x00, 0x01, 0x00, 0x00,
                0xFF, 0xC0, 0x00, 0x11, 0x08,                         // SOF0, length 17, precision 8
                0x01, 0x90,                                           // height 400
                0x02, 0x58,                                           // width 600
                0x03, 0x01, 0x22, 0x00, 0x02, 0x11, 0x01, 0x03, 0x11, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00
            };
            var r = TrackTag.GetCoverDimensions(data);
            Assert.True(r.width == 600 && r.height == 400,
                "JPEG 600x400 should read as (600,400), got (" + r.width + "," + r.height + ")");
        }

        public static void Cover_TruncatedPng_ReturnsZero()
        {
            byte[] data = new byte[]
            {
                0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
                0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
                0x00, 0x00, 0x01, 0xF4
            };
            Assert.True(data.Length == 20, "fixture should be 20 bytes");
            var r = TrackTag.GetCoverDimensions(data);
            Assert.True(r.width == 0 && r.height == 0,
                "truncated PNG should read as (0,0), got (" + r.width + "," + r.height + ")");
        }

        public static void Cover_UnknownFormat_ReturnsZero()
        {
            byte[] data = new byte[]
            {
                0x47, 0x49, 0x46, 0x38, 0x39, 0x61,                   // GIF89a
                0x01, 0x00, 0x01, 0x00, 0x80, 0x00, 0x00, 0x00, 0x00, 0x00
            };
            var r = TrackTag.GetCoverDimensions(data);
            Assert.True(r.width == 0 && r.height == 0,
                "unknown format should read as (0,0), got (" + r.width + "," + r.height + ")");
        }

        public static void Cover_NullInput_ReturnsZero()
        {
            var r = TrackTag.GetCoverDimensions(null);
            Assert.True(r.width == 0 && r.height == 0,
                "null input should read as (0,0), got (" + r.width + "," + r.height + ")");
        }

        public static void Cover_ThreeByteInput_ReturnsZero()
        {
            byte[] data = new byte[] { 0x89, 0x50, 0x4E };
            var r = TrackTag.GetCoverDimensions(data);
            Assert.True(r.width == 0 && r.height == 0,
                "3-byte input should read as (0,0), got (" + r.width + "," + r.height + ")");
        }
    }
}
