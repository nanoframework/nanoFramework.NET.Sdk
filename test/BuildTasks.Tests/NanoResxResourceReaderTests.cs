//
// Copyright (c) .NET Foundation and Contributors
// See LICENSE file in the project root for full license information.
//

// NanoResxResourceReader is only compiled on non-Framework targets (see
// ProcessResourceFiles.CrossPlatform.cs). The tests here mirror that guard.
#if !NETFRAMEWORK

using System;
using System.IO;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using nanoFramework.Tools;

namespace nanoFramework.Tools.BuildTasks.Tests
{
    /// <summary>
    /// Unit tests for <see cref="NanoResxResourceReader"/>, the cross-platform
    /// (net8.0+) .resx parser used in place of
    /// <c>System.Resources.ResXResourceReader</c> (WinForms / net472 only).
    /// </summary>
    [TestClass]
    public class NanoResxResourceReaderTests
    {
        // ─── helpers ───────────────────────────────────────────────────────────────

        private const string ResxHeader = @"<?xml version=""1.0"" encoding=""utf-8""?>
<root>
  <resheader name=""resmimetype""><value>text/microsoft-resx</value></resheader>
  <resheader name=""version""><value>2.0</value></resheader>";

        private const string ResxFooter = "\n</root>";

        private static NanoResxResourceReader ParseResx(string resxBody, string? basePath = null)
        {
            string xml = ResxHeader + resxBody + ResxFooter;
            using var sr = new StringReader(xml);
            return new NanoResxResourceReader(sr, basePath ?? Path.GetTempPath());
        }

        private static System.Collections.Hashtable ToHashtable(NanoResxResourceReader reader)
        {
            var ht = new System.Collections.Hashtable();
            var en = reader.GetEnumerator();
            while (en.MoveNext())
                ht[en.Key] = en.Value;
            return ht;
        }

        // ─── inline string resources ───────────────────────────────────────────────

        [TestMethod]
        public void Parse_InlineString_ReturnsStringValue()
        {
            var reader = ParseResx(@"
  <data name=""Greeting""><value>Hello nanoFramework</value></data>");

            var resources = ToHashtable(reader);

            Assert.AreEqual(1, resources.Count);
            Assert.IsInstanceOfType(resources["Greeting"], typeof(string));
            Assert.AreEqual("Hello nanoFramework", (string)resources["Greeting"]!);
        }

        [TestMethod]
        public void Parse_MultipleInlineStrings_AllPresent()
        {
            var reader = ParseResx(@"
  <data name=""A""><value>alpha</value></data>
  <data name=""B""><value>beta</value></data>
  <data name=""C""><value>gamma</value></data>");

            var resources = ToHashtable(reader);

            Assert.AreEqual(3, resources.Count);
            Assert.AreEqual("alpha", resources["A"]);
            Assert.AreEqual("beta",  resources["B"]);
            Assert.AreEqual("gamma", resources["C"]);
        }

        [TestMethod]
        public void Parse_EmptyStringValue_ReturnsEmptyString()
        {
            var reader = ParseResx(@"
  <data name=""Empty""><value></value></data>");

            var resources = ToHashtable(reader);

            Assert.AreEqual(1, resources.Count);
            Assert.AreEqual(string.Empty, resources["Empty"]);
        }

        [TestMethod]
        public void Parse_MissingNameAttribute_EntryIsSkipped()
        {
            // An entry without a 'name' attribute should be silently ignored.
            var reader = ParseResx(@"
  <data><value>no name</value></data>
  <data name=""Valid""><value>ok</value></data>");

            var resources = ToHashtable(reader);

            Assert.AreEqual(1, resources.Count, "nameless entry should be skipped");
            Assert.IsTrue(resources.ContainsKey("Valid"));
        }

        // ─── inline base64 byte arrays ─────────────────────────────────────────────

        [TestMethod]
        public void Parse_InlineBase64ByteArray_ReturnsByteArray()
        {
            byte[] expected = new byte[] { 0x01, 0x02, 0x03, 0x04 };
            string base64 = Convert.ToBase64String(expected);

            var reader = ParseResx($@"
  <data name=""Blob"" mimetype=""application/x-microsoft.net.object.bytearray.base64"">
    <value>{base64}</value>
  </data>");

            var resources = ToHashtable(reader);

            Assert.AreEqual(1, resources.Count);
            Assert.IsInstanceOfType(resources["Blob"], typeof(byte[]));
            CollectionAssert.AreEqual(expected, (byte[])resources["Blob"]!);
        }

        [TestMethod]
        public void Parse_UnsupportedMimetype_ThrowsNotSupportedException()
        {
            Assert.ThrowsException<NotSupportedException>(() =>
            {
                var reader = ParseResx(@"
  <data name=""Obj"" mimetype=""application/x-microsoft.net.object.binary.base64"">
    <value>AQID</value>
  </data>");
                // the exception is thrown during construction (inside Parse)
            });
        }

        // ─── ResXFileRef (file references) ────────────────────────────────────────

        [TestMethod]
        public void Parse_FileRef_ByteArray_ReturnsByteArray()
        {
            string dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            Directory.CreateDirectory(dir);
            try
            {
                byte[] expected = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
                File.WriteAllBytes(Path.Combine(dir, "blob.bin"), expected);

                var reader = ParseResx(@"
  <data name=""Blob"" type=""System.Resources.ResXFileRef, System.Windows.Forms"">
    <value>blob.bin;System.Byte[], mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value>
  </data>", basePath: dir);

                var resources = ToHashtable(reader);

                Assert.AreEqual(1, resources.Count);
                Assert.IsInstanceOfType(resources["Blob"], typeof(byte[]));
                CollectionAssert.AreEqual(expected, (byte[])resources["Blob"]!);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [TestMethod]
        public void Parse_FileRef_String_ReturnsStringContent()
        {
            string dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            Directory.CreateDirectory(dir);
            try
            {
                const string content = "Hello from file";
                File.WriteAllText(Path.Combine(dir, "text.txt"), content, Encoding.UTF8);

                var reader = ParseResx(@"
  <data name=""FileText"" type=""System.Resources.ResXFileRef, System.Windows.Forms"">
    <value>text.txt;System.String, mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value>
  </data>", basePath: dir);

                var resources = ToHashtable(reader);

                Assert.AreEqual(1, resources.Count);
                Assert.IsInstanceOfType(resources["FileText"], typeof(string));
                Assert.AreEqual(content, (string)resources["FileText"]!);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [TestMethod]
        public void Parse_FileRef_AbsolutePath_Resolved()
        {
            string dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            Directory.CreateDirectory(dir);
            try
            {
                byte[] expected = new byte[] { 0xCA, 0xFE };
                string absPath = Path.Combine(dir, "abs.bin");
                File.WriteAllBytes(absPath, expected);

                // Use an absolute path in the file ref value
                string absPathEscaped = absPath.Replace("\\", "/");
                var reader = ParseResx($@"
  <data name=""Abs"" type=""System.Resources.ResXFileRef, System.Windows.Forms"">
    <value>{absPath};System.Byte[], mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value>
  </data>", basePath: null);

                var resources = ToHashtable(reader);

                Assert.AreEqual(1, resources.Count);
                CollectionAssert.AreEqual(expected, (byte[])resources["Abs"]!);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [TestMethod]
        public void Parse_UnsupportedType_ThrowsNotSupportedException()
        {
            // A resource with an explicit non-file-ref, non-string type is unsupported.
            Assert.ThrowsException<NotSupportedException>(() =>
            {
                var reader = ParseResx(@"
  <data name=""Icon"" type=""System.Drawing.Icon, System.Drawing"">
    <value>AQID</value>
  </data>");
            });
        }

        [TestMethod]
        public void Parse_ResxHeaderEntries_Ignored()
        {
            // <resheader> nodes must not appear in the resource enumeration.
            var reader = ParseResx(@"
  <data name=""MyString""><value>hello</value></data>");

            var resources = ToHashtable(reader);

            // Only the one <data> entry — resheader nodes not counted
            Assert.AreEqual(1, resources.Count);
            Assert.IsTrue(resources.ContainsKey("MyString"));
        }
    }
}

#endif
