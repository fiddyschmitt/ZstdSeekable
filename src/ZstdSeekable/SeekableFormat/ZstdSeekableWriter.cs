using System;
using System.Collections.Generic;
using System.IO;
using ZstdSeekable.Internal;

namespace ZstdSeekable
{
    /// <summary>
    /// Compresses to the official zstd seekable format: the data is emitted as a series of
    /// independent zstd frames of at most <see cref="ZstdSeekableWriterOptions.MaxFrameSize"/>
    /// decompressed bytes, and <see cref="Finish"/> (or <see cref="Stream.Dispose()"/>) appends the
    /// seek table. The output is a valid zstd stream for any decompressor, and seekable by any
    /// implementation of the seekable format (including <see cref="ZstdSeekableReader"/>).
    /// </summary>
    public sealed class ZstdSeekableWriter : Stream
    {
        readonly Stream destination;
        readonly bool leaveOpen;
        readonly bool writeChecksums;
        readonly ZstdSharp.Compressor compressor;
        readonly byte[] frameBuffer;
        int frameBufferUsed;
        readonly List<(uint CompressedSize, uint UncompressedSize, uint Checksum)> entries = [];
        bool finished;
        bool disposed;

        /// <summary>Total decompressed bytes written so far.</summary>
        public long UncompressedBytesWritten { get; private set; }

        /// <summary>Number of data frames emitted so far (a partial frame still being buffered is not counted).</summary>
        public int FrameCount => entries.Count;

        /// <summary>Wraps <paramref name="destination"/>; compressed frames are written through as
        /// they fill. Call <see cref="Finish"/> (or dispose) to append the seek table.</summary>
        public ZstdSeekableWriter(Stream destination, ZstdSeekableWriterOptions? options = null, bool leaveOpen = false)
        {
            this.destination = destination ?? throw new ArgumentNullException(nameof(destination));
            if (!destination.CanWrite) throw new ArgumentException("The destination stream must be writable.", nameof(destination));
            this.leaveOpen = leaveOpen;

            options ??= new ZstdSeekableWriterOptions();
            if (options.MaxFrameSize < 4 * 1024 || options.MaxFrameSize > 1024 * 1024 * 1024)
                throw new ArgumentOutOfRangeException(nameof(options), $"MaxFrameSize must be between 4 KiB and 1 GiB (was {options.MaxFrameSize:N0}).");

            writeChecksums = options.WriteChecksums;
            compressor = new ZstdSharp.Compressor(options.CompressionLevel);
            frameBuffer = new byte[options.MaxFrameSize];
        }

        /// <summary>Creates <paramref name="path"/> and returns a writer over it.</summary>
        public static ZstdSeekableWriter Create(string path, ZstdSeekableWriterOptions? options = null) =>
            new(File.Create(path), options);

        /// <inheritdoc/>
        public override void Write(byte[] buffer, int offset, int count)
        {
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            if (offset < 0 || count < 0 || offset + count > buffer.Length) throw new ArgumentOutOfRangeException(nameof(count));
            if (finished) throw new InvalidOperationException("Cannot write after Finish() has been called.");

            while (count > 0)
            {
                var toCopy = Math.Min(count, frameBuffer.Length - frameBufferUsed);
                Array.Copy(buffer, offset, frameBuffer, frameBufferUsed, toCopy);
                frameBufferUsed += toCopy;
                offset += toCopy;
                count -= toCopy;
                UncompressedBytesWritten += toCopy;

                if (frameBufferUsed == frameBuffer.Length) EmitFrame();
            }
        }

        void EmitFrame()
        {
            if (frameBufferUsed == 0) return;

            var content = frameBuffer.AsSpan(0, frameBufferUsed);
            var compressed = compressor.Wrap(content);
            var checksum = writeChecksums ? XxHash32.Hash(content) : 0;

#if NET8_0_OR_GREATER
            destination.Write(compressed);
            var compressedLength = compressed.Length;
#else
            var compressedArray = compressed.ToArray();
            destination.Write(compressedArray, 0, compressedArray.Length);
            var compressedLength = compressedArray.Length;
#endif
            entries.Add(((uint)compressedLength, (uint)frameBufferUsed, checksum));
            frameBufferUsed = 0;
        }

        /// <summary>Flushes any partial frame and writes the seek table + footer. Idempotent; called
        /// automatically on dispose.</summary>
        public void Finish()
        {
            if (finished) return;
            EmitFrame();
            WriteSeekTable();
            destination.Flush();
            finished = true;
        }

        void WriteSeekTable()
        {
            var entrySize = writeChecksums ? 12 : 8;
            var tableFrameSize = entries.Count * (long)entrySize + ZstdFrameHelpers.SeekableFooterSize;
            if (tableFrameSize > uint.MaxValue) throw new InvalidOperationException("Too many frames for a seekable-format seek table.");

            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);
            bw.Write(ZstdFrameHelpers.SeekTableSkippableMagic);
            bw.Write((uint)tableFrameSize);
            foreach (var (compressedSize, uncompressedSize, checksum) in entries)
            {
                bw.Write(compressedSize);
                bw.Write(uncompressedSize);
                if (writeChecksums) bw.Write(checksum);
            }
            bw.Write((uint)entries.Count);
            bw.Write((byte)(writeChecksums ? 0x80 : 0x00));     //descriptor: bit 7 = checksum flag
            bw.Write(ZstdFrameHelpers.SeekableFooterMagic);
            bw.Flush();

            var table = ms.ToArray();
            destination.Write(table, 0, table.Length);
        }

        /// <inheritdoc/>
        public override void Flush() => destination.Flush();

        /// <inheritdoc/>
        public override bool CanRead => false;
        /// <inheritdoc/>
        public override bool CanSeek => false;
        /// <inheritdoc/>
        public override bool CanWrite => !finished;
        /// <inheritdoc/>
        public override long Length => throw new NotSupportedException();
        /// <inheritdoc/>
        public override long Position { get => UncompressedBytesWritten; set => throw new NotSupportedException(); }
        /// <inheritdoc/>
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        /// <inheritdoc/>
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        /// <inheritdoc/>
        public override void SetLength(long value) => throw new NotSupportedException();

        /// <inheritdoc/>
        protected override void Dispose(bool disposing)
        {
            if (disposing && !disposed)
            {
                disposed = true;
                try
                {
                    Finish();
                }
                finally
                {
                    compressor.Dispose();
                    if (!leaveOpen) destination.Dispose();
                }
            }
            base.Dispose(disposing);
        }
    }
}
