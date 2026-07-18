using System;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ZstdSeekable.Tests
{
    /// <summary>
    /// Backward compatibility with the "ZSTZRAN3" index format (ZstdSeekable 0.3.x). The committed
    /// fixture pair was produced by the 0.3.0 builder (window-prefix points with insurance-verified
    /// spans, plus fill spans); those points must keep loading and serving via the window-prefix
    /// path under 0.4.0. Reference bytes come from plainly decompressing the fixture.
    /// </summary>
    [TestClass]
    public class V3CompatTests
    {
        static string ZstPath => Path.Combine(AppContext.BaseDirectory, "Fixtures", "v3-compat.zst");
        static string ZsiPath => Path.Combine(AppContext.BaseDirectory, "Fixtures", "v3-compat.zsi");

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
                TestData.AssertSame(TestData.Slice(reference, offset, count), TestData.ReadAt(stream, offset, count), $"v3 read {i} at {offset:N0}");
            }
        }

        [TestMethod]
        public void V3FileLoadsAndReadsCorrectly()
        {
            var reference = Reference();
            using var index = ZstdIndex.Load(ZsiPath);

            Assert.AreEqual(reference.Length, index.UncompressedLength);
            Assert.IsTrue(index.Points.Any(p => !p.IsFrameStart), "the fixture must exercise v3 mid-frame windows");
            Assert.IsTrue(index.Points.All(p => !p.IsExact), "v3 points are window-prefix, not exact");
            Assert.IsTrue(index.FillSpans.Count > 0, "the fixture must exercise v3 fill spans");
            AssertReadsMatch(reference, index, seed: 95);
        }

        [TestMethod]
        public void V3LoadsFromSeekableAndNonSeekableStreams()
        {
            var reference = Reference();
            var bytes = File.ReadAllBytes(ZsiPath);

            using (var lazy = ZstdIndex.Load(new MemoryStream(bytes)))
            {
                AssertReadsMatch(reference, lazy, seed: 96);
            }

            using (var eager = ZstdIndex.Load(new NonSeekableStream(new MemoryStream(bytes))))
            {
                AssertReadsMatch(reference, eager, seed: 97);
            }
        }

        [TestMethod]
        public void V3IndexMigratesToV4OnSave()
        {
            var reference = Reference();
            using var v3 = ZstdIndex.Load(ZsiPath);

            var migrated = new MemoryStream();
            v3.Save(migrated);
            migrated.Position = 0;

            using var v4 = ZstdIndex.Load(migrated, leaveOpen: true);
            Assert.AreEqual(v3.Points.Count, v4.Points.Count);
            Assert.AreEqual(v3.FillSpans.Count, v4.FillSpans.Count);
            Assert.IsTrue(v4.Points.All(p => !p.IsExact), "migrated window-prefix points stay window-prefix");
            AssertReadsMatch(reference, v4, seed: 98);
        }
    }
}
