using System;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ZstdSeekable.Tests
{
    /// <summary>Counts Read calls, to prove the fill-span fast path never touches the compressed stream.</summary>
    public sealed class CountingStream(Stream inner) : Stream
    {
        public int ReadCalls { get; private set; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ReadCalls++;
            return inner.Read(buffer, offset, count);
        }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [TestClass]
    public class FillSpanTests
    {
        static readonly ZstdIndexOptions Options = new() { TargetSpanBytes = 256 * 1024, FillSpanThreshold = 256 * 1024 };

        static void AssertSpansMatchReference(byte[] reference, ZstdIndex index)
        {
            long previousEnd = 0;
            foreach (var span in index.FillSpans)
            {
                Assert.IsTrue(span.UncompressedOffset >= previousEnd, "spans must be sorted and non-overlapping");
                Assert.IsTrue(span.Length > 0 && span.UncompressedOffset + span.Length <= reference.Length, "spans must be in-bounds");
                for (var i = span.UncompressedOffset; i < span.UncompressedOffset + span.Length; i++)
                {
                    if (reference[i] != span.FillByte)
                        Assert.Fail($"span at {span.UncompressedOffset:N0} claims 0x{span.FillByte:X2} but reference[{i:N0}] is 0x{reference[i]:X2}");
                }
                previousEnd = span.UncompressedOffset + span.Length;
            }
        }

        static void FuzzReads(byte[] reference, ZstdIndexedStream stream, int seed, int reads = 200)
        {
            var random = new Random(seed);
            for (var i = 0; i < reads; i++)
            {
                var offset = random.Next(reference.Length);
                var count = random.Next(1, 200_000);
                TestData.AssertSame(TestData.Slice(reference, offset, count), TestData.ReadAt(stream, offset, count), $"read {i} at {offset:N0}");
            }
        }

        [TestMethod]
        public void ZeroRunsAreRecordedAndReadsAreCorrect()
        {
            var data = TestData.SparseBytes(seed: 100, length: 10_000_000);
            var compressed = new MemoryStream(TestData.StandardZstd(data));
            using var index = ZstdIndex.Build(compressed, Options);

            Assert.IsTrue(index.FillSpans.Count > 0, "sparse data must yield fill spans");
            Assert.IsTrue(index.FillSpans.All(s => s.FillByte == 0));
            AssertSpansMatchReference(data, index);

            using var stream = new ZstdIndexedStream(compressed, index, leaveOpen: true);
            FuzzReads(data, stream, seed: 1000);

            //reads straddling a span boundary
            var span = index.FillSpans[index.FillSpans.Count / 2];
            foreach (var edge in new[] { span.UncompressedOffset, span.UncompressedOffset + span.Length })
            {
                var start = Math.Max(0, edge - 5_000);
                TestData.AssertSame(TestData.Slice(data, start, 10_000), TestData.ReadAt(stream, start, 10_000), $"straddle at {edge:N0}");
            }
        }

        [TestMethod]
        public void FfDesertsAreRecorded()
        {
            //erased-flash shape: long 0xFF runs with compressible islands
            var data = TestData.FillRunBytes(seed: 101, length: 8_000_000, fillByte: 0xFF);
            var compressed = new MemoryStream(TestData.StandardZstd(data));
            using var index = ZstdIndex.Build(compressed, Options);

            Assert.IsTrue(index.FillSpans.Count > 0, "0xFF deserts must yield fill spans");
            Assert.IsTrue(index.FillSpans.All(s => s.FillByte == 0xFF));
            AssertSpansMatchReference(data, index);

            using var stream = new ZstdIndexedStream(compressed, index, leaveOpen: true);
            FuzzReads(data, stream, seed: 1010);
        }

        [TestMethod]
        public void RunsBelowTheThresholdAreNotRecorded()
        {
            //zero runs of ~100 KB only: below the 256 KiB minimum threshold
            var random = new Random(102);
            var data = new byte[4_000_000];
            var pos = 0;
            while (pos < data.Length)
            {
                pos += random.Next(80_000, 120_000);    //short zero run
                if (pos >= data.Length) break;
                var islandEnd = Math.Min(pos + random.Next(30_000, 60_000), data.Length);
                while (pos < islandEnd) data[pos++] = (byte)random.Next(1, 256);
            }

            var compressed = new MemoryStream(TestData.StandardZstd(data));
            using var index = ZstdIndex.Build(compressed, Options);

            Assert.AreEqual(0, index.FillSpans.Count, "no run reaches the threshold");
            using var stream = new ZstdIndexedStream(compressed, index, leaveOpen: true);
            FuzzReads(data, stream, seed: 1020, reads: 50);
        }

        [TestMethod]
        public void FillSpansSurvivePersistence()
        {
            var data = TestData.SparseBytes(seed: 103, length: 8_000_000);
            var compressed = new MemoryStream(TestData.StandardZstd(data));
            using var built = ZstdIndex.Build(compressed, Options);
            Assert.IsTrue(built.FillSpans.Count > 0);

            void AssertFillsEqual(ZstdIndex actual)
            {
                Assert.AreEqual(built.FillSpans.Count, actual.FillSpans.Count);
                for (var i = 0; i < built.FillSpans.Count; i++)
                {
                    Assert.AreEqual(built.FillSpans[i].UncompressedOffset, actual.FillSpans[i].UncompressedOffset);
                    Assert.AreEqual(built.FillSpans[i].Length, actual.FillSpans[i].Length);
                    Assert.AreEqual(built.FillSpans[i].FillByte, actual.FillSpans[i].FillByte);
                }
            }

            var indexPath = Path.Combine(Path.GetTempPath(), $"zstdseekable_test_{Guid.NewGuid():N}.zsi");
            try
            {
                built.Save(indexPath);
                using var fromFile = ZstdIndex.Load(indexPath);
                AssertFillsEqual(fromFile);
            }
            finally
            {
                File.Delete(indexPath);
            }

            var ms = new MemoryStream();
            built.Save(ms);
            ms.Position = 0;
            using var fromStream = ZstdIndex.Load(ms, leaveOpen: true);
            AssertFillsEqual(fromStream);

            ms.Position = 0;
            using var fromNonSeekable = ZstdIndex.Load(new NonSeekableStream(ms));
            AssertFillsEqual(fromNonSeekable);
        }

        [TestMethod]
        public void ReadsInsideFillSpansNeverTouchTheCompressedStream()
        {
            var data = TestData.SparseBytes(seed: 104, length: 8_000_000);
            var compressedBytes = TestData.StandardZstd(data);
            using var index = ZstdIndex.Build(new MemoryStream(compressedBytes), Options);
            var largest = index.FillSpans.OrderByDescending(s => s.Length).First();

            var counting = new CountingStream(new MemoryStream(compressedBytes));
            using var stream = new ZstdIndexedStream(counting, index, leaveOpen: true);

            var interior = TestData.ReadAt(stream, largest.UncompressedOffset + 10, (int)Math.Min(100_000, largest.Length - 20));
            Assert.IsTrue(interior.All(b => b == largest.FillByte));
            Assert.AreEqual(0, counting.ReadCalls, "a fill-span read must not touch the compressed stream");

            //sanity: a read outside fill spans does decode
            var nonFill = index.Points[0].UncompressedOffset;
            TestData.ReadAt(stream, nonFill, 1000);
            Assert.IsTrue(counting.ReadCalls > 0);
        }

        [TestMethod]
        public void ResumedBuildsProduceIdenticalFillSpans()
        {
            var data = TestData.SparseBytes(seed: 105, length: 12_000_000);
            var zst = TestData.StandardZstd(data);
            var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"zstdseekable_test_{Guid.NewGuid():N}")).FullName;
            try
            {
                using var reference = ZstdIndex.LoadOrBuild(new MemoryStream(zst), Path.Combine(dir, "reference.zsi"), Options);
                Assert.IsTrue(reference.FillSpans.Count > 0);

                var interruptedPath = Path.Combine(dir, "interrupted.zsi");
                var failing = new FailingStream(new MemoryStream(zst), zst.Length / 2);
                Assert.ThrowsException<IOException>(() => ZstdIndex.LoadOrBuild(failing, interruptedPath, Options));

                using var resumed = ZstdIndex.LoadOrBuild(new MemoryStream(zst), interruptedPath, Options);
                Assert.AreEqual(reference.FillSpans.Count, resumed.FillSpans.Count, "resume must neither drop nor duplicate fill spans");
                for (var i = 0; i < reference.FillSpans.Count; i++)
                {
                    Assert.AreEqual(reference.FillSpans[i].UncompressedOffset, resumed.FillSpans[i].UncompressedOffset);
                    Assert.AreEqual(reference.FillSpans[i].Length, resumed.FillSpans[i].Length);
                    Assert.AreEqual(reference.FillSpans[i].FillByte, resumed.FillSpans[i].FillByte);
                }

                using var stream = new ZstdIndexedStream(new MemoryStream(zst), resumed, leaveOpen: true);
                FuzzReads(data, stream, seed: 1050, reads: 100);
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }
}
