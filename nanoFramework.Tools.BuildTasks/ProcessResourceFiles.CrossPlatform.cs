//
// Copyright (c) .NET Foundation and Contributors
// See LICENSE file in the project root for full license information.
//
// Cross-platform (net8.0+) companion for ProcessResourceFiles.
//
// On .NET Framework the resource pipeline relies on System.Windows.Forms
// (ResXResourceReader) and System.Drawing (GDI+) which are Windows-only and
// unavailable under `dotnet build`. This file provides the cross-platform
// equivalents used when the build task runs on modern .NET:
//
//   * NanoResxResourceReader - a focused .resx reader covering the cases the
//     nanoFramework pipeline uses (inline strings, ResXFileRef byte[]/string,
//     and base64 byte-array blobs).
//   * CrossPlatformBitmap - bitmap decoding + RGB565 conversion via ImageSharp,
//     producing the exact CLR_GFX_BitmapDescription + payload the nanoCLR expects.
//

#if !NETFRAMEWORK

using System;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Resources;
using System.Text;
using System.Xml;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.PixelFormats;

using BitmapDescription = nanoFramework.Tools.ProcessResourceFiles.NanoResourceFile.CLR_GFX_BitmapDescription;

namespace nanoFramework.Tools
{
    /// <summary>
    /// Minimal cross-platform .resx reader.
    /// Implements just enough of the .resx schema for the nanoFramework resource
    /// pipeline: inline string values, <see cref="System.Resources.ResXFileRef"/>
    /// style file references (resolved to <c>byte[]</c> or <c>string</c>), and
    /// inline base64 byte arrays. Anything requiring runtime type activation
    /// (BinaryFormatter-serialized objects) is reported as unsupported.
    /// </summary>
    internal sealed class NanoResxResourceReader : IResourceReader
    {
        private const string ResXFileRefTypeMarker = "ResXFileRef";
        private const string ByteArrayMimeType = "application/x-microsoft.net.object.bytearray.base64";

        private readonly Hashtable _resources = new Hashtable();

        internal NanoResxResourceReader(TextReader reader, string basePath)
        {
            Parse(reader, basePath);
        }

        private void Parse(TextReader reader, string basePath)
        {
            XmlDocument doc = new XmlDocument();
            doc.Load(reader);

            XmlNodeList dataNodes = doc.SelectNodes("/root/data");
            if (dataNodes == null)
            {
                return;
            }

            foreach (XmlNode dataNode in dataNodes)
            {
                string name = dataNode.Attributes?["name"]?.Value;
                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }

                string type = dataNode.Attributes?["type"]?.Value;
                string mimetype = dataNode.Attributes?["mimetype"]?.Value;
                XmlNode valueNode = dataNode.SelectSingleNode("value");
                string rawValue = valueNode != null ? valueNode.InnerText : string.Empty;

                object value = ResolveValue(type, mimetype, rawValue, basePath);
                if (value != null)
                {
                    _resources[name] = value;
                }
            }
        }

        private object ResolveValue(string type, string mimetype, string rawValue, string basePath)
        {
            // Inline base64 byte array (e.g. small binary blobs embedded in the .resx).
            if (!string.IsNullOrEmpty(mimetype))
            {
                if (string.Equals(mimetype, ByteArrayMimeType, StringComparison.OrdinalIgnoreCase))
                {
                    return Convert.FromBase64String(rawValue.Trim());
                }

                throw new NotSupportedException(
                    $"Resource mimetype '{mimetype}' is not supported when building with 'dotnet build'. " +
                    "Use full .NET Framework MSBuild / Visual Studio for this resource.");
            }

            // File reference (ResXFileRef): "<path>;<type>[;<encoding>]".
            if (!string.IsNullOrEmpty(type) && type.Contains(ResXFileRefTypeMarker))
            {
                return ResolveFileRef(rawValue, basePath);
            }

            // Anything else with an explicit non-primitive type is not supported here.
            if (!string.IsNullOrEmpty(type) && !IsStringType(type))
            {
                throw new NotSupportedException(
                    $"Resource type '{type}' is not supported when building with 'dotnet build'. " +
                    "Use full .NET Framework MSBuild / Visual Studio for this resource.");
            }

            // Plain inline string.
            return rawValue;
        }

        private object ResolveFileRef(string fileRef, string basePath)
        {
            // Format: filePath;typeName[;encoding]
            string[] parts = SplitFileRef(fileRef);
            string path = parts[0].Trim();
            string typeName = parts.Length > 1 ? parts[1].Trim() : "System.Byte[]";

            string resolvedPath = Path.IsPathRooted(path)
                ? path
                : Path.GetFullPath(Path.Combine(basePath ?? Directory.GetCurrentDirectory(), path));

            if (IsStringType(typeName))
            {
                Encoding encoding = Encoding.UTF8;
                if (parts.Length > 2 && !string.IsNullOrWhiteSpace(parts[2]))
                {
                    try
                    {
                        encoding = Encoding.GetEncoding(parts[2].Trim());
                    }
                    catch (ArgumentException)
                    {
                        // fall back to UTF-8
                    }
                }

                return File.ReadAllText(resolvedPath, encoding);
            }

            if (IsMemoryStreamType(typeName))
            {
                return new MemoryStream(File.ReadAllBytes(resolvedPath));
            }

            // Default (and the common nanoFramework case): raw bytes (System.Byte[]).
            return File.ReadAllBytes(resolvedPath);
        }

        /// <summary>
        /// Splits a ResXFileRef value into [path, type, encoding]. The path may itself
        /// contain ';' so we only split on the separators that precede the type/encoding.
        /// </summary>
        private static string[] SplitFileRef(string fileRef)
        {
            // ResXFileRef.Parse splits on ';' into at most 3 segments, but a quoted path
            // ("path";type;encoding) protects a path containing ';'. Handle the quoted form.
            fileRef = fileRef.Trim();
            if (fileRef.StartsWith("\""))
            {
                int closingQuote = fileRef.IndexOf('"', 1);
                if (closingQuote > 0)
                {
                    string path = fileRef.Substring(1, closingQuote - 1);
                    string remainder = fileRef.Substring(closingQuote + 1).TrimStart(';');
                    string[] rest = remainder.Split(';');
                    string[] result = new string[1 + rest.Length];
                    result[0] = path;
                    Array.Copy(rest, 0, result, 1, rest.Length);
                    return result;
                }
            }

            return fileRef.Split(';');
        }

        private static bool IsStringType(string typeName)
        {
            return typeName != null && typeName.StartsWith("System.String", StringComparison.Ordinal);
        }

        private static bool IsMemoryStreamType(string typeName)
        {
            return typeName != null && typeName.StartsWith("System.IO.MemoryStream", StringComparison.Ordinal);
        }

        #region IResourceReader / IEnumerable / IDisposable

        public IDictionaryEnumerator GetEnumerator()
        {
            return _resources.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return _resources.GetEnumerator();
        }

        public void Close()
        {
            _resources.Clear();
        }

        public void Dispose()
        {
            Close();
        }

        #endregion
    }

    /// <summary>
    /// Cross-platform bitmap handling for the nanoFramework resource pipeline,
    /// backed by ImageSharp. Mirrors the .NET Framework / GDI+ behaviour:
    /// BMP images are converted to uncompressed 16bpp RGB565; JPEG and GIF
    /// images are stored as-is with the matching bitmap type.
    /// </summary>
    internal static class CrossPlatformBitmap
    {
        /// <summary>
        /// Returns true if the raw bytes decode as one of the bitmap formats the
        /// nanoFramework runtime supports (BMP, GIF, JPEG). PNG and other formats
        /// are intentionally rejected so they are treated as binary resources,
        /// matching the .NET Framework implementation.
        /// </summary>
        internal static bool IsSupportedBitmap(byte[] rawValue)
        {
            return TryGetBitmapType(rawValue, out _);
        }

        internal static byte[] GenerateBitmapResourceData(byte[] rawValue)
        {
            if (!TryGetBitmapType(rawValue, out byte bitmapType))
            {
                throw new NotSupportedException("Bitmap format not supported.");
            }

            using (Image<Rgba32> image = Image.Load<Rgba32>(rawValue))
            {
                if (image.Width > 0xFFFF || image.Height > 0xFFFF)
                {
                    throw new ArgumentException("bitmap dimensions out of range");
                }

                BitmapDescription description;
                byte[] data;

                if (bitmapType == BitmapDescription.c_TypeBitmap)
                {
                    // BMP -> uncompressed 16bpp RGB565.
                    data = ConvertToRgb565(image);
                    description = new BitmapDescription(
                        (ushort)image.Width,
                        (ushort)image.Height,
                        0,
                        16,
                        BitmapDescription.c_TypeBitmap);
                }
                else
                {
                    // JPEG / GIF -> stored encoded, decoded by the device at runtime.
                    data = rawValue;
                    description = new BitmapDescription(
                        (ushort)image.Width,
                        (ushort)image.Height,
                        0,
                        1,
                        bitmapType);
                }

                using (MemoryStream stream = new MemoryStream())
                using (BinaryWriter writer = new BinaryWriter(stream))
                {
                    description.Serialize(writer);
                    writer.Write(data);
                    writer.Flush();
                    return stream.ToArray();
                }
            }
        }

        private static bool TryGetBitmapType(byte[] rawValue, out byte bitmapType)
        {
            bitmapType = 0;

            if (rawValue == null || rawValue.Length == 0)
            {
                return false;
            }

            try
            {
                IImageFormat format = Image.DetectFormat(rawValue);
                if (format == null)
                {
                    return false;
                }

                switch (format.Name.ToUpperInvariant())
                {
                    case "BMP":
                        bitmapType = BitmapDescription.c_TypeBitmap;
                        return true;
                    case "GIF":
                        bitmapType = BitmapDescription.c_TypeGif;
                        return true;
                    case "JPEG":
                        bitmapType = BitmapDescription.c_TypeJpeg;
                        return true;
                    default:
                        return false;
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Converts an image to contiguous 16bpp RGB565, row-major, top-down,
        /// little-endian per pixel — the layout the nanoCLR expects (and that
        /// GDI+ Format16bppRgb565 produces for even widths).
        /// </summary>
        private static byte[] ConvertToRgb565(Image<Rgba32> image)
        {
            int width = image.Width;
            int height = image.Height;
            byte[] data = new byte[width * height * 2];

            image.ProcessPixelRows(accessor =>
            {
                int offset = 0;
                for (int y = 0; y < height; y++)
                {
                    Span<Rgba32> row = accessor.GetRowSpan(y);
                    for (int x = 0; x < width; x++)
                    {
                        Rgba32 p = row[x];

                        // Quantize 8-bit channels to RGB565. Green/blue match GDI+
                        // (the net472 System.Drawing path) exactly. Red uses round-to-
                        // nearest, which matches GDI+ except for colors that land exactly
                        // on a half-level boundary: there GDI+ applies ordered (Bayer)
                        // dithering by pixel position, which is NOT reproduced here. Such
                        // pixels can differ by 1 LSB of red from the legacy/net472 output.
                        // See SmokeTest Resources.resx parity notes.
                        int r5 = Math.Min(31, (p.R + 4) >> 3);
                        int g6 = p.G >> 2;
                        int b5 = Math.Min(31, (p.B + 4) >> 3);

                        ushort value = (ushort)((r5 << 11) | (g6 << 5) | b5);
                        data[offset++] = (byte)(value & 0xFF);
                        data[offset++] = (byte)(value >> 8);
                    }
                }
            });

            return data;
        }
    }
}

#endif
