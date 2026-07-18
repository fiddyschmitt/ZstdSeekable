using System;
using System.Collections.Generic;
using System.IO;
using ZstdSeekable.Internal;

namespace ZstdSeekable
{
    /// <summary>Where a <see cref="ZstdIndex"/> fetches its (zstd-compressed) blobs from: window
    /// snapshots and, for v4 exact points, entropy-state snapshots. Blobs are loaded lazily on cold
    /// seeks, potentially from several threads at once.</summary>
    internal abstract class WindowSource : IDisposable
    {
        public abstract byte[] GetCompressedWindow(ZstdIndexPoint point);

        /// <summary>The zstd-compressed entropy-state blob of a v4 exact point ([] otherwise).</summary>
        public abstract byte[] GetCompressedEntropy(ZstdIndexPoint point);

        public virtual void Dispose() { }
    }

    /// <summary>Reads blobs from an index file, opening it per call - naturally thread-safe with
    /// zero contention.</summary>
    internal sealed class FileWindowSource(string path) : WindowSource
    {
        public override byte[] GetCompressedWindow(ZstdIndexPoint point) =>
            ReadAt(point.WindowPositionInFile, point.WindowCompressedLength);

        public override byte[] GetCompressedEntropy(ZstdIndexPoint point) =>
            ReadAt(point.EntropyPositionInFile, point.EntropyCompressedLength);

        byte[] ReadAt(long position, int length)
        {
            if (length == 0) return [];

            using var fs = File.OpenRead(path);
            fs.Position = position;
            var stored = new byte[length];
            fs.ReadExactly(stored);
            return stored;
        }
    }

    /// <summary>Reads blobs from a seekable index stream, serialising access through a lock. The
    /// stream must remain open for the life of the index.</summary>
    internal sealed class StreamWindowSource(Stream stream, long baseOffset, bool leaveOpen) : WindowSource
    {
        readonly object gate = new();

        public override byte[] GetCompressedWindow(ZstdIndexPoint point) =>
            ReadAt(point.WindowPositionInFile, point.WindowCompressedLength);

        public override byte[] GetCompressedEntropy(ZstdIndexPoint point) =>
            ReadAt(point.EntropyPositionInFile, point.EntropyCompressedLength);

        byte[] ReadAt(long position, int length)
        {
            if (length == 0) return [];

            var stored = new byte[length];
            lock (gate)
            {
                stream.Position = baseOffset + position;
                stream.ReadExactly(stored);
            }
            return stored;
        }

        public override void Dispose()
        {
            if (!leaveOpen) stream.Dispose();
        }
    }

    /// <summary>Holds all blobs in memory (still zstd-compressed, so small). Used right after a
    /// build, and when loading from a non-seekable stream.</summary>
    internal sealed class MemoryWindowSource(
        Dictionary<ZstdIndexPoint, byte[]> windows,
        Dictionary<ZstdIndexPoint, byte[]>? entropies = null) : WindowSource
    {
        public override byte[] GetCompressedWindow(ZstdIndexPoint point) =>
            windows.TryGetValue(point, out var window) ? window : [];

        public override byte[] GetCompressedEntropy(ZstdIndexPoint point) =>
            entropies != null && entropies.TryGetValue(point, out var entropy) ? entropy : [];
    }
}
