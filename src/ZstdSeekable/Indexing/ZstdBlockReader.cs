using System;
using System.Buffers.Binary;
using System.IO;
using ZstdSeekable.Internal;

namespace ZstdSeekable
{
    internal enum FrameKind { Zstd, Skippable, EndOfStream }

    /// <summary>Walks zstd frame/block structure on the compressed side (no decoding).</summary>
    internal sealed class ZstdBlockReader(Stream stream)
    {
        byte[] blockBuffer = new byte[128 * 1024 + 16];

        public long Position => stream.Position;

        public FrameKind BeginFrame(out byte[] headerBytes, out long windowSize, out byte windowDescriptor, out bool hasChecksum)
        {
            headerBytes = [];
            windowSize = 0;
            windowDescriptor = 0;
            hasChecksum = false;

            var magic = new byte[4];
            var got = stream.ReadAtLeast(magic, 4, throwOnEndOfStream: false);
            if (got == 0) return FrameKind.EndOfStream;
            if (got < 4) throw new InvalidDataException("Truncated zstd stream (magic).");

            var magicValue = BinaryPrimitives.ReadUInt32LittleEndian(magic);
            if (magicValue >= ZstdFrameHelpers.SkippableMagicMin && magicValue <= ZstdFrameHelpers.SkippableMagicMax)
            {
                var size = new byte[4];
                stream.ReadExactly(size);
                stream.Seek(BinaryPrimitives.ReadUInt32LittleEndian(size), SeekOrigin.Current);
                return FrameKind.Skippable;
            }
            if (magicValue != ZstdFrameHelpers.ZstdMagic) throw new InvalidDataException($"Unexpected zstd magic 0x{magicValue:X8}.");

            var frameHeaderDescriptor = ReadByteStrict();
            var singleSegment = (frameHeaderDescriptor & 0x20) != 0;
            hasChecksum = (frameHeaderDescriptor & 0x04) != 0;
            var dictIdBytes = (frameHeaderDescriptor & 0x03) switch { 0 => 0, 1 => 1, 2 => 2, _ => 4 };
            var fcsBytes = (frameHeaderDescriptor >> 6) switch { 0 => singleSegment ? 1 : 0, 1 => 2, 2 => 4, _ => 8 };

            var rest = new byte[(singleSegment ? 0 : 1) + dictIdBytes + fcsBytes];
            if (rest.Length > 0) stream.ReadExactly(rest);

            if (singleSegment)
            {
                var fcsSpan = rest.AsSpan(rest.Length - fcsBytes);
                var contentSize = fcsBytes switch
                {
                    1 => fcsSpan[0],
                    2 => BinaryPrimitives.ReadUInt16LittleEndian(fcsSpan) + 256,
                    4 => BinaryPrimitives.ReadUInt32LittleEndian(fcsSpan),
                    _ => (long)BinaryPrimitives.ReadUInt64LittleEndian(fcsSpan),
                };
                windowSize = Math.Max(1024, contentSize);
                windowDescriptor = ZstdFrameHelpers.DescriptorForWindowSize(windowSize);
            }
            else
            {
                windowDescriptor = rest[0];
                var exponent = windowDescriptor >> 3;
                var mantissa = windowDescriptor & 7;
                var windowBase = 1L << (10 + exponent);
                windowSize = windowBase + (windowBase / 8) * mantissa;
            }

            headerBytes = new byte[4 + 1 + rest.Length];
            magic.CopyTo(headerBytes, 0);
            headerBytes[4] = frameHeaderDescriptor;
            rest.CopyTo(headerBytes, 5);
            return FrameKind.Zstd;
        }

        public ReadOnlySpan<byte> ReadBlock(out bool lastBlock)
        {
            var header = new byte[3];
            stream.ReadExactly(header);
            var blockHeader = header[0] | (header[1] << 8) | (header[2] << 16);
            lastBlock = (blockHeader & 1) != 0;
            var blockType = (blockHeader >> 1) & 3;
            if (blockType == 3) throw new InvalidDataException("Reserved zstd block type.");
            var blockSize = blockHeader >> 3;
            var payload = blockType == 1 ? 1 : blockSize;   //an RLE block stores 1 byte and regenerates blockSize

            if (blockBuffer.Length < 3 + payload) blockBuffer = new byte[3 + payload];
            header.CopyTo(blockBuffer, 0);
            stream.ReadExactly(blockBuffer, 3, payload);
            return blockBuffer.AsSpan(0, 3 + payload);
        }

        public void EndFrame(bool hasChecksum)
        {
            if (hasChecksum) stream.Seek(4, SeekOrigin.Current);
        }

        byte ReadByteStrict()
        {
            var b = stream.ReadByte();
            if (b < 0) throw new InvalidDataException("Truncated zstd stream.");
            return (byte)b;
        }
    }
}
