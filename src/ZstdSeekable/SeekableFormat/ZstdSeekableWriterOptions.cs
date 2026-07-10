namespace ZstdSeekable
{
    /// <summary>Options for <see cref="ZstdSeekableWriter"/>.</summary>
    public sealed class ZstdSeekableWriterOptions
    {
        /// <summary>zstd compression level (1-22). Default 3.</summary>
        public int CompressionLevel { get; set; } = 3;

        /// <summary>
        /// Maximum decompressed bytes per frame. Smaller frames give finer seek granularity at a small
        /// compression-ratio cost (each frame compresses independently). Default 1 MiB; the seekable
        /// format allows up to 1 GiB.
        /// </summary>
        public int MaxFrameSize { get; set; } = 1 * 1024 * 1024;

        /// <summary>Store an XXH32 checksum per frame in the seek table. Default true.</summary>
        public bool WriteChecksums { get; set; } = true;
    }
}
