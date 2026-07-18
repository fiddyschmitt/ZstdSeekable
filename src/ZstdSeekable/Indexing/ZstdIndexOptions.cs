using System;
using Microsoft.Extensions.Logging;

namespace ZstdSeekable
{
    /// <summary>Options for building a <see cref="ZstdIndex"/>.</summary>
    public sealed class ZstdIndexOptions
    {
        /// <summary>
        /// Target decompressed bytes per resume point. Smaller spans seek faster but make a bigger
        /// index (each mid-frame point stores a snapshot of the decoder window plus its entropy
        /// state, zstd-compressed). Since 0.4.0 every block boundary at the target spacing is
        /// accepted (points carry the exact decoder state), so spans are uniform. Default 64 MiB.
        /// </summary>
        public long TargetSpanBytes { get; set; } = 64L * 1024 * 1024;

        /// <summary>
        /// How many resume-point spans to byte-verify against a shadow decoder during the build.
        /// A 0.4.0 point captures the decoder state EXACTLY, so it restores perfectly or not at
        /// all - and every point uses the identical code path, so verifying the first few confirms
        /// the machinery for this stream while the rest are correct by construction. A sampled
        /// mismatch is a hard error (it would indicate a bug), never healed. Set very large to
        /// verify every span. Default 3.
        /// </summary>
        public int VerifiedSampleSpans { get; set; } = 3;

        /// <summary>Unused since 0.2.0: the single-pass build verifies every span inline (shadow
        /// decoders alongside the true decode), so there is no separate verification pass.</summary>
        [Obsolete("The single-pass build verifies as it goes; there is no separate verification pass. This option is ignored.")]
        public int VerifyParallelism { get; set; } = Math.Min(4, Environment.ProcessorCount);

        /// <summary>zstd level used to compress window snapshots inside the index. Default 3.</summary>
        public int WindowCompressionLevel { get; set; } = 3;

        /// <summary>
        /// Minimum length of a single-repeated-byte run (zeros in disk images, 0xFF in flash dumps)
        /// to record as a <see cref="ZstdFillSpan"/>, letting reads inside it skip decompression
        /// entirely. Clamped to ≥256 KiB; set to <see cref="long.MaxValue"/> to disable.
        /// Default 1 MiB.
        /// </summary>
        public long FillSpanThreshold { get; set; } = 1024 * 1024;

        /// <summary>Optional diagnostics logger.</summary>
        public ILogger? Logger { get; set; }

        /// <summary>Test seam: plant only frame-start points (the divergence-fallback mode).</summary>
        internal bool FrameStartsOnly { get; set; }
    }

    /// <summary>The phase an index build is in.</summary>
    public enum ZstdIndexPhase
    {
        /// <summary>The single sequential decode of the stream, verifying resume points inline.</summary>
        Scanning,
        /// <summary>No longer reported since 0.2.0: verification happens inline during
        /// <see cref="Scanning"/> (shadow decoders alongside the true decode).</summary>
        [Obsolete("Since 0.2.0 verification happens inline during Scanning; this phase is never reported.")]
        Verifying,
    }

    /// <summary>A progress report from <see cref="ZstdIndex.Build"/>.</summary>
    public readonly struct ZstdIndexProgress
    {
        /// <summary>The current build phase.</summary>
        public ZstdIndexPhase Phase { get; }
        /// <summary>Compressed bytes consumed so far (scanning phase).</summary>
        public long CompressedBytesProcessed { get; }
        /// <summary>Total compressed bytes.</summary>
        public long CompressedTotalBytes { get; }
        /// <summary>Decompressed bytes produced so far (scanning phase).</summary>
        public long UncompressedBytesProduced { get; }
        /// <summary>Resume points planted so far.</summary>
        public int PointCount { get; }
        /// <summary>Overall progress in [0, 1] across both phases.</summary>
        public double Fraction { get; }

        internal ZstdIndexProgress(ZstdIndexPhase phase, long compressedBytesProcessed, long compressedTotalBytes,
                                   long uncompressedBytesProduced, int pointCount, double fraction)
        {
            Phase = phase;
            CompressedBytesProcessed = compressedBytesProcessed;
            CompressedTotalBytes = compressedTotalBytes;
            UncompressedBytesProduced = uncompressedBytesProduced;
            PointCount = pointCount;
            Fraction = fraction;
        }
    }
}
