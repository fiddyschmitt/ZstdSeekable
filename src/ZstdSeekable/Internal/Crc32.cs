namespace ZstdSeekable.Internal
{
    /// <summary>Standard IEEE (zlib) CRC-32, used for the index header's stream fingerprint.</summary>
    internal static class Crc32
    {
        static readonly uint[] Table = BuildTable();

        static uint[] BuildTable()
        {
            var table = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                var c = n;
                for (var k = 0; k < 8; k++)
                {
                    c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                }
                table[n] = c;
            }
            return table;
        }

        public static uint Compute(byte[] buffer, int offset, int count)
        {
            var crc = 0xFFFFFFFFu;
            for (var i = 0; i < count; i++)
            {
                crc = Table[(crc ^ buffer[offset + i]) & 0xFF] ^ (crc >> 8);
            }
            return crc ^ 0xFFFFFFFFu;
        }
    }
}
