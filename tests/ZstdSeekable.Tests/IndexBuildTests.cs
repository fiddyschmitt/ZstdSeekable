using System;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ZstdSeekable.Tests
{
    [TestClass]
    public class IndexBuildTests
    {
        static readonly ZstdIndexOptions SmallSpans = new() { TargetSpanBytes = 256 * 1024 };

        [TestMethod]
        public void MidFramePointsArePlantedInLargeCompressibleFrames()
        {
            //one giant frame (the Clonezilla shape); a candidate is confirmed after surviving one
            //whole span byte-identical, so verified mid-frame points land roughly every span
            var data = TestData.CompressibleBytes(seed: 20, length: 16_000_000);
            var compressed = new MemoryStream(TestData.StandardZstd(data));

            using var index = ZstdIndex.Build(compressed, SmallSpans);

            Assert.AreEqual(data.Length, index.UncompressedLength);
            Assert.IsTrue(index.Points[0].IsFrameStart && index.Points[0].UncompressedOffset == 0);
            Assert.IsTrue(index.Points.Count(p => !p.IsFrameStart) >= 10, $"expected many verified mid-frame resume points at 256 KiB spans over 16 MB, got {index.Points.Count(p => !p.IsFrameStart)}");
        }

        [TestMethod]
        public void EveryFrameStartBecomesAPoint()
        {
            var pieces = Enumerable.Range(0, 5).Select(i => TestData.CompressibleBytes(seed: 21 + i, length: 200_000)).ToArray();
            var compressed = new MemoryStream(TestData.MultiFrameZstd(pieces));

            using var index = ZstdIndex.Build(compressed, SmallSpans);

            Assert.AreEqual(pieces.Length, index.Points.Count(p => p.IsFrameStart));
            Assert.AreEqual(pieces.Sum(p => p.Length), index.UncompressedLength);
        }

        [TestMethod]
        public void SkippableFramesAreSkipped()
        {
            var dataA = TestData.CompressibleBytes(seed: 22, length: 300_000);
            var dataB = TestData.CompressibleBytes(seed: 23, length: 300_000);
            var compressed = new MemoryStream(TestData.Concat(
                TestData.StandardZstd(dataA),
                TestData.SkippableFrame(TestData.RandomBytes(seed: 24, length: 1000), magicVariant: 3),
                TestData.StandardZstd(dataB)));

            using var index = ZstdIndex.Build(compressed, SmallSpans);

            Assert.AreEqual(dataA.Length + dataB.Length, index.UncompressedLength);
            using var stream = new ZstdIndexedStream(compressed, index, leaveOpen: true);
            TestData.AssertSame(TestData.Concat(dataA, dataB), TestData.ReadAt(stream, 0, dataA.Length + dataB.Length), "read across skippable frame");
        }

        [TestMethod]
        public void SparseDataBuildsACorrectIndex()
        {
            //RLE/zero runs: the path the verify-and-heal pass exists for
            var data = TestData.SparseBytes(seed: 25, length: 12_000_000);
            var compressed = new MemoryStream(TestData.StandardZstd(data));

            using var index = ZstdIndex.Build(compressed, SmallSpans);
            using var stream = new ZstdIndexedStream(compressed, index, leaveOpen: true);

            var random = new Random(250);
            for (var i = 0; i < 100; i++)
            {
                var offset = random.Next(data.Length);
                var count = random.Next(1, 200_000);
                TestData.AssertSame(TestData.Slice(data, offset, count), TestData.ReadAt(stream, offset, count), $"sparse read {i} at {offset:N0}");
            }
        }

        [TestMethod]
        public void ProgressIsReportedInASingleInlineVerifiedPhase()
        {
            var data = TestData.CompressibleBytes(seed: 26, length: 8_000_000);
            var compressed = new MemoryStream(TestData.StandardZstd(data));
            var progress = new CollectingProgress();

            using var index = ZstdIndex.Build(compressed, SmallSpans, progress);

            var reports = progress.Reports;
            Assert.IsTrue(reports.Count > 0, "expected progress reports");
            Assert.IsTrue(reports.All(r => r.Phase == ZstdIndexPhase.Scanning), "verification is inline; Scanning is the only phase");
            Assert.IsTrue(reports.All(r => r.Fraction >= 0 && r.Fraction <= 1));
            Assert.AreEqual(1.0, reports[reports.Count - 1].Fraction, 1e-9, "final report should be 100%");
        }

        [TestMethod]
        public void FrameStartsOnlyFallbackModeProducesACorrectCoarseIndex()
        {
            //the mode the build degrades to if an insurance shadow ever diverged deeper than a span
            var options = new ZstdIndexOptions { TargetSpanBytes = 256 * 1024, FrameStartsOnly = true };

            var pieces = Enumerable.Range(0, 3).Select(i => TestData.CompressibleBytes(seed: 90 + i, length: 400_000)).ToArray();
            var multiFrame = new MemoryStream(TestData.MultiFrameZstd(pieces));
            using (var index = ZstdIndex.Build(multiFrame, options))
            {
                Assert.AreEqual(3, index.Points.Count);
                Assert.IsTrue(index.Points.All(p => p.IsFrameStart));
                using var stream = new ZstdIndexedStream(multiFrame, index, leaveOpen: true);
                var expected = TestData.Concat(pieces);
                TestData.AssertSame(TestData.Slice(expected, 500_000, 20_000), TestData.ReadAt(stream, 500_000, 20_000), "coarse read (multi-frame)");
            }

            var data = TestData.CompressibleBytes(seed: 93, length: 2_000_000);
            var singleFrame = new MemoryStream(TestData.StandardZstd(data));
            using (var index = ZstdIndex.Build(singleFrame, options))
            {
                Assert.AreEqual(1, index.Points.Count);     //one giant frame: worst case, still correct
                using var stream = new ZstdIndexedStream(singleFrame, index, leaveOpen: true);
                TestData.AssertSame(TestData.Slice(data, 1_500_000, 10_000), TestData.ReadAt(stream, 1_500_000, 10_000), "coarse read (single frame)");
            }
        }

        [TestMethod]
        public void CancellationAborts()
        {
            var data = TestData.CompressibleBytes(seed: 27, length: 8_000_000);
            var compressed = new MemoryStream(TestData.StandardZstd(data));

            using var cts = new CancellationTokenSource();
            cts.Cancel();
            Assert.ThrowsException<OperationCanceledException>(() => ZstdIndex.Build(compressed, SmallSpans, progress: null, cts.Token));
        }

        [TestMethod]
        public void NonSeekableCompressedInputIsRejected()
        {
            var compressed = new NonSeekableStream(new MemoryStream(TestData.StandardZstd(TestData.RandomBytes(28, 1000))));
            Assert.ThrowsException<ArgumentException>(() => ZstdIndex.Build(compressed));
        }

        [TestMethod]
        public void GarbageInputThrows()
        {
            var garbage = new MemoryStream(TestData.RandomBytes(seed: 29, length: 100_000));
            Assert.ThrowsException<InvalidDataException>(() => ZstdIndex.Build(garbage));
        }
    }
}
