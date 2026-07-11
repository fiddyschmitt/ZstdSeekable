using System;
using System.IO;
using ZstdSharp.Unsafe;

namespace ZstdSeekable.Internal
{
    internal static class ZstdFrameHelpers
    {
        public const uint ZstdMagic = 0xFD2FB528;
        public const uint SkippableMagicMin = 0x184D2A50;
        public const uint SkippableMagicMax = 0x184D2A5F;

        //official zstd seekable format (contrib/seekable_format)
        public const uint SeekTableSkippableMagic = 0x184D2A5E;
        public const uint SeekableFooterMagic = 0x8F92EAB1;
        public const int SeekableFooterSize = 9;    //Number_Of_Frames(4) + Seek_Table_Descriptor(1) + Seekable_Magic_Number(4)

        /// <summary>
        /// A minimal frame header that puts a decoder into mid-frame state: magic, a frame-header
        /// descriptor with no flags, and the window descriptor of the original frame. Fed before the
        /// resumed blocks so ZSTD_decompressStream accepts them (with the prior content supplied via
        /// ZSTD_DCtx_refPrefix).
        /// </summary>
        public static byte[] SyntheticFrameHeader(byte windowDescriptor) =>
            [0x28, 0xB5, 0x2F, 0xFD, 0x00 /*FHD: no flags*/, windowDescriptor];

        /// <summary>Smallest window-descriptor byte whose window is ≥ <paramref name="windowSize"/>.</summary>
        public static byte DescriptorForWindowSize(long windowSize)
        {
            for (var exponent = 0; exponent <= 31; exponent++)
            {
                var windowBase = 1L << (10 + exponent);
                for (var mantissa = 0; mantissa <= 7; mantissa++)
                {
                    if (windowBase + (windowBase / 8) * mantissa >= windowSize)
                        return (byte)((exponent << 3) | mantissa);
                }
            }
            return 0xF8;
        }

        /// <summary>Feeds <paramref name="data"/> to a streaming decompression context, collecting all
        /// output into <paramref name="outBuf"/>. Returns false on a zstd decode error.</summary>
        public static unsafe bool Feed(ZSTD_DCtx_s* dctx, ReadOnlySpan<byte> data, byte[] outBuf, out int produced)
        {
            produced = 0;
            fixed (byte* inputPtr = data, outputPtr = outBuf)
            {
                var input = new ZSTD_inBuffer_s { src = inputPtr, size = (nuint)data.Length, pos = 0 };
                while (input.pos < input.size)
                {
                    var output = new ZSTD_outBuffer_s { dst = outputPtr + produced, size = (nuint)(outBuf.Length - produced), pos = 0 };
                    var r = Methods.ZSTD_decompressStream(dctx, &output, &input);
                    if (Methods.ZSTD_isError(r)) return false;
                    produced += (int)output.pos;
                    if (output.pos == 0 && r == 0 && input.pos < input.size) break;    //frame ended with input left over
                }
            }
            return true;
        }

        /// <summary>Index of the first byte differing from <paramref name="value"/>, or -1.</summary>
        public static int IndexOfNot(ReadOnlySpan<byte> span, byte value)
        {
#if NET8_0_OR_GREATER
            return span.IndexOfAnyExcept(value);
#else
            for (var i = 0; i < span.Length; i++)
            {
                if (span[i] != value) return i;
            }
            return -1;
#endif
        }

        /// <summary>Index of the last byte differing from <paramref name="value"/>, or -1.</summary>
        public static int LastIndexOfNot(ReadOnlySpan<byte> span, byte value)
        {
#if NET8_0_OR_GREATER
            return span.LastIndexOfAnyExcept(value);
#else
            for (var i = span.Length - 1; i >= 0; i--)
            {
                if (span[i] != value) return i;
            }
            return -1;
#endif
        }

        /// <summary>Decode-and-discard <paramref name="count"/> bytes from a forward-only stream.</summary>
        public static void Skip(Stream stream, long count, byte[] scratch)
        {
            while (count > 0)
            {
                var read = stream.Read(scratch, 0, (int)Math.Min(scratch.Length, count));
                if (read == 0) throw new EndOfStreamException("Unexpected end of decompressed data while seeking.");
                count -= read;
            }
        }
    }
}
