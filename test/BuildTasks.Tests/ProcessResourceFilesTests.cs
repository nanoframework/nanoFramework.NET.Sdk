//
// Copyright (c) .NET Foundation and Contributors
// See LICENSE file in the project root for full license information.
//

// ProcessResourceFiles has a field of type TaskLoggingHelper
// (Microsoft.Build.Utilities.Core). On net8.0 that NuGet package ships only a
// reference assembly (no runtime lib), so instantiating ProcessResourceFiles fails
// at test-run time. The cross-platform components it exercises on net8.0
// (NanoResxResourceReader, CrossPlatformBitmap) are covered directly by the other
// test files. These integration tests therefore run on net472 only.
#if NETFRAMEWORK

using System;
using System.IO;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using nanoFramework.Tools;

namespace nanoFramework.Tools.BuildTasks.Tests
{
    /// <summary>
    /// End-to-end tests for <see cref="ProcessResourceFiles"/>: creates real .resx
    /// files on disk, runs the resource processor, and validates the produced
    /// .nanoresources binary output.
    ///
    /// These tests use the public <c>ReadResources</c> / <c>WriteResources</c> API
    /// rather than <c>Run()</c> so that the test project has no runtime dependency on
    /// <c>Microsoft.Build.Utilities.Core</c> (whose assembly is not on the default
    /// probe path when running <c>dotnet test</c>). This file is compiled and run only
    /// for NETFRAMEWORK (net472).
    /// </summary>
    [TestClass]
    public class ProcessResourceFilesTests
    {
        // ─── minimal .resx template ────────────────────────────────────────────────

        private const string ResxHeader = @"<?xml version=""1.0"" encoding=""utf-8""?>
<root>
  <resheader name=""resmimetype""><value>text/microsoft-resx</value></resheader>
  <resheader name=""version""><value>2.0</value></resheader>
  <resheader name=""reader""><value>System.Resources.ResXResourceReader, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value></resheader>
  <resheader name=""writer""><value>System.Resources.ResXResourceWriter, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value></resheader>";

        private const string ResxFooter = "\n</root>";

        // ─── helpers ───────────────────────────────────────────────────────────────

        /// <summary>
        /// Runs <see cref="ProcessResourceFiles.ReadResources"/> +
        /// <see cref="ProcessResourceFiles.WriteResources"/> on a temp .resx file and
        /// returns the path to the produced .nanoresources file.
        /// </summary>
        private static string RunOnResx(string dir, string resxBody, bool useSourcePath = true)
        {
            string resxPath = Path.Combine(dir, "Resources.resx");
            string outPath  = Path.Combine(dir, "Resources.nanoresources");
            File.WriteAllText(resxPath, ResxHeader + resxBody + ResxFooter, Encoding.UTF8);

            var prf = new ProcessResourceFiles();
            // StronglyTypedNamespace must be non-empty so Entry.CreateEntry works.
            prf.StronglyTypedNamespace = "Test";
            prf.StronglyTypedClassName = "Resources";

            prf.ReadResources(resxPath, useSourcePath);
            prf.WriteResources(outPath);

            return outPath;
        }

        /// <summary>Deserializes a .nanoresources file.</summary>
        private static ProcessResourceFiles.NanoResourceFile ReadNanoresources(string path)
        {
            var nrf = new ProcessResourceFiles.NanoResourceFile();
            using var br = new BinaryReader(File.OpenRead(path));
            nrf.Deserialize(br);
            return nrf;
        }

        /// <summary>
        /// Creates a minimal valid 24bpp 1×1 BMP with the given RGB pixel.
        /// BMP pixels are stored as BGR, rows are bottom-up and
        /// padded to a 4-byte boundary.
        /// </summary>
        private static byte[] MakeOnePxBmp(byte r, byte g, byte b)
        {
            // 14-byte file header + 40-byte BITMAPINFOHEADER + 4-byte pixel row
            var bmp = new byte[58];

            // File header
            bmp[0] = 0x42; bmp[1] = 0x4D;
            Buffer.BlockCopy(BitConverter.GetBytes(58u), 0, bmp, 2,  4); // file size
            Buffer.BlockCopy(BitConverter.GetBytes(54u), 0, bmp, 10, 4); // pixel offset

            // BITMAPINFOHEADER
            Buffer.BlockCopy(BitConverter.GetBytes(40u), 0, bmp, 14, 4); // header size
            Buffer.BlockCopy(BitConverter.GetBytes(1u),  0, bmp, 18, 4); // width  = 1
            Buffer.BlockCopy(BitConverter.GetBytes(1u),  0, bmp, 22, 4); // height = 1
            bmp[26] = 1;  bmp[27] = 0;   // planes = 1
            bmp[28] = 24; bmp[29] = 0;   // bits per pixel = 24
            Buffer.BlockCopy(BitConverter.GetBytes(4u),  0, bmp, 34, 4); // image size = 4

            // Pixel: BGR + 1 padding byte
            bmp[54] = b;
            bmp[55] = g;
            bmp[56] = r;

            return bmp;
        }

        // ─── tests ─────────────────────────────────────────────────────────────────

        [TestMethod]
        public void ReadWrite_TwoStringResources_BothPresentWithCorrectKind()
        {
            string dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            Directory.CreateDirectory(dir);
            try
            {
                string resxBody = @"
  <data name=""Greeting""><value>Hello nanoFramework</value></data>
  <data name=""Farewell""><value>Goodbye</value></data>";

                string outPath = RunOnResx(dir, resxBody);

                Assert.IsTrue(File.Exists(outPath), "nanoresources file was not created");

                var nrf = ReadNanoresources(outPath);
                Assert.AreEqual(2, nrf.resources.Length, "expected 2 resources");

                foreach (var res in nrf.resources)
                {
                    Assert.AreEqual(
                        ProcessResourceFiles.NanoResourceFile.ResourceHeader.RESOURCE_String,
                        res.header.kind,
                        "resource kind should be RESOURCE_String");
                }

                // String values present (order may differ by ID hash)
                var strings = new string[2];
                for (int i = 0; i < 2; i++)
                    strings[i] = Encoding.UTF8.GetString(nrf.resources[i].data).TrimEnd('\0');

                CollectionAssert.AreEquivalent(
                    new[] { "Hello nanoFramework", "Goodbye" },
                    strings,
                    "string resource values do not match");
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [TestMethod]
        public void ReadWrite_BinaryBlobFileRef_EmittedAsBinaryResource()
        {
            string dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            Directory.CreateDirectory(dir);
            try
            {
                byte[] blob = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08 };
                File.WriteAllBytes(Path.Combine(dir, "data.bin"), blob);

                string resxBody = @"
  <data name=""BlobData"" type=""System.Resources.ResXFileRef, System.Windows.Forms"">
    <value>data.bin;System.Byte[], mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value>
  </data>";

                string outPath = RunOnResx(dir, resxBody);

                Assert.IsTrue(File.Exists(outPath), "nanoresources file was not created");

                var nrf = ReadNanoresources(outPath);
                Assert.AreEqual(1, nrf.resources.Length, "expected 1 resource");
                Assert.AreEqual(
                    ProcessResourceFiles.NanoResourceFile.ResourceHeader.RESOURCE_Binary,
                    nrf.resources[0].header.kind,
                    "resource kind should be RESOURCE_Binary");

                CollectionAssert.AreEqual(blob, nrf.resources[0].data, "binary blob content mismatch");
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [TestMethod]
        public void ReadWrite_BitmapFileRef_EmittedAsBitmapResource()
        {
            string dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            Directory.CreateDirectory(dir);
            try
            {
                // Write a minimal 1×1 pure-red BMP (dither-safe colour: no half-level boundaries)
                File.WriteAllBytes(Path.Combine(dir, "logo.bmp"), MakeOnePxBmp(r: 255, g: 0, b: 0));

                string resxBody = @"
  <data name=""LogoBitmap"" type=""System.Resources.ResXFileRef, System.Windows.Forms"">
    <value>logo.bmp;System.Byte[], mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value>
  </data>";

                string outPath = RunOnResx(dir, resxBody);

                Assert.IsTrue(File.Exists(outPath), "nanoresources file was not created");

                var nrf = ReadNanoresources(outPath);
                Assert.AreEqual(1, nrf.resources.Length, "expected 1 resource");
                Assert.AreEqual(
                    ProcessResourceFiles.NanoResourceFile.ResourceHeader.RESOURCE_Bitmap,
                    nrf.resources[0].header.kind,
                    "resource kind should be RESOURCE_Bitmap");
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [TestMethod]
        public void ReadWrite_DuplicateResourceName_OnlyOneIsEmitted()
        {
            // ResXResourceReader (net472) uses a Hashtable internally: the last value
            // for a duplicate key overwrites previous ones at the reader level, so only
            // one entry reaches AddResource. We verify the count is 1, not which value
            // wins (that is an implementation detail of the underlying reader).
            string dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            Directory.CreateDirectory(dir);
            try
            {
                string resxBody = @"
  <data name=""DupName""><value>first value</value></data>
  <data name=""DupName""><value>second value</value></data>";

                string outPath = RunOnResx(dir, resxBody);

                var nrf = ReadNanoresources(outPath);
                Assert.AreEqual(1, nrf.resources.Length, "duplicate should be deduplicated — expected 1 resource");
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [TestMethod]
        public void ReadWrite_StringAndBinaryMixed_BothEmittedWithCorrectKinds()
        {
            string dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            Directory.CreateDirectory(dir);
            try
            {
                byte[] blob = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
                File.WriteAllBytes(Path.Combine(dir, "blob.bin"), blob);

                string resxBody = @"
  <data name=""Title""><value>nanoFramework</value></data>
  <data name=""Blob"" type=""System.Resources.ResXFileRef, System.Windows.Forms"">
    <value>blob.bin;System.Byte[], mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value>
  </data>";

                string outPath = RunOnResx(dir, resxBody);

                var nrf = ReadNanoresources(outPath);
                Assert.AreEqual(2, nrf.resources.Length, "expected 2 resources");

                bool hasString = false, hasBinary = false;
                foreach (var res in nrf.resources)
                {
                    if (res.header.kind == ProcessResourceFiles.NanoResourceFile.ResourceHeader.RESOURCE_String)
                        hasString = true;
                    else if (res.header.kind == ProcessResourceFiles.NanoResourceFile.ResourceHeader.RESOURCE_Binary)
                        hasBinary = true;
                }

                Assert.IsTrue(hasString, "no RESOURCE_String found");
                Assert.IsTrue(hasBinary, "no RESOURCE_Binary found");
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [TestMethod]
        public void ReadWrite_SameInputs_ProduceDeterministicOutput()
        {
            // Regression guard: identical inputs must always produce byte-identical output.
            string dir1 = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            string dir2 = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            Directory.CreateDirectory(dir1);
            Directory.CreateDirectory(dir2);
            try
            {
                byte[] blob = new byte[] { 0x01, 0x02, 0x03, 0x04 };
                File.WriteAllBytes(Path.Combine(dir1, "data.bin"), blob);
                File.WriteAllBytes(Path.Combine(dir2, "data.bin"), blob);

                string resxBody = @"
  <data name=""AppName""><value>SmokeApp</value></data>
  <data name=""DataBlob"" type=""System.Resources.ResXFileRef, System.Windows.Forms"">
    <value>data.bin;System.Byte[], mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value>
  </data>";

                byte[] out1 = File.ReadAllBytes(RunOnResx(dir1, resxBody));
                byte[] out2 = File.ReadAllBytes(RunOnResx(dir2, resxBody));

                CollectionAssert.AreEqual(out1, out2, "identical inputs must produce identical output");
            }
            finally
            {
                Directory.Delete(dir1, true);
                Directory.Delete(dir2, true);
            }
        }
    }
}

#endif
