using System;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ZstdSeekable.Internal;

namespace ZstdSeekable.Tests
{
    [TestClass]
    public class SeekableWriterTests
    {
        [TestMethod]
        public void OutputDecodesWithAnyZstdDecompressor()
        {
            var data = TestData.CompressibleBytes(seed: 1, length: 3_500_000);
            var seekable = TestData.SeekableZstd(data, maxFrameSize: 256 * 1024);

            //a standard decoder must read seekable-format files (the seek table is a skippable frame)
            using var plain = new ZstdSharp.DecompressionStream(new MemoryStream(seekable));
            using var decoded = new MemoryStream();
            plain.CopyTo(decoded);

            TestData.AssertSame(data, decoded.ToArray(), "plain zstd decode of seekable output");
        }

        [TestMethod]
        public void SeekTableFieldsAreCorrect()
        {
            var data = TestData.CompressibleBytes(seed: 2, length: 1_000_000);
            const int frameSize = 128 * 1024;
            var seekable = TestData.SeekableZstd(data, frameSize);

            var table = ZstdSeekTable.Read(new MemoryStream(seekable));

            var expectedFrames = (data.Length + frameSize - 1) / frameSize;
            Assert.AreEqual(expectedFrames, table.Entries.Count);
            Assert.IsTrue(table.HasChecksums);
            Assert.AreEqual(data.Length, table.UncompressedLength);

            //every checksum matches an independent XXH32 of the corresponding plaintext slice
            foreach (var entry in table.Entries)
            {
                var slice = TestData.Slice(data, entry.UncompressedOffset, (int)entry.UncompressedSize);
                Assert.AreEqual(XxHash32.Hash(slice), entry.Checksum, $"checksum of frame at {entry.UncompressedOffset:N0}");
            }

            //offsets are cumulative and gap-free
            long compressedOffset = 0, uncompressedOffset = 0;
            foreach (var entry in table.Entries)
            {
                Assert.AreEqual(compressedOffset, entry.CompressedOffset);
                Assert.AreEqual(uncompressedOffset, entry.UncompressedOffset);
                compressedOffset += entry.CompressedSize;
                uncompressedOffset += entry.UncompressedSize;
            }
        }

        [TestMethod]
        public void NoChecksumsVariant()
        {
            var data = TestData.RandomBytes(seed: 3, length: 300_000);
            var seekable = TestData.SeekableZstd(data, maxFrameSize: 64 * 1024, checksums: false);

            var table = ZstdSeekTable.Read(new MemoryStream(seekable));
            Assert.IsFalse(table.HasChecksums);
            Assert.IsTrue(table.Entries.All(e => e.Checksum == 0));
            Assert.AreEqual(data.Length, table.UncompressedLength);
        }

        [TestMethod]
        public void EmptyInputYieldsValidEmptySeekableFile()
        {
            var seekable = TestData.SeekableZstd([], maxFrameSize: 64 * 1024);

            var table = ZstdSeekTable.Read(new MemoryStream(seekable));
            Assert.AreEqual(0, table.Entries.Count);
            Assert.AreEqual(0L, table.UncompressedLength);

            using var plain = new ZstdSharp.DecompressionStream(new MemoryStream(seekable));
            using var decoded = new MemoryStream();
            plain.CopyTo(decoded);
            Assert.AreEqual(0L, decoded.Length);
        }

        [DataTestMethod]
        [DataRow(64 * 1024 - 1)]
        [DataRow(64 * 1024)]
        [DataRow(64 * 1024 + 1)]
        public void FrameBoundaryEdges(int dataLength)
        {
            var data = TestData.CompressibleBytes(seed: 4, length: dataLength);
            var seekable = TestData.SeekableZstd(data, maxFrameSize: 64 * 1024);

            var table = ZstdSeekTable.Read(new MemoryStream(seekable));
            Assert.AreEqual(dataLength <= 64 * 1024 ? 1 : 2, table.Entries.Count);
            Assert.AreEqual(dataLength, table.UncompressedLength);
        }

        [TestMethod]
        public void FinishIsIdempotentAndWriteAfterFinishThrows()
        {
            using var ms = new MemoryStream();
            var writer = new ZstdSeekableWriter(ms, leaveOpen: true);
            writer.Write(new byte[100], 0, 100);
            writer.Finish();
            var lengthAfterFinish = ms.Length;
            writer.Finish();
            Assert.AreEqual(lengthAfterFinish, ms.Length, "second Finish must write nothing");

            Assert.ThrowsException<InvalidOperationException>(() => writer.Write(new byte[1], 0, 1));
            writer.Dispose();
            Assert.AreEqual(lengthAfterFinish, ms.Length, "Dispose after Finish must write nothing");
        }

        [TestMethod]
        public void InvalidFrameSizeRejected()
        {
            using var ms = new MemoryStream();
            Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
                new ZstdSeekableWriter(ms, new ZstdSeekableWriterOptions { MaxFrameSize = 100 }));
        }
    }
}
