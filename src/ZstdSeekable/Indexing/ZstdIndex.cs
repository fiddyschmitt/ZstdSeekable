using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Extensions.Logging;
using ZstdSeekable.Internal;
using ZstdSharp.Unsafe;

namespace ZstdSeekable
{
    /// <summary>
    /// Random-access index over a standard (non-seekable-format) zstd stream: verified resume points
    /// every ~<see cref="ZstdIndexOptions.TargetSpanBytes"/> of output, each carrying the preceding
    /// ≤windowSize of content (zstd-compressed in the index, loaded lazily).
    ///
    /// Built in a SINGLE sequential pass: at any moment two shadow decoders run alongside the true
    /// decode - one for the last confirmed point (insurance covering its still-open span) and one for
    /// the current candidate. A candidate that survives a full span byte-identical is confirmed,
    /// which seals its predecessor (span fully verified by the insurance shadow) and - when building
    /// to a file or seekable stream - appends it to the destination immediately, keeping build
    /// memory flat. A diverging candidate is simply re-armed at a later boundary. A diverging
    /// CONFIRMED shadow (divergence deeper than a whole span; never yet observed on real data)
    /// triggers a rebuild with frame-start points only: never wrong data, at worst a coarse index.
    ///
    /// A persisted index grows incrementally (header counts zeroed until finalisation), so an
    /// interrupted build RESUMES: sealed points are kept, and the build fast-forwards from the last
    /// sealed frame-start point (a frame-start resume IS the true decode, so the verification truth
    /// chain stays rooted). The persisted format is binary ("ZSTZRAN2"; "ZSTZRAN1" files from older
    /// versions still load), all integers little-endian.
    /// </summary>
    public sealed class ZstdIndex : IDisposable
    {
        static readonly byte[] MagicV1 = "ZSTZRAN1"u8.ToArray();
        static readonly byte[] MagicV2 = "ZSTZRAN2"u8.ToArray();
        const int HeaderSize = 8 + 8 + 4;                   //magic, totalUncompressed, pointCount (both formats)
        const int RecordSizeV1 = 8 + 8 + 1 + 1 + 16 + 8 + 4;
        const int RecordSizeV2 = 8 + 8 + 1 + 1 + 4;         //fixed part; the compressed window follows inline

        readonly List<ZstdIndexPoint> points;
        readonly WindowSource windowSource;

        /// <summary>The verified resume points, in stream order. The first point is always offset 0.</summary>
        public IReadOnlyList<ZstdIndexPoint> Points => points;

        /// <summary>Total decompressed size of the indexed stream.</summary>
        public long UncompressedLength { get; }

        ZstdIndex(List<ZstdIndexPoint> points, long uncompressedLength, WindowSource windowSource)
        {
            this.points = points;
            this.windowSource = windowSource;
            UncompressedLength = uncompressedLength;
        }

        /// <summary>Releases the index's window source (closes the index stream for indexes loaded
        /// from a stream with leaveOpen: false).</summary>
        public void Dispose() => windowSource.Dispose();

        /// <summary>Loads and decompresses one point's window. Thread-safe.</summary>
        internal byte[] LoadWindow(ZstdIndexPoint point)
        {
            var stored = windowSource.GetCompressedWindow(point);
            if (stored.Length == 0) return [];

            using var decompressor = new ZstdSharp.Decompressor();
            return decompressor.Unwrap(stored).ToArray();
        }

        //================================================================= load

        /// <summary>Loads an index from a file (either format version). Window snapshots are read
        /// lazily (the file is opened per window load), so the file must remain in place while the
        /// index is in use.</summary>
        public static ZstdIndex Load(string indexPath)
        {
            ParsedIndex parsed;
            using (var fs = File.OpenRead(indexPath))
            {
                parsed = Parse(fs, eagerWindows: null, tolerateTruncatedTail: false);
            }
            if (!parsed.Complete) throw new InvalidDataException("The zstd index was not finalised (interrupted build). LoadOrBuild can resume it.");
            return new ZstdIndex(parsed.Points, parsed.TotalLength, new FileWindowSource(indexPath));
        }

        /// <summary>
        /// Loads an index from a stream, starting at its current position. A seekable stream is read
        /// lazily (it must remain open while the index is in use; window loads seek it under a lock).
        /// A non-seekable stream is read eagerly into memory and can be discarded afterwards.
        /// </summary>
        public static ZstdIndex Load(Stream indexStream, bool leaveOpen = false)
        {
            if (indexStream == null) throw new ArgumentNullException(nameof(indexStream));

            if (indexStream.CanSeek)
            {
                var baseOffset = indexStream.Position;
                var parsed = Parse(indexStream, eagerWindows: null, tolerateTruncatedTail: false);
                if (!parsed.Complete) throw new InvalidDataException("The zstd index was not finalised (interrupted build). LoadOrBuild can resume it.");
                return new ZstdIndex(parsed.Points, parsed.TotalLength, new StreamWindowSource(indexStream, baseOffset, leaveOpen));
            }
            else
            {
                var windows = new Dictionary<ZstdIndexPoint, byte[]>();
                var parsed = Parse(indexStream, eagerWindows: windows, tolerateTruncatedTail: false);
                if (!parsed.Complete) throw new InvalidDataException("The zstd index was not finalised (interrupted build). LoadOrBuild can resume it.");
                if (!leaveOpen) indexStream.Dispose();
                return new ZstdIndex(parsed.Points, parsed.TotalLength, new MemoryWindowSource(windows));
            }
        }

        struct ParsedIndex
        {
            public List<ZstdIndexPoint> Points;
            public long TotalLength;
            public bool Complete;
        }

        /// <summary>
        /// Reads either format version from the stream's current position. Window positions in the
        /// returned points are relative to that position. When <paramref name="eagerWindows"/> is
        /// non-null the window bytes are collected into it (required for non-seekable streams);
        /// otherwise they are skipped. <paramref name="tolerateTruncatedTail"/>: an unfinalised v2
        /// index may end mid-record after an interruption - keep the complete points instead of
        /// throwing (used to resume builds).
        /// </summary>
        static ParsedIndex Parse(Stream s, Dictionary<ZstdIndexPoint, byte[]>? eagerWindows, bool tolerateTruncatedTail)
        {
            var header = new byte[HeaderSize];
            if (ReadUpTo(s, header, HeaderSize) < HeaderSize) throw new InvalidDataException("zstd index truncated (header).");

            var totalLength = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(8));
            var count = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(16));

            if (header.AsSpan(0, 8).SequenceEqual(MagicV2)) return ParseV2(s, totalLength, count, eagerWindows, tolerateTruncatedTail);
            if (header.AsSpan(0, 8).SequenceEqual(MagicV1)) return ParseV1(s, totalLength, count, eagerWindows);
            throw new InvalidDataException("Not a zstd random-access index (bad magic).");
        }

        //v1 ("ZSTZRAN1", pre-0.2.0 and older clonezilla-util): fixed records carrying a span MD5 and
        //an absolute window position, followed by a blob of all windows in point order
        static ParsedIndex ParseV1(Stream s, long totalLength, int count, Dictionary<ZstdIndexPoint, byte[]>? eagerWindows)
        {
            if (count <= 0) throw new InvalidDataException("zstd index has no points.");

            var loaded = new List<ZstdIndexPoint>(count);
            var record = new byte[RecordSizeV1];
            for (var i = 0; i < count; i++)
            {
                if (ReadUpTo(s, record, RecordSizeV1) < RecordSizeV1) throw new InvalidDataException("zstd index truncated (records).");

                loaded.Add(new ZstdIndexPoint(
                    uncompressedOffset: BinaryPrimitives.ReadInt64LittleEndian(record.AsSpan(0)),
                    compressedOffset: BinaryPrimitives.ReadInt64LittleEndian(record.AsSpan(8)),
                    isFrameStart: record[16] != 0,
                    windowDescriptor: record[17])
                {
                    //bytes 18..33 are the span MD5, used only by the old two-pass builder - discarded
                    WindowPositionInFile = BinaryPrimitives.ReadInt64LittleEndian(record.AsSpan(34)),
                    WindowCompressedLength = BinaryPrimitives.ReadInt32LittleEndian(record.AsSpan(42)),
                });
            }

            if (eagerWindows != null)
            {
                //the windows sit after the records, concatenated in point order
                foreach (var point in loaded)
                {
                    if (point.WindowCompressedLength <= 0) continue;
                    var window = new byte[point.WindowCompressedLength];
                    if (ReadUpTo(s, window, window.Length) < window.Length) throw new InvalidDataException("zstd index truncated (windows).");
                    eagerWindows[point] = window;
                }
            }

            return new ParsedIndex { Points = loaded, TotalLength = totalLength, Complete = true };
        }

        //v2 ("ZSTZRAN2", 0.2.0+ and current clonezilla-util): each fixed record is followed
        //immediately by its compressed window; zeroed header counts mark an unfinalised index
        static ParsedIndex ParseV2(Stream s, long totalLength, int count, Dictionary<ZstdIndexPoint, byte[]>? eagerWindows, bool tolerateTruncatedTail)
        {
            var complete = count > 0;
            var loaded = new List<ZstdIndexPoint>();
            var record = new byte[RecordSizeV2];
            long offset = HeaderSize;   //relative to the index's first byte

            while (complete ? loaded.Count < count : true)
            {
                var got = ReadUpTo(s, record, RecordSizeV2);
                if (got < RecordSizeV2)
                {
                    if (complete || (!tolerateTruncatedTail && got != 0)) throw new InvalidDataException("zstd index truncated.");
                    break;
                }

                var uncompressedOffset = BinaryPrimitives.ReadInt64LittleEndian(record.AsSpan(0));
                var compressedOffset = BinaryPrimitives.ReadInt64LittleEndian(record.AsSpan(8));
                var windowLength = BinaryPrimitives.ReadInt32LittleEndian(record.AsSpan(18));

                if (windowLength < 0 || uncompressedOffset < 0 || compressedOffset < 0)
                {
                    if (tolerateTruncatedTail && !complete) break;
                    throw new InvalidDataException("zstd index point has implausible values.");
                }

                var point = new ZstdIndexPoint(uncompressedOffset, compressedOffset, isFrameStart: record[16] != 0, windowDescriptor: record[17])
                {
                    WindowPositionInFile = offset + RecordSizeV2,
                    WindowCompressedLength = windowLength,
                };

                if (eagerWindows != null)
                {
                    var window = new byte[windowLength];
                    if (ReadUpTo(s, window, windowLength) < windowLength)
                    {
                        if (tolerateTruncatedTail && !complete) break;
                        throw new InvalidDataException("zstd index truncated inside a window.");
                    }
                    if (windowLength > 0) eagerWindows[point] = window;
                }
                else
                {
                    if (s.Position + windowLength > s.Length)
                    {
                        if (tolerateTruncatedTail && !complete) break;
                        throw new InvalidDataException("zstd index truncated inside a window.");
                    }
                    s.Seek(windowLength, SeekOrigin.Current);
                }

                offset += RecordSizeV2 + windowLength;
                loaded.Add(point);
            }

            return new ParsedIndex { Points = loaded, TotalLength = totalLength, Complete = complete };
        }

        static int ReadUpTo(Stream s, byte[] buffer, int count)
        {
            var total = 0;
            while (total < count)
            {
                var n = s.Read(buffer, total, count - total);
                if (n == 0) break;
                total += n;
            }
            return total;
        }

        //================================================================= save

        /// <summary>Saves the index atomically (written to a temporary file, then moved over
        /// <paramref name="indexPath"/>). Always writes the current ("ZSTZRAN2") format.</summary>
        public void Save(string indexPath)
        {
            var tempPath = indexPath + ".tmp";
            using (var fs = File.Create(tempPath))
            {
                Save(fs);
            }
            FileHelpers.MoveOverwrite(tempPath, indexPath);
        }

        /// <summary>Writes a finalised index to <paramref name="destination"/> at its current
        /// position. The stream is left open. Always writes the current ("ZSTZRAN2") format.</summary>
        public void Save(Stream destination)
        {
            WriteHeader(destination, UncompressedLength, points.Count);
            var record = new byte[RecordSizeV2];
            foreach (var point in points)
            {
                var window = windowSource.GetCompressedWindow(point);
                FillRecord(record, point, window.Length);
                destination.Write(record, 0, record.Length);
                destination.Write(window, 0, window.Length);
            }
            destination.Flush();
        }

        static void WriteHeader(Stream s, long totalLength, int count)
        {
            var header = new byte[HeaderSize];
            MagicV2.CopyTo(header, 0);
            BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(8), totalLength);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(16), count);
            s.Write(header, 0, header.Length);
        }

        static void FillRecord(byte[] record, ZstdIndexPoint point, int windowLength)
        {
            BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(0), point.UncompressedOffset);
            BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(8), point.CompressedOffset);
            record[16] = point.IsFrameStart ? (byte)1 : (byte)0;
            record[17] = point.WindowDescriptor;
            BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(18), windowLength);
        }

        //================================================================= load-or-build

        /// <summary>
        /// Loads the index at <paramref name="indexPath"/> if it exists and is valid; otherwise
        /// builds one from <paramref name="compressed"/> into <c>indexPath + ".wip"</c> (sealed
        /// points are flushed as they are verified, so build memory stays flat and an interrupted
        /// build resumes on the next call) and moves it into place when finalised.
        /// </summary>
        public static ZstdIndex LoadOrBuild(Stream compressed, string indexPath, ZstdIndexOptions? options = null,
                                            IProgress<ZstdIndexProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            options ??= new ZstdIndexOptions();
            if (File.Exists(indexPath))
            {
                try
                {
                    return Load(indexPath);
                }
                catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or IOException)
                {
                    options.Logger?.LogWarning("Could not load zstd index {IndexPath} ({Message}). Rebuilding.", Path.GetFileName(indexPath), ex.Message);
                }
            }

            ValidateForBuild(compressed);
            var wipPath = indexPath + ".wip";
            List<ZstdIndexPoint> built;
            long totalLength;
            using (var wip = new FileStream(wipPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read))
            {
                (built, totalLength) = BuildIncremental(compressed, wip, baseOffset: 0, options, progress, cancellationToken);
            }
            FileHelpers.MoveOverwrite(wipPath, indexPath);
            return new ZstdIndex(built, totalLength, new FileWindowSource(indexPath));
        }

        /// <summary>
        /// Loads the index from <paramref name="indexStream"/> if it holds a valid one; otherwise
        /// builds one from <paramref name="compressed"/> into the stream. A seekable stream is
        /// written incrementally (flat build memory) and an interrupted build resumes on the next
        /// call. The index stream is always left open (the caller owns it), and must remain open
        /// while the returned index is in use if it was loaded/built lazily (seekable streams).
        /// </summary>
        public static ZstdIndex LoadOrBuild(Stream compressed, Stream indexStream, ZstdIndexOptions? options = null,
                                            IProgress<ZstdIndexProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            if (indexStream == null) throw new ArgumentNullException(nameof(indexStream));
            options ??= new ZstdIndexOptions();

            if (indexStream.CanSeek)
            {
                var basePosition = indexStream.Position;
                if (indexStream.CanRead && indexStream.Length - basePosition >= HeaderSize)
                {
                    try
                    {
                        return Load(indexStream, leaveOpen: true);
                    }
                    catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException)
                    {
                        options.Logger?.LogWarning("Could not load the zstd index from the supplied stream ({Message}). Building.", ex.Message);
                        indexStream.Position = basePosition;
                    }
                }
                if (!indexStream.CanRead || !indexStream.CanWrite)
                    throw new ArgumentException("A seekable index stream must be readable and writable to build into.", nameof(indexStream));

                ValidateForBuild(compressed);
                var (built, totalLength) = BuildIncremental(compressed, indexStream, basePosition, options, progress, cancellationToken);
                try { indexStream.SetLength(indexStream.Position); } catch (NotSupportedException) { }
                indexStream.Flush();
                return new ZstdIndex(built, totalLength, new StreamWindowSource(indexStream, basePosition, leaveOpen: true));
            }

            if (indexStream.CanRead && !indexStream.CanWrite)
            {
                //read-only pipe: it can only be a source
                return Load(indexStream, leaveOpen: true);
            }

            //non-seekable writable stream: it can only be a destination, and cannot be finalised
            //in place - build in memory, then stream the complete index out
            ValidateForBuild(compressed);
            var index = Build(compressed, options, progress, cancellationToken);
            index.Save(indexStream);
            indexStream.Flush();
            return index;
        }

        //================================================================= build

        /// <summary>
        /// Builds an in-memory index by decoding <paramref name="compressed"/> once from the start,
        /// verifying every resume point as it goes (see the class remarks). Never fails on valid
        /// zstd input: in the theoretical worst case the index degrades to frame-start points only -
        /// correct, but slow to seek within giant frames. Compressed window snapshots are held in
        /// memory; prefer <see cref="LoadOrBuild(Stream, string, ZstdIndexOptions?, IProgress{ZstdIndexProgress}?, CancellationToken)"/>
        /// for large streams (flat memory, resumable).
        /// </summary>
        public static ZstdIndex Build(Stream compressed, ZstdIndexOptions? options = null,
                                      IProgress<ZstdIndexProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            ValidateForBuild(compressed);
            options ??= new ZstdIndexOptions();

            var windows = new Dictionary<ZstdIndexPoint, byte[]>();
            var sink = new MemorySink(windows);
            var (built, totalLength) = BuildWithDivergenceFallback(compressed, sink, options, progress, cancellationToken);
            return new ZstdIndex(built, totalLength, new MemoryWindowSource(windows));
        }

        static void ValidateForBuild(Stream compressed)
        {
            if (compressed == null) throw new ArgumentNullException(nameof(compressed));
            if (!compressed.CanSeek) throw new ArgumentException("The compressed stream must be seekable to build an index over it.", nameof(compressed));
        }

        static (List<ZstdIndexPoint> Points, long TotalLength) BuildIncremental(Stream compressed, Stream destination, long baseOffset,
            ZstdIndexOptions options, IProgress<ZstdIndexProgress>? progress, CancellationToken cancellationToken)
        {
            //an interrupted earlier build left sealed, fully verified points - keep them and continue
            List<ZstdIndexPoint> sealedPoints = [];
            if (destination.Length - baseOffset >= HeaderSize)
            {
                try
                {
                    destination.Position = baseOffset;
                    var parsed = Parse(destination, eagerWindows: null, tolerateTruncatedTail: true);
                    sealedPoints = parsed.Points;
                }
                catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException)
                {
                    options.Logger?.LogWarning("Could not read the partial zstd index ({Message}). Starting fresh.", ex.Message);
                    sealedPoints = [];
                }
            }

            var resuming = sealedPoints.Count > 0;
            var sink = new StreamSink(destination, baseOffset, sealedPoints);
            try
            {
                return BuildWithDivergenceFallback(compressed, sink, options, progress, cancellationToken);
            }
            catch (InvalidDataException ex) when (resuming)
            {
                //e.g. the compressed stream does not match the partial index - retry from scratch
                options.Logger?.LogWarning("Could not resume the zstd index build ({Message}). Rebuilding from scratch.", ex.Message);
                sink.Reset();
                return BuildWithDivergenceFallback(compressed, sink, options, progress, cancellationToken);
            }
        }

        static (List<ZstdIndexPoint> Points, long TotalLength) BuildWithDivergenceFallback(Stream compressed, IIndexSink sink,
            ZstdIndexOptions options, IProgress<ZstdIndexProgress>? progress, CancellationToken cancellationToken)
        {
            try
            {
                return BuildCore(compressed, sink, options, progress, cancellationToken, options.FrameStartsOnly);
            }
            catch (InsuranceDivergenceException ex)
            {
                //divergence deeper than a whole span - never observed on real data. Degrade to
                //frame-start-only points (always sound) rather than fail: never wrong data.
                options.Logger?.LogWarning("{Message} Rebuilding with frame-start points only (correct, but slow to seek within large frames).", ex.Message);
                sink.Reset();
                return BuildCore(compressed, sink, options, progress, cancellationToken, frameStartsOnly: true);
            }
        }

        //internal control flow only (caught by BuildWithDivergenceFallback); never escapes this class
        sealed class InsuranceDivergenceException(long uncompressedOffset)
            : Exception($"zstd resume state diverged deeper than a span at {uncompressedOffset:N0}.");

        static unsafe (List<ZstdIndexPoint> Points, long TotalLength) BuildCore(Stream compressed, IIndexSink sink,
            ZstdIndexOptions options, IProgress<ZstdIndexProgress>? progress, CancellationToken cancellationToken, bool frameStartsOnly)
        {
            var logger = options.Logger;
            var targetSpanBytes = Math.Max(64 * 1024, options.TargetSpanBytes);
            var sealedPoints = sink.SealedPoints;
            var resuming = sealedPoints.Count > 0;
            var compressedTotal = compressed.Length;

            if (resuming) logger?.LogInformation("Resuming the zstd random-access index ({Count:N0} verified points already sealed).", sealedPoints.Count);
            else logger?.LogInformation("Creating a zstd random-access index ({Length:N0} compressed bytes).", compressedTotal);

            var reader = new ZstdBlockReader(compressed);
            using var windowCompressor = new ZstdSharp.Compressor(options.WindowCompressionLevel);

            var main = Methods.ZSTD_createDCtx();
            Methods.ZSTD_DCtx_setParameter(main, ZSTD_dParameter.ZSTD_d_windowLogMax, 31);

            //the two in-flight points and their shadows
            ZstdIndexPoint? confirmed = null;       //last confirmed point; its span is still open
            byte[] confirmedWindowRaw = [];
            ShadowDecoder? confirmedShadow = null;  //null for frame-start points (exact by construction)
            var confirmedAlreadyWritten = false;    //true for a re-adopted point after a resume
            ZstdIndexPoint? candidate = null;
            byte[] candidateWindowRaw = [];
            ShadowDecoder? candidateShadow = null;

            try
            {
                var outBuf = new byte[1 << 20];     //one block regenerates ≤128 KB

                long uncompressedPos = 0;
                long lastProgressReportAt = 0;

                //rolling ring of true output (window snapshots), indexed by absolute position % windowSize
                var ring = Array.Empty<byte>();
                long frameWindowSize = 0;
                byte frameWindowDescriptor = 0;

                //---- resume: fast-forward the true decode from the last sealed frame-start point,
                //then re-adopt the last sealed point as the open confirmed point ----
                long fastForwardUntil = 0;
                var adoptPending = false;
                ZstdIndexPoint? adoptPoint = null;
                if (resuming)
                {
                    var frameStart = sealedPoints.FindLast(p => p.IsFrameStart)
                        ?? throw new InvalidDataException("The partial zstd index has no frame-start point.");
                    adoptPoint = sealedPoints[sealedPoints.Count - 1];
                    adoptPending = true;
                    fastForwardUntil = adoptPoint.UncompressedOffset;
                    uncompressedPos = frameStart.UncompressedOffset;
                    compressed.Seek(frameStart.CompressedOffset, SeekOrigin.Begin);
                    lastProgressReportAt = frameStart.CompressedOffset;
                    logger?.LogInformation("Fast-forwarding {Bytes:N0} bytes to the last verified point (a zstd point cannot checkpoint full decoder state the way a gzip point can).", fastForwardUntil - uncompressedPos);
                }
                else
                {
                    compressed.Seek(0, SeekOrigin.Begin);
                }

                byte[] SnapshotWindow()
                {
                    var size = (int)Math.Min(uncompressedPos, frameWindowSize);
                    var snapshot = new byte[size];
                    if (size > 0)
                    {
                        var ringPos = (int)(uncompressedPos % frameWindowSize);
                        if (uncompressedPos <= frameWindowSize)
                        {
                            Array.Copy(ring, 0, snapshot, 0, size);
                        }
                        else
                        {
                            Array.Copy(ring, ringPos, snapshot, 0, frameWindowSize - ringPos);
                            Array.Copy(ring, 0, snapshot, frameWindowSize - ringPos, ringPos);
                        }
                    }
                    return snapshot;
                }

                void SealConfirmed()
                {
                    if (confirmed == null) return;
                    if (!confirmedAlreadyWritten)
                    {
                        var windowCompressed = confirmedWindowRaw.Length == 0 ? [] : windowCompressor.Wrap(confirmedWindowRaw).ToArray();
                        sink.Append(confirmed, windowCompressed);
                    }
                    confirmedAlreadyWritten = false;
                    confirmedShadow?.Dispose();
                    confirmedShadow = null;
                    confirmed = null;
                    confirmedWindowRaw = [];
                }

                void DropCandidate()
                {
                    candidateShadow?.Dispose();
                    candidateShadow = null;
                    candidate = null;
                    candidateWindowRaw = [];
                }

                //candidate survived its whole span: it becomes the confirmed point (sealing its predecessor)
                void PromoteCandidate()
                {
                    SealConfirmed();
                    confirmed = candidate;
                    confirmedWindowRaw = candidateWindowRaw;
                    confirmedShadow = candidateShadow;
                    candidate = null;
                    candidateWindowRaw = [];
                    candidateShadow = null;
                }

                while (true)
                {
                    var frameStartOffset = reader.Position;
                    var frameKind = reader.BeginFrame(out var headerBytes, out frameWindowSize, out frameWindowDescriptor, out var hasChecksum);
                    if (frameKind == FrameKind.EndOfStream) break;
                    if (frameKind == FrameKind.Skippable) continue;

                    if (frameWindowSize > 1L << 30) throw new InvalidDataException($"zstd window {frameWindowSize:N0} too large to index.");
                    if (ring.Length < frameWindowSize) ring = new byte[frameWindowSize];

                    var fastForwarding = uncompressedPos < fastForwardUntil;

                    //a frame start is a perfect (stateless) resume point: confirm it immediately
                    if (!fastForwarding)
                    {
                        if (adoptPending)
                        {
                            //the adoption point can only coincide with a frame start here (a mid-frame
                            //adoption happens at a block boundary inside the loop below)
                            if (uncompressedPos != adoptPoint!.UncompressedOffset || frameStartOffset != adoptPoint.CompressedOffset)
                                throw new InvalidDataException("The compressed stream does not match the partial index (resume position not found).");

                            confirmed = adoptPoint;
                            confirmedAlreadyWritten = true;
                            confirmedWindowRaw = [];
                            confirmedShadow = null;     //frame start: exact by construction
                            adoptPending = false;
                        }
                        else
                        {
                            SealConfirmed();
                            confirmed = new ZstdIndexPoint(uncompressedPos, frameStartOffset, isFrameStart: true, frameWindowDescriptor);
                            confirmedWindowRaw = [];
                            confirmedShadow = null;     //exact by construction
                        }
                    }

                    if (!ZstdFrameHelpers.Feed(main, headerBytes, outBuf, out _)) throw new InvalidDataException("zstd frame header rejected.");

                    var lastBlock = false;
                    while (!lastBlock)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        var boundaryCompressedOffset = reader.Position;
                        fastForwarding = uncompressedPos < fastForwardUntil;

                        if (!fastForwarding)
                        {
                            if (adoptPending)
                            {
                                //fast-forward complete: re-adopt the last sealed point as the open
                                //confirmed point, with a fresh insurance shadow from the rebuilt ring
                                if (uncompressedPos != adoptPoint!.UncompressedOffset || boundaryCompressedOffset != adoptPoint.CompressedOffset)
                                    throw new InvalidDataException("The compressed stream does not match the partial index (resume position not found).");

                                confirmed = adoptPoint;
                                confirmedAlreadyWritten = true;
                                confirmedWindowRaw = SnapshotWindow();
                                confirmedShadow = new ShadowDecoder(confirmedWindowRaw, adoptPoint.WindowDescriptor);
                                if (!confirmedShadow.Healthy) throw new InvalidDataException("Could not re-establish the resume point's shadow decoder.");
                                adoptPending = false;
                            }

                            //confirm the candidate once it has survived one whole span byte-identical
                            if (candidate != null && uncompressedPos >= candidate.UncompressedOffset + targetSpanBytes)
                            {
                                PromoteCandidate();
                            }

                            //arm a new candidate a span past the confirmed point
                            if (!frameStartsOnly && candidate == null && confirmed != null && uncompressedPos >= confirmed.UncompressedOffset + targetSpanBytes)
                            {
                                candidateWindowRaw = SnapshotWindow();
                                candidate = new ZstdIndexPoint(uncompressedPos, boundaryCompressedOffset, isFrameStart: false, frameWindowDescriptor);
                                candidateShadow = new ShadowDecoder(candidateWindowRaw, frameWindowDescriptor);
                                if (!candidateShadow.Healthy) DropCandidate();
                            }
                        }

                        var block = reader.ReadBlock(out lastBlock);

                        if (!ZstdFrameHelpers.Feed(main, block, outBuf, out var produced)) throw new InvalidDataException("zstd decode error during index build.");
                        var truth = outBuf.AsSpan(0, produced);

                        //rolling window ring
                        for (var copied = 0; copied < produced;)
                        {
                            var ringPos = (int)((uncompressedPos + copied) % frameWindowSize);
                            var n = (int)Math.Min(produced - copied, frameWindowSize - ringPos);
                            Array.Copy(outBuf, copied, ring, ringPos, n);
                            copied += n;
                        }

                        if (!fastForwarding)
                        {
                            //insurance shadow: covers the confirmed point's still-open span. A mismatch
                            //here means a divergence DEEPER than a whole span (never observed on real
                            //data) - degrade to frame-start points only. Never wrong data.
                            if (confirmedShadow != null && !confirmedShadow.FeedAndCompare(block, truth))
                            {
                                throw new InsuranceDivergenceException(uncompressedPos);
                            }

                            //candidate shadow: divergence is normal (~12% of boundaries) - re-arm later
                            if (candidateShadow != null && !candidateShadow.FeedAndCompare(block, truth))
                            {
                                DropCandidate();
                            }
                        }

                        uncompressedPos += produced;

                        if (reader.Position - lastProgressReportAt >= 16 * 1024 * 1024)
                        {
                            lastProgressReportAt = reader.Position;
                            ReportProgress(progress, reader.Position, compressedTotal, uncompressedPos, sealedPoints.Count);
                        }
                    }

                    reader.EndFrame(hasChecksum);

                    //frame end: a live candidate has verified [candidate -> frame end] = its whole
                    //actual span (the next point will be at or after the next frame's start)
                    if (candidate != null)
                    {
                        PromoteCandidate();
                    }
                }

                if (adoptPending) throw new InvalidDataException("The compressed stream ended before the previously indexed position.");
                SealConfirmed();

                if (sealedPoints.Count == 0) throw new InvalidDataException("No zstd frames found.");

                sink.Finalise(uncompressedPos);
                ReportProgress(progress, compressedTotal, compressedTotal, uncompressedPos, sealedPoints.Count);

                logger?.LogInformation("Finished the zstd index: {Count:N0} verified points.", sealedPoints.Count);
                return (sealedPoints, uncompressedPos);
            }
            finally
            {
                Methods.ZSTD_freeDCtx(main);
                confirmedShadow?.Dispose();
                candidateShadow?.Dispose();
            }
        }

        static void ReportProgress(IProgress<ZstdIndexProgress>? progress, long compressedProcessed, long compressedTotal, long uncompressedProduced, int pointCount)
        {
            var fraction = compressedTotal > 0 ? Math.Min(1.0, (double)compressedProcessed / compressedTotal) : 1.0;
            progress?.Report(new ZstdIndexProgress(ZstdIndexPhase.Scanning, compressedProcessed, compressedTotal, uncompressedProduced, pointCount, fraction));
        }

        //================================================================= build sinks

        /// <summary>Where sealed (verified) points go as the build produces them.</summary>
        interface IIndexSink
        {
            /// <summary>Already-sealed points (non-empty when resuming an interrupted build).
            /// <see cref="Append"/> adds to this list.</summary>
            List<ZstdIndexPoint> SealedPoints { get; }
            void Append(ZstdIndexPoint point, byte[] windowCompressed);
            void Finalise(long totalUncompressedLength);
            /// <summary>Discard everything and start over (divergence fallback / failed resume).</summary>
            void Reset();
        }

        sealed class MemorySink(Dictionary<ZstdIndexPoint, byte[]> windows) : IIndexSink
        {
            public List<ZstdIndexPoint> SealedPoints { get; } = [];

            public void Append(ZstdIndexPoint point, byte[] windowCompressed)
            {
                point.WindowCompressedLength = windowCompressed.Length;
                if (windowCompressed.Length > 0) windows[point] = windowCompressed;
                SealedPoints.Add(point);
            }

            public void Finalise(long totalUncompressedLength) { }

            public void Reset()
            {
                SealedPoints.Clear();
                windows.Clear();
            }
        }

        /// <summary>Appends each sealed point (record + inline window) to a seekable stream and
        /// flushes, so every sealed point survives an interruption. The header's counts stay zeroed
        /// (= incomplete) until <see cref="Finalise"/> patches them.</summary>
        sealed class StreamSink : IIndexSink
        {
            readonly Stream stream;
            readonly long baseOffset;

            public List<ZstdIndexPoint> SealedPoints { get; private set; }

            public StreamSink(Stream stream, long baseOffset, List<ZstdIndexPoint> existingSealedPoints)
            {
                this.stream = stream;
                this.baseOffset = baseOffset;
                SealedPoints = existingSealedPoints;

                if (SealedPoints.Count > 0)
                {
                    //continue after the last complete record (dropping any partial tail)
                    var last = SealedPoints[SealedPoints.Count - 1];
                    var end = baseOffset + last.WindowPositionInFile + last.WindowCompressedLength;
                    try { stream.SetLength(end); } catch (NotSupportedException) { }
                    stream.Position = end;
                }
                else
                {
                    Reset();    //also discards any unparseable prior content
                }
            }

            void WriteFreshHeader()
            {
                stream.Position = baseOffset;
                WriteHeader(stream, 0, 0);      //zeroed counts mark the index incomplete
                stream.Flush();
            }

            public void Append(ZstdIndexPoint point, byte[] windowCompressed)
            {
                var record = new byte[RecordSizeV2];
                FillRecord(record, point, windowCompressed.Length);
                stream.Write(record, 0, record.Length);
                point.WindowPositionInFile = stream.Position - baseOffset;
                point.WindowCompressedLength = windowCompressed.Length;
                stream.Write(windowCompressed, 0, windowCompressed.Length);
                stream.Flush();     //each sealed point survives an interruption
                SealedPoints.Add(point);
            }

            public void Finalise(long totalUncompressedLength)
            {
                var end = stream.Position;
                stream.Position = baseOffset + 8;
                var tail = new byte[12];
                BinaryPrimitives.WriteInt64LittleEndian(tail.AsSpan(0), totalUncompressedLength);
                BinaryPrimitives.WriteInt32LittleEndian(tail.AsSpan(8), SealedPoints.Count);
                stream.Write(tail, 0, tail.Length);
                stream.Position = end;
                stream.Flush();
            }

            public void Reset()
            {
                SealedPoints = [];
                try { stream.SetLength(baseOffset); } catch (NotSupportedException) { }
                WriteFreshHeader();
            }
        }
    }
}
