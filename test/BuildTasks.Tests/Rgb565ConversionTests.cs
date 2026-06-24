//
// Copyright (c) .NET Foundation and Contributors
// See LICENSE file in the project root for full license information.
//

// CrossPlatformBitmap is only compiled on non-Framework targets.
#if !NETFRAMEWORK

using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using nanoFramework.Tools;

namespace nanoFramework.Tools.BuildTasks.Tests
{
    /// <summary>
    /// Unit tests for <see cref="CrossPlatformBitmap"/>: validates that the
    /// ImageSharp-backed RGB565 converter produces the correct 16-bit pixel values
    /// for known input colours, and that bitmap format detection works correctly.
    /// </summary>
    [TestClass]
    public class Rgb565ConversionTests
    {
        // ─── CLR_GFX_BitmapDescription layout ────────────────────────────────────
        //
        // GenerateBitmapResourceData returns:
        //   [12 bytes: CLR_GFX_BitmapDescription serialised]
        //   [width * height * 2 bytes: RGB565 pixel data, little-endian]
        //
        // BitmapDescription.Serialize writes:
        //   m_width  (uint32) = 4 bytes
        //   m_height (uint32) = 4 bytes
        //   m_flags  (uint16) = 2 bytes
        //   m_bitsPerPixel (byte) = 1 byte
        //   m_type         (byte) = 1 byte
        //   ────────────────────────────────
        //   Total             = 12 bytes

        private const int BitmapDescriptionSize = 12;

        // ─── helpers ───────────────────────────────────────────────────────────────

        /// <summary>
        /// Creates a minimal valid 24bpp 1×1 BMP with the given RGB pixel.
        /// BMP stores pixels as BGR, bottom-up, rows padded to 4-byte boundary.
        /// </summary>
        private static byte[] MakeOnePxBmp(byte r, byte g, byte b)
        {
            // 14-byte file header + 40-byte BITMAPINFOHEADER + 4-byte pixel row
            var bmp = new byte[58];

            // File header
            bmp[0] = 0x42; bmp[1] = 0x4D;
            Buffer.BlockCopy(BitConverter.GetBytes(58u), 0, bmp, 2,  4); // file size
            Buffer.BlockCopy(BitConverter.GetBytes(54u), 0, bmp, 10, 4); // pixel data offset

            // BITMAPINFOHEADER
            Buffer.BlockCopy(BitConverter.GetBytes(40u), 0, bmp, 14, 4); // header size
            Buffer.BlockCopy(BitConverter.GetBytes(1u),  0, bmp, 18, 4); // width  = 1
            Buffer.BlockCopy(BitConverter.GetBytes(1u),  0, bmp, 22, 4); // height = 1
            bmp[26] = 1;  bmp[27] = 0;  // planes = 1
            bmp[28] = 24; bmp[29] = 0;  // bits per pixel = 24
            Buffer.BlockCopy(BitConverter.GetBytes(4u),  0, bmp, 34, 4); // image size = 4

            // Pixel data: BGR order + 1 padding byte (row must be 4-byte aligned)
            bmp[54] = b;
            bmp[55] = g;
            bmp[56] = r;

            return bmp;
        }

        /// <summary>
        /// Calls <see cref="CrossPlatformBitmap.GenerateBitmapResourceData"/> on a
        /// 1×1 BMP and returns the resulting RGB565 value.
        /// </summary>
        private static ushort ConvertPixelToRgb565(byte r, byte g, byte b)
        {
            byte[] bmpBytes = MakeOnePxBmp(r, g, b);
            byte[] resourceData = CrossPlatformBitmap.GenerateBitmapResourceData(bmpBytes);

            // Pixel immediately follows the 12-byte description
            Assert.IsTrue(resourceData.Length >= BitmapDescriptionSize + 2,
                "resource data too short");

            return (ushort)(resourceData[BitmapDescriptionSize] |
                            (resourceData[BitmapDescriptionSize + 1] << 8));
        }

        // ─── RGB565 pixel conversion tests ────────────────────────────────────────
        //
        // Quantisation rules (from ProcessResourceFiles.CrossPlatform.cs):
        //   r5 = min(31, (R + 4) >> 3)    ← round-to-nearest (5 bits)
        //   g6 =          G        >> 2    ← truncate          (6 bits)
        //   b5 = min(31, (B + 4) >> 3)    ← round-to-nearest (5 bits)
        //   RGB565 = (r5 << 11) | (g6 << 5) | b5

        [TestMethod]
        public void Convert_PureBlack_YieldsZero()
        {
            ushort rgb565 = ConvertPixelToRgb565(0, 0, 0);
            Assert.AreEqual(0x0000, rgb565, "black should be 0x0000");
        }

        [TestMethod]
        public void Convert_PureWhite_YieldsAllOnes()
        {
            // R=255 → r5=min(31,32)=31, G=255 → g6=63, B=255 → b5=31 → 0xFFFF
            ushort rgb565 = ConvertPixelToRgb565(255, 255, 255);
            Assert.AreEqual(0xFFFF, rgb565, "white should be 0xFFFF");
        }

        [TestMethod]
        public void Convert_PureRed_YieldsExpectedValue()
        {
            // R=255 → r5=31, G=0 → g6=0, B=0 → b5=0 → 0xF800
            ushort rgb565 = ConvertPixelToRgb565(255, 0, 0);
            Assert.AreEqual(0xF800, rgb565, "pure red should be 0xF800");
        }

        [TestMethod]
        public void Convert_PureGreen_YieldsExpectedValue()
        {
            // R=0 → r5=0, G=255 → g6=63, B=0 → b5=0 → 0x07E0
            ushort rgb565 = ConvertPixelToRgb565(0, 255, 0);
            Assert.AreEqual(0x07E0, rgb565, "pure green should be 0x07E0");
        }

        [TestMethod]
        public void Convert_PureBlue_YieldsExpectedValue()
        {
            // R=0 → r5=0, G=0 → g6=0, B=255 → b5=31 → 0x001F
            ushort rgb565 = ConvertPixelToRgb565(0, 0, 255);
            Assert.AreEqual(0x001F, rgb565, "pure blue should be 0x001F");
        }

        [TestMethod]
        public void Convert_Yellow_YieldsExpectedValue()
        {
            // R=255 → r5=31, G=255 → g6=63, B=0 → b5=0 → 0xFFE0
            ushort rgb565 = ConvertPixelToRgb565(255, 255, 0);
            Assert.AreEqual(0xFFE0, rgb565, "yellow should be 0xFFE0");
        }

        [TestMethod]
        public void Convert_Cyan_YieldsExpectedValue()
        {
            // R=0 → r5=0, G=255 → g6=63, B=255 → b5=31 → 0x07FF
            ushort rgb565 = ConvertPixelToRgb565(0, 255, 255);
            Assert.AreEqual(0x07FF, rgb565, "cyan should be 0x07FF");
        }

        [TestMethod]
        public void Convert_Magenta_YieldsExpectedValue()
        {
            // R=255 → r5=31, G=0 → g6=0, B=255 → b5=31 → 0xF81F
            ushort rgb565 = ConvertPixelToRgb565(255, 0, 255);
            Assert.AreEqual(0xF81F, rgb565, "magenta should be 0xF81F");
        }

        [TestMethod]
        public void Convert_MidGray_ChannelsQuantisedIndependently()
        {
            // R=128 → (128+4)>>3 = 16, G=128 → 128>>2 = 32, B=128 → 16
            // RGB565 = (16<<11)|(32<<5)|16 = 0x8410
            ushort rgb565 = ConvertPixelToRgb565(128, 128, 128);
            Assert.AreEqual(0x8410, rgb565, "mid-gray quantisation mismatch");
        }

        // ─── bitmap format detection tests ────────────────────────────────────────

        [TestMethod]
        public void IsSupportedBitmap_ValidBmp_ReturnsTrue()
        {
            byte[] bmp = MakeOnePxBmp(255, 0, 0);
            Assert.IsTrue(CrossPlatformBitmap.IsSupportedBitmap(bmp), "1×1 BMP should be detected");
        }

        [TestMethod]
        public void IsSupportedBitmap_PngBytes_ReturnsFalse()
        {
            // Minimal PNG magic bytes — not in the supported set (nanoFramework rejects PNG)
            byte[] pngMagic = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
            Assert.IsFalse(CrossPlatformBitmap.IsSupportedBitmap(pngMagic), "PNG should not be supported");
        }

        [TestMethod]
        public void IsSupportedBitmap_RandomBytes_ReturnsFalse()
        {
            byte[] garbage = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05 };
            Assert.IsFalse(CrossPlatformBitmap.IsSupportedBitmap(garbage), "garbage bytes should not be supported");
        }

        [TestMethod]
        public void IsSupportedBitmap_EmptyArray_ReturnsFalse()
        {
            Assert.IsFalse(CrossPlatformBitmap.IsSupportedBitmap(Array.Empty<byte>()), "empty array should not be supported");
        }

        // ─── resource data structure tests ────────────────────────────────────────

        [TestMethod]
        public void GenerateBitmapResourceData_OnePxBmp_HasCorrectDimensions()
        {
            byte[] bmpBytes = MakeOnePxBmp(0, 0, 0);
            byte[] data = CrossPlatformBitmap.GenerateBitmapResourceData(bmpBytes);

            Assert.AreEqual(BitmapDescriptionSize + 2, data.Length,
                "1×1 16bpp BMP should produce 12 (description) + 2 (pixel) bytes");

            // m_width at offset 0: uint32 little-endian
            uint width = BitConverter.ToUInt32(data, 0);
            Assert.AreEqual(1u, width, "width should be 1");

            // m_height at offset 4: uint32 little-endian
            uint height = BitConverter.ToUInt32(data, 4);
            Assert.AreEqual(1u, height, "height should be 1");

            // m_bitsPerPixel at offset 10
            Assert.AreEqual(16, data[10], "bits per pixel should be 16 for BMP");

            // m_type at offset 11 (0 = c_TypeBitmap)
            Assert.AreEqual(0, data[11], "type should be c_TypeBitmap (0)");
        }
    }
}

#endif
