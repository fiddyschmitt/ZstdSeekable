using System.Collections.Generic;

namespace ZstdSeekable.Internal
{
    /// <summary>One block of the compressed-to-uncompressed offset table behind a seekable stream.</summary>
    internal sealed class Mapping
    {
        public long CompressedStartByte;
        public long CompressedEndByte;
        public long UncompressedStartByte;
        public long UncompressedEndByte;
        public int Tag;     //index into the owner's frame/point table

        /// <inheritdoc/>
        public override string ToString() =>
            $"Compressed {CompressedStartByte:N0} == Uncompressed {UncompressedStartByte:N0} ({UncompressedEndByte - UncompressedStartByte:N0} bytes uncompressed)";
    }

    internal static class BlockMap
    {
        /// <summary>Binary search for the mapping whose uncompressed range contains <paramref name="position"/>.</summary>
        public static Mapping? Find(List<Mapping> blocks, long position)
        {
            var lo = 0;
            var hi = blocks.Count - 1;
            while (lo <= hi)
            {
                var mid = lo + (hi - lo) / 2;
                var mapping = blocks[mid];
                if (position < mapping.UncompressedStartByte) hi = mid - 1;
                else if (position >= mapping.UncompressedEndByte) lo = mid + 1;
                else return mapping;
            }
            return null;
        }
    }
}
