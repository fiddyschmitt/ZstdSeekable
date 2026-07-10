using System;
using System.IO;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ZstdSeekable.Tests
{
    /// <summary>
    /// Cross-implementation checks against a committed fixture produced by pyzstd's
    /// SeekableZstdFile (an independent C implementation of the official seekable format) - see
    /// Fixtures\pyzstd-seekable.zst. Guards against a mirrored spec misunderstanding shared by our
    /// writer and our parser, which same-suite round-trips cannot catch.
    /// </summary>
    [TestClass]
    public class SeekableInteropTests
    {
        const int LineCount = 40000;
        const int MaxFrameContentSize = 131072;     //the max_frame_content_size pyzstd was given

        static string FixturePath => Path.Combine(AppContext.BaseDirectory, "Fixtures", "pyzstd-seekable.zst");

        /// <summary>Byte-for-byte reproduction of what make_fixture.py compressed:
        /// b"Line %07d of the ZstdSeekable interop fixture.\n" for 0..39999.</summary>
        static byte[] FixtureContent()
        {
            var sb = new StringBuilder(LineCount * 50);
            for (var i = 0; i < LineCount; i++)
            {
                sb.Append("Line ").Append(i.ToString("D7")).Append(" of the ZstdSeekable interop fixture.\n");
            }
            return Encoding.ASCII.GetBytes(sb.ToString());
        }

        [TestMethod]
        public void ParsesThirdPartySeekTable()
        {
            using var fs = File.OpenRead(FixturePath);
            Assert.IsTrue(ZstdSeekableStream.HasSeekTable(fs));

            var table = ZstdSeekTable.Read(fs);
            var expectedContent = FixtureContent();

            Assert.AreEqual(16, table.Entries.Count);           //ceil(2,000,000 / 131,072)
            Assert.IsFalse(table.HasChecksums);                 //pyzstd's default
            Assert.AreEqual(expectedContent.Length, table.UncompressedLength);

            foreach (var entry in table.Entries)
            {
                Assert.IsTrue(entry.UncompressedSize <= MaxFrameContentSize, $"frame at {entry.UncompressedOffset:N0} exceeds pyzstd's max frame size");
            }
        }

        [TestMethod]
        public void ReadsThirdPartyFileCorrectly()
        {
            var expected = FixtureContent();
            using var reader = ZstdSeekableReader.Open(FixturePath);
            Assert.AreEqual(expected.Length, reader.Length);

            //full sequential read
            using var whole = new MemoryStream();
            reader.CopyTo(whole);
            TestData.AssertSame(expected, whole.ToArray(), "sequential read of pyzstd file");

            //seeded random seeks
            var random = new Random(60);
            for (var i = 0; i < 100; i++)
            {
                var offset = random.Next(expected.Length);
                var count = random.Next(1, 100_000);
                TestData.AssertSame(TestData.Slice(expected, offset, count), TestData.ReadAt(reader, offset, count), $"pyzstd read {i} at {offset:N0}");
            }
        }

        [TestMethod]
        public void AutoDetectUsesTheThirdPartySeekTable()
        {
            var progress = new CollectingProgress();
            using var stream = ZstdSeekableStream.OpenRead(FixturePath, new ZstdSeekableOptions { Progress = progress });

            Assert.IsInstanceOfType(stream, typeof(ZstdSeekableReader));
            Assert.AreEqual(0, progress.Reports.Count, "no index build for a file with a seek table");

            var expected = FixtureContent();
            TestData.AssertSame(TestData.Slice(expected, 1_000_000, 47), TestData.ReadAt(stream, 1_000_000, 47), "auto-detect read of pyzstd file");
        }
    }
}
