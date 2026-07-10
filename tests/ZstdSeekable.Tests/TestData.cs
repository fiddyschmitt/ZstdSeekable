using System;
using System.Collections.Generic;
using System.IO;

namespace ZstdSeekable.Tests
{
    /// <summary>Seeded, deterministic generators and zstd stream builders.</summary>
    public static class TestData
    {
        static readonly string[] Words =
        [
            "the", "quick", "brown", "fox", "jumps", "over", "lazy", "dog", "zstandard", "stream",
            "random", "access", "seekable", "index", "frame", "block", "window", "resume", "point",
            "compression", "decompression", "verify", "span", "offset", "byte",
        ];

        /// <summary>Pseudo-text: highly compressible, with plenty of match sequences to exercise
        /// repeat-offset decoder state (the thing that makes mid-frame resume points hard).</summary>
        public static byte[] CompressibleBytes(int seed, int length)
        {
            var random = new Random(seed);
            using var ms = new MemoryStream(length + 64);
            using var writer = new StreamWriter(ms);
            while (ms.Length < length)
            {
                writer.Write(Words[random.Next(Words.Length)]);
                writer.Write(random.Next(100) == 0 ? '\n' : ' ');
                writer.Flush();
            }
            var bytes = ms.ToArray();
            Array.Resize(ref bytes, length);
            return bytes;
        }

        /// <summary>Long zero runs mixed with random data: produces RLE/raw blocks, the stateless
        /// stretches that can fool the inline trial and exercise the verify-and-heal pass.</summary>
        public static byte[] SparseBytes(int seed, int length)
        {
            var random = new Random(seed);
            var bytes = new byte[length];
            var position = 0;
            while (position < length)
            {
                var zeroRun = random.Next(100 * 1024, 600 * 1024);
                position += zeroRun;    //already zero
                if (position >= length) break;

                var dataRun = Math.Min(random.Next(20 * 1024, 120 * 1024), length - position);
                var data = new byte[dataRun];
                random.NextBytes(data);
                Array.Copy(data, 0, bytes, position, dataRun);
                position += dataRun;
            }
            return bytes;
        }

        /// <summary>Incompressible noise.</summary>
        public static byte[] RandomBytes(int seed, int length)
        {
            var bytes = new byte[length];
            new Random(seed).NextBytes(bytes);
            return bytes;
        }

        /// <summary>One ordinary zstd frame (many blocks, no seek table) - the Clonezilla shape.</summary>
        public static byte[] StandardZstd(byte[] data, int level = 3)
        {
            using var compressor = new ZstdSharp.Compressor(level);
            return compressor.Wrap(data).ToArray();
        }

        /// <summary>Multiple independent frames concatenated (still no seek table).</summary>
        public static byte[] MultiFrameZstd(IEnumerable<byte[]> frames, int level = 3)
        {
            using var compressor = new ZstdSharp.Compressor(level);
            using var ms = new MemoryStream();
            foreach (var frame in frames)
            {
                var wrapped = compressor.Wrap(frame).ToArray();
                ms.Write(wrapped, 0, wrapped.Length);
            }
            return ms.ToArray();
        }

        /// <summary>A skippable frame (magic 0x184D2A50 + variant) carrying an arbitrary payload.</summary>
        public static byte[] SkippableFrame(byte[] payload, int magicVariant = 0)
        {
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);
            bw.Write((uint)(0x184D2A50 + magicVariant));
            bw.Write((uint)payload.Length);
            bw.Write(payload);
            bw.Flush();
            return ms.ToArray();
        }

        /// <summary>Official seekable format, produced by our own writer.</summary>
        public static byte[] SeekableZstd(byte[] data, int maxFrameSize, bool checksums = true, int level = 3)
        {
            using var ms = new MemoryStream();
            using (var writer = new ZstdSeekableWriter(ms, new ZstdSeekableWriterOptions { MaxFrameSize = maxFrameSize, WriteChecksums = checksums, CompressionLevel = level }, leaveOpen: true))
            {
                writer.Write(data, 0, data.Length);
            }
            return ms.ToArray();
        }

        public static byte[] Concat(params byte[][] parts)
        {
            using var ms = new MemoryStream();
            foreach (var part in parts) ms.Write(part, 0, part.Length);
            return ms.ToArray();
        }

        /// <summary>Reads exactly <paramref name="count"/> bytes at <paramref name="offset"/> (looping
        /// over short reads), or fewer only at end of stream.</summary>
        public static byte[] ReadAt(Stream stream, long offset, int count)
        {
            stream.Position = offset;
            var buffer = new byte[count];
            var total = 0;
            while (total < count)
            {
                var read = stream.Read(buffer, total, count - total);
                if (read == 0) break;
                total += read;
            }
            Array.Resize(ref buffer, total);
            return buffer;
        }

        public static void AssertSame(byte[] expected, byte[] actual, string context)
        {
            if (expected.Length != actual.Length)
                throw new Exception($"{context}: expected {expected.Length:N0} bytes, got {actual.Length:N0}.");
            for (var i = 0; i < expected.Length; i++)
            {
                if (expected[i] != actual[i])
                    throw new Exception($"{context}: first mismatch at {i:N0} (expected 0x{expected[i]:X2}, got 0x{actual[i]:X2}).");
            }
        }

        public static byte[] Slice(byte[] source, long offset, int count)
        {
            var end = Math.Min(source.Length, offset + count);
            var slice = new byte[Math.Max(0, end - offset)];
            if (slice.Length > 0) Array.Copy(source, offset, slice, 0, slice.Length);
            return slice;
        }
    }

    /// <summary>A synchronous IProgress that just collects reports (Progress&lt;T&gt; posts to a
    /// SynchronizationContext, which races with test asserts).</summary>
    public sealed class CollectingProgress : IProgress<ZstdIndexProgress>
    {
        readonly object gate = new();
        readonly List<ZstdIndexProgress> reports = [];

        public void Report(ZstdIndexProgress value)
        {
            lock (gate) reports.Add(value);
        }

        public List<ZstdIndexProgress> Reports
        {
            get { lock (gate) return new List<ZstdIndexProgress>(reports); }
        }
    }
}
