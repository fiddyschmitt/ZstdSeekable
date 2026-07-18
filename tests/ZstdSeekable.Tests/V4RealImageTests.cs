using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ZstdSeekable.Tests
{
    /// <summary>
    /// The 0.4.0 exact-state index against real Clonezilla images (the IndexTests pattern on real
    /// data): every block boundary at the target spacing must be accepted (no drops), serving must
    /// be byte-exact, persistence must round-trip, and an interrupted build must resume to the
    /// identical index.
    /// </summary>
    [TestClass]
    public class V4RealImageTests
    {
        const string Sdb1 = @"E:\clonezilla-util-test resources\clonezilla images\2022-07-16-22-img_pb-devops1_zst\sdb1.ntfs-ptcl-img.zst";
        const string Sda1 = @"E:\clonezilla-util-test resources\clonezilla images\2022-07-16-22-img_pb-devops1_zst\sda1.ntfs-ptcl-img.zst";

        static readonly ZstdIndexOptions SpanOptions = new() { TargetSpanBytes = 8 * 1024 * 1024 };

        public TestContext TestContext { get; set; } = null!;

        void Report(string line)
        {
            TestContext.WriteLine(line);
            Console.WriteLine(line);
        }

        static byte[] DecodeAll(string path)
        {
            using var plain = new ZstdSharp.DecompressionStream(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20));
            using var ms = new MemoryStream();
            plain.CopyTo(ms, 1 << 20);
            return ms.ToArray();
        }

        static string Md5Hex(byte[] data)
        {
            using var md5 = MD5.Create();
            return string.Concat(md5.ComputeHash(data).Select(b => b.ToString("x2")));
        }

        static string SequentialMd5(Stream stream)
        {
            stream.Position = 0;
            using var md5 = MD5.Create();
            var buffer = new byte[1 << 20];
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                md5.TransformBlock(buffer, 0, read, null, 0);
            }
            md5.TransformFinalBlock([], 0, 0);
            return string.Concat(md5.Hash!.Select(b => b.ToString("x2")));
        }

        [DataTestMethod]
        [DataRow(Sdb1)]
        [DataRow(Sda1)]
        public void BuildServePersistAndResumeByteExact(string path)
        {
            if (!File.Exists(path)) Assert.Inconclusive($"Fixture not present: {path}");

            var reference = DecodeAll(path);
            var referenceMd5 = Md5Hex(reference);
            var span = SpanOptions.TargetSpanBytes;

            //---- build: every boundary at the spacing must be accepted (no drops) ----
            var buildTimer = Stopwatch.StartNew();
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
            using var index = ZstdIndex.Build(fs, SpanOptions);
            buildTimer.Stop();

            Assert.AreEqual(reference.Length, index.UncompressedLength, "index length");
            Assert.IsTrue(index.Points.Where(p => !p.IsFrameStart).All(p => p.IsExact), "all mid-frame points are exact");

            long maxGap = 0;
            for (var i = 1; i < index.Points.Count; i++)
                maxGap = Math.Max(maxGap, index.Points[i].UncompressedOffset - index.Points[i - 1].UncompressedOffset);
            maxGap = Math.Max(maxGap, index.UncompressedLength - index.Points[index.Points.Count - 1].UncompressedOffset);
            Assert.IsTrue(maxGap <= span + 256 * 1024,
                $"no boundary at the spacing may be dropped: maxGap {maxGap:N0} > span {span:N0} + block slack");
            var expectedFamily = reference.Length / span;
            Assert.IsTrue(index.Points.Count >= expectedFamily,
                $"point count {index.Points.Count} below the floor(len/span) family ({expectedFamily})");

            Report($"BUILD {Path.GetFileName(path)}: decoded={reference.Length:N0} md5={referenceMd5} " +
                   $"points={index.Points.Count} (exact={index.Points.Count(p => p.IsExact)}, frameStarts={index.Points.Count(p => p.IsFrameStart)}) " +
                   $"fills={index.FillSpans.Count} maxGap={maxGap:N0} buildSeconds={buildTimer.Elapsed.TotalSeconds:F1}");

            //---- serve: 200 random reads + every point-boundary straddle + sequential MD5 ----
            using (var stream = new ZstdIndexedStream(fs, index, leaveOpen: true))
            {
                Assert.AreEqual(reference.Length, stream.Length);

                var random = new Random(20260717);
                for (var i = 0; i < 200; i++)
                {
                    var offset = random.Next(reference.Length);
                    var count = random.Next(1, 200_000);
                    TestData.AssertSame(TestData.Slice(reference, offset, count), TestData.ReadAt(stream, offset, count), $"random {i} at {offset:N0}");
                }
                for (var p = 1; p < index.Points.Count; p++)
                {
                    var boundary = index.Points[p].UncompressedOffset;
                    var start = Math.Max(0, boundary - 5000);
                    TestData.AssertSame(TestData.Slice(reference, start, 10_000), TestData.ReadAt(stream, start, 10_000), $"straddle point {p} at {boundary:N0}");
                }
                Assert.AreEqual(referenceMd5, SequentialMd5(stream), "sequential MD5 through the index");
            }
            Report($"SERVE {Path.GetFileName(path)}: 200 random + {index.Points.Count - 1} straddles + sequential MD5 byte-exact");

            //---- persist / reload ----
            var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"zstdseekable_v4_{Guid.NewGuid():N}")).FullName;
            try
            {
                var indexPath = Path.Combine(dir, "image.zsi");
                using (var built = ZstdIndex.LoadOrBuild(fs, indexPath, SpanOptions))
                {
                    Assert.AreEqual(index.Points.Count, built.Points.Count, "LoadOrBuild point count");
                }
                var indexSize = new FileInfo(indexPath).Length;
                var writeTime = File.GetLastWriteTimeUtc(indexPath);

                using (var reloaded = ZstdIndex.LoadOrBuild(fs, indexPath, SpanOptions))
                {
                    Assert.AreEqual(writeTime, File.GetLastWriteTimeUtc(indexPath), "no rewrite on reload");
                    Assert.AreEqual(index.Points.Count, reloaded.Points.Count, "reloaded point count");
                    using var stream = new ZstdIndexedStream(fs, reloaded, leaveOpen: true);
                    var random = new Random(424242);
                    for (var i = 0; i < 50; i++)
                    {
                        var offset = random.Next(reference.Length);
                        var count = random.Next(1, 200_000);
                        TestData.AssertSame(TestData.Slice(reference, offset, count), TestData.ReadAt(stream, offset, count), $"reloaded {i}");
                    }
                }
                Report($"PERSIST {Path.GetFileName(path)}: indexBytes={indexSize:N0} ({100.0 * indexSize / reference.Length:F2}% of uncompressed), reload serves byte-exact");

                //---- interrupted build resumes to the identical index ----
                var interruptedPath = Path.Combine(dir, "interrupted.zsi");
                var failing = new FailingStream(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20), fs.Length / 2);
                Assert.ThrowsException<IOException>(() => ZstdIndex.LoadOrBuild(failing, interruptedPath, SpanOptions));
                Assert.IsTrue(File.Exists(interruptedPath + ".wip"), "interrupted build leaves its .wip");

                using (var resumed = ZstdIndex.LoadOrBuild(fs, interruptedPath, SpanOptions))
                {
                    Assert.IsFalse(File.Exists(interruptedPath + ".wip"), ".wip finalised into place");
                    Assert.AreEqual(index.Points.Count, resumed.Points.Count, "resumed build must produce the identical point set");
                    for (var i = 0; i < index.Points.Count; i++)
                    {
                        Assert.AreEqual(index.Points[i].UncompressedOffset, resumed.Points[i].UncompressedOffset, $"point {i} uOffset");
                        Assert.AreEqual(index.Points[i].CompressedOffset, resumed.Points[i].CompressedOffset, $"point {i} cOffset");
                        Assert.AreEqual(index.Points[i].IsFrameStart, resumed.Points[i].IsFrameStart, $"point {i} kind");
                    }
                    Assert.AreEqual(index.FillSpans.Count, resumed.FillSpans.Count, "resumed fill spans");

                    using var stream = new ZstdIndexedStream(fs, resumed, leaveOpen: true);
                    Assert.AreEqual(referenceMd5, SequentialMd5(stream), "resumed index sequential MD5");
                }
                Report($"RESUME {Path.GetFileName(path)}: interrupted at 50% compressed, resumed to an identical index, serves byte-exact");
            }
            finally
            {
                try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
            }
        }
    }
}
