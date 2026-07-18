using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Extensions.Logging;
using ZstdSeekable.Internal;
using ZstdSharp.Unsafe;

namespace ZstdSeekable
{
    /// <summary>
    /// Random-access index over a standard (non-seekable-format) zstd stream: resume points every
    /// ~<see cref="ZstdIndexOptions.TargetSpanBytes"/> of output, plus <see cref="ZstdFillSpan"/>
    /// records for long single-byte runs, which are served without touching the decoder at all.
    ///
    /// Since 0.4.0 each mid-frame point carries the EXACT carried decoder state - the window
    /// (zstd-compressed in the index, loaded lazily) plus the entropy tables / repeat offsets /
    /// table-selector classification / flags that a block can reuse via Repeat_Mode. A resume
    /// reinstates all of it, so EVERY block boundary at the target spacing is a valid point: the
    /// old candidate/insurance trial machinery and its divergence fallback are gone, the build is
    /// a single decode pass, and no boundary is ever dropped. A small sample of spans
    /// (<see cref="ZstdIndexOptions.VerifiedSampleSpans"/>) is still byte-verified with a shadow
    /// decoder as an integration check; a sampled mismatch is a hard error, never healed.
    ///
    /// A persisted index grows incrementally (header counts zeroed until finalisation), so an
    /// interrupted build RESUMES: sealed records are kept, and the build continues by exact-
    /// restoring the LAST sealed point - no fast-forward decode. The header fingerprints the
    /// compressed stream (length + CRC32 of its first 64 KiB) so a stale .wip is never resumed
    /// against the wrong input. The persisted format is binary ("ZSTZRAN4": typed records -
    /// frame-start point, exact mid-frame point with inline entropy+window, fill span; the
    /// selector pointers are stored symbolically, never as addresses). "ZSTZRAN1"/"2"/"3" files
    /// from older versions still load; their window-prefix mid-frame points keep serving via the
    /// verified-span path they were built with. All integers little-endian.
    /// </summary>
    public sealed class ZstdIndex : IDisposable
    {
        static readonly byte[] MagicV1 = "ZSTZRAN1"u8.ToArray();
        static readonly byte[] MagicV2 = "ZSTZRAN2"u8.ToArray();
        static readonly byte[] MagicV3 = "ZSTZRAN3"u8.ToArray();
        static readonly byte[] MagicV4 = "ZSTZRAN4"u8.ToArray();
        const int HeaderSize = 8 + 8 + 4;                   //v1-v3: magic, totalUncompressed, recordCount
        const int HeaderSizeV4 = 8 + 8 + 4 + 8 + 4;         //v4: magic, fingerprint(compressedLength, crc32 of first 64 KiB), totalUncompressed, recordCount
        const int FingerprintProbeBytes = 64 * 1024;
        const int RecordSizeV1 = 8 + 8 + 1 + 1 + 16 + 8 + 4;
        const int RecordSizeV2 = 8 + 8 + 1 + 1 + 4;

        //record kinds (v3 introduced 0-2; v4 adds 3); each record is the kind byte + its payload
        //(+ inline blobs for kinds 1 and 3)
        const byte KindFrameStart = 0;      //uOffset i64, cOffset i64
        const byte KindMidFrame = 1;        //uOffset i64, cOffset i64, windowDescriptor, windowLength i32, window bytes (legacy window-prefix)
        const byte KindFillSpan = 2;        //uOffset i64, length i64, fillByte
        const byte KindExactMidFrame = 3;   //uOffset i64, cOffset i64, windowDescriptor, frameHasChecksum, litEntropy, fseEntropy, 4x(ptrClass u8 + ptrOffset i32), entropyLength i32, windowLength i32, entropy bytes, window bytes
        const int FrameStartPayload = 16;
        const int MidFramePayload = 21;
        const int FillSpanPayload = 17;
        const int ExactMidFramePayload = 8 + 8 + 1 + 1 + 1 + 1 + 20 + 4 + 4;    //48

        readonly List<ZstdIndexPoint> points;
        readonly List<ZstdFillSpan> fillSpans;
        readonly WindowSource windowSource;
        readonly long fingerprintLength;    //0 = none recorded (v1-v3 files)
        readonly uint fingerprintCrc;

        /// <summary>The resume points, in stream order. The first point is always offset 0.</summary>
        public IReadOnlyList<ZstdIndexPoint> Points => points;

        /// <summary>Long single-byte runs of the decompressed stream (sorted, non-overlapping).
        /// Reads inside them never touch the compressed stream; they also serve as sparse-extent
        /// metadata for consumers.</summary>
        public IReadOnlyList<ZstdFillSpan> FillSpans => fillSpans;

        /// <summary>Total decompressed size of the indexed stream.</summary>
        public long UncompressedLength { get; }

        ZstdIndex(List<ZstdIndexPoint> points, List<ZstdFillSpan> fillSpans, long uncompressedLength,
                  WindowSource windowSource, long fingerprintLength, uint fingerprintCrc)
        {
            this.points = points;
            this.fillSpans = fillSpans;
            this.windowSource = windowSource;
            this.fingerprintLength = fingerprintLength;
            this.fingerprintCrc = fingerprintCrc;
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

        /// <summary>Loads a v4 point's exact decoder state (entropy tables, rep offsets, selector
        /// classification, flags), ready to reinstate into a resumed DCtx. Null for frame starts
        /// and legacy window-prefix points. Thread-safe.</summary>
        internal ZstdExactState? LoadExactState(ZstdIndexPoint point)
        {
            if (!point.IsExact) return null;

            var stored = windowSource.GetCompressedEntropy(point);
            if (stored.Length == 0)
                throw new InvalidDataException("zstd index: a v4 exact point has no entropy snapshot.");

            using var decompressor = new ZstdSharp.Decompressor();
            var entropyRaw = decompressor.Unwrap(stored).ToArray();
            return ZstdExactState.FromSymbolic(entropyRaw, point.PtrClasses, point.PtrOffsets, point.LitEntropy, point.FseEntropy);
        }

        //================================================================= load

        /// <summary>Loads an index from a file (any format version). Window/state snapshots are read
        /// lazily (the file is opened per load), so the file must remain in place while the index is
        /// in use.</summary>
        public static ZstdIndex Load(string indexPath)
        {
            ParsedIndex parsed;
            using (var fs = File.OpenRead(indexPath))
            {
                parsed = Parse(fs, eagerWindows: null, eagerEntropies: null, tolerateTruncatedTail: false);
            }
            if (!parsed.Complete) throw new InvalidDataException("The zstd index was not finalised (interrupted build). LoadOrBuild can resume it.");
            return new ZstdIndex(parsed.Points, parsed.FillSpans, parsed.TotalLength, new FileWindowSource(indexPath),
                                 parsed.FingerprintLength, parsed.FingerprintCrc);
        }

        /// <summary>
        /// Loads an index from a stream, starting at its current position. A seekable stream is read
        /// lazily (it must remain open while the index is in use; blob loads seek it under a lock).
        /// A non-seekable stream is read eagerly into memory and can be discarded afterwards.
        /// </summary>
        public static ZstdIndex Load(Stream indexStream, bool leaveOpen = false)
        {
            if (indexStream == null) throw new ArgumentNullException(nameof(indexStream));

            if (indexStream.CanSeek)
            {
                var baseOffset = indexStream.Position;
                var parsed = Parse(indexStream, eagerWindows: null, eagerEntropies: null, tolerateTruncatedTail: false);
                if (!parsed.Complete) throw new InvalidDataException("The zstd index was not finalised (interrupted build). LoadOrBuild can resume it.");
                return new ZstdIndex(parsed.Points, parsed.FillSpans, parsed.TotalLength,
                                     new StreamWindowSource(indexStream, baseOffset, leaveOpen),
                                     parsed.FingerprintLength, parsed.FingerprintCrc);
            }
            else
            {
                var windows = new Dictionary<ZstdIndexPoint, byte[]>();
                var entropies = new Dictionary<ZstdIndexPoint, byte[]>();
                var parsed = Parse(indexStream, eagerWindows: windows, eagerEntropies: entropies, tolerateTruncatedTail: false);
                if (!parsed.Complete) throw new InvalidDataException("The zstd index was not finalised (interrupted build). LoadOrBuild can resume it.");
                if (!leaveOpen) indexStream.Dispose();
                return new ZstdIndex(parsed.Points, parsed.FillSpans, parsed.TotalLength,
                                     new MemoryWindowSource(windows, entropies),
                                     parsed.FingerprintLength, parsed.FingerprintCrc);
            }
        }

        struct ParsedIndex
        {
            public List<ZstdIndexPoint> Points;
            public List<ZstdFillSpan> FillSpans;
            public long TotalLength;
            public long FingerprintLength;  //0 = none (v1-v3)
            public uint FingerprintCrc;
            public bool Complete;
            public int Version;
            public long BytesConsumed;      //index-relative offset just past the last complete record
        }

        /// <summary>
        /// Reads any format version from the stream's current position. Blob positions in the
        /// returned points are relative to that position. When the eager dictionaries are non-null
        /// the blob bytes are collected into them (required for non-seekable streams); otherwise
        /// they are skipped. <paramref name="tolerateTruncatedTail"/>: an unfinalised index may end
        /// mid-record after an interruption - keep the complete records instead of throwing (used
        /// to resume builds).
        /// </summary>
        static ParsedIndex Parse(Stream s, Dictionary<ZstdIndexPoint, byte[]>? eagerWindows,
                                 Dictionary<ZstdIndexPoint, byte[]>? eagerEntropies, bool tolerateTruncatedTail)
        {
            var magic = new byte[8];
            if (ReadUpTo(s, magic, 8) < 8) throw new InvalidDataException("zstd index truncated (magic).");

            if (magic.AsSpan().SequenceEqual(MagicV4))
            {
                var rest = new byte[HeaderSizeV4 - 8];
                if (ReadUpTo(s, rest, rest.Length) < rest.Length) throw new InvalidDataException("zstd index truncated (header).");
                var fingerprintLength = BinaryPrimitives.ReadInt64LittleEndian(rest.AsSpan(0));
                var fingerprintCrc = BinaryPrimitives.ReadUInt32LittleEndian(rest.AsSpan(8));
                var totalLength = BinaryPrimitives.ReadInt64LittleEndian(rest.AsSpan(12));
                var count = BinaryPrimitives.ReadInt32LittleEndian(rest.AsSpan(20));
                var parsed = ParseTypedRecords(s, totalLength, count, HeaderSizeV4, allowExact: true,
                                               eagerWindows, eagerEntropies, tolerateTruncatedTail);
                parsed.Version = 4;
                parsed.FingerprintLength = fingerprintLength;
                parsed.FingerprintCrc = fingerprintCrc;
                return parsed;
            }

            var header = new byte[HeaderSize - 8];
            if (ReadUpTo(s, header, header.Length) < header.Length) throw new InvalidDataException("zstd index truncated (header).");
            var total = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(0));
            var recordCount = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(8));

            if (magic.AsSpan().SequenceEqual(MagicV3))
            {
                var parsed = ParseTypedRecords(s, total, recordCount, HeaderSize, allowExact: false,
                                               eagerWindows, eagerEntropies, tolerateTruncatedTail);
                parsed.Version = 3;
                return parsed;
            }
            if (magic.AsSpan().SequenceEqual(MagicV2)) return ParseV2(s, total, recordCount, eagerWindows, tolerateTruncatedTail);
            if (magic.AsSpan().SequenceEqual(MagicV1)) return ParseV1(s, total, recordCount, eagerWindows);
            throw new InvalidDataException("Not a zstd random-access index (bad magic).");
        }

        //v1 ("ZSTZRAN1", ZstdSeekable 0.1.x): fixed records carrying a span MD5 and an absolute
        //window position, followed by a blob of all windows in point order
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

            return new ParsedIndex { Points = loaded, FillSpans = [], TotalLength = totalLength, Complete = true, Version = 1 };
        }

        //v2 ("ZSTZRAN2", ZstdSeekable 0.2.x): each fixed record is followed immediately by its
        //compressed window; zeroed header counts mark an unfinalised index
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

                if (!TryConsumeBlob(s, windowLength, eagerWindows, point, tolerateTruncatedTail && !complete)) break;

                offset += RecordSizeV2 + windowLength;
                loaded.Add(point);
            }

            return new ParsedIndex { Points = loaded, FillSpans = [], TotalLength = totalLength, Complete = complete, Version = 2, BytesConsumed = offset };
        }

        //v3/v4 typed records, appended in the order they are proven; v4 adds the exact mid-frame kind
        static ParsedIndex ParseTypedRecords(Stream s, long totalLength, int count, long headerSize, bool allowExact,
                                             Dictionary<ZstdIndexPoint, byte[]>? eagerWindows,
                                             Dictionary<ZstdIndexPoint, byte[]>? eagerEntropies, bool tolerateTruncatedTail)
        {
            var complete = count > 0;
            var points = new List<ZstdIndexPoint>();
            var fills = new List<ZstdFillSpan>();
            var kindBuffer = new byte[1];
            var payload = new byte[ExactMidFramePayload];
            long offset = headerSize;   //relative to the index's first byte
            var tolerate = tolerateTruncatedTail && !complete;

            while (complete ? points.Count + fills.Count < count : true)
            {
                if (ReadUpTo(s, kindBuffer, 1) < 1)
                {
                    if (complete) throw new InvalidDataException("zstd index truncated.");
                    break;
                }
                var kind = kindBuffer[0];
                var payloadSize = kind switch
                {
                    KindFrameStart => FrameStartPayload,
                    KindMidFrame => MidFramePayload,
                    KindFillSpan => FillSpanPayload,
                    KindExactMidFrame when allowExact => ExactMidFramePayload,
                    _ => -1,
                };
                if (payloadSize < 0)
                {
                    if (tolerate) break;    //a torn write; complete records up to here are good
                    throw new InvalidDataException($"Unknown zstd index record kind {kind}.");
                }
                if (ReadUpTo(s, payload, payloadSize) < payloadSize)
                {
                    if (tolerate) break;
                    throw new InvalidDataException("zstd index truncated.");
                }

                if (kind == KindFillSpan)
                {
                    var fillOffset = BinaryPrimitives.ReadInt64LittleEndian(payload.AsSpan(0));
                    var fillLength = BinaryPrimitives.ReadInt64LittleEndian(payload.AsSpan(8));
                    if (fillOffset < 0 || fillLength <= 0)
                    {
                        if (tolerate) break;
                        throw new InvalidDataException("zstd index fill span has implausible values.");
                    }
                    fills.Add(new ZstdFillSpan(fillOffset, fillLength, payload[16]));
                    offset += 1 + payloadSize;
                    continue;
                }

                var uncompressedOffset = BinaryPrimitives.ReadInt64LittleEndian(payload.AsSpan(0));
                var compressedOffset = BinaryPrimitives.ReadInt64LittleEndian(payload.AsSpan(8));
                if (uncompressedOffset < 0 || compressedOffset < 0)
                {
                    if (tolerate) break;
                    throw new InvalidDataException("zstd index point has implausible values.");
                }

                if (kind == KindFrameStart)
                {
                    points.Add(new ZstdIndexPoint(uncompressedOffset, compressedOffset, isFrameStart: true, windowDescriptor: 0));
                    offset += 1 + payloadSize;
                }
                else if (kind == KindMidFrame)
                {
                    var windowLength = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(17));
                    if (windowLength < 0)
                    {
                        if (tolerate) break;
                        throw new InvalidDataException("zstd index point has implausible values.");
                    }
                    var point = new ZstdIndexPoint(uncompressedOffset, compressedOffset, isFrameStart: false, windowDescriptor: payload[16])
                    {
                        WindowPositionInFile = offset + 1 + payloadSize,
                        WindowCompressedLength = windowLength,
                    };
                    if (!TryConsumeBlob(s, windowLength, eagerWindows, point, tolerate)) break;
                    offset += 1 + payloadSize + windowLength;
                    points.Add(point);
                }
                else //KindExactMidFrame
                {
                    var ptrClasses = new byte[4];
                    var ptrOffsets = new int[4];
                    for (var i = 0; i < 4; i++)
                    {
                        ptrClasses[i] = payload[20 + i * 5];
                        ptrOffsets[i] = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(21 + i * 5));
                    }
                    var entropyLength = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(40));
                    var windowLength = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(44));
                    if (entropyLength <= 0 || windowLength < 0)
                    {
                        if (tolerate) break;
                        throw new InvalidDataException("zstd index point has implausible values.");
                    }
                    var point = new ZstdIndexPoint(uncompressedOffset, compressedOffset, isFrameStart: false, windowDescriptor: payload[16])
                    {
                        IsExact = true,
                        FrameHasChecksum = payload[17],
                        LitEntropy = payload[18],
                        FseEntropy = payload[19],
                        PtrClasses = ptrClasses,
                        PtrOffsets = ptrOffsets,
                        EntropyPositionInFile = offset + 1 + payloadSize,
                        EntropyCompressedLength = entropyLength,
                        WindowPositionInFile = offset + 1 + payloadSize + entropyLength,
                        WindowCompressedLength = windowLength,
                    };
                    if (!TryConsumeBlob(s, entropyLength, eagerEntropies, point, tolerate)) break;
                    if (!TryConsumeBlob(s, windowLength, eagerWindows, point, tolerate)) break;
                    offset += 1 + payloadSize + entropyLength + windowLength;
                    points.Add(point);
                }
            }

            if (complete) ValidateFillSpans(fills, totalLength);

            return new ParsedIndex { Points = points, FillSpans = fills, TotalLength = totalLength, Complete = complete, BytesConsumed = offset };
        }

        //reads (or skips) an inline blob; false = truncated tail in tolerant mode
        static bool TryConsumeBlob(Stream s, int length, Dictionary<ZstdIndexPoint, byte[]>? eager, ZstdIndexPoint point, bool tolerate)
        {
            if (eager != null)
            {
                var blob = new byte[length];
                if (ReadUpTo(s, blob, length) < length)
                {
                    if (tolerate) return false;
                    throw new InvalidDataException("zstd index truncated inside a blob.");
                }
                if (length > 0) eager[point] = blob;
            }
            else
            {
                if (s.Position + length > s.Length)
                {
                    if (tolerate) return false;
                    throw new InvalidDataException("zstd index truncated inside a blob.");
                }
                s.Seek(length, SeekOrigin.Current);
            }
            return true;
        }

        static void ValidateFillSpans(List<ZstdFillSpan> fills, long totalLength)
        {
            long previousEnd = 0;
            foreach (var fill in fills)
            {
                if (fill.UncompressedOffset < previousEnd || fill.Length <= 0 || fill.UncompressedOffset + fill.Length > totalLength)
                    throw new InvalidDataException("zstd index fill spans are not sorted, non-overlapping and in-bounds.");
                previousEnd = fill.UncompressedOffset + fill.Length;
            }
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
        /// <paramref name="indexPath"/>). Always writes the current ("ZSTZRAN4") format; legacy
        /// window-prefix points migrate unchanged and keep serving via their verified-span path.</summary>
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
        /// position. The stream is left open. Always writes the current ("ZSTZRAN4") format.</summary>
        public void Save(Stream destination)
        {
            WriteHeader(destination, fingerprintLength, fingerprintCrc, UncompressedLength, points.Count + fillSpans.Count);
            foreach (var point in points)
            {
                var window = windowSource.GetCompressedWindow(point);
                var entropy = windowSource.GetCompressedEntropy(point);
                WritePointRecord(destination, point, window, entropy);
            }
            foreach (var fill in fillSpans)
            {
                WriteFillSpanRecord(destination, fill);
            }
            destination.Flush();
        }

        static void WriteHeader(Stream s, long fingerprintLength, uint fingerprintCrc, long totalLength, int count)
        {
            var header = new byte[HeaderSizeV4];
            MagicV4.CopyTo(header, 0);
            BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(8), fingerprintLength);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16), fingerprintCrc);
            BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(20), totalLength);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(28), count);
            s.Write(header, 0, header.Length);
        }

        static void WritePointRecord(Stream s, ZstdIndexPoint point, byte[] window, byte[] entropy)
        {
            if (point.IsFrameStart)
            {
                var record = new byte[1 + FrameStartPayload];
                record[0] = KindFrameStart;
                BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(1), point.UncompressedOffset);
                BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(9), point.CompressedOffset);
                s.Write(record, 0, record.Length);
                return;
            }

            if (point.IsExact)
            {
                var record = new byte[1 + ExactMidFramePayload];
                record[0] = KindExactMidFrame;
                BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(1), point.UncompressedOffset);
                BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(9), point.CompressedOffset);
                record[17] = point.WindowDescriptor;
                record[18] = point.FrameHasChecksum;
                record[19] = point.LitEntropy;
                record[20] = point.FseEntropy;
                for (var i = 0; i < 4; i++)
                {
                    record[21 + i * 5] = point.PtrClasses[i];
                    BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(22 + i * 5), point.PtrOffsets[i]);
                }
                BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(41), entropy.Length);
                BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(45), window.Length);
                s.Write(record, 0, record.Length);
                s.Write(entropy, 0, entropy.Length);
                s.Write(window, 0, window.Length);
                return;
            }

            //legacy (v1-v3) window-prefix point, migrated verbatim
            var legacy = new byte[1 + MidFramePayload];
            legacy[0] = KindMidFrame;
            BinaryPrimitives.WriteInt64LittleEndian(legacy.AsSpan(1), point.UncompressedOffset);
            BinaryPrimitives.WriteInt64LittleEndian(legacy.AsSpan(9), point.CompressedOffset);
            legacy[17] = point.WindowDescriptor;
            BinaryPrimitives.WriteInt32LittleEndian(legacy.AsSpan(18), window.Length);
            s.Write(legacy, 0, legacy.Length);
            s.Write(window, 0, window.Length);
        }

        static void WriteFillSpanRecord(Stream s, ZstdFillSpan fill)
        {
            var record = new byte[1 + FillSpanPayload];
            record[0] = KindFillSpan;
            BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(1), fill.UncompressedOffset);
            BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(9), fill.Length);
            record[17] = fill.FillByte;
            s.Write(record, 0, record.Length);
        }

        //================================================================= load-or-build

        /// <summary>
        /// Loads the index at <paramref name="indexPath"/> if it exists, is valid, and matches the
        /// compressed stream; otherwise builds one from <paramref name="compressed"/> into
        /// <c>indexPath + ".wip"</c> (sealed records are flushed as they are proven, so build memory
        /// stays flat and an interrupted build resumes on the next call) and moves it into place
        /// when finalised.
        /// </summary>
        public static ZstdIndex LoadOrBuild(Stream compressed, string indexPath, ZstdIndexOptions? options = null,
                                            IProgress<ZstdIndexProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            options ??= new ZstdIndexOptions();
            ValidateForBuild(compressed);
            var fingerprint = ComputeFingerprint(compressed);

            if (File.Exists(indexPath))
            {
                try
                {
                    var loaded = Load(indexPath);
                    if (loaded.fingerprintLength != 0
                        && (loaded.fingerprintLength != fingerprint.Length || loaded.fingerprintCrc != fingerprint.Crc))
                    {
                        loaded.Dispose();
                        throw new InvalidDataException("the index's fingerprint does not match the compressed stream");
                    }
                    return loaded;
                }
                catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or IOException)
                {
                    options.Logger?.LogWarning("Could not load zstd index {IndexPath} ({Message}). Rebuilding.", Path.GetFileName(indexPath), ex.Message);
                }
            }

            var wipPath = indexPath + ".wip";
            BuildResult built;
            using (var wip = new FileStream(wipPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read))
            {
                built = BuildIncremental(compressed, wip, baseOffset: 0, fingerprint, options, progress, cancellationToken);
            }
            FileHelpers.MoveOverwrite(wipPath, indexPath);
            return new ZstdIndex(built.Points, built.FillSpans, built.TotalLength, new FileWindowSource(indexPath),
                                 fingerprint.Length, fingerprint.Crc);
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
            ValidateForBuild(compressed);
            var fingerprint = ComputeFingerprint(compressed);

            if (indexStream.CanSeek)
            {
                var basePosition = indexStream.Position;
                if (indexStream.CanRead && indexStream.Length - basePosition >= HeaderSize)
                {
                    try
                    {
                        var loaded = Load(indexStream, leaveOpen: true);
                        if (loaded.fingerprintLength != 0
                            && (loaded.fingerprintLength != fingerprint.Length || loaded.fingerprintCrc != fingerprint.Crc))
                        {
                            loaded.Dispose();
                            throw new InvalidDataException("the index's fingerprint does not match the compressed stream");
                        }
                        return loaded;
                    }
                    catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException)
                    {
                        options.Logger?.LogWarning("Could not load the zstd index from the supplied stream ({Message}). Building.", ex.Message);
                        indexStream.Position = basePosition;
                    }
                }
                if (!indexStream.CanRead || !indexStream.CanWrite)
                    throw new ArgumentException("A seekable index stream must be readable and writable to build into.", nameof(indexStream));

                var built = BuildIncremental(compressed, indexStream, basePosition, fingerprint, options, progress, cancellationToken);
                try { indexStream.SetLength(indexStream.Position); } catch (NotSupportedException) { }
                indexStream.Flush();
                return new ZstdIndex(built.Points, built.FillSpans, built.TotalLength,
                                     new StreamWindowSource(indexStream, basePosition, leaveOpen: true),
                                     fingerprint.Length, fingerprint.Crc);
            }

            if (indexStream.CanRead && !indexStream.CanWrite)
            {
                //read-only pipe: it can only be a source
                return Load(indexStream, leaveOpen: true);
            }

            //non-seekable writable stream: it can only be a destination, and cannot be finalised
            //in place - build in memory, then stream the complete index out
            var index = Build(compressed, options, progress, cancellationToken);
            index.Save(indexStream);
            indexStream.Flush();
            return index;
        }

        //================================================================= build

        /// <summary>
        /// Builds an in-memory index by decoding <paramref name="compressed"/> once from the start.
        /// Every block boundary at the target spacing becomes an exact-state resume point; a small
        /// sample of spans is byte-verified (see the class remarks). Compressed snapshots are held
        /// in memory; prefer <see cref="LoadOrBuild(Stream, string, ZstdIndexOptions?, IProgress{ZstdIndexProgress}?, CancellationToken)"/>
        /// for large streams (flat memory, resumable).
        /// </summary>
        public static ZstdIndex Build(Stream compressed, ZstdIndexOptions? options = null,
                                      IProgress<ZstdIndexProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            ValidateForBuild(compressed);
            options ??= new ZstdIndexOptions();
            var fingerprint = ComputeFingerprint(compressed);

            var windows = new Dictionary<ZstdIndexPoint, byte[]>();
            var entropies = new Dictionary<ZstdIndexPoint, byte[]>();
            var sink = new MemorySink(windows, entropies);
            var built = BuildCore(compressed, sink, options, progress, cancellationToken, resume: null);
            return new ZstdIndex(built.Points, built.FillSpans, built.TotalLength,
                                 new MemoryWindowSource(windows, entropies), fingerprint.Length, fingerprint.Crc);
        }

        static void ValidateForBuild(Stream compressed)
        {
            if (compressed == null) throw new ArgumentNullException(nameof(compressed));
            if (!compressed.CanSeek) throw new ArgumentException("The compressed stream must be seekable to build an index over it.", nameof(compressed));
        }

        static (long Length, uint Crc) ComputeFingerprint(Stream compressed)
        {
            var position = compressed.Position;
            compressed.Position = 0;
            var probe = new byte[(int)Math.Min(compressed.Length, FingerprintProbeBytes)];
            var got = ReadUpTo(compressed, probe, probe.Length);
            compressed.Position = position;
            return (compressed.Length, Crc32.Compute(probe, 0, got));
        }

        readonly struct BuildResult(List<ZstdIndexPoint> points, List<ZstdFillSpan> fillSpans, long totalLength)
        {
            public List<ZstdIndexPoint> Points { get; } = points;
            public List<ZstdFillSpan> FillSpans { get; } = fillSpans;
            public long TotalLength { get; } = totalLength;
        }

        sealed class ResumeSeed
        {
            public ZstdIndexPoint Point = null!;
            public byte[] WindowRaw = [];
            public ZstdExactState? State;   //null for a frame-start resume
        }

        static BuildResult BuildIncremental(Stream compressed, Stream destination, long baseOffset, (long Length, uint Crc) fingerprint,
            ZstdIndexOptions options, IProgress<ZstdIndexProgress>? progress, CancellationToken cancellationToken)
        {
            //an interrupted earlier build left sealed records - keep them and continue from the
            //LAST sealed point (exact restore; no fast-forward decode)
            List<ZstdIndexPoint> sealedPoints = [];
            List<ZstdFillSpan> fillSpans = [];
            long resumeEnd = 0;
            ResumeSeed? resume = null;
            if (destination.Length - baseOffset >= HeaderSize)
            {
                try
                {
                    destination.Position = baseOffset;
                    var parsed = Parse(destination, eagerWindows: null, eagerEntropies: null, tolerateTruncatedTail: true);
                    if (parsed.Version != 4)
                    {
                        options.Logger?.LogWarning("The partial zstd index uses an older format. Starting fresh.");
                    }
                    else if (parsed.FingerprintLength != fingerprint.Length || parsed.FingerprintCrc != fingerprint.Crc)
                    {
                        options.Logger?.LogWarning("The partial zstd index was built from a different compressed stream. Starting fresh.");
                    }
                    else if (parsed.Points.Count > 0)
                    {
                        var last = parsed.Points[parsed.Points.Count - 1];
                        if (last.IsFrameStart || last.IsExact)
                        {
                            resume = new ResumeSeed { Point = last };
                            if (last.IsExact)
                            {
                                using var decompressor = new ZstdSharp.Decompressor();
                                if (last.WindowCompressedLength > 0)
                                {
                                    var windowBlob = new byte[last.WindowCompressedLength];
                                    destination.Position = baseOffset + last.WindowPositionInFile;
                                    if (ReadUpTo(destination, windowBlob, windowBlob.Length) < windowBlob.Length)
                                        throw new InvalidDataException("zstd index truncated inside the resume window.");
                                    resume.WindowRaw = decompressor.Unwrap(windowBlob).ToArray();
                                }
                                var entropyBlob = new byte[last.EntropyCompressedLength];
                                destination.Position = baseOffset + last.EntropyPositionInFile;
                                if (ReadUpTo(destination, entropyBlob, entropyBlob.Length) < entropyBlob.Length)
                                    throw new InvalidDataException("zstd index truncated inside the resume state.");
                                var entropyRaw = decompressor.Unwrap(entropyBlob).ToArray();
                                resume.State = ZstdExactState.FromSymbolic(entropyRaw, last.PtrClasses, last.PtrOffsets, last.LitEntropy, last.FseEntropy);
                            }
                            sealedPoints = parsed.Points;
                            fillSpans = parsed.FillSpans;
                            resumeEnd = parsed.BytesConsumed;
                        }
                        else
                        {
                            options.Logger?.LogWarning("The partial zstd index's last point cannot seed an exact resume. Starting fresh.");
                        }
                    }
                }
                catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException)
                {
                    options.Logger?.LogWarning("Could not read the partial zstd index ({Message}). Starting fresh.", ex.Message);
                    sealedPoints = [];
                    fillSpans = [];
                    resume = null;
                    resumeEnd = 0;
                }
            }

            var sink = new StreamSink(destination, baseOffset, sealedPoints, fillSpans, resumeEnd, fingerprint);
            try
            {
                return BuildCore(compressed, sink, options, progress, cancellationToken, resume);
            }
            catch (InvalidDataException ex) when (resume != null)
            {
                //e.g. the partial index does not line up with the compressed stream - retry from
                //scratch. (Sampled-verification failures throw ZstdExactVerificationException,
                //which is deliberately NOT caught here: never heal a real mismatch.)
                options.Logger?.LogWarning("Could not resume the zstd index build ({Message}). Rebuilding from scratch.", ex.Message);
                sink.Reset();
                return BuildCore(compressed, sink, options, progress, cancellationToken, resume: null);
            }
        }

        static long WindowSizeFromDescriptor(byte descriptor)
        {
            var exponent = descriptor >> 3;
            var mantissa = descriptor & 7;
            var windowBase = 1L << (10 + exponent);
            return windowBase + (windowBase / 8) * mantissa;
        }

        static unsafe BuildResult BuildCore(Stream compressed, IIndexSink sink,
            ZstdIndexOptions options, IProgress<ZstdIndexProgress>? progress, CancellationToken cancellationToken, ResumeSeed? resume)
        {
            var logger = options.Logger;
            var targetSpanBytes = Math.Max(64 * 1024, options.TargetSpanBytes);
            var sampleSpans = options.VerifiedSampleSpans;
            var frameStartsOnly = options.FrameStartsOnly;
            var sealedPoints = sink.SealedPoints;
            var compressedTotal = compressed.Length;

            if (resume != null)
                logger?.LogInformation("Resuming the zstd random-access index from uncompressed offset {Offset:N0} ({Count:N0} sealed records kept; exact state needs no fast-forward decode).",
                                       resume.Point.UncompressedOffset, sealedPoints.Count + sink.FillSpans.Count);
            else
                logger?.LogInformation("Creating a zstd random-access index ({Length:N0} compressed bytes).", compressedTotal);

            var reader = new ZstdBlockReader(compressed);
            using var windowCompressor = new ZstdSharp.Compressor(options.WindowCompressionLevel);

            var main = Methods.ZSTD_createDCtx();
            Methods.ZSTD_DCtx_setParameter(main, ZSTD_dParameter.ZSTD_d_windowLogMax, 31);

            //---- fill-run tracker: long single-byte runs of the true output become fill-span
            //records, so reads inside them never touch the decoder. The threshold is clamped above
            //the maximum block size (128 KB), so a qualifying run always spans blocks and only a
            //block's TRAILING run can begin one. On resume, a run that was still open at the
            //interruption is re-recorded from the resume point (possibly with a truncated start -
            //an under-approximation, which is always safe); already-recorded runs are deduped via
            //lastRecordedFillEnd.
            var fillThreshold = Math.Max(256 * 1024, options.FillSpanThreshold);
            var lastRecordedFillEnd = sink.FillSpans.Count > 0
                ? sink.FillSpans[sink.FillSpans.Count - 1].UncompressedOffset + sink.FillSpans[sink.FillSpans.Count - 1].Length
                : 0;
            var fillRunActive = false;
            byte fillRunByte = 0;
            long fillRunStart = 0;
            long fillRunLength = 0;

            ExactResumeShadow? samplingShadow = null;
            long samplingShadowStart = 0;
            var verified = 0;
            var symbolicSkipWarned = false;
            var resumeWindowPin = default(GCHandle);

            try
            {
                var outBuf = new byte[1 << 20];     //one block regenerates ≤128 KB

                long uncompressedPos = 0;
                long lastPointOffset = 0;
                long lastProgressReportAt = 0;

                //rolling ring of true output (window snapshots), indexed by absolute position % windowSize
                var ring = Array.Empty<byte>();
                long frameWindowSize = 0;
                byte frameWindowDescriptor = 0;
                var frameHasChecksum = false;
                var skipNextFrameStartPlant = false;

                void CloseFillRun()
                {
                    if (fillRunActive && fillRunLength >= fillThreshold && fillRunStart >= lastRecordedFillEnd)
                    {
                        sink.AppendFillSpan(new ZstdFillSpan(fillRunStart, fillRunLength, fillRunByte));
                        lastRecordedFillEnd = fillRunStart + fillRunLength;
                    }
                    fillRunActive = false;
                    fillRunLength = 0;
                }

                void TrackFillRuns(ReadOnlySpan<byte> truth, long outputOffset)
                {
                    if (truth.Length == 0) return;

                    var scanFrom = 0;
                    if (fillRunActive)
                    {
                        var firstDifferent = ZstdFrameHelpers.IndexOfNot(truth, fillRunByte);
                        if (firstDifferent < 0)
                        {
                            fillRunLength += truth.Length;      //whole block extends the run
                            return;
                        }
                        fillRunLength += firstDifferent;
                        scanFrom = firstDifferent;
                        CloseFillRun();
                    }

                    //only a trailing run can grow past the (block-sized) threshold
                    var lastByte = truth[truth.Length - 1];
                    var tailStart = ZstdFrameHelpers.LastIndexOfNot(truth, lastByte) + 1;
                    if (tailStart < scanFrom) tailStart = scanFrom;
                    fillRunActive = true;
                    fillRunByte = lastByte;
                    fillRunStart = outputOffset + tailStart;
                    fillRunLength = truth.Length - tailStart;
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

                void CompleteSamplingShadow()
                {
                    if (samplingShadow == null) return;
                    samplingShadow.Dispose();
                    samplingShadow = null;
                    verified++;
                }

                void PlantExactPoint(long boundaryCompressedOffset)
                {
                    var window = SnapshotWindow();
                    var state = ZstdExactState.Capture(main);
                    if (!state.TryGetSymbolicPointers(out var ptrClasses, out var ptrOffsets))
                    {
                        //an external selector pointer that is not one of the three default tables:
                        //never observed; skip the boundary rather than store something unsound
                        if (!symbolicSkipWarned)
                        {
                            symbolicSkipWarned = true;
                            logger?.LogWarning("Skipped a resume point at {Offset:N0}: unrecognised decoder table pointer.", uncompressedPos);
                        }
                        return;
                    }

                    var point = new ZstdIndexPoint(uncompressedPos, boundaryCompressedOffset, isFrameStart: false, frameWindowDescriptor)
                    {
                        IsExact = true,
                        FrameHasChecksum = (byte)(frameHasChecksum ? 1 : 0),
                        LitEntropy = (byte)state.LitEntropy,
                        FseEntropy = (byte)state.FseEntropy,
                        PtrClasses = ptrClasses,
                        PtrOffsets = ptrOffsets,
                    };
                    var windowCompressed = window.Length == 0 ? [] : windowCompressor.Wrap(window).ToArray();
                    var entropyCompressed = windowCompressor.Wrap(state.Entropy).ToArray();
                    sink.Append(point, windowCompressed, entropyCompressed);        //sealed immediately
                    lastPointOffset = uncompressedPos;

                    //sampled verification: the shadow armed at the previous point has now survived
                    //its whole span byte-identical - and this new point starts the next sample
                    CompleteSamplingShadow();
                    if (verified < sampleSpans)
                    {
                        samplingShadow = new ExactResumeShadow(window, frameWindowDescriptor, state);
                        samplingShadowStart = uncompressedPos;
                        if (!samplingShadow.Healthy)
                            throw new ZstdExactVerificationException($"zstd exact-resume shadow failed to initialise at uncompressed {uncompressedPos:N0}.");
                    }
                }

                //frame-body decode; entered at the first block (fresh frames) or mid-frame (resume)
                void DecodeFrameBlocks(bool resumedMidFrame)
                {
                    var lastBlock = false;
                    while (!lastBlock)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        var boundaryCompressedOffset = reader.Position;
                        if (!frameStartsOnly && uncompressedPos >= lastPointOffset + targetSpanBytes)
                        {
                            PlantExactPoint(boundaryCompressedOffset);
                        }

                        var block = reader.ReadBlock(out lastBlock);
                        if (!ZstdFrameHelpers.Feed(main, block, outBuf, out var produced))
                            throw new InvalidDataException($"zstd decode error during index build at uncompressed {uncompressedPos:N0}.");
                        var truth = outBuf.AsSpan(0, produced);

                        //rolling window ring
                        for (var copied = 0; copied < produced; )
                        {
                            var ringPos = (int)((uncompressedPos + copied) % frameWindowSize);
                            var n = (int)Math.Min(produced - copied, frameWindowSize - ringPos);
                            Array.Copy(outBuf, copied, ring, ringPos, n);
                            copied += n;
                        }

                        TrackFillRuns(truth, uncompressedPos);

                        if (samplingShadow != null)
                        {
                            var (ok, mismatchAt, shadowProduced) = samplingShadow.FeedAndCompare(block, truth);
                            if (!ok)
                            {
                                var detail = mismatchAt >= 0
                                    ? $"first differing byte at uncompressed {uncompressedPos + mismatchAt:N0}"
                                    : mismatchAt == -2
                                        ? $"length mismatch (shadow {shadowProduced}, truth {truth.Length}) at uncompressed {uncompressedPos:N0}"
                                        : $"shadow decode error at uncompressed {uncompressedPos:N0}";
                                throw new ZstdExactVerificationException(
                                    $"zstd exact-resume verification FAILED for the point at {samplingShadowStart:N0}: {detail}. "
                                    + "This indicates a state-capture bug, not bad data - refusing to build a wrong index.");
                            }
                        }

                        uncompressedPos += produced;

                        if (reader.Position - lastProgressReportAt >= 16 * 1024 * 1024)
                        {
                            lastProgressReportAt = reader.Position;
                            ReportProgress(progress, reader.Position, compressedTotal, uncompressedPos, sealedPoints.Count);
                        }
                    }

                    //frame end: the ORIGINAL frame may carry a 4-byte content checksum. A fresh
                    //frame's decoder expects (and validates) it; a resumed frame's synthetic header
                    //declared none, so the bytes are skipped in the stream instead.
                    if (frameHasChecksum)
                    {
                        if (resumedMidFrame)
                        {
                            compressed.Seek(4, SeekOrigin.Current);
                        }
                        else
                        {
                            var checksum = new byte[4];
                            if (ReadUpTo(compressed, checksum, 4) < 4) throw new InvalidDataException("Truncated zstd stream (checksum).");
                            if (!ZstdFrameHelpers.Feed(main, checksum, outBuf, out _))
                                throw new InvalidDataException($"zstd content checksum mismatch at uncompressed {uncompressedPos:N0}.");
                        }
                    }

                    //a live sampling shadow has verified [its point -> frame end] = its whole span
                    CompleteSamplingShadow();
                }

                //---- fresh build: start at the beginning of the compressed stream ----
                if (resume == null)
                {
                    compressed.Seek(0, SeekOrigin.Begin);
                }

                //---- resume: reinstate the last sealed point exactly and continue from it ----
                if (resume != null)
                {
                    var p = resume.Point;
                    uncompressedPos = p.UncompressedOffset;
                    lastPointOffset = p.UncompressedOffset;
                    lastProgressReportAt = p.CompressedOffset;

                    if (p.IsFrameStart)
                    {
                        compressed.Seek(p.CompressedOffset, SeekOrigin.Begin);
                        skipNextFrameStartPlant = true;     //the point is already sealed
                    }
                    else
                    {
                        frameWindowDescriptor = p.WindowDescriptor;
                        frameWindowSize = WindowSizeFromDescriptor(p.WindowDescriptor);
                        frameHasChecksum = p.FrameHasChecksum != 0;
                        if (frameWindowSize > 1L << 30) throw new InvalidDataException($"zstd window {frameWindowSize:N0} too large to index.");
                        ring = new byte[frameWindowSize];
                        for (var i = 0; i < resume.WindowRaw.Length; i++)
                        {
                            ring[(uncompressedPos - resume.WindowRaw.Length + i) % frameWindowSize] = resume.WindowRaw[i];
                        }

                        if (resume.WindowRaw.Length > 0)
                        {
                            resumeWindowPin = GCHandle.Alloc(resume.WindowRaw, GCHandleType.Pinned);
                            var r = Methods.ZSTD_DCtx_refPrefix(main, (byte*)resumeWindowPin.AddrOfPinnedObject(), (nuint)resume.WindowRaw.Length);
                            if (Methods.ZSTD_isError(r)) throw new InvalidDataException($"zstd resume refPrefix failed: {Methods.ZSTD_getErrorName(r)}");
                        }
                        if (!ZstdFrameHelpers.Feed(main, ZstdFrameHelpers.SyntheticFrameHeader(frameWindowDescriptor), outBuf, out _))
                            throw new InvalidDataException("zstd resume header rejected.");
                        resume.State!.Restore(main);

                        compressed.Seek(p.CompressedOffset, SeekOrigin.Begin);
                        DecodeFrameBlocks(resumedMidFrame: true);

                        if (resumeWindowPin.IsAllocated)
                        {
                            resumeWindowPin.Free();
                            resumeWindowPin = default;
                        }
                    }
                }

                while (true)
                {
                    var frameStartOffset = reader.Position;
                    var frameKind = reader.BeginFrame(out var headerBytes, out frameWindowSize, out frameWindowDescriptor, out frameHasChecksum);
                    if (frameKind == FrameKind.EndOfStream) break;
                    if (frameKind == FrameKind.Skippable) continue;

                    if (frameWindowSize > 1L << 30) throw new InvalidDataException($"zstd window {frameWindowSize:N0} too large to index.");
                    if (ring.Length < frameWindowSize) ring = new byte[frameWindowSize];

                    //a frame start is a perfect (stateless) resume point, sealed immediately
                    if (skipNextFrameStartPlant)
                    {
                        skipNextFrameStartPlant = false;
                        var adopted = resume!.Point;
                        if (uncompressedPos != adopted.UncompressedOffset || frameStartOffset != adopted.CompressedOffset)
                            throw new InvalidDataException("The compressed stream does not match the partial index (resume position not found).");
                    }
                    else
                    {
                        sink.Append(new ZstdIndexPoint(uncompressedPos, frameStartOffset, isFrameStart: true, frameWindowDescriptor), [], []);
                        lastPointOffset = uncompressedPos;
                    }

                    if (!ZstdFrameHelpers.Feed(main, headerBytes, outBuf, out _)) throw new InvalidDataException("zstd frame header rejected.");

                    DecodeFrameBlocks(resumedMidFrame: false);
                }

                CloseFillRun();

                if (sealedPoints.Count == 0) throw new InvalidDataException("No zstd frames found.");

                sink.Finalise(uncompressedPos);
                ReportProgress(progress, compressedTotal, compressedTotal, uncompressedPos, sealedPoints.Count);

                logger?.LogInformation("Finished the zstd index: {Count:N0} exact resume points ({Verified:N0} byte-verified), {Fills:N0} fill spans.",
                                       sealedPoints.Count, verified, sink.FillSpans.Count);
                return new BuildResult(sealedPoints, sink.FillSpans, uncompressedPos);
            }
            finally
            {
                Methods.ZSTD_freeDCtx(main);
                samplingShadow?.Dispose();
                if (resumeWindowPin.IsAllocated) resumeWindowPin.Free();
            }
        }

        static void ReportProgress(IProgress<ZstdIndexProgress>? progress, long compressedProcessed, long compressedTotal, long uncompressedProduced, int pointCount)
        {
            var fraction = compressedTotal > 0 ? Math.Min(1.0, (double)compressedProcessed / compressedTotal) : 1.0;
            progress?.Report(new ZstdIndexProgress(ZstdIndexPhase.Scanning, compressedProcessed, compressedTotal, uncompressedProduced, pointCount, fraction));
        }

        //================================================================= build sinks

        /// <summary>Where sealed records go as the build produces them.</summary>
        interface IIndexSink
        {
            /// <summary>Already-sealed points (non-empty when resuming an interrupted build).
            /// <see cref="Append"/> adds to this list.</summary>
            List<ZstdIndexPoint> SealedPoints { get; }
            /// <summary>Already-recorded fill spans (non-empty when resuming).
            /// <see cref="AppendFillSpan"/> adds to this list.</summary>
            List<ZstdFillSpan> FillSpans { get; }
            void Append(ZstdIndexPoint point, byte[] windowCompressed, byte[] entropyCompressed);
            void AppendFillSpan(ZstdFillSpan fillSpan);
            void Finalise(long totalUncompressedLength);
            /// <summary>Discard everything and start over (failed resume).</summary>
            void Reset();
        }

        sealed class MemorySink(Dictionary<ZstdIndexPoint, byte[]> windows, Dictionary<ZstdIndexPoint, byte[]> entropies) : IIndexSink
        {
            public List<ZstdIndexPoint> SealedPoints { get; } = [];
            public List<ZstdFillSpan> FillSpans { get; } = [];

            public void Append(ZstdIndexPoint point, byte[] windowCompressed, byte[] entropyCompressed)
            {
                point.WindowCompressedLength = windowCompressed.Length;
                point.EntropyCompressedLength = entropyCompressed.Length;
                if (windowCompressed.Length > 0) windows[point] = windowCompressed;
                if (entropyCompressed.Length > 0) entropies[point] = entropyCompressed;
                SealedPoints.Add(point);
            }

            public void AppendFillSpan(ZstdFillSpan fillSpan) => FillSpans.Add(fillSpan);

            public void Finalise(long totalUncompressedLength) { }

            public void Reset()
            {
                SealedPoints.Clear();
                FillSpans.Clear();
                windows.Clear();
                entropies.Clear();
            }
        }

        /// <summary>Appends each sealed record (typed, blobs inline) to a seekable stream and
        /// flushes, so every sealed record survives an interruption. The header's counts stay zeroed
        /// (= incomplete) until <see cref="Finalise"/> patches them.</summary>
        sealed class StreamSink : IIndexSink
        {
            readonly Stream stream;
            readonly long baseOffset;
            readonly (long Length, uint Crc) fingerprint;

            public List<ZstdIndexPoint> SealedPoints { get; private set; }
            public List<ZstdFillSpan> FillSpans { get; private set; }

            public StreamSink(Stream stream, long baseOffset, List<ZstdIndexPoint> existingSealedPoints, List<ZstdFillSpan> existingFillSpans,
                              long resumeEndOffset, (long Length, uint Crc) fingerprint)
            {
                this.stream = stream;
                this.baseOffset = baseOffset;
                this.fingerprint = fingerprint;
                SealedPoints = existingSealedPoints;
                FillSpans = existingFillSpans;

                if (SealedPoints.Count > 0)
                {
                    //continue after the last complete record (dropping any partial tail)
                    var end = baseOffset + resumeEndOffset;
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
                WriteHeader(stream, fingerprint.Length, fingerprint.Crc, 0, 0);     //zeroed counts mark the index incomplete
                stream.Flush();
            }

            public void Append(ZstdIndexPoint point, byte[] windowCompressed, byte[] entropyCompressed)
            {
                WritePointRecord(stream, point, windowCompressed, entropyCompressed);
                point.WindowPositionInFile = stream.Position - baseOffset - windowCompressed.Length;
                point.WindowCompressedLength = windowCompressed.Length;
                point.EntropyPositionInFile = stream.Position - baseOffset - windowCompressed.Length - entropyCompressed.Length;
                point.EntropyCompressedLength = entropyCompressed.Length;
                stream.Flush();     //each sealed record survives an interruption
                SealedPoints.Add(point);
            }

            public void AppendFillSpan(ZstdFillSpan fillSpan)
            {
                WriteFillSpanRecord(stream, fillSpan);
                stream.Flush();
                FillSpans.Add(fillSpan);
            }

            public void Finalise(long totalUncompressedLength)
            {
                var end = stream.Position;
                stream.Position = baseOffset + 20;      //v4 header: totalUncompressed at 20, recordCount at 28
                var tail = new byte[12];
                BinaryPrimitives.WriteInt64LittleEndian(tail.AsSpan(0), totalUncompressedLength);
                BinaryPrimitives.WriteInt32LittleEndian(tail.AsSpan(8), SealedPoints.Count + FillSpans.Count);
                stream.Write(tail, 0, tail.Length);
                stream.Position = end;
                stream.Flush();
            }

            public void Reset()
            {
                SealedPoints = [];
                FillSpans = [];
                try { stream.SetLength(baseOffset); } catch (NotSupportedException) { }
                WriteFreshHeader();
            }
        }
    }

    /// <summary>Thrown when a sampled exact-resume verification fails during an index build: the
    /// shadow decoder restored from a just-captured state did not reproduce the true output. This
    /// indicates a state-capture bug, never bad luck - it is deliberately not healed or retried.</summary>
    public sealed class ZstdExactVerificationException : Exception
    {
        internal ZstdExactVerificationException(string message)
            : base(message) { }
    }
}
