using System;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ZstdSeekable.Tests
{
    /// <summary>
    /// Z0 gate for exact decoder-state snapshots (ZstdSeekable 0.4.0 design): capturing
    /// {window, entropy tables, rep offsets, table-selector pointers, flags} at a block boundary
    /// and reinstating them into a fresh DCtx must make a mid-frame resume BYTE-EXACT at every
    /// boundary - 100%, unlike today's window-only resume which relies on the encoder not using
    /// Repeat_Mode across the boundary.
    /// </summary>
    [TestClass]
    public class ExactStateTests
    {
        const string RealImage =
            @"E:\clonezilla-util-test resources\clonezilla images\2022-07-16-22-img_pb-devops1_zst\sdb1.ntfs-ptcl-img.zst";

        public TestContext TestContext { get; set; } = null!;

        void Report(string line)
        {
            TestContext.WriteLine(line);
            Console.WriteLine(line);
        }

        [TestMethod]
        public void SyntheticEveryBlockBoundaryResumesByteExact()
        {
            // Mixed content at level 19: text (heavy entropy reuse / Repeat_Mode), sparse (RLE/raw
            // blocks), and noise (raw literals) - exercises every table-pointer state.
            var data = TestData.Concat(
                TestData.CompressibleBytes(seed: 90, length: 4_000_000),
                TestData.SparseBytes(seed: 91, length: 2_000_000),
                TestData.RandomBytes(seed: 92, length: 1_000_000),
                TestData.CompressibleBytes(seed: 93, length: 1_000_000));
            var compressed = new MemoryStream(TestData.StandardZstd(data, level: 19));

            var result = ZstdExactStatePrototype.Run(
                compressed,
                shouldCapture: (_, _) => true,          //every mid-frame block boundary
                compareBudget: 2 * 1024 * 1024);

            Report(result.Summary());
            foreach (var note in result.CaptureNotes.Take(5))
            {
                Report("  " + note);
            }

            Assert.IsTrue(result.BoundariesCaptured > 20, $"expected many block boundaries, got {result.BoundariesCaptured}");
            Assert.AreEqual(data.Length, result.TotalUncompressed, "true decode length");
            Assert.AreEqual(0, result.ExactFailed,
                "EXACT resume must be byte-exact at EVERY boundary. " + result.FirstExactDivergence);
            Assert.AreEqual(result.BoundariesCaptured, result.ExactCompleted, "all exact shadows completed");
        }

        [TestMethod]
        public void RealImageTwentyBoundariesResume32MBByteExact()
        {
            if (!File.Exists(RealImage))
            {
                Assert.Inconclusive($"Fixture not present: {RealImage}");
            }

            using var fs = new FileStream(RealImage, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
            var total = fs.Length;
            var targets = Enumerable.Range(1, 20).Select(k => total * k / 21).ToArray();
            var nextTarget = 0;

            var result = ZstdExactStatePrototype.Run(
                fs,
                shouldCapture: (compressedPos, _) =>
                {
                    if (nextTarget >= targets.Length || compressedPos < targets[nextTarget])
                    {
                        return false;
                    }
                    while (nextTarget < targets.Length && targets[nextTarget] <= compressedPos)
                    {
                        nextTarget++;
                    }
                    return true;
                },
                compareBudget: 32L * 1024 * 1024);

            Report(result.Summary());
            foreach (var note in result.CaptureNotes)
            {
                Report("  " + note);
            }

            Assert.IsTrue(result.BoundariesCaptured >= 15,
                $"expected ~20 captures spread across the stream, got {result.BoundariesCaptured}");
            Assert.AreEqual(0, result.ExactFailed,
                "EXACT resume must be byte-exact at EVERY boundary - 100%, not 88%. " + result.FirstExactDivergence);
            Assert.AreEqual(result.BoundariesCaptured, result.ExactCompleted, "all exact shadows completed");
        }
    }
}
