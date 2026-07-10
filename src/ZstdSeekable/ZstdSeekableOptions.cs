using System;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace ZstdSeekable
{
    /// <summary>Options for the <see cref="ZstdSeekableStream"/> auto-detecting factory methods.</summary>
    public sealed class ZstdSeekableOptions
    {
        /// <summary>Optional diagnostics logger, used by whichever mechanism is selected.</summary>
        public ILogger? Logger { get; set; }

        /// <summary>Progress reports while a custom index is being built (a full sequential decode -
        /// can take minutes on large streams). Not used when the input has a seek table.</summary>
        public IProgress<ZstdIndexProgress>? Progress { get; set; }

        /// <summary>Options for the custom index, when one has to be built.</summary>
        public ZstdIndexOptions Index { get; set; } = new();

        /// <summary>Verify per-frame XXH32 checksums when reading official seekable-format input that
        /// carries them. Default false.</summary>
        public bool VerifyChecksums { get; set; }

        /// <summary>Leave the compressed stream open when the returned stream is disposed.
        /// Default false. (Only applies to the Stream-based overloads.)</summary>
        public bool LeaveOpen { get; set; }

        /// <summary>Aborts an index build in progress.</summary>
        public CancellationToken CancellationToken { get; set; }
    }
}
