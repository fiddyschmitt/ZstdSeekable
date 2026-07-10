namespace ZstdSeekable
{
    /// <summary>
    /// One resume point in a standard zstd stream. Unlike gzip/bzip2, a zstd block can depend on
    /// decoder state beyond the content window (repeat offsets and entropy tables carried from
    /// earlier blocks), so a block boundary is only usable as a resume point if a resumed decode is
    /// bit-identical to the true decode - an empirical property of the encoder's output. Points in a
    /// <see cref="ZstdIndex"/> are therefore VERIFIED: trial-decoded during the build and
    /// byte-checked across their whole span. Readers never serve bytes beyond a point's span from
    /// that point's resume (only output <i>within</i> the verified span is guaranteed, not decoder
    /// state at its end).
    /// </summary>
    public sealed class ZstdIndexPoint
    {
        /// <summary>Where this point's span starts in the decompressed output.</summary>
        public long UncompressedOffset { get; }

        /// <summary>The block (or frame) boundary in the compressed stream where decoding resumes.</summary>
        public long CompressedOffset { get; }

        /// <summary>True when the point sits on a real frame header: a stateless, always-sound resume
        /// that needs no window snapshot.</summary>
        public bool IsFrameStart { get; }

        internal byte WindowDescriptor;         //window-descriptor byte for the synthetic frame header (mid-frame points)
        internal long WindowPositionInFile;     //where this point's zstd-compressed window sits, relative to the index's first byte
        internal int WindowCompressedLength;    //0 = no window (frame starts)

        internal ZstdIndexPoint(long uncompressedOffset, long compressedOffset, bool isFrameStart, byte windowDescriptor)
        {
            UncompressedOffset = uncompressedOffset;
            CompressedOffset = compressedOffset;
            IsFrameStart = isFrameStart;
            WindowDescriptor = windowDescriptor;
        }

        /// <inheritdoc/>
        public override string ToString() =>
            $"Uncompressed {UncompressedOffset:N0} == Compressed {CompressedOffset:N0}{(IsFrameStart ? " (frame start)" : "")}";
    }
}
