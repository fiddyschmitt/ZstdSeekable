using System;
using System.IO;
using Microsoft.Extensions.Logging;

namespace ZstdSeekable
{
    /// <summary>
    /// Opens any zstd stream for random-access reading, picking the right mechanism automatically:
    /// input already in the official seekable format (a seek table at EOF) is served through
    /// <see cref="ZstdSeekableReader"/> with instant seeks; ordinary zstd input gets a
    /// <see cref="ZstdIndex"/> of verified resume points (loaded from the supplied index
    /// file/stream when valid, otherwise built by one sequential decode and saved there) and is
    /// served through <see cref="ZstdIndexedStream"/>.
    /// </summary>
    public static class ZstdSeekableStream
    {
        /// <summary>True if <paramref name="compressed"/> ends with a valid official-format seek
        /// table. The stream's position is restored.</summary>
        public static bool HasSeekTable(Stream compressed) => ZstdSeekTable.TryRead(compressed, out _);

        /// <summary>True if the file at <paramref name="compressedPath"/> ends with a valid
        /// official-format seek table.</summary>
        public static bool HasSeekTable(string compressedPath)
        {
            using var fs = File.OpenRead(compressedPath);
            return HasSeekTable(fs);
        }

        /// <summary>Opens a seekable view over <paramref name="compressed"/>. Without a seek table
        /// the custom index is built in memory only (nothing is persisted) - prefer an overload with
        /// an index argument to avoid rebuilding on every open.</summary>
        public static Stream OpenRead(Stream compressed, ZstdSeekableOptions? options = null)
        {
            options ??= new ZstdSeekableOptions();
            ValidateCompressed(compressed);

            if (ZstdSeekTable.TryRead(compressed, out _))
                return OpenWithSeekTable(compressed, options);

            options.Logger?.LogDebug("No seek table found; building an in-memory index.");
            var index = ZstdIndex.Build(compressed, IndexOptions(options), options.Progress, options.CancellationToken);
            return new ZstdIndexedStream(compressed, index, ownsSource: !options.LeaveOpen, ownsIndex: true, gate: new object());
        }

        /// <summary>Opens a seekable view over the file at <paramref name="compressedPath"/> (index
        /// kept in memory only when one has to be built).</summary>
        public static Stream OpenRead(string compressedPath, ZstdSeekableOptions? options = null) =>
            OpenRead(OpenFile(compressedPath), OwnFile(options));

        /// <summary>Opens a seekable view over <paramref name="compressed"/>, using
        /// <paramref name="indexStream"/> to load/save the custom index when the input has no seek
        /// table. The index stream is always left open and, if it is seekable, must remain open while
        /// the returned stream is in use.</summary>
        public static Stream OpenRead(Stream compressed, Stream indexStream, ZstdSeekableOptions? options = null)
        {
            options ??= new ZstdSeekableOptions();
            ValidateCompressed(compressed);

            if (ZstdSeekTable.TryRead(compressed, out _))
                return OpenWithSeekTable(compressed, options);

            var index = ZstdIndex.LoadOrBuild(compressed, indexStream, IndexOptions(options), options.Progress, options.CancellationToken);
            return new ZstdIndexedStream(compressed, index, ownsSource: !options.LeaveOpen, ownsIndex: true, gate: new object());
        }

        /// <summary>Opens a seekable view over <paramref name="compressed"/>, using the file at
        /// <paramref name="indexPath"/> to load/save the custom index when the input has no seek
        /// table.</summary>
        public static Stream OpenRead(Stream compressed, string indexPath, ZstdSeekableOptions? options = null)
        {
            options ??= new ZstdSeekableOptions();
            ValidateCompressed(compressed);
            if (indexPath == null) throw new ArgumentNullException(nameof(indexPath));

            if (ZstdSeekTable.TryRead(compressed, out _))
                return OpenWithSeekTable(compressed, options);

            var index = ZstdIndex.LoadOrBuild(compressed, indexPath, IndexOptions(options), options.Progress, options.CancellationToken);
            return new ZstdIndexedStream(compressed, index, ownsSource: !options.LeaveOpen, ownsIndex: true, gate: new object());
        }

        /// <summary>Opens a seekable view over the file at <paramref name="compressedPath"/>, using
        /// <paramref name="indexStream"/> for the custom index when needed.</summary>
        public static Stream OpenRead(string compressedPath, Stream indexStream, ZstdSeekableOptions? options = null) =>
            OpenRead(OpenFile(compressedPath), indexStream, OwnFile(options));

        /// <summary>Opens a seekable view over the file at <paramref name="compressedPath"/>, using
        /// the file at <paramref name="indexPath"/> for the custom index when needed.
        /// <paramref name="indexPath"/> defaults to <c>compressedPath + ".zsi"</c>.</summary>
        public static Stream OpenRead(string compressedPath, string? indexPath, ZstdSeekableOptions? options = null) =>
            OpenRead(OpenFile(compressedPath), indexPath ?? compressedPath + ".zsi", OwnFile(options));

        static Stream OpenWithSeekTable(Stream compressed, ZstdSeekableOptions options)
        {
            options.Logger?.LogDebug("Seek table found; using the official seekable format directly.");
            var readerOptions = new ZstdSeekableReaderOptions
            {
                VerifyChecksums = options.VerifyChecksums,
                Logger = options.Logger,
            };
            return new ZstdSeekableReader(compressed, readerOptions, options.LeaveOpen);
        }

        static ZstdIndexOptions IndexOptions(ZstdSeekableOptions options)
        {
            var indexOptions = options.Index ?? new ZstdIndexOptions();
            indexOptions.Logger ??= options.Logger;
            return indexOptions;
        }

        static FileStream OpenFile(string compressedPath) =>
            new(compressedPath, FileMode.Open, FileAccess.Read, FileShare.Read);

        //file overloads open the FileStream themselves, so the returned stream must always close it
        static ZstdSeekableOptions OwnFile(ZstdSeekableOptions? options)
        {
            options ??= new ZstdSeekableOptions();
            options.LeaveOpen = false;
            return options;
        }

        static void ValidateCompressed(Stream compressed)
        {
            if (compressed == null) throw new ArgumentNullException(nameof(compressed));
            if (!compressed.CanSeek) throw new ArgumentException("The compressed stream must be seekable - both mechanisms need random access to the compressed bytes.", nameof(compressed));
        }
    }
}
