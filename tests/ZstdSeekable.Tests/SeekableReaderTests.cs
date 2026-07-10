using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ZstdSeekable.Tests
{
    [TestClass]
    public class SeekableReaderTests
    {
        static void RandomReadFuzz(byte[] data, byte[] seekable, int seed, int reads = 300)
        {
            using var reader = new ZstdSeekableReader(new MemoryStream(seekable));
            Assert.AreEqual(data.Length, reader.Length);

            var random = new Random(seed);
            for (var i = 0; i < reads; i++)
            {
                var offset = random.Next(data.Length + 10);         //sometimes past EOF
                var count = random.Next(1, 200_000);
                var expected = TestData.Slice(data, offset, count);
                var actual = TestData.ReadAt(reader, offset, count);
                TestData.AssertSame(expected, actual, $"read {i} at {offset:N0} x{count:N0}");
            }
        }

        [DataTestMethod]
        [DataRow(4 * 1024)]
        [DataRow(64 * 1024)]
        [DataRow(1024 * 1024)]
        public void RandomReadsCompressible(int frameSize)
        {
            var data = TestData.CompressibleBytes(seed: 10, length: 3_000_000);
            RandomReadFuzz(data, TestData.SeekableZstd(data, frameSize), seed: 100 + frameSize);
        }

        [TestMethod]
        public void RandomReadsSparse()
        {
            var data = TestData.SparseBytes(seed: 11, length: 3_000_000);
            RandomReadFuzz(data, TestData.SeekableZstd(data, 128 * 1024), seed: 111);
        }

        [TestMethod]
        public void RandomReadsIncompressible()
        {
            var data = TestData.RandomBytes(seed: 12, length: 2_000_000);
            RandomReadFuzz(data, TestData.SeekableZstd(data, 128 * 1024), seed: 121);
        }

        [TestMethod]
        public void SequentialFullReadAndSeekOrigins()
        {
            var data = TestData.CompressibleBytes(seed: 13, length: 1_500_000);
            using var reader = new ZstdSeekableReader(new MemoryStream(TestData.SeekableZstd(data, 64 * 1024)));

            using var whole = new MemoryStream();
            reader.CopyTo(whole);
            TestData.AssertSame(data, whole.ToArray(), "sequential full read");

            reader.Seek(-1000, SeekOrigin.End);
            var tail = TestData.ReadAt(reader, reader.Position, 1000);
            TestData.AssertSame(TestData.Slice(data, data.Length - 1000, 1000), tail, "seek from End");

            reader.Position = 500;
            reader.Seek(250, SeekOrigin.Current);
            Assert.AreEqual(750, reader.Position);

            Assert.AreEqual(0, reader.Read(new byte[10], 0, 0), "zero-length read");
            reader.Position = data.Length;
            Assert.AreEqual(0, reader.Read(new byte[10], 0, 10), "read at EOF");
        }

        [TestMethod]
        public void ChecksumVerificationCatchesCorruption()
        {
            var data = TestData.CompressibleBytes(seed: 14, length: 500_000);
            var seekable = TestData.SeekableZstd(data, 64 * 1024);
            var table = ZstdSeekTable.Read(new MemoryStream(seekable));

            //flip one byte in the middle of the first frame's compressed payload - the decode itself
            //usually breaks, surfaced as InvalidDataException either way
            var corruptedData = (byte[])seekable.Clone();
            corruptedData[table.Entries[0].CompressedOffset + table.Entries[0].CompressedSize / 2] ^= 0xFF;
            using (var verifying = new ZstdSeekableReader(new MemoryStream(corruptedData), new ZstdSeekableReaderOptions { VerifyChecksums = true }))
            {
                Assert.ThrowsException<InvalidDataException>(() => verifying.Read(new byte[100], 0, 100));
            }

            //flip a bit in the STORED checksum instead: the frame decodes fine, so this exercises the
            //actual XXH32 comparison
            var entrySize = 12;     //with checksums
            var tableStart = seekable.Length - 8 - (table.Entries.Count * entrySize + 9);
            var corruptedChecksum = (byte[])seekable.Clone();
            corruptedChecksum[tableStart + 8 + 8] ^= 0x01;      //first entry's checksum field

            using (var verifying = new ZstdSeekableReader(new MemoryStream(corruptedChecksum), new ZstdSeekableReaderOptions { VerifyChecksums = true }))
            {
                Assert.ThrowsException<InvalidDataException>(() => verifying.Read(new byte[100], 0, 100));
            }

            //without verification the same file reads fine
            using (var lax = new ZstdSeekableReader(new MemoryStream(corruptedChecksum)))
            {
                TestData.AssertSame(TestData.Slice(data, 0, 100), TestData.ReadAt(lax, 0, 100), "read without checksum verification");
            }
        }

        [TestMethod]
        public void MalformedFootersAreRejected()
        {
            var data = TestData.CompressibleBytes(seed: 15, length: 100_000);
            var good = TestData.SeekableZstd(data, 64 * 1024);

            //wrong magic
            var wrongMagic = (byte[])good.Clone();
            wrongMagic[wrongMagic.Length - 1] ^= 0x01;
            Assert.IsFalse(ZstdSeekTable.TryRead(new MemoryStream(wrongMagic), out _));
            Assert.ThrowsException<InvalidDataException>(() => ZstdSeekTable.Read(new MemoryStream(wrongMagic)));

            //reserved descriptor bits set
            var reservedBits = (byte[])good.Clone();
            reservedBits[reservedBits.Length - 5] |= 0x40;
            Assert.IsFalse(ZstdSeekTable.TryRead(new MemoryStream(reservedBits), out _));

            //truncated table
            var truncated = new byte[good.Length - 20];
            Array.Copy(good, truncated, truncated.Length);
            Assert.IsFalse(ZstdSeekTable.TryRead(new MemoryStream(truncated), out _));

            //plain (non-seekable-format) zstd
            var plain = TestData.StandardZstd(data);
            Assert.IsFalse(ZstdSeekTable.TryRead(new MemoryStream(plain), out _));

            //position is restored by the probe
            var stream = new MemoryStream(good) { Position = 17 };
            ZstdSeekTable.TryRead(stream, out _);
            Assert.AreEqual(17, stream.Position);
        }

        [TestMethod]
        public void ConcurrentViews()
        {
            var data = TestData.CompressibleBytes(seed: 16, length: 2_000_000);
            using var reader = new ZstdSeekableReader(new MemoryStream(TestData.SeekableZstd(data, 64 * 1024)));

            Parallel.For(0, 4, worker =>
            {
                using var view = reader.CreateView();
                var random = new Random(1600 + worker);
                for (var i = 0; i < 50; i++)
                {
                    var offset = random.Next(data.Length);
                    var count = random.Next(1, 100_000);
                    var expected = TestData.Slice(data, offset, count);
                    var actual = TestData.ReadAt(view, offset, count);
                    TestData.AssertSame(expected, actual, $"view {worker} read {i} at {offset:N0}");
                }
            });
        }
    }
}
