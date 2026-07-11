using System;
using Microsoft.Extensions.Logging;

namespace ZstdSeekable
{
    /// <summary>Options for building a <see cref="ZstdIndex"/>.</summary>
    public sealed class ZstdIndexOptions
    {
        /// <summary>
        /// Target decompressed bytes per resume point, and therefore also the depth a candidate
        /// point must survive byte-identical before being confirmed. Smaller spans seek faster but
        /// make a bigger index (each mid-frame point stores a snapshot of the decoder window,
        /// zstd-compressed). Default 64 MiB.
        /// </summary>
        public long TargetSpanBytes { get; set; } = 64L * 1024 * 1024;

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
