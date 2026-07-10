using System;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ZstdSeekable.Tests
{
    /// <summary>A seekable stream that throws after a budget of bytes has been read - simulates a
    /// crash / lost connection midway through an index build.</summary>
    public sealed class FailingStream(Stream inner, long failAfterBytesRead) : Stream
    {
        long bytesRead;

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (bytesRead >= failAfterBytesRead) throw new IOException("Simulated interruption.");
            var n = inner.Read(buffer, offset, count);
            bytesRead += n;
            return n;
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
    public class IndexResumeTests
    {
        static readonly ZstdIndexOptions SmallSpans = new() { TargetSpanBytes = 256 * 1024 };

        static void AssertPointsEqual(ZstdIndex expected, ZstdIndex actual)
        {
            Assert.AreEqual(expected.UncompressedLength, actual.UncompressedLength);
            Assert.AreEqual(expected.Points.Count, actual.Points.Count, "resumed build should produce the identical point set");
            for (var i = 0; i < expected.Points.Count; i++)
            {
                Assert.AreEqual(expected.Points[i].UncompressedOffset, actual.Points[i].UncompressedOffset);
                Assert.AreEqual(expected.Points[i].CompressedOffset, actual.Points[i].CompressedOffset);
                Assert.AreEqual(expected.Points[i].IsFrameStart, actual.Points[i].IsFrameStart);
            }
        }

        static void AssertReadsMatch(byte[] data, Stream compressed, ZstdIndex index, int seed)
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
        public void InterruptedFileBuildResumesToAnIdenticalIndex()
        {
            var data = TestData.CompressibleBytes(seed: 70, length: 12_000_000);
            var zst = TestData.StandardZstd(data);
            var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"zstdseekable_test_{Guid.NewGuid():N}")).FullName;
            try
            {
                var referencePath = Path.Combine(dir, "reference.zsi");
                var interruptedPath = Path.Combine(dir, "interrupted.zsi");

                using var reference = ZstdIndex.LoadOrBuild(new MemoryStream(zst), referencePath, SmallSpans);

                //crash halfway through the compressed source
                var failing = new FailingStream(new MemoryStream(zst), zst.Length / 2);
                Assert.ThrowsException<IOException>(() => ZstdIndex.LoadOrBuild(failing, interruptedPath, SmallSpans));

                var wipPath = interruptedPath + ".wip";
                Assert.IsTrue(File.Exists(wipPath), "the interrupted build must leave its .wip behind");
                Assert.IsTrue(new FileInfo(wipPath).Length > 20, "sealed points should have been flushed before the crash");

                //an unfinalised index must not load...
                Assert.ThrowsException<InvalidDataException>(() => ZstdIndex.Load(wipPath));

                //...but LoadOrBuild resumes it
                using var resumed = ZstdIndex.LoadOrBuild(new MemoryStream(zst), interruptedPath, SmallSpans);
                Assert.IsFalse(File.Exists(wipPath), "the finalised index should have been moved into place");
                AssertPointsEqual(reference, resumed);
                AssertReadsMatch(data, new MemoryStream(zst), resumed, seed: 700);
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        [TestMethod]
        public void TruncatedWipTailIsToleratedOnResume()
        {
            var data = TestData.SparseBytes(seed: 71, length: 12_000_000);
            var zst = TestData.StandardZstd(data);
            var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"zstdseekable_test_{Guid.NewGuid():N}")).FullName;
            try
            {
                var indexPath = Path.Combine(dir, "index.zsi");
                var failing = new FailingStream(new MemoryStream(zst), zst.Length / 2);
                Assert.ThrowsException<IOException>(() => ZstdIndex.LoadOrBuild(failing, indexPath, SmallSpans));

                //chop a few bytes off the .wip: a write interrupted mid-record
                var wipPath = indexPath + ".wip";
                using (var wip = new FileStream(wipPath, FileMode.Open, FileAccess.ReadWrite))
                {
                    wip.SetLength(Math.Max(20, wip.Length - 7));
                }

                using var resumed = ZstdIndex.LoadOrBuild(new MemoryStream(zst), indexPath, SmallSpans);
                AssertReadsMatch(data, new MemoryStream(zst), resumed, seed: 710);
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        [TestMethod]
        public void InterruptedStreamBuildResumesInPlace()
        {
            var data = TestData.CompressibleBytes(seed: 72, length: 10_000_000);
            var zst = TestData.StandardZstd(data);

            using var referenceIndex = ZstdIndex.Build(new MemoryStream(zst), SmallSpans);

            var indexStream = new MemoryStream();
            var failing = new FailingStream(new MemoryStream(zst), zst.Length / 2);
            Assert.ThrowsException<IOException>(() => ZstdIndex.LoadOrBuild(failing, indexStream, SmallSpans));
            Assert.IsTrue(indexStream.Length > 20, "sealed points should have been flushed before the crash");

            indexStream.Position = 0;
            using var resumed = ZstdIndex.LoadOrBuild(new MemoryStream(zst), indexStream, SmallSpans);
            AssertPointsEqual(referenceIndex, resumed);
            AssertReadsMatch(data, new MemoryStream(zst), resumed, seed: 720);

            //and a third call just loads it (no rebuild)
            indexStream.Position = 0;
            var progress = new CollectingProgress();
            using var loaded = ZstdIndex.LoadOrBuild(new MemoryStream(zst), indexStream, SmallSpans, progress);
            Assert.AreEqual(0, progress.Reports.Count, "a finalised index must load without building");
        }

        [TestMethod]
        public void ResumeAgainstAMismatchedStreamRebuildsFromScratch()
        {
            var dataA = TestData.CompressibleBytes(seed: 73, length: 8_000_000);
            var dataB = TestData.CompressibleBytes(seed: 74, length: 8_000_000);    //different content
            var zstA = TestData.StandardZstd(dataA);
            var zstB = TestData.StandardZstd(dataB);
            var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"zstdseekable_test_{Guid.NewGuid():N}")).FullName;
            try
            {
                var indexPath = Path.Combine(dir, "index.zsi");
                var failing = new FailingStream(new MemoryStream(zstA), zstA.Length / 2);
                Assert.ThrowsException<IOException>(() => ZstdIndex.LoadOrBuild(failing, indexPath, SmallSpans));

                //resume with a DIFFERENT compressed stream: the partial index does not match it
                using var rebuilt = ZstdIndex.LoadOrBuild(new MemoryStream(zstB), indexPath, SmallSpans);
                Assert.AreEqual(dataB.Length, rebuilt.UncompressedLength);
                AssertReadsMatch(dataB, new MemoryStream(zstB), rebuilt, seed: 730);
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }
}
