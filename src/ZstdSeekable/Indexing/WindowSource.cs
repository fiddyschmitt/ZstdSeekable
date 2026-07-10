using System;
using System.Collections.Generic;
using System.IO;
using ZstdSeekable.Internal;

namespace ZstdSeekable
{
    /// <summary>Where a <see cref="ZstdIndex"/> fetches its (zstd-compressed) window snapshots from.
    /// Windows are loaded lazily on cold seeks, potentially from several threads at once.</summary>
    internal abstract class WindowSource : IDisposable
    {
        public abstract byte[] GetCompressedWindow(ZstdIndexPoint point);
        public virtual void Dispose() { }
    }

    /// <summary>Reads windows from an index file, opening it per call - naturally thread-safe with
    /// zero contention.</summary>
    internal sealed class FileWindowSource(string path) : WindowSource
    {
        public override byte[] GetCompressedWindow(ZstdIndexPoint point)
        {
            if (point.WindowCompressedLength == 0) return [];

            using var fs = File.OpenRead(path);
            fs.Position = point.WindowPositionInFile;
            var stored = new byte[point.WindowCompressedLength];
            fs.ReadExactly(stored);
            return stored;
        }
    }

    /// <summary>Reads windows from a seekable index stream, serialising access through a lock. The
    /// stream must remain open for the life of the index.</summary>
    internal sealed class StreamWindowSource(Stream stream, long baseOffset, bool leaveOpen) : WindowSource
    {
        readonly object gate = new();

        public override byte[] GetCompressedWindow(ZstdIndexPoint point)
        {
            if (point.WindowCompressedLength == 0) return [];

            var stored = new byte[point.WindowCompressedLength];
            lock (gate)
            {
                stream.Position = baseOffset + point.WindowPositionInFile;
                stream.ReadExactly(stored);
            }
            return stored;
        }

        public override void Dispose()
        {
            if (!leaveOpen) stream.Dispose();
        }
    }

    /// <summary>Holds all windows in memory (still zstd-compressed, so small). Used right after a
    /// build, and when loading from a non-seekable stream.</summary>
    internal sealed class MemoryWindowSource(Dictionary<ZstdIndexPoint, byte[]> windows) : WindowSource
    {
        public override byte[] GetCompressedWindow(ZstdIndexPoint point) =>
            windows.TryGetValue(point, out var window) ? window : [];
    }
}
