using System;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ZstdSeekable.Tests
{
    [TestClass]
    public class AutoDetectTests
    {
        static ZstdSeekableOptions SmallSpanOptions(CollectingProgress? progress = null) => new()
        {
            Index = new ZstdIndexOptions { TargetSpanBytes = 256 * 1024 },
            Progress = progress,
        };

        [TestMethod]
        public void SeekableFormatInputUsesTheSeekTable()
        {
            var data = TestData.CompressibleBytes(seed: 50, length: 1_000_000);
            var seekable = new MemoryStream(TestData.SeekableZstd(data, 64 * 1024));
            var progress = new CollectingProgress();

            using var stream = ZstdSeekableStream.OpenRead(seekable, SmallSpanOptions(progress));

            Assert.IsInstanceOfType(stream, typeof(ZstdSeekableReader), "input with a seek table must use the official mechanism");
            Assert.AreEqual(0, progress.Reports.Count, "no index build should have happened");
            TestData.AssertSame(TestData.Slice(data, 500_000, 10_000), TestData.ReadAt(stream, 500_000, 10_000), "read via auto-detect");
        }

        [TestMethod]
        public void StandardInputBuildsAndPersistsAnIndex()
        {
            var data = TestData.CompressibleBytes(seed: 51, length: 2_000_000);
            var compressed = new MemoryStream(TestData.StandardZstd(data));
            var indexStream = new MemoryStream();

            var firstProgress = new CollectingProgress();
            using (var first = ZstdSeekableStream.OpenRead(compressed, indexStream, SmallSpanOptions(firstProgress)))
            {
                Assert.IsInstanceOfType(first, typeof(ZstdIndexedStream));
                Assert.IsTrue(firstProgress.Reports.Any(r => r.Phase == ZstdIndexPhase.Scanning), "first open must build the index");
                TestData.AssertSame(TestData.Slice(data, 1_000_000, 5_000), TestData.ReadAt(first, 1_000_000, 5_000), "first open read");
            }

            //second open must load the persisted index, not rebuild
            compressed = new MemoryStream(TestData.StandardZstd(data));
            indexStream.Position = 0;
            var secondProgress = new CollectingProgress();
            using var second = ZstdSeekableStream.OpenRead(compressed, indexStream, SmallSpanOptions(secondProgress));
            Assert.AreEqual(0, secondProgress.Reports.Count, "second open must not rebuild");
            TestData.AssertSame(TestData.Slice(data, 1_500_000, 5_000), TestData.ReadAt(second, 1_500_000, 5_000), "second open read");
        }

        [TestMethod]
        public void FilePathsWithDefaultIndexPath()
        {
            var data = TestData.CompressibleBytes(seed: 52, length: 1_500_000);
            var dir = Path.Combine(Path.GetTempPath(), $"zstdseekable_test_{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try
            {
                var compressedPath = Path.Combine(dir, "data.zst");
                File.WriteAllBytes(compressedPath, TestData.StandardZstd(data));

                using (var stream = ZstdSeekableStream.OpenRead(compressedPath, indexPath: null, SmallSpanOptions()))
                {
                    TestData.AssertSame(TestData.Slice(data, 800_000, 4_000), TestData.ReadAt(stream, 800_000, 4_000), "read via file paths");
                }

                Assert.IsTrue(File.Exists(compressedPath + ".zsi"), "default index path must be <compressed>.zsi");
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        [TestMethod]
        public void HasSeekTableProbe()
        {
            var data = TestData.CompressibleBytes(seed: 53, length: 200_000);
            Assert.IsTrue(ZstdSeekableStream.HasSeekTable(new MemoryStream(TestData.SeekableZstd(data, 64 * 1024))));
            Assert.IsFalse(ZstdSeekableStream.HasSeekTable(new MemoryStream(TestData.StandardZstd(data))));
        }

        [TestMethod]
        public void NonSeekableCompressedInputIsRejected()
        {
            var inner = new MemoryStream(TestData.StandardZstd(TestData.RandomBytes(54, 1000)));
            Assert.ThrowsException<ArgumentException>(() => ZstdSeekableStream.OpenRead(new NonSeekableStream(inner)));
        }
    }
}
