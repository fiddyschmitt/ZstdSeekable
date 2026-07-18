using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ZstdSeekable.Tests
{
    /// <summary>
    /// The 0.4.0 acceptance run on the image that broke 0.3.0: the raw ddrescue sda drive image
    /// (2.2 TB decoded), whose dense data at uncompressed offset 5,860,098,048 made the old
    /// window-prefix insurance shadow diverge and degrade the index to frame-start-only points.
    /// With exact state there is no divergence class: the build must complete with every boundary
    /// at the spacing accepted, dense THROUGH that offset, and spot reads must be byte-exact.
    /// Long-running (full decode of the whole image) - run detached, never in a default test pass.
    /// </summary>
    [TestClass]
    public class V4BigImageTest
    {
        const string BigImage = @"E:\clonezilla-util-test resources\drive images\ddrescue backups (even includes deleted)\2021-12-28_pb-devops1_sda.img.zst";
        const long DivergenceOffset = 5_860_098_048;    //where 0.3.0's window-prefix approach diverged

        public TestContext TestContext { get; set; } = null!;

        void Report(string line)
        {
            TestContext.WriteLine(line);
            Console.WriteLine(line);
        }

        [TestMethod]
        public void FullBuildIsDenseThroughTheOldDivergenceOffsetAndServesByteExact()
        {
            if (!File.Exists(BigImage)) Assert.Inconclusive($"Fixture not present: {BigImage}");

            var scratchDir = Environment.GetEnvironmentVariable("ZSTD_BIG_SCRATCH")
                ?? Path.Combine(Path.GetTempPath(), "zstdseekable_big");
            Directory.CreateDirectory(scratchDir);
            var indexPath = Path.Combine(scratchDir, "sda.img.zst.zsi");

            var options = new ZstdIndexOptions { TargetSpanBytes = 256L * 1024 * 1024 };
            var span = options.TargetSpanBytes;

            //---- full build (resumable; a previous partial run continues from its last point) ----
            var buildTimer = Stopwatch.StartNew();
            long lastPercent = -1;
            var progress = new InlineProgress(p =>
            {
                var percent = (long)(p.Fraction * 100);
                if (percent != lastPercent)
                {
                    lastPercent = percent;
                    Console.WriteLine($"[build] {percent}% compressed={p.CompressedBytesProcessed:N0}/{p.CompressedTotalBytes:N0} uncompressed={p.UncompressedBytesProduced:N0} points={p.PointCount:N0} elapsed={buildTimer.Elapsed:hh\\:mm\\:ss}");
                }
            });

            using var fs = new FileStream(BigImage, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
            using var index = ZstdIndex.LoadOrBuild(fs, indexPath, options, progress);
            buildTimer.Stop();

            var exactPoints = index.Points.Count(p => p.IsExact);
            var frameStarts = index.Points.Count(p => p.IsFrameStart);
            var legacyPoints = index.Points.Count(p => !p.IsFrameStart && !p.IsExact);
            Assert.AreEqual(0, legacyPoints, "a 0.4.0 build must contain no window-prefix points (no divergence fallback exists)");
            Assert.IsTrue(exactPoints > 0, "mid-frame exact points must exist");

            //---- density: no gap over 4x span from 0 through the old divergence offset ----
            long maxGapThroughDivergence = 0;
            long maxGapOverall = 0;
            for (var i = 1; i < index.Points.Count; i++)
            {
                var gap = index.Points[i].UncompressedOffset - index.Points[i - 1].UncompressedOffset;
                maxGapOverall = Math.Max(maxGapOverall, gap);
                if (index.Points[i - 1].UncompressedOffset <= DivergenceOffset)
                    maxGapThroughDivergence = Math.Max(maxGapThroughDivergence, gap);
            }
            Assert.IsTrue(maxGapThroughDivergence <= 4 * span,
                $"index must stay dense through {DivergenceOffset:N0}: maxGap {maxGapThroughDivergence:N0} > 4x span {4 * span:N0}");

            Report($"BUILD points={index.Points.Count:N0} (exact={exactPoints:N0}, frameStarts={frameStarts:N0}, legacy={legacyPoints}) " +
                   $"fills={index.FillSpans.Count:N0} uncompressed={index.UncompressedLength:N0} " +
                   $"indexBytes={new FileInfo(indexPath).Length:N0} buildTime={buildTimer.Elapsed:hh\\:mm\\:ss} " +
                   $"maxGapThrough{DivergenceOffset}={maxGapThroughDivergence:N0} maxGapOverall={maxGapOverall:N0}");

            //---- spot-serve vs a fresh sequential decode, clustered around the divergence offset ----
            long[] offsets =
            [
                1L << 30,                       //1 GiB
                3L << 30,                       //3 GiB
                DivergenceOffset - 4 * 1024 * 1024,
                DivergenceOffset,
                DivergenceOffset + 4 * 1024 * 1024,
            ];
            const int sliceLength = 1 * 1024 * 1024;

            //one sequential pass collecting the reference slices
            var slices = new byte[offsets.Length][];
            using (var plain = new ZstdSharp.DecompressionStream(new FileStream(BigImage, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20)))
            {
                var skip = new byte[1 << 20];
                long pos = 0;
                for (var i = 0; i < offsets.Length; i++)
                {
                    while (pos < offsets[i])
                    {
                        var want = (int)Math.Min(skip.Length, offsets[i] - pos);
                        var n = plain.Read(skip, 0, want);
                        if (n == 0) throw new EndOfStreamException($"reference decode ended at {pos:N0}");
                        pos += n;
                    }
                    var slice = new byte[sliceLength];
                    var got = 0;
                    while (got < sliceLength)
                    {
                        var n = plain.Read(slice, got, sliceLength - got);
                        if (n == 0) break;
                        got += n;
                    }
                    Array.Resize(ref slice, got);
                    slices[i] = slice;
                    pos += got;
                }
            }

            using (var stream = new ZstdIndexedStream(fs, index, leaveOpen: true))
            {
                for (var i = 0; i < offsets.Length; i++)
                {
                    var actual = TestData.ReadAt(stream, offsets[i], slices[i].Length);
                    TestData.AssertSame(slices[i], actual, $"spot read at {offsets[i]:N0}");
                    Report($"SPOT {offsets[i]:N0} x {slices[i].Length:N0}: byte-exact");
                }
            }
        }
    }

    /// <summary>IProgress that reports inline (Progress&lt;T&gt; posts to a SynchronizationContext).</summary>
    public sealed class InlineProgress(Action<ZstdIndexProgress> action) : IProgress<ZstdIndexProgress>
    {
        public void Report(ZstdIndexProgress value) => action(value);
    }
}
