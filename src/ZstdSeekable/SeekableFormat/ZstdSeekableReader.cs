using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Extensions.Logging;
using ZstdSeekable.Internal;

namespace ZstdSeekable
{
    /// <summary>
    /// Random-access read of a stream in the official zstd seekable format. Seeking is instant: the
    /// seek table maps any position to the single frame that contains it, and a read decodes at most
    /// that one frame. One instance is not thread-safe; use <see cref="CreateView"/> for cheap
    /// independent cursors over the same source.
    /// </summary>
    public sealed class ZstdSeekableReader : Stream
    {
        readonly Stream compressed;
        readonly object gate;
        readonly bool ownsSource;
        readonly ZstdSeekableReaderOptions options;
        readonly List<Mapping> mappings;
        readonly ZstdSharp.Decompressor decompressor = new();
        long position;

        //one decoded frame kept per instance: sequential consumers decode each frame exactly once
        int cachedEntryIndex = -1;
        byte[]? cachedFrame;

        byte[]? skipScratch;

        /// <summary>The parsed seek table.</summary>
        public ZstdSeekTable SeekTable { get; }

        /// <summary>Opens a reader over <paramref name="compressed"/>, which must contain a valid
        /// seek table (see <see cref="ZstdSeekTable.TryRead"/> to probe first).</summary>
        public ZstdSeekableReader(Stream compressed, ZstdSeekableReaderOptions? options = null, bool leaveOpen = false)
            : this(compressed, ZstdSeekTable.Read(compressed), options, ownsSource: !leaveOpen, gate: new object())
        {
        }

        ZstdSeekableReader(Stream compressed, ZstdSeekTable seekTable, ZstdSeekableReaderOptions? options, bool ownsSource, object gate)
        {
            this.compressed = compressed ?? throw new ArgumentNullException(nameof(compressed));
            if (!compressed.CanSeek) throw new ArgumentException("The compressed stream must be seekable.", nameof(compressed));
            this.options = options ?? new ZstdSeekableReaderOptions();
            this.ownsSource = ownsSource;
            this.gate = gate;
            SeekTable = seekTable;

            mappings = new List<Mapping>(seekTable.Entries.Count);
            for (var i = 0; i < seekTable.Entries.Count; i++)
            {
                var entry = seekTable.Entries[i];
                if (entry.UncompressedSize == 0) continue;      //embedded skippable frame: no output
                mappings.Add(new Mapping
                {
                    CompressedStartByte = entry.CompressedOffset,
                    CompressedEndByte = entry.CompressedOffset + entry.CompressedSize,
                    UncompressedStartByte = entry.UncompressedOffset,
                    UncompressedEndByte = entry.UncompressedOffset + entry.UncompressedSize,
                    Tag = i,
                });
            }
        }

        /// <summary>Opens <paramref name="path"/> for random-access reading.</summary>
        public static ZstdSeekableReader Open(string path, ZstdSeekableReaderOptions? options = null) =>
            new(File.OpenRead(path), options);

        /// <summary>An independent cursor over the same source: its own position and frame cache,
        /// sharing the underlying stream via a lock. Views may be read concurrently with this
        /// instance and each other. Disposing a view never closes the shared source.</summary>
        public ZstdSeekableReader CreateView() =>
            new(compressed, SeekTable, options, ownsSource: false, gate);

        /// <inheritdoc/>
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            if (count == 0) return 0;

            var chunk = BlockMap.Find(mappings, position);
            if (chunk == null) return 0;

            var bytesLeftInChunk = chunk.UncompressedEndByte - position;
            count = (int)Math.Min(count, bytesLeftInChunk);

            var entry = SeekTable.Entries[chunk.Tag];
            var positionInChunk = position - chunk.UncompressedStartByte;

            int read;
            if (entry.UncompressedSize <= Math.Min(options.MaxCachedFrameBytes, int.MaxValue))
            {
                if (cachedEntryIndex != chunk.Tag)
                {
                    cachedFrame = DecompressWholeFrame(entry);
                    cachedEntryIndex = chunk.Tag;
                }
                Array.Copy(cachedFrame!, positionInChunk, buffer, offset, count);
                read = count;
            }
            else
            {
                read = ReadByStreaming(chunk, positionInChunk, buffer, offset, count);
            }

            position += read;
            return read;
        }

        byte[] DecompressWholeFrame(ZstdSeekTableEntry entry)
        {
            var compressedFrame = new byte[entry.CompressedSize];
            lock (gate)
            {
                compressed.Position = entry.CompressedOffset;
                compressed.ReadExactly(compressedFrame);
            }

            var decompressed = new byte[entry.UncompressedSize];
            int written;
            try
            {
                written = decompressor.Unwrap(compressedFrame, decompressed);
            }
            catch (ZstdSharp.ZstdException ex)
            {
                throw new InvalidDataException($"The frame at {entry.CompressedOffset:N0} failed to decompress: {ex.Message}", ex);
            }
            if (written != decompressed.Length)
                throw new InvalidDataException($"Frame at {entry.CompressedOffset:N0} decompressed to {written:N0} bytes but the seek table says {decompressed.Length:N0}.");

            if (options.VerifyChecksums && SeekTable.HasChecksums)
            {
                var actual = XxHash32.Hash(decompressed);
                if (actual != entry.Checksum)
                    throw new InvalidDataException($"XXH32 mismatch for the frame at {entry.CompressedOffset:N0}: table says 0x{entry.Checksum:X8}, data is 0x{actual:X8}.");
            }

            return decompressed;
        }

        //frames above the cache cap: decode forward from the frame start without materialising it.
        //(checksums cannot be verified on this path - that would force decoding the whole frame.)
        int ReadByStreaming(Mapping chunk, long positionInChunk, byte[] buffer, int offset, int count)
        {
            options.Logger?.LogDebug("Streaming oversized frame at compressed offset {Offset:N0}.", chunk.CompressedStartByte);

            var view = new SharedStreamView(compressed, gate) { Position = chunk.CompressedStartByte };
            using var decompression = new ZstdSharp.DecompressionStream(view);

            if (positionInChunk > 0)
            {
                skipScratch ??= new byte[512 * 1024];
                ZstdFrameHelpers.Skip(decompression, positionInChunk, skipScratch);
            }

            var total = 0;
            while (total < count)
            {
                var n = decompression.Read(buffer, offset + total, count - total);
                if (n == 0) break;
                total += n;
            }
            return total;
        }

        /// <inheritdoc/>
        public override bool CanRead => true;
        /// <inheritdoc/>
        public override bool CanSeek => true;
        /// <inheritdoc/>
        public override bool CanWrite => false;
        /// <inheritdoc/>
        public override long Length => SeekTable.UncompressedLength;

        /// <inheritdoc/>
        public override long Position
        {
            get => position;
            set => Seek(value, SeekOrigin.Begin);
        }

        /// <inheritdoc/>
        public override long Seek(long offset, SeekOrigin origin)
        {
            var target = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => position + offset,
                SeekOrigin.End => Length + offset,
                _ => throw new ArgumentOutOfRangeException(nameof(origin)),
            };
            if (target < 0) throw new IOException("Attempted to seek before the beginning of the stream.");
            position = target;
            return position;
        }

        /// <inheritdoc/>
        public override void Flush() { }
        /// <inheritdoc/>
        public override void SetLength(long value) => throw new NotSupportedException();
        /// <inheritdoc/>
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        /// <inheritdoc/>
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                decompressor.Dispose();
                if (ownsSource) compressed.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
