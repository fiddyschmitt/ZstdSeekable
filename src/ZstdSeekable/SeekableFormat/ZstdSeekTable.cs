using System;
using System.Collections.Generic;
using System.IO;
using ZstdSeekable.Internal;

namespace ZstdSeekable
{
    /// <summary>One frame entry of an official-format seek table, with its cumulative offsets.</summary>
    public readonly struct ZstdSeekTableEntry
    {
        /// <summary>Where this frame's first compressed byte sits in the stream.</summary>
        public long CompressedOffset { get; }
        /// <summary>Where this frame's first decompressed byte sits in the decompressed output.</summary>
        public long UncompressedOffset { get; }
        /// <summary>Compressed size of the frame in bytes.</summary>
        public uint CompressedSize { get; }
        /// <summary>Decompressed size of the frame in bytes (0 for embedded skippable frames).</summary>
        public uint UncompressedSize { get; }
        /// <summary>XXH32 (seed 0) of the frame's decompressed data; 0 when the table carries no checksums.</summary>
        public uint Checksum { get; }

        internal ZstdSeekTableEntry(long compressedOffset, long uncompressedOffset, uint compressedSize, uint uncompressedSize, uint checksum)
        {
            CompressedOffset = compressedOffset;
            UncompressedOffset = uncompressedOffset;
            CompressedSize = compressedSize;
            UncompressedSize = uncompressedSize;
            Checksum = checksum;
        }
    }

    /// <summary>
    /// The seek table of the official zstd seekable format: a skippable frame at the end of the
    /// stream listing every frame's compressed and decompressed size, closed by a 9-byte footer
    /// (frame count + descriptor + magic 0x8F92EAB1). See
    /// https://github.com/facebook/zstd/blob/dev/contrib/seekable_format/zstd_seekable_compression_format.md
    /// </summary>
    public sealed class ZstdSeekTable
    {
        /// <summary>Every frame in the stream, in order, including any embedded skippable frames
        /// (recognisable by <see cref="ZstdSeekTableEntry.UncompressedSize"/> == 0).</summary>
        public IReadOnlyList<ZstdSeekTableEntry> Entries { get; }

        /// <summary>Whether the table stores an XXH32 checksum per frame.</summary>
        public bool HasChecksums { get; }

        /// <summary>Total compressed size of the data frames (the seek-table frame itself excluded).</summary>
        public long CompressedLength { get; }

        /// <summary>Total decompressed size of the stream.</summary>
        public long UncompressedLength { get; }

        ZstdSeekTable(IReadOnlyList<ZstdSeekTableEntry> entries, bool hasChecksums, long compressedLength, long uncompressedLength)
        {
            Entries = entries;
            HasChecksums = hasChecksums;
            CompressedLength = compressedLength;
            UncompressedLength = uncompressedLength;
        }

        /// <summary>Probes the end of <paramref name="compressed"/> for a seek table. Returns false if
        /// none is present or it is malformed. The stream's position is restored.</summary>
        public static bool TryRead(Stream compressed, out ZstdSeekTable? table)
        {
            try
            {
                table = ReadCore(compressed);
                return table != null;
            }
            catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or IOException)
            {
                table = null;
                return false;
            }
        }

        /// <summary>Reads the seek table at the end of <paramref name="compressed"/>. Throws
        /// <see cref="InvalidDataException"/> if the stream has no valid seek table. The stream's
        /// position is restored.</summary>
        public static ZstdSeekTable Read(Stream compressed) =>
            ReadCore(compressed) ?? throw new InvalidDataException("The stream has no zstd seekable-format seek table.");

        static ZstdSeekTable? ReadCore(Stream compressed)
        {
            if (compressed == null) throw new ArgumentNullException(nameof(compressed));
            if (!compressed.CanSeek) throw new ArgumentException("The compressed stream must be seekable to locate the seek table.", nameof(compressed));

            var originalPosition = compressed.Position;
            try
            {
                var length = compressed.Length;
                if (length < ZstdFrameHelpers.SeekableFooterSize + 8) return null;

                var footer = new byte[ZstdFrameHelpers.SeekableFooterSize];
                compressed.Position = length - footer.Length;
                compressed.ReadExactly(footer);

                var magic = ReadUInt32(footer, 5);
                if (magic != ZstdFrameHelpers.SeekableFooterMagic) return null;

                var frameCount = ReadUInt32(footer, 0);
                var descriptor = footer[4];
                if ((descriptor & 0x7C) != 0)
                    throw new InvalidDataException("Reserved bits are set in the seek-table descriptor; refusing to parse (per the seekable-format spec).");
                var hasChecksums = (descriptor & 0x80) != 0;

                var entrySize = hasChecksums ? 12 : 8;
                var tableFrameSize = (long)frameCount * entrySize + ZstdFrameHelpers.SeekableFooterSize;
                var tableStart = length - 8 - tableFrameSize;   //8 = skippable magic + frame-size field
                if (tableStart < 0) throw new InvalidDataException("Seek table is larger than the stream.");

                var tableHeader = new byte[8];
                compressed.Position = tableStart;
                compressed.ReadExactly(tableHeader);
                if (ReadUInt32(tableHeader, 0) != ZstdFrameHelpers.SeekTableSkippableMagic)
                    throw new InvalidDataException("Seek-table skippable-frame magic not found where the footer says it should be.");
                if (ReadUInt32(tableHeader, 4) != tableFrameSize)
                    throw new InvalidDataException("Seek-table frame size does not match the footer's frame count.");

                var tableBytes = new byte[checked((int)((long)frameCount * entrySize))];
                compressed.ReadExactly(tableBytes);

                var entries = new List<ZstdSeekTableEntry>((int)frameCount);
                long compressedOffset = 0;
                long uncompressedOffset = 0;
                for (var i = 0; i < frameCount; i++)
                {
                    var recordStart = i * entrySize;
                    var compressedSize = ReadUInt32(tableBytes, recordStart);
                    var uncompressedSize = ReadUInt32(tableBytes, recordStart + 4);
                    var checksum = hasChecksums ? ReadUInt32(tableBytes, recordStart + 8) : 0;
                    entries.Add(new ZstdSeekTableEntry(compressedOffset, uncompressedOffset, compressedSize, uncompressedSize, checksum));
                    compressedOffset += compressedSize;
                    uncompressedOffset += uncompressedSize;
                }

                if (compressedOffset != tableStart)
                    throw new InvalidDataException($"Seek-table compressed sizes sum to {compressedOffset:N0} but the data region is {tableStart:N0} bytes.");

                return new ZstdSeekTable(entries, hasChecksums, compressedOffset, uncompressedOffset);
            }
            finally
            {
                compressed.Position = originalPosition;
            }
        }

        static uint ReadUInt32(byte[] buffer, int offset) =>
            (uint)(buffer[offset] | buffer[offset + 1] << 8 | buffer[offset + 2] << 16 | buffer[offset + 3] << 24);
    }
}
