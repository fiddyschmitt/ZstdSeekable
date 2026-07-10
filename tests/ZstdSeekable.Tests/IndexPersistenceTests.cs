using System;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ZstdSeekable.Tests
{
    [TestClass]
    public class IndexPersistenceTests
    {
        static readonly ZstdIndexOptions SmallSpans = new() { TargetSpanBytes = 256 * 1024 };

        static (byte[] Data, MemoryStream Compressed) MakeInput(int seed, int length = 10_000_000)
        {
            var data = TestData.CompressibleBytes(seed, length);
            return (data, new MemoryStream(TestData.StandardZstd(data)));
        }

        static void AssertIndexesEqual(ZstdIndex expected, ZstdIndex actual)
        {
            Assert.AreEqual(expected.UncompressedLength, actual.UncompressedLength);
            Assert.AreEqual(expected.Points.Count, actual.Points.Count);
            for (var i = 0; i < expected.Points.Count; i++)
            {
                Assert.AreEqual(expected.Points[i].UncompressedOffset, actual.Points[i].UncompressedOffset);
                Assert.AreEqual(expected.Points[i].CompressedOffset, actual.Points[i].CompressedOffset);
                Assert.AreEqual(expected.Points[i].IsFrameStart, actual.Points[i].IsFrameStart);
            }
        }

        static void AssertReadsMatch(byte[] data, MemoryStream compressed, ZstdIndex index, int seed)
        {
            using var stream = new ZstdIndexedStream(compressed, index, leaveOpen: true);
            var random = new Random(seed);
            for (var i = 0; i < 50; i++)
            {
                var offset = random.Next(data.Length);
                var count = random.Next(1, 200_000);
                TestData.AssertSame(TestData.Slice(data, offset, count), TestData.ReadAt(stream, offset, count), $"read {i} at {offset:N0}");
            }
        }

        [TestMethod]
        public void FileRoundTrip()
        {
            var (data, compressed) = MakeInput(seed: 30);
            var indexPath = Path.Combine(Path.GetTempPath(), $"zstdseekable_test_{Guid.NewGuid():N}.zsi");
            try
            {
                using var built = ZstdIndex.Build(compressed, SmallSpans);
                built.Save(indexPath);

                using var loaded = ZstdIndex.Load(indexPath);
                AssertIndexesEqual(built, loaded);
                AssertReadsMatch(data, compressed, loaded, seed: 300);
            }
            finally
            {
                File.Delete(indexPath);
            }
        }

        [TestMethod]
        public void SeekableStreamRoundTripIsLazy()
        {
            var (data, compressed) = MakeInput(seed: 31);
            using var built = ZstdIndex.Build(compressed, SmallSpans);

            var indexStream = new MemoryStream();
            built.Save(indexStream);
            indexStream.Position = 0;

            using var loaded = ZstdIndex.Load(indexStream, leaveOpen: true);
            AssertIndexesEqual(built, loaded);
            AssertReadsMatch(data, compressed, loaded, seed: 310);
        }

        [TestMethod]
        public void NonSeekableStreamLoadsEagerly()
        {
            var (data, compressed) = MakeInput(seed: 32);
            using var built = ZstdIndex.Build(compressed, SmallSpans);

            var indexStream = new MemoryStream();
            built.Save(indexStream);
            indexStream.Position = 0;

            using var loaded = ZstdIndex.Load(new NonSeekableStream(indexStream));
            indexStream.Dispose();      //eager load must not need the stream afterwards

            AssertIndexesEqual(built, loaded);
            AssertReadsMatch(data, compressed, loaded, seed: 320);
        }

        [TestMethod]
        public void LoadAtNonZeroStreamOffset()
        {
            var (data, compressed) = MakeInput(seed: 33);
            using var built = ZstdIndex.Build(compressed, SmallSpans);

            var indexStream = new MemoryStream();
            var padding = TestData.RandomBytes(seed: 330, length: 137);
            indexStream.Write(padding, 0, padding.Length);
            built.Save(indexStream);

            indexStream.Position = padding.Length;
            using var loaded = ZstdIndex.Load(indexStream, leaveOpen: true);
            AssertIndexesEqual(built, loaded);
            AssertReadsMatch(data, compressed, loaded, seed: 331);
        }

        [TestMethod]
        public void CorruptIndexesAreRejected()
        {
            var (_, compressed) = MakeInput(seed: 34, length: 2_000_000);
            using var built = ZstdIndex.Build(compressed, SmallSpans);
            var ms = new MemoryStream();
            built.Save(ms);
            var good = ms.ToArray();

            //bad magic
            var badMagic = (byte[])good.Clone();
            badMagic[0] ^= 0xFF;
            Assert.ThrowsException<InvalidDataException>(() => ZstdIndex.Load(new MemoryStream(badMagic)));

            //truncated records
            var truncated = new byte[40];
            Array.Copy(good, truncated, truncated.Length);
            Assert.ThrowsException<EndOfStreamException>(() => ZstdIndex.Load(new MemoryStream(truncated)));
        }

        [TestMethod]
        public void LoadOrBuildRebuildsOverCorruptFile()
        {
            var (data, compressed) = MakeInput(seed: 35, length: 2_000_000);
            var indexPath = Path.Combine(Path.GetTempPath(), $"zstdseekable_test_{Guid.NewGuid():N}.zsi");
            try
            {
                File.WriteAllBytes(indexPath, TestData.RandomBytes(seed: 350, length: 500));

                using var index = ZstdIndex.LoadOrBuild(compressed, indexPath, SmallSpans);
                AssertReadsMatch(data, compressed, index, seed: 351);

                //the rebuilt file is now valid
                using var reloaded = ZstdIndex.Load(indexPath);
                AssertIndexesEqual(index, reloaded);
            }
            finally
            {
                File.Delete(indexPath);
            }
        }

        [TestMethod]
        public void LoadOrBuildOnStreamBuildsOnceThenLoads()
        {
            var (data, compressed) = MakeInput(seed: 36, length: 2_000_000);
            var indexStream = new MemoryStream();

            var firstProgress = new CollectingProgress();
            using (var first = ZstdIndex.LoadOrBuild(compressed, indexStream, SmallSpans, firstProgress))
            {
                Assert.IsTrue(firstProgress.Reports.Any(r => r.Phase == ZstdIndexPhase.Scanning), "first call must build");
                AssertReadsMatch(data, compressed, first, seed: 360);
            }

            indexStream.Position = 0;
            var secondProgress = new CollectingProgress();
            using var second = ZstdIndex.LoadOrBuild(compressed, indexStream, SmallSpans, secondProgress);
            Assert.AreEqual(0, secondProgress.Reports.Count, "second call must load, not build");
            AssertReadsMatch(data, compressed, second, seed: 361);
        }
    }
}
