using System;

namespace ZstdSeekable.Internal
{
    /// <summary>
    /// XXH32 (one-shot, seed 0 unless given) - the per-frame checksum of the official zstd seekable
    /// format. Implemented here because ZstdSharp does not expose its internal copy.
    /// Reference: https://github.com/Cyan4973/xxHash/blob/dev/doc/xxhash_spec.md
    /// </summary>
    internal static class XxHash32
    {
        const uint Prime1 = 2654435761U;
        const uint Prime2 = 2246822519U;
        const uint Prime3 = 3266489917U;
        const uint Prime4 = 668265263U;
        const uint Prime5 = 374761393U;

        public static uint Hash(ReadOnlySpan<byte> data, uint seed = 0)
        {
            uint hash;
            var remaining = data;

            if (data.Length >= 16)
            {
                var acc1 = seed + Prime1 + Prime2;
                var acc2 = seed + Prime2;
                var acc3 = seed;
                var acc4 = seed - Prime1;

                while (remaining.Length >= 16)
                {
                    acc1 = Round(acc1, ReadUInt32(remaining));
                    acc2 = Round(acc2, ReadUInt32(remaining.Slice(4)));
                    acc3 = Round(acc3, ReadUInt32(remaining.Slice(8)));
                    acc4 = Round(acc4, ReadUInt32(remaining.Slice(12)));
                    remaining = remaining.Slice(16);
                }

                hash = RotateLeft(acc1, 1) + RotateLeft(acc2, 7) + RotateLeft(acc3, 12) + RotateLeft(acc4, 18);
            }
            else
            {
                hash = seed + Prime5;
            }

            hash += (uint)data.Length;

            while (remaining.Length >= 4)
            {
                hash = RotateLeft(hash + ReadUInt32(remaining) * Prime3, 17) * Prime4;
                remaining = remaining.Slice(4);
            }

            for (var i = 0; i < remaining.Length; i++)
            {
                hash = RotateLeft(hash + remaining[i] * Prime5, 11) * Prime1;
            }

            hash ^= hash >> 15;
            hash *= Prime2;
            hash ^= hash >> 13;
            hash *= Prime3;
            hash ^= hash >> 16;
            return hash;
        }

        static uint Round(uint acc, uint lane) => RotateLeft(acc + lane * Prime2, 13) * Prime1;

        static uint RotateLeft(uint value, int bits) => (value << bits) | (value >> (32 - bits));

        static uint ReadUInt32(ReadOnlySpan<byte> span) =>
            (uint)(span[0] | span[1] << 8 | span[2] << 16 | span[3] << 24);
    }
}
