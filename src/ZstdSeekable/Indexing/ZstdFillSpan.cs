namespace ZstdSeekable
{
    /// <summary>
    /// A run of a single repeated byte (usually 0x00 in disk images, 0xFF in flash dumps) in the
    /// decompressed stream, recorded in the index during the build. Reads inside a fill span are
    /// served by filling the buffer directly - no window load, no decoder, no compressed I/O.
    /// Fill spans are derived from the build's true decode, so they are verification-grade; they
    /// are also useful as sparse-extent metadata (e.g. for mounted filesystem images).
    /// </summary>
    public readonly struct ZstdFillSpan
    {
        /// <summary>Where the run starts in the decompressed output.</summary>
        public long UncompressedOffset { get; }

        /// <summary>Run length in bytes.</summary>
        public long Length { get; }

        /// <summary>The repeated byte value.</summary>
        public byte FillByte { get; }

        internal ZstdFillSpan(long uncompressedOffset, long length, byte fillByte)
        {
            UncompressedOffset = uncompressedOffset;
            Length = length;
            FillByte = fillByte;
        }

        /// <inheritdoc/>
        public override string ToString() =>
            $"0x{FillByte:X2} x {Length:N0} at {UncompressedOffset:N0}";
    }
}
