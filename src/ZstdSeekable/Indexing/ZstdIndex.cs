using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using ZstdSeekable.Internal;
using ZstdSharp.Unsafe;

namespace ZstdSeekable
{
    /// <summary>
    /// Random-access index over a standard (non-seekable-format) zstd stream: verified resume points
    /// every ~<see cref="ZstdIndexOptions.TargetSpanBytes"/> of output, each carrying the preceding
    /// ≤windowSize of content (zstd-compressed in the index, loaded lazily). Built by one sequential
    /// decode with inline trial-validation of every candidate point, then a parallel whole-span
    /// verification pass; only points whose resumed decode is byte-identical to the true decode
    /// survive. The persisted format is binary, magic "ZSTZRAN1", all integers little-endian.
    /// </summary>
    public sealed class ZstdIndex : IDisposable
    {
        const int TrialLookaheadBytes = 4 * 1024 * 1024;    //inline candidate validation depth (the verify pass then covers full spans)
        const int RecordSize = 8 + 8 + 1 + 1 + 16 + 8 + 4;
        const int HeaderSize = 8 + 8 + 4;
        static readonly byte[] Magic = "ZSTZRAN1"u8.ToArray();

        List<ZstdIndexPoint> points;
        WindowSource windowSource;

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

        //================================================================= load / save

        /// <summary>Loads an index from a file. Window snapshots are read lazily (the file is opened
        /// per window load), so the file must remain in place while the index is in use.</summary>
        public static ZstdIndex Load(string indexPath)
        {
            List<ZstdIndexPoint> loaded;
            long totalLength;
            using (var fs = File.OpenRead(indexPath))
            using (var br = new BinaryReader(fs))
            {
                (loaded, totalLength) = ReadHeaderAndRecords(br);
            }
            return new ZstdIndex(loaded, totalLength, new FileWindowSource(indexPath));
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
                using var br = new BinaryReader(indexStream, Encoding.UTF8, leaveOpen: true);
                var (loaded, totalLength) = ReadHeaderAndRecords(br);
                return new ZstdIndex(loaded, totalLength, new StreamWindowSource(indexStream, baseOffset, leaveOpen));
            }
            else
            {
                using var br = new BinaryReader(indexStream, Encoding.UTF8, leaveOpen: true);
                var (loaded, totalLength) = ReadHeaderAndRecords(br);

                //windows are concatenated after the records in point order - read them all now
                var windows = new Dictionary<ZstdIndexPoint, byte[]>();
                foreach (var point in loaded)
                {
                    if (point.WindowCompressedLength == 0) continue;
                    windows[point] = br.ReadBytes(point.WindowCompressedLength);
                    if (windows[point].Length < point.WindowCompressedLength)
                        throw new InvalidDataException("Truncated zstd index (windows).");
                }
                if (!leaveOpen) indexStream.Dispose();
                return new ZstdIndex(loaded, totalLength, new MemoryWindowSource(windows));
            }
        }

        static (List<ZstdIndexPoint> Points, long TotalLength) ReadHeaderAndRecords(BinaryReader br)
        {
            if (!br.ReadBytes(8).AsSpan().SequenceEqual(Magic)) throw new InvalidDataException("Not a zstd random-access index (bad magic).");
            var totalLength = br.ReadInt64();
            var count = br.ReadInt32();
            if (count <= 0) throw new InvalidDataException("zstd index has no points.");

            var loaded = new List<ZstdIndexPoint>(count);
            for (var i = 0; i < count; i++)
            {
                var uncompressedOffset = br.ReadInt64();
                var compressedOffset = br.ReadInt64();
                var isFrameStart = br.ReadBoolean();
                var windowDescriptor = br.ReadByte();
                var spanMd5 = br.ReadBytes(16);
                var windowPositionInFile = br.ReadInt64();
                var windowCompressedLength = br.ReadInt32();
                if (spanMd5.Length < 16) throw new InvalidDataException("Truncated zstd index (records).");

                loaded.Add(new ZstdIndexPoint(uncompressedOffset, compressedOffset, isFrameStart, windowDescriptor)
                {
                    SpanMd5 = spanMd5,
                    WindowPositionInFile = windowPositionInFile,
                    WindowCompressedLength = windowCompressedLength,
                });
            }
            return (loaded, totalLength);
        }

        /// <summary>Saves the index atomically (written to a temporary file, then moved over
        /// <paramref name="indexPath"/>).</summary>
        public void Save(string indexPath)
        {
            var tempPath = indexPath + ".wip";
            using (var fs = File.Create(tempPath))
            {
                Save(fs);
            }
            FileHelpers.MoveOverwrite(tempPath, indexPath);
        }

        /// <summary>Writes the index to <paramref name="destination"/> at its current position. The
        /// stream is left open.</summary>
        public void Save(Stream destination)
        {
            var windows = points.Select(windowSource.GetCompressedWindow).ToList();

            using var bw = new BinaryWriter(destination, Encoding.UTF8, leaveOpen: true);
            bw.Write(Magic);
            bw.Write(UncompressedLength);
            bw.Write(points.Count);

            var windowOffset = (long)HeaderSize + (long)points.Count * RecordSize;
            for (var i = 0; i < points.Count; i++)
            {
                var p = points[i];
                p.WindowPositionInFile = windowOffset;
                p.WindowCompressedLength = windows[i].Length;
                windowOffset += p.WindowCompressedLength;

                bw.Write(p.UncompressedOffset);
                bw.Write(p.CompressedOffset);
                bw.Write(p.IsFrameStart);
                bw.Write(p.WindowDescriptor);
                bw.Write(p.SpanMd5.Length == 16 ? p.SpanMd5 : new byte[16]);
                bw.Write(p.WindowPositionInFile);
                bw.Write(p.WindowCompressedLength);
            }
            foreach (var w in windows) bw.Write(w);
            bw.Flush();
        }

        /// <summary>Loads the index at <paramref name="indexPath"/> if it exists and is valid;
        /// otherwise builds one from <paramref name="compressed"/> and saves it there.</summary>
        public static ZstdIndex LoadOrBuild(Stream compressed, string indexPath, ZstdIndexOptions? options = null,
                                            IProgress<ZstdIndexProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            if (File.Exists(indexPath))
            {
                try
                {
                    return Load(indexPath);
                }
                catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or IOException)
                {
                    options?.Logger?.LogWarning("Could not load zstd index {IndexPath} ({Message}). Rebuilding.", Path.GetFileName(indexPath), ex.Message);
                }
            }

            var index = Build(compressed, options, progress, cancellationToken);
            index.Save(indexPath);
            return index;
        }

        /// <summary>
        /// Loads the index from <paramref name="indexStream"/> if it holds a valid one; otherwise
        /// builds an index from <paramref name="compressed"/> and writes it to the stream. The index
        /// stream is always left open (the caller owns it), and must remain open while the returned
        /// index is in use if it was loaded lazily (seekable streams).
        /// </summary>
        public static ZstdIndex LoadOrBuild(Stream compressed, Stream indexStream, ZstdIndexOptions? options = null,
                                            IProgress<ZstdIndexProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            if (indexStream == null) throw new ArgumentNullException(nameof(indexStream));

            if (indexStream.CanSeek)
            {
                var basePosition = indexStream.Position;
                if (indexStream.Length - basePosition >= HeaderSize)
                {
                    try
                    {
                        return Load(indexStream, leaveOpen: true);
                    }
                    catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException)
                    {
                        options?.Logger?.LogWarning("Could not load the zstd index from the supplied stream ({Message}). Rebuilding.", ex.Message);
                        indexStream.Position = basePosition;
                    }
                }

                var index = Build(compressed, options, progress, cancellationToken);
                indexStream.Position = basePosition;
                index.Save(indexStream);
                try { indexStream.SetLength(indexStream.Position); } catch (NotSupportedException) { }
                indexStream.Flush();
                return index;
            }

            if (indexStream.CanRead && !indexStream.CanWrite)
            {
                //read-only pipe: it can only be a source
                return Load(indexStream, leaveOpen: true);
            }

            //non-seekable writable stream: it can only be a destination
            var built = Build(compressed, options, progress, cancellationToken);
            built.Save(indexStream);
            indexStream.Flush();
            return built;
        }

        //================================================================= build

        /// <summary>
        /// Builds an index by decoding <paramref name="compressed"/> once from the start, planting a
        /// verified resume point roughly every <see cref="ZstdIndexOptions.TargetSpanBytes"/> of
        /// output, then re-decoding every span in parallel to prove it byte-identical. Frame starts
        /// always verify, so this never fails on valid zstd input - worst case is an index with
        /// frame-start points only (correct, but slow to seek within giant frames).
        /// </summary>
        public static unsafe ZstdIndex Build(Stream compressed, ZstdIndexOptions? options = null,
                                             IProgress<ZstdIndexProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            if (compressed == null) throw new ArgumentNullException(nameof(compressed));
            if (!compressed.CanSeek) throw new ArgumentException("The compressed stream must be seekable to build an index over it.", nameof(compressed));

            options ??= new ZstdIndexOptions();
            var logger = options.Logger;
            var targetSpanBytes = Math.Max(64 * 1024, options.TargetSpanBytes);
            logger?.LogInformation("Creating a zstd random-access index ({Length:N0} compressed bytes).", compressed.Length);

            var newPoints = new List<ZstdIndexPoint>();
            var windows = new List<byte[]>();       //compressed window per point (parallel to newPoints)

            compressed.Seek(0, SeekOrigin.Begin);
            var compressedTotal = compressed.Length;
            var reader = new ZstdBlockReader(compressed);
            using var windowCompressor = new ZstdSharp.Compressor(options.WindowCompressionLevel);

            var main = Methods.ZSTD_createDCtx();
            Methods.ZSTD_DCtx_setParameter(main, ZSTD_dParameter.ZSTD_d_windowLogMax, 31);
            ZSTD_DCtx_s* trial = null;

            try
            {
                var outBuf = new byte[1 << 20];     //one block regenerates ≤128 KB
                var trialBuf = new byte[1 << 20];

                long uncompressedPos = 0;
                var nextCandidateAt = targetSpanBytes;
                var spanMd5 = MD5.Create();
                long lastProgressReportAt = 0;

                //rolling ring of true output (window snapshots), indexed by absolute position % windowSize
                var ring = Array.Empty<byte>();
                long frameWindowSize = 0;
                byte frameWindowDescriptor = 0;

                //at most one candidate under trial at a time. While a trial is active, output hashing
                //is DEFERRED into `pending`: those bytes belong to the candidate's span if it is
                //accepted (its offset precedes them), or to the current span if it is rejected.
                ZstdIndexPoint? candidate = null;
                byte[]? candidateWindowRaw = null;
                var pending = new byte[TrialLookaheadBytes + (1 << 20)];
                var pendingLength = 0;
                long trialCompared = 0;

                void FinishSpanHash()
                {
                    spanMd5.TransformFinalBlock([], 0, 0);
                    if (newPoints.Count > 0) newPoints[^1].SpanMd5 = spanMd5.Hash!;
                    spanMd5.Dispose();
                    spanMd5 = MD5.Create();
                }

                void DropTrial(bool flushPendingIntoCurrentSpan)
                {
                    if (trial != null) { Methods.ZSTD_freeDCtx(trial); trial = null; }
                    candidate = null;
                    candidateWindowRaw = null;
                    if (flushPendingIntoCurrentSpan && pendingLength > 0)
                    {
                        spanMd5.TransformBlock(pending, 0, pendingLength, null, 0);
                    }
                    pendingLength = 0;
                }

                void AcceptCandidate()
                {
                    //everything hashed so far (excluding `pending`) is the PREVIOUS span - exactly up
                    //to the candidate's offset
                    FinishSpanHash();
                    newPoints.Add(candidate!);
                    windows.Add(candidateWindowRaw!.Length == 0 ? [] : windowCompressor.Wrap(candidateWindowRaw).ToArray());
                    nextCandidateAt = candidate!.UncompressedOffset + targetSpanBytes;

                    //the deferred bytes are the start of the NEW span
                    spanMd5.TransformBlock(pending, 0, pendingLength, null, 0);
                    pendingLength = 0;

                    if (trial != null) { Methods.ZSTD_freeDCtx(trial); trial = null; }
                    candidate = null;
                    candidateWindowRaw = null;
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

                while (true)
                {
                    var frameStartOffset = reader.Position;
                    var frameKind = reader.BeginFrame(out var headerBytes, out frameWindowSize, out frameWindowDescriptor, out var hasChecksum);
                    if (frameKind == FrameKind.EndOfStream) break;
                    if (frameKind == FrameKind.Skippable) continue;

                    if (frameWindowSize > 1L << 30) throw new InvalidDataException($"zstd window {frameWindowSize:N0} too large to index.");
                    if (ring.Length < frameWindowSize) ring = new byte[frameWindowSize];

                    //a frame start is a perfect (stateless) resume point
                    FinishSpanHash();
                    newPoints.Add(new ZstdIndexPoint(uncompressedPos, frameStartOffset, isFrameStart: true, frameWindowDescriptor));
                    windows.Add([]);
                    nextCandidateAt = uncompressedPos + targetSpanBytes;

                    if (!ZstdFrameHelpers.Feed(main, headerBytes, outBuf, out _)) throw new InvalidDataException("zstd frame header rejected.");

                    var lastBlock = false;
                    while (!lastBlock)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        var boundaryCompressedOffset = reader.Position;

                        if (candidate == null && uncompressedPos >= nextCandidateAt)
                        {
                            //arm a trial at this block boundary
                            candidateWindowRaw = SnapshotWindow();
                            candidate = new ZstdIndexPoint(uncompressedPos, boundaryCompressedOffset, isFrameStart: false, frameWindowDescriptor);
                            trialCompared = 0;

                            trial = Methods.ZSTD_createDCtx();
                            Methods.ZSTD_DCtx_setParameter(trial, ZSTD_dParameter.ZSTD_d_windowLogMax, 31);
                            var initOk = true;
                            if (candidateWindowRaw.Length > 0)
                            {
                                fixed (byte* windowPtr = candidateWindowRaw)
                                {
                                    var r = Methods.ZSTD_DCtx_refPrefix(trial, windowPtr, (nuint)candidateWindowRaw.Length);
                                    initOk = !Methods.ZSTD_isError(r);
                                }
                            }
                            if (initOk) initOk = ZstdFrameHelpers.Feed(trial, ZstdFrameHelpers.SyntheticFrameHeader(frameWindowDescriptor), trialBuf, out _);
                            if (!initOk) DropTrial(flushPendingIntoCurrentSpan: true);
                        }

                        var block = reader.ReadBlock(out lastBlock);

                        if (!ZstdFrameHelpers.Feed(main, block, outBuf, out var produced)) throw new InvalidDataException("zstd decode error during index build.");

                        //rolling window ring
                        for (var copied = 0; copied < produced;)
                        {
                            var ringPos = (int)((uncompressedPos + copied) % frameWindowSize);
                            var n = (int)Math.Min(produced - copied, frameWindowSize - ringPos);
                            Array.Copy(outBuf, copied, ring, ringPos, n);
                            copied += n;
                        }

                        if (candidate != null && trial != null)
                        {
                            //defer hashing while the trial is undecided; compare the trial decoder's
                            //output for this same block against the truth
                            Array.Copy(outBuf, 0, pending, pendingLength, produced);
                            pendingLength += produced;

                            if (!ZstdFrameHelpers.Feed(trial, block, trialBuf, out var trialProduced)
                                || trialProduced != produced
                                || !outBuf.AsSpan(0, produced).SequenceEqual(trialBuf.AsSpan(0, trialProduced)))
                            {
                                DropTrial(flushPendingIntoCurrentSpan: true);   //not a valid resume point; try a later boundary
                            }
                            else
                            {
                                trialCompared += produced;
                                if (trialCompared >= TrialLookaheadBytes)
                                {
                                    AcceptCandidate();
                                }
                            }
                        }
                        else
                        {
                            spanMd5.TransformBlock(outBuf, 0, produced, null, 0);
                        }

                        uncompressedPos += produced;

                        if (reader.Position - lastProgressReportAt >= 16 * 1024 * 1024)
                        {
                            lastProgressReportAt = reader.Position;
                            ReportScanProgress(progress, reader.Position, compressedTotal, uncompressedPos, newPoints.Count);
                        }
                    }

                    reader.EndFrame(hasChecksum);
                    DropTrial(flushPendingIntoCurrentSpan: true);   //an unresolved trial cannot span frames
                }

                FinishSpanHash();
                spanMd5.Dispose();

                if (newPoints.Count == 0) throw new InvalidDataException("No zstd frames found.");
                ReportScanProgress(progress, compressedTotal, compressedTotal, uncompressedPos, newPoints.Count);

                logger?.LogInformation("zstd index: {Count:N0} candidate points over {Bytes:N0} bytes. Verifying every span.", newPoints.Count, uncompressedPos);
                VerifyAndHeal(compressed, newPoints, windows, uncompressedPos, options, progress, cancellationToken);

                var windowsByPoint = new Dictionary<ZstdIndexPoint, byte[]>();
                for (var i = 0; i < newPoints.Count; i++)
                {
                    if (windows[i].Length > 0) windowsByPoint[newPoints[i]] = windows[i];
                }

                logger?.LogInformation("Finished the zstd index: {Count:N0} verified points.", newPoints.Count);
                return new ZstdIndex(newPoints, uncompressedPos, new MemoryWindowSource(windowsByPoint));
            }
            finally
            {
                Methods.ZSTD_freeDCtx(main);
                if (trial != null) Methods.ZSTD_freeDCtx(trial);
            }
        }

        static void ReportScanProgress(IProgress<ZstdIndexProgress>? progress, long compressedProcessed, long compressedTotal, long uncompressedProduced, int pointCount)
        {
            var fraction = compressedTotal > 0 ? 0.5 * Math.Min(1.0, (double)compressedProcessed / compressedTotal) : 0.5;
            progress?.Report(new ZstdIndexProgress(ZstdIndexPhase.Scanning, compressedProcessed, compressedTotal, uncompressedProduced, pointCount, fraction));
        }

        /// <summary>
        /// The hard guarantee: re-decode every span from its point exactly as readers will, comparing
        /// the true decode's MD5 piecewise at every candidate boundary inside the span. A point whose
        /// resume diverges anywhere in its span gets DROPPED and its predecessor re-verified over the
        /// merged span (the inline trial can be fooled by long stateless stretches - RLE/raw blocks
        /// neither use nor update repeat-offset state, so a divergence can surface much later).
        /// Healing always terminates: a frame-start resume IS the true decode, sound at any depth.
        /// Only points whose full (possibly merged) span verified survive. Mutates
        /// <paramref name="points"/> and <paramref name="compressedWindows"/> in place.
        /// </summary>
        static void VerifyAndHeal(Stream compressedStream, List<ZstdIndexPoint> points, List<byte[]> compressedWindows,
                                  long uncompressedTotalLength, ZstdIndexOptions options,
                                  IProgress<ZstdIndexProgress>? progress, CancellationToken cancellationToken)
        {
            var logger = options.Logger;
            var streamLock = new object();
            var markers = points;                       //every candidate stays a hash boundary
            var alive = points.Select(_ => true).ToArray();
            var compressedTotal = compressedStream.Length;

            var spansCompleted = 0;
            var spansPlanned = points.Count;

            void ReportVerifyProgress()
            {
                if (progress == null) return;
                var done = Volatile.Read(ref spansCompleted);
                var planned = Volatile.Read(ref spansPlanned);
                var fraction = 0.5 + 0.5 * Math.Min(1.0, planned > 0 ? (double)done / planned : 1.0);
                progress.Report(new ZstdIndexProgress(ZstdIndexPhase.Verifying, compressedTotal, compressedTotal, uncompressedTotalLength, markers.Count, fraction));
            }

            //returns the marker index (a < failIdx <= b) of the first piecewise-hash mismatch, or -1 if
            //the whole span [markers[a], nextAliveEndOffset) verified
            int VerifySpan(int a, int b /*exclusive end marker index; markers.Count = EOF*/)
            {
                var point = markers[a];
                var endUncompressed = b < markers.Count ? markers[b].UncompressedOffset : uncompressedTotalLength;
                var endCompressed = b < markers.Count ? markers[b].CompressedOffset : compressedStream.Length;

                byte[] window = [];
                if (compressedWindows[a].Length > 0)
                {
                    using var windowDecompressor = new ZstdSharp.Decompressor();
                    window = windowDecompressor.Unwrap(compressedWindows[a]).ToArray();
                }

                var source = new SharedStreamView(compressedStream, streamLock);
                using var resume = new ZstdResumeStream(source, point.CompressedOffset, endCompressed, point.IsFrameStart, point.WindowDescriptor, window);

                var buffer = new byte[1 << 20];
                var position = point.UncompressedOffset;
                var intervalIdx = a;    //hash of [markers[j], markers[j+1]) lives in markers[j].SpanMd5
                var md5 = MD5.Create();
                try
                {
                    while (position < endUncompressed)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        var intervalEnd = intervalIdx + 1 < markers.Count ? markers[intervalIdx + 1].UncompressedOffset : uncompressedTotalLength;
                        var want = (int)Math.Min(buffer.Length, intervalEnd - position);
                        var n = resume.Read(buffer, 0, want);
                        if (n == 0) return intervalIdx + 1;     //short decode: treat as divergence in this interval
                        md5.TransformBlock(buffer, 0, n, null, 0);
                        position += n;

                        if (position == intervalEnd)
                        {
                            md5.TransformFinalBlock([], 0, 0);
                            var expected = markers[intervalIdx].SpanMd5;
                            if (!md5.Hash!.AsSpan().SequenceEqual(expected)) return intervalIdx + 1;
                            md5.Dispose();
                            md5 = MD5.Create();
                            intervalIdx++;
                        }
                    }
                    return -1;
                }
                finally
                {
                    md5.Dispose();
                }
            }

            //verify all currently-alive spans in parallel; drop failures; repeat for the merged spans
            var toVerify = Enumerable.Range(0, markers.Count).Where(i => alive[i]).ToList();
            var round = 0;
            while (toVerify.Count > 0)
            {
                round++;
                if (round > markers.Count + 2) throw new InvalidDataException("zstd index verification did not converge.");

                var dropped = new List<int>();
                var next = new List<int>();

                //each worker takes a CONTIGUOUS slice of spans, in order: a handful of sequential
                //cursors through the compressed source instead of a random interleave (a seek storm
                //on spinning disks - the verify pass is I/O-bound, not CPU-bound)
                var workerCount = Math.Max(1, Math.Min(options.VerifyParallelism, toVerify.Count));
                var ordered = toVerify.OrderBy(x => x).ToList();
                Parallel.For(0, workerCount, w =>
                {
                    var from = w * ordered.Count / workerCount;
                    var to = (w + 1) * ordered.Count / workerCount;
                    for (var idx = from; idx < to; idx++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        var a = ordered[idx];
                        var b = a + 1;
                        while (b < markers.Count && !alive[b]) b++;

                        if (VerifySpan(a, b) >= 0)
                        {
                            lock (dropped) dropped.Add(a);
                        }

                        Interlocked.Increment(ref spansCompleted);
                        ReportVerifyProgress();
                    }
                });

                cancellationToken.ThrowIfCancellationRequested();

                foreach (var i in dropped.OrderBy(x => x))
                {
                    if (markers[i].IsFrameStart)
                    {
                        //cannot happen (a frame-start resume is the true decode), but never drop one
                        throw new InvalidDataException($"zstd frame-start span failed verification at {markers[i].UncompressedOffset:N0}.");
                    }
                    alive[i] = false;
                    //the nearest earlier alive point now covers a longer span - re-verify it
                    var predecessor = i - 1;
                    while (predecessor >= 0 && !alive[predecessor]) predecessor--;
                    if (predecessor >= 0 && !next.Contains(predecessor)) next.Add(predecessor);
                }

                if (dropped.Count > 0)
                {
                    Interlocked.Add(ref spansPlanned, next.Count);
                    logger?.LogDebug("zstd index verification round {Round}: dropped {Dropped} unsound resume point(s); re-verifying {Merged} merged span(s).", round, dropped.Count, next.Count);
                }

                toVerify = next;
            }

            var survivingWindows = new List<byte[]>();
            var surviving = new List<ZstdIndexPoint>();
            for (var i = 0; i < markers.Count; i++)
            {
                if (!alive[i]) continue;
                surviving.Add(markers[i]);
                survivingWindows.Add(compressedWindows[i]);
            }

            if (surviving.Count < markers.Count)
                logger?.LogInformation("zstd index: {Removed:N0} of {Total:N0} candidate points were unsound and removed; {Surviving:N0} verified points remain.", markers.Count - surviving.Count, markers.Count, surviving.Count);

            points.Clear();
            points.AddRange(surviving);
            compressedWindows.Clear();
            compressedWindows.AddRange(survivingWindows);

            ReportVerifyProgress();
        }
    }
}
