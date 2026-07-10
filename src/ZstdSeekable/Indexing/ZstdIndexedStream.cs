using System;
using System.Collections.Generic;
using System.IO;
using ZstdSeekable.Internal;

namespace ZstdSeekable
{
    /// <summary>
    /// Random-access view over a standard zstd stream, backed by a <see cref="ZstdIndex"/> of
    /// verified resume points (see <see cref="ZstdIndexPoint"/> for why points must be verified).
    /// A read within a span resumes decoding at the span's point and decodes forward, so seek cost is
    /// bounded by the index's span size. One instance is not thread-safe; use
    /// <see cref="CreateView"/> for cheap independent cursors over the same source.
    /// </summary>
    public sealed class ZstdIndexedStream : Stream
    {
        readonly Stream compressed;
        readonly object gate;
        readonly bool ownsSource;
        readonly bool ownsIndex;
        readonly List<Mapping> mappings;
        long position;
        byte[]? skipScratch;

        /// <summary>The index this stream seeks with.</summary>
        public ZstdIndex Index { get; }

        /// <summary>Wraps <paramref name="compressed"/> (which must be seekable) using
        /// <paramref name="index"/> to serve random-access reads.</summary>
        public ZstdIndexedStream(Stream compressed, ZstdIndex index, bool leaveOpen = false)
            : this(compressed, index, ownsSource: !leaveOpen, ownsIndex: false, gate: new object())
        {
        }

        internal ZstdIndexedStream(Stream compressed, ZstdIndex index, bool ownsSource, bool ownsIndex, object gate)
        {
            this.compressed = compressed ?? throw new ArgumentNullException(nameof(compressed));
            Index = index ?? throw new ArgumentNullException(nameof(index));
            if (!compressed.CanSeek) throw new ArgumentException("The compressed stream must be seekable.", nameof(compressed));
            this.ownsSource = ownsSource;
            this.ownsIndex = ownsIndex;
            this.gate = gate;

            mappings = new List<Mapping>(index.Points.Count);
            for (var i = 0; i < index.Points.Count; i++)
            {
                var point = index.Points[i];
                var next = i + 1 < index.Points.Count ? index.Points[i + 1] : null;
                mappings.Add(new Mapping
                {
                    CompressedStartByte = point.CompressedOffset,
                    CompressedEndByte = next?.CompressedOffset ?? compressed.Length,
                    UncompressedStartByte = point.UncompressedOffset,
                    UncompressedEndByte = next?.UncompressedOffset ?? index.UncompressedLength,
                    Tag = i,
                });
            }
        }

        /// <summary>An independent cursor over the same source: its own position, sharing the
        /// underlying stream (via a lock) and the index. Views may be read concurrently with this
        /// instance and each other. Disposing a view never closes the shared source.</summary>
        public ZstdIndexedStream CreateView() =>
            new(compressed, Index, ownsSource: false, ownsIndex: false, gate);

        /// <inheritdoc/>
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            if (count == 0) return 0;

            var chunk = BlockMap.Find(mappings, position);
            if (chunk == null) return 0;

            //never serve bytes beyond this chunk from this chunk's resume: only output WITHIN the
            //verified span is guaranteed (decoder state at the span's end is not). A short read makes
            //the caller re-enter at the next chunk.
            var bytesLeftInChunk = chunk.UncompressedEndByte - position;
            count = (int)Math.Min(count, bytesLeftInChunk);

            var point = Index.Points[chunk.Tag];
            var positionInChunk = position - chunk.UncompressedStartByte;

            var window = Index.LoadWindow(point);
            var source = new SharedStreamView(compressed, gate);
            using var resume = new ZstdResumeStream(source, chunk.CompressedStartByte, chunk.CompressedEndByte,
                                                    point.IsFrameStart, point.WindowDescriptor, window);

            if (positionInChunk > 0)
            {
                skipScratch ??= new byte[512 * 1024];
                ZstdFrameHelpers.Skip(resume, positionInChunk, skipScratch);
            }

            var total = 0;
            while (total < count)
            {
                var n = resume.Read(buffer, offset + total, count - total);
                if (n == 0) break;
                total += n;
            }

            position += total;
            return total;
        }

        /// <inheritdoc/>
        public override bool CanRead => true;
        /// <inheritdoc/>
        public override bool CanSeek => true;
        /// <inheritdoc/>
        public override bool CanWrite => false;
        /// <inheritdoc/>
        public override long Length => Index.UncompressedLength;

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
                if (ownsIndex) Index.Dispose();
                if (ownsSource) compressed.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
