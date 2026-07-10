using System.IO;

namespace ZstdSeekable.Internal
{
#if NETSTANDARD2_0
    internal static class StreamPolyfills
    {
        public static void ReadExactly(this Stream stream, byte[] buffer) =>
            ReadExactly(stream, buffer, 0, buffer.Length);

        public static void ReadExactly(this Stream stream, byte[] buffer, int offset, int count)
        {
            var total = 0;
            while (total < count)
            {
                var read = stream.Read(buffer, offset + total, count - total);
                if (read == 0) throw new EndOfStreamException();
                total += read;
            }
        }

        public static int ReadAtLeast(this Stream stream, byte[] buffer, int minimumBytes, bool throwOnEndOfStream = true)
        {
            var total = 0;
            while (total < minimumBytes)
            {
                var read = stream.Read(buffer, total, buffer.Length - total);
                if (read == 0)
                {
                    if (throwOnEndOfStream) throw new EndOfStreamException();
                    return total;
                }
                total += read;
            }
            return total;
        }
    }
#endif

    internal static class FileHelpers
    {
        public static void MoveOverwrite(string source, string destination)
        {
#if NETSTANDARD2_0
            if (File.Exists(destination)) File.Delete(destination);
            File.Move(source, destination);
#else
            File.Move(source, destination, overwrite: true);
#endif
        }
    }
}
