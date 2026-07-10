using System;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ZstdSeekable.Tests
{
    /// <summary>
    /// Backward compatibility with the original "ZSTZRAN1" index format (ZstdSeekable 0.1.0 and
    /// older clonezilla-util). The committed fixture pair was produced by the 0.1.0 code: a 12 MB
    /// sparse/text stream (v1-compat.zst) and its v1 index with mid-frame window points
    /// (v1-compat.zsi). Reference bytes come from plainly decompressing the fixture, so these tests
    /// are self-contained.
    /// </summary>
    [TestClass]
    public class V1CompatTests
    {
        static string ZstPath => Path.Combine(AppContext.BaseDirectory, "Fixtures", "v1-compat.zst");
        static string ZsiPath => Path.Combine(AppContext.BaseDirectory, "Fixtures", "v1-compat.zsi");

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
                TestData.AssertSame(TestData.Slice(reference, offset, count), TestData.ReadAt(stream, offset, count), $"v1 read {i} at {offset:N0}");
            }
        }

        [TestMethod]
        public void V1FileLoadsAndReadsCorrectly()
        {
            var reference = Reference();
            using var index = ZstdIndex.Load(ZsiPath);

            Assert.AreEqual(reference.Length, index.UncompressedLength);
            Assert.IsTrue(index.Points.Any(p => !p.IsFrameStart), "the fixture must exercise v1 mid-frame windows");
            AssertReadsMatch(reference, index, seed: 80);
        }

        [TestMethod]
        public void V1LoadsFromSeekableAndNonSeekableStreams()
        {
            var reference = Reference();
            var bytes = File.ReadAllBytes(ZsiPath);

            using (var lazy = ZstdIndex.Load(new MemoryStream(bytes)))
            {
                AssertReadsMatch(reference, lazy, seed: 81);
            }

            using (var eager = ZstdIndex.Load(new NonSeekableStream(new MemoryStream(bytes))))
            {
                AssertReadsMatch(reference, eager, seed: 82);
            }
        }

        [TestMethod]
        public void V1IndexMigratesToV2OnSave()
        {
            var reference = Reference();
            using var v1 = ZstdIndex.Load(ZsiPath);

            var migrated = new MemoryStream();
            v1.Save(migrated);
            migrated.Position = 0;

            using var v2 = ZstdIndex.Load(migrated, leaveOpen: true);
            Assert.AreEqual(v1.Points.Count, v2.Points.Count);
            for (var i = 0; i < v1.Points.Count; i++)
            {
                Assert.AreEqual(v1.Points[i].UncompressedOffset, v2.Points[i].UncompressedOffset);
                Assert.AreEqual(v1.Points[i].CompressedOffset, v2.Points[i].CompressedOffset);
                Assert.AreEqual(v1.Points[i].IsFrameStart, v2.Points[i].IsFrameStart);
            }
            AssertReadsMatch(reference, v2, seed: 83);
        }
    }
}
