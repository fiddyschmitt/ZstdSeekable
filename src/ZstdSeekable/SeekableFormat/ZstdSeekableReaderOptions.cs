using Microsoft.Extensions.Logging;

namespace ZstdSeekable
{
    /// <summary>Options for <see cref="ZstdSeekableReader"/>.</summary>
    public sealed class ZstdSeekableReaderOptions
    {
        /// <summary>Verify each frame's XXH32 checksum (when the seek table carries checksums) as the
        /// frame is decompressed. Default false.</summary>
        public bool VerifyChecksums { get; set; }

        /// <summary>
        /// Frames whose decompressed size is at most this are decompressed whole and cached (one frame
        /// per reader instance), so sequential reads decode each frame once. Larger frames are streamed
        /// per read without caching. Default 128 MiB.
        /// </summary>
        public long MaxCachedFrameBytes { get; set; } = 128L * 1024 * 1024;

        /// <summary>Optional diagnostics logger.</summary>
        public ILogger? Logger { get; set; }
    }
}
