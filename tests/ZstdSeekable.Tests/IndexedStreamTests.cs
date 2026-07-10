using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ZstdSeekable.Tests
{
    [TestClass]
    public class IndexedStreamTests
    {
        static readonly ZstdIndexOptions SmallSpans = new() { TargetSpanBytes = 256 * 1024 };

        static void RandomReadFuzz(byte[] data, int seed, int reads = 300)
        {
            var compressed = new MemoryStream(TestData.StandardZstd(data));
            using var index = ZstdIndex.Build(compressed, SmallSpans);
            using var stream = new ZstdIndexedStream(compressed, index, leaveOpen: true);
            Assert.AreEqual(data.Length, stream.Length);

            var random = new Random(seed);
            for (var i = 0; i < reads; i++)
            {
                var offset = random.Next(data.Length + 10);
                var count = random.Next(1, 200_000);
                var expected = TestData.Slice(data, offset, count);
                var actual = TestData.ReadAt(stream, offset, count);
                TestData.AssertSame(expected, actual, $"read {i} at {offset:N0} x{count:N0}");
            }
        }

        [TestMethod]
        public void RandomReadsCompressible() => RandomReadFuzz(TestData.CompressibleBytes(seed: 40, length: 10_000_000), seed: 400);

        [TestMethod]
        public void RandomReadsSparse() => RandomReadFuzz(TestData.SparseBytes(seed: 41, length: 10_000_000), seed: 410);

        [TestMethod]
        public void RandomReadsIncompressible() => RandomReadFuzz(TestData.RandomBytes(seed: 42, length: 4_000_000), seed: 420, reads: 150);

        [TestMethod]
        public void SequentialFullRead()
        {
            var data = TestData.CompressibleBytes(seed: 43, length: 6_000_000);
            var compressed = new MemoryStream(TestData.StandardZstd(data));
            using var index = ZstdIndex.Build(compressed, SmallSpans);
            using var stream = new ZstdIndexedStream(compressed, index, leaveOpen: true);

            using var whole = new MemoryStream();
            stream.CopyTo(whole);
            TestData.AssertSame(data, whole.ToArray(), "sequential full read");
        }

        [TestMethod]
        public void ConcurrentViews()
        {
            var data = TestData.CompressibleBytes(seed: 44, length: 8_000_000);
            var compressed = new MemoryStream(TestData.StandardZstd(data));
            using var index = ZstdIndex.Build(compressed, SmallSpans);
            using var stream = new ZstdIndexedStream(compressed, index, leaveOpen: true);

            Parallel.For(0, 4, worker =>
            {
                using var view = stream.CreateView();
                var random = new Random(440 + worker);
                for (var i = 0; i < 30; i++)
                {
                    var offset = random.Next(data.Length);
                    var count = random.Next(1, 150_000);
                    var expected = TestData.Slice(data, offset, count);
                    var actual = TestData.ReadAt(view, offset, count);
                    TestData.AssertSame(expected, actual, $"view {worker} read {i} at {offset:N0}");
                }
            });
        }

        [TestMethod]
        public void MultiFrameSeeksLandOnFrameStarts()
        {
            var pieces = new[]
            {
                TestData.CompressibleBytes(seed: 45, length: 700_000),
                TestData.SparseBytes(seed: 46, length: 700_000),
                TestData.RandomBytes(seed: 47, length: 700_000),
            };
            var data = TestData.Concat(pieces);
            var compressed = new MemoryStream(TestData.MultiFrameZstd(pieces));
            using var index = ZstdIndex.Build(compressed, SmallSpans);
            using var stream = new ZstdIndexedStream(compressed, index, leaveOpen: true);

            //reads straddling the frame boundaries
            foreach (var boundary in new long[] { 700_000, 1_400_000 })
            {
                var expected = TestData.Slice(data, boundary - 5_000, 10_000);
                var actual = TestData.ReadAt(stream, boundary - 5_000, 10_000);
                TestData.AssertSame(expected, actual, $"read straddling {boundary:N0}");
            }
        }
    }
}
