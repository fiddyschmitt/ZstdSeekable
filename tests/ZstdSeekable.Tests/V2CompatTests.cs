using System;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ZstdSeekable.Tests
{
    /// <summary>
    /// Backward compatibility with the "ZSTZRAN2" index format (ZstdSeekable 0.2.x). The committed
    /// fixture pair was produced by the 0.2.0 code; reference bytes come from plainly decompressing
    /// the fixture, so these tests are self-contained.
    /// </summary>
    [TestClass]
    public class V2CompatTests
    {
        static string ZstPath => Path.Combine(AppContext.BaseDirectory, "Fixtures", "v2-compat.zst");
        static string ZsiPath => Path.Combine(AppContext.BaseDirectory, "Fixtures", "v2-compat.zsi");

        static byte[] Reference()
        {
            using var plain = new ZstdSharp.DecompressionStream(File.OpenRead(ZstPath));
            using var ms = new MemoryStream();
            plain.CopyTo(ms);
            return ms.ToArray();
        }

        static void AssertReadsMatch(byte[] reference, ZstdIndex index, int seed)
        {
            using var compressed = File.OpenRead(ZstPath);
            using var stream = new ZstdIndexedStream(compressed, index, leaveOpen: true);
            var random = new Random(seed);
            for (var i = 0; i < 100; i++)
            {
                var offset = random.Next(reference.Length);
                var count = random.Next(1, 200_000);
                TestData.AssertSame(TestData.Slice(reference, offset, count), TestData.ReadAt(stream, offset, count), $"v2 read {i} at {offset:N0}");
            }
        }

        [TestMethod]
        public void V2FileLoadsAndReadsCorrectly()
        {
            var reference = Reference();
            using var index = ZstdIndex.Load(ZsiPath);

            Assert.AreEqual(reference.Length, index.UncompressedLength);
            Assert.IsTrue(index.Points.Any(p => !p.IsFrameStart), "the fixture must exercise v2 mid-frame windows");
            Assert.AreEqual(0, index.FillSpans.Count, "v2 files carry no fill spans");
            AssertReadsMatch(reference, index, seed: 85);
        }

        [TestMethod]
        public void V2LoadsFromSeekableAndNonSeekableStreams()
        {
            var reference = Reference();
            var bytes = File.ReadAllBytes(ZsiPath);

            using (var lazy = ZstdIndex.Load(new MemoryStream(bytes)))
            {
                AssertReadsMatch(reference, lazy, seed: 86);
            }

            using (var eager = ZstdIndex.Load(new NonSeekableStream(new MemoryStream(bytes))))
            {
                AssertReadsMatch(reference, eager, seed: 87);
            }
        }

        [TestMethod]
        public void V2IndexMigratesToV3OnSave()
        {
            var reference = Reference();
            using var v2 = ZstdIndex.Load(ZsiPath);

            var migrated = new MemoryStream();
            v2.Save(migrated);
            migrated.Position = 0;

            using var v3 = ZstdIndex.Load(migrated, leaveOpen: true);
            Assert.AreEqual(v2.Points.Count, v3.Points.Count);
            for (var i = 0; i < v2.Points.Count; i++)
            {
                Assert.AreEqual(v2.Points[i].UncompressedOffset, v3.Points[i].UncompressedOffset);
                Assert.AreEqual(v2.Points[i].CompressedOffset, v3.Points[i].CompressedOffset);
                Assert.AreEqual(v2.Points[i].IsFrameStart, v3.Points[i].IsFrameStart);
            }
            AssertReadsMatch(reference, v3, seed: 88);
        }
    }
}
