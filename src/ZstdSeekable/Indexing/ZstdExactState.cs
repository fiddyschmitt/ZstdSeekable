using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using ZstdSeekable.Internal;
using ZstdSharp.Unsafe;

namespace ZstdSeekable
{
    /// <summary>
    /// Z0 PROTOTYPE - an EXACT snapshot of zstd's carried inter-block decoder state, captured at a
    /// block boundary and reinstated into a fresh DCtx after the synthetic-frame-header +
    /// refPrefix(window) resume dance. zstd's carried state between blocks is precisely:
    /// the window (handled by refPrefix, as today), the repeat offsets rep[3], and the entropy
    /// tables a block can reuse via Repeat_Mode (HUF literals DTable + LL/ML/OF FSE DTables) - all
    /// of which live inside <c>dctx-&gt;entropy</c> (ZSTD_entropyDTables_t, one flat struct), plus
    /// the DCtx table-selector pointers LLTptr/MLTptr/OFTptr/HUFptr and the litEntropy/fseEntropy
    /// flags. Snapshotting these makes a resume exact by construction - no trial verification, no
    /// insurance-divergence class.
    ///
    /// Pointer handling: at a boundary each selector either points INTO dctx->entropy (tables
    /// loaded from the stream / RLE) or at a process-wide static default table (Predefined_Mode) or
    /// is as-initialised. Entropy-relative pointers are stored as offsets and rebased on restore;
    /// external pointers (the three static default tables) are stable for the process lifetime and
    /// are copied verbatim. (For a persisted index they would be stored symbolically and re-derived
    /// at load - trivial, since there are only three such tables plus null.)
    /// </summary>
    internal sealed unsafe class ZstdExactState
    {
        /// <summary>Raw byte copy of dctx->entropy (ZSTD_entropyDTables_t).</summary>
        public byte[] Entropy = Array.Empty<byte>();

        // Selector pointers: offset >= 0 => entropy-relative; -1 => external (raw value kept).
        public long LLTOffset, MLTOffset, OFTOffset, HUFOffset;
        public ulong LLTExternal, MLTExternal, OFTExternal, HUFExternal;

        public uint LitEntropy;
        public uint FseEntropy;

        public static int EntropyStructSize => sizeof(ZSTD_entropyDTables_t);

        /// <summary>Captures the carried state from a streaming DCtx positioned at a block boundary
        /// (the previous block fully decoded and flushed).</summary>
        public static ZstdExactState Capture(ZSTD_DCtx_s* dctx)
        {
            var size = sizeof(ZSTD_entropyDTables_t);
            var state = new ZstdExactState { Entropy = new byte[size] };
            fixed (byte* dst = state.Entropy)
            {
                Buffer.MemoryCopy(&dctx->entropy, dst, size, size);
            }

            var entropyBase = (byte*)&dctx->entropy;
            Classify((byte*)dctx->LLTptr, entropyBase, size, out state.LLTOffset, out state.LLTExternal);
            Classify((byte*)dctx->MLTptr, entropyBase, size, out state.MLTOffset, out state.MLTExternal);
            Classify((byte*)dctx->OFTptr, entropyBase, size, out state.OFTOffset, out state.OFTExternal);
            Classify((byte*)dctx->HUFptr, entropyBase, size, out state.HUFOffset, out state.HUFExternal);
            state.LitEntropy = dctx->litEntropy;
            state.FseEntropy = dctx->fseEntropy;
            return state;
        }

        private static void Classify(byte* p, byte* entropyBase, int size, out long offset, out ulong external)
        {
            if (p >= entropyBase && p < entropyBase + size)
            {
                offset = p - entropyBase;
                external = 0;
            }
            else
            {
                offset = -1;
                external = (ulong)p; // null or a process-wide static default table
            }
        }

        /// <summary>
        /// Reinstates the captured state into a fresh DCtx. Must be called AFTER the synthetic frame
        /// header has been fully consumed (frame init resets dctx->entropy) and BEFORE the first
        /// resumed block is fed.
        /// </summary>
        public void Restore(ZSTD_DCtx_s* dctx)
        {
            var size = sizeof(ZSTD_entropyDTables_t);
            if (Entropy.Length != size)
            {
                throw new InvalidOperationException(
                    $"entropy snapshot size {Entropy.Length} != ZSTD_entropyDTables_t size {size}"
                );
            }
            fixed (byte* src = Entropy)
            {
                Buffer.MemoryCopy(src, &dctx->entropy, size, size);
            }

            var entropyBase = (byte*)&dctx->entropy;
            dctx->LLTptr = (ZSTD_seqSymbol*)(LLTOffset >= 0 ? entropyBase + LLTOffset : (byte*)LLTExternal);
            dctx->MLTptr = (ZSTD_seqSymbol*)(MLTOffset >= 0 ? entropyBase + MLTOffset : (byte*)MLTExternal);
            dctx->OFTptr = (ZSTD_seqSymbol*)(OFTOffset >= 0 ? entropyBase + OFTOffset : (byte*)OFTExternal);
            dctx->HUFptr = (uint*)(HUFOffset >= 0 ? entropyBase + HUFOffset : (byte*)HUFExternal);
            dctx->litEntropy = LitEntropy;
            dctx->fseEntropy = FseEntropy;
        }

        /// <summary>Human-readable pointer classification, for diagnostics.</summary>
        public string Describe() =>
            $"LLT={DescribeOne(LLTOffset, LLTExternal)} MLT={DescribeOne(MLTOffset, MLTExternal)} "
            + $"OFT={DescribeOne(OFTOffset, OFTExternal)} HUF={DescribeOne(HUFOffset, HUFExternal)} "
            + $"lit={LitEntropy} fse={FseEntropy}";

        private static string DescribeOne(long offset, ulong external) =>
            offset >= 0 ? $"entropy+{offset}" : external == 0 ? "null" : "static";

        // ---- symbolic form (what the index format stores - never raw addresses) ----

        public const byte PtrNull = 0;
        public const byte PtrEntropy = 1; // offset into dctx->entropy; the offset accompanies it
        public const byte PtrLLDefault = 2; // static predefined LL FSE table
        public const byte PtrMLDefault = 3;
        public const byte PtrOFDefault = 4;

        /// <summary>
        /// Converts the captured pointers to a symbolic {class, offset} form suitable for
        /// persistence. Returns false if an external pointer is not one of the three process-wide
        /// default tables (never observed; callers should then skip planting the point).
        /// </summary>
        public bool TryGetSymbolicPointers(out byte[] classes, out int[] offsets)
        {
            classes = new byte[4];
            offsets = new int[4];
            return TrySymbolise(LLTOffset, LLTExternal, out classes[0], out offsets[0])
                && TrySymbolise(MLTOffset, MLTExternal, out classes[1], out offsets[1])
                && TrySymbolise(OFTOffset, OFTExternal, out classes[2], out offsets[2])
                && TrySymbolise(HUFOffset, HUFExternal, out classes[3], out offsets[3]);
        }

        private static bool TrySymbolise(long offset, ulong external, out byte cls, out int symOffset)
        {
            symOffset = 0;
            if (offset >= 0)
            {
                cls = PtrEntropy;
                symOffset = (int)offset;
                return true;
            }
            if (external == 0)
            {
                cls = PtrNull;
                return true;
            }
            if (external == (ulong)ZstdDefaultTables.LL)
            {
                cls = PtrLLDefault;
                return true;
            }
            if (external == (ulong)ZstdDefaultTables.ML)
            {
                cls = PtrMLDefault;
                return true;
            }
            if (external == (ulong)ZstdDefaultTables.OF)
            {
                cls = PtrOFDefault;
                return true;
            }
            cls = PtrNull;
            return false;
        }

        /// <summary>Rebuilds a restorable state from its persisted symbolic form.</summary>
        public static ZstdExactState FromSymbolic(
            byte[] entropyRaw,
            byte[] classes,
            int[] offsets,
            byte litEntropy,
            byte fseEntropy
        )
        {
            if (entropyRaw.Length != EntropyStructSize)
            {
                throw new InvalidDataException(
                    $"zstd index entropy snapshot is {entropyRaw.Length} bytes; this build of ZstdSharp "
                        + $"expects {EntropyStructSize} (index written by an incompatible version?)."
                );
            }
            var state = new ZstdExactState
            {
                Entropy = entropyRaw,
                LitEntropy = litEntropy,
                FseEntropy = fseEntropy,
            };
            Resolve(classes[0], offsets[0], out state.LLTOffset, out state.LLTExternal);
            Resolve(classes[1], offsets[1], out state.MLTOffset, out state.MLTExternal);
            Resolve(classes[2], offsets[2], out state.OFTOffset, out state.OFTExternal);
            Resolve(classes[3], offsets[3], out state.HUFOffset, out state.HUFExternal);
            return state;
        }

        private static void Resolve(byte cls, int symOffset, out long offset, out ulong external)
        {
            switch (cls)
            {
                case PtrEntropy:
                    if (symOffset < 0 || symOffset >= EntropyStructSize)
                    {
                        throw new InvalidDataException("zstd index entropy pointer offset out of range.");
                    }
                    offset = symOffset;
                    external = 0;
                    return;
                case PtrNull:
                    offset = -1;
                    external = 0;
                    return;
                case PtrLLDefault:
                    offset = -1;
                    external = (ulong)ZstdDefaultTables.LL;
                    return;
                case PtrMLDefault:
                    offset = -1;
                    external = (ulong)ZstdDefaultTables.ML;
                    return;
                case PtrOFDefault:
                    offset = -1;
                    external = (ulong)ZstdDefaultTables.OF;
                    return;
                default:
                    throw new InvalidDataException($"Unknown zstd index pointer class {cls}.");
            }
        }
    }

    /// <summary>
    /// The three process-wide static predefined FSE decode tables (zstd's LL/ML/OF_defaultDTable),
    /// which a DCtx table selector points at after a Predefined_Mode block. They are private
    /// statics in ZstdSharp, resolved once via reflection; their addresses are stable for the
    /// process lifetime (ZstdSharp keeps them in native/pinned memory).
    /// </summary>
    internal static unsafe class ZstdDefaultTables
    {
        public static readonly void* LL;
        public static readonly void* ML;
        public static readonly void* OF;

        static ZstdDefaultTables()
        {
            LL = ReadStaticPointer("LL_defaultDTable");
            ML = ReadStaticPointer("ML_defaultDTable");
            OF = ReadStaticPointer("OF_defaultDTable");
        }

        private static void* ReadStaticPointer(string fieldName)
        {
            var field = typeof(Methods).GetField(
                fieldName,
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic
            );
            if (field == null)
            {
                throw new InvalidOperationException(
                    $"ZstdSharp.Unsafe.Methods.{fieldName} not found (ZstdSharp version change?)."
                );
            }
            var boxed = field.GetValue(null);
            if (boxed is System.Reflection.Pointer pointer)
            {
                return System.Reflection.Pointer.Unbox(pointer);
            }
            throw new InvalidOperationException(
                $"ZstdSharp.Unsafe.Methods.{fieldName} is not a pointer field (ZstdSharp version change?)."
            );
        }
    }

    /// <summary>
    /// A decoder resumed at a block boundary with FULL state: window preloaded via refPrefix,
    /// synthetic frame header, then the exact entropy/rep/flag snapshot reinstated - then fed the
    /// following blocks and byte-compared against the true decode.
    /// </summary>
    internal sealed unsafe class ExactResumeShadow : IDisposable
    {
        private ZSTD_DCtx_s* dctx;
        private GCHandle windowPin;
        private readonly byte[] scratch = new byte[1 << 20];

        public bool Healthy { get; }

        public ExactResumeShadow(byte[] windowRaw, byte windowDescriptor, ZstdExactState state)
        {
            dctx = Methods.ZSTD_createDCtx();
            Methods.ZSTD_DCtx_setParameter(dctx, ZSTD_dParameter.ZSTD_d_windowLogMax, 31);

            var ok = true;
            if (windowRaw.Length > 0)
            {
                windowPin = GCHandle.Alloc(windowRaw, GCHandleType.Pinned);
                var r = Methods.ZSTD_DCtx_refPrefix(dctx, (byte*)windowPin.AddrOfPinnedObject(), (nuint)windowRaw.Length);
                ok = !Methods.ZSTD_isError(r);
            }
            if (ok)
            {
                // Consuming the full synthetic header runs the frame init (which resets entropy)...
                ok = ZstdFrameHelpers.Feed(dctx, ZstdFrameHelpers.SyntheticFrameHeader(windowDescriptor), scratch, out _);
            }
            if (ok)
            {
                // ...so the exact snapshot is reinstated afterwards, before the first block.
                state.Restore(dctx);
            }
            Healthy = ok;
        }

        /// <summary>Feeds one block; compares the produced bytes with the true decode's output.
        /// Returns ok, the index of the first mismatching byte (or -1 decode error / -2 length
        /// mismatch), and the produced byte count.</summary>
        public (bool Ok, int MismatchAt, int Produced) FeedAndCompare(ReadOnlySpan<byte> block, ReadOnlySpan<byte> truth)
        {
            if (!ZstdFrameHelpers.Feed(dctx, block, scratch, out var produced))
            {
                return (false, -1, produced);
            }
            if (produced != truth.Length)
            {
                return (false, -2, produced);
            }
            var mine = scratch.AsSpan(0, produced);
            if (mine.SequenceEqual(truth))
            {
                return (true, 0, produced);
            }
            for (var i = 0; i < produced; i++)
            {
                if (mine[i] != truth[i])
                {
                    return (false, i, produced);
                }
            }
            return (false, -3, produced);
        }

        public void Dispose()
        {
            if (dctx != null)
            {
                Methods.ZSTD_freeDCtx(dctx);
                dctx = null;
            }
            if (windowPin.IsAllocated)
            {
                windowPin.Free();
            }
        }
    }

    /// <summary>Aggregated outcome of a capture/resume probe run.</summary>
    internal sealed class ExactStateProbeResult
    {
        public int BoundariesCaptured;
        public int ExactCompleted;
        public int ExactFailed;
        public int WindowOnlyCompleted;
        public int WindowOnlyFailed;
        public long TotalUncompressed;
        public int EntropyBlobBytes;
        public List<string> CaptureNotes = new();
        public string? FirstExactDivergence;

        public string Summary() =>
            $"boundaries={BoundariesCaptured} exactOk={ExactCompleted} exactFailed={ExactFailed} "
            + $"windowOnlyOk={WindowOnlyCompleted} windowOnlyFailed={WindowOnlyFailed} "
            + $"totalUncompressed={TotalUncompressed:N0} entropyBlobBytes={EntropyBlobBytes}"
            + (FirstExactDivergence != null ? $"\nFIRST EXACT DIVERGENCE: {FirstExactDivergence}" : "");
    }

    /// <summary>
    /// Z0 driver: one true streaming decode over the compressed stream; at chosen mid-frame block
    /// boundaries, captures {window snapshot, exact state} and runs BOTH an exact-state shadow and
    /// (for comparison) today's window-only shadow alongside the true decode, byte-comparing every
    /// block for up to <c>compareBudget</c> output bytes. Purely additive - existing build/serve
    /// paths untouched.
    /// </summary>
    internal static class ZstdExactStatePrototype
    {
        private sealed class ActivePair
        {
            public ExactResumeShadow Exact = null!;
            public ShadowDecoder? WindowOnly;
            public bool WindowOnlyAlive;
            public long StartUncompressed;
            public long Remaining;
            public string Note = "";
        }

        public static unsafe ExactStateProbeResult Run(
            Stream compressed,
            Func<long, long, bool> shouldCapture,
            long compareBudget
        )
        {
            var result = new ExactStateProbeResult { EntropyBlobBytes = ZstdExactState.EntropyStructSize };
            var reader = new ZstdBlockReader(compressed);
            var dctx = Methods.ZSTD_createDCtx();
            Methods.ZSTD_DCtx_setParameter(dctx, ZSTD_dParameter.ZSTD_d_windowLogMax, 31);

            var outBuf = new byte[1 << 20];
            var ring = Array.Empty<byte>();
            long uncompressedPos = 0;
            var active = new List<ActivePair>();

            void CompletePair(ActivePair pair)
            {
                result.ExactCompleted++;
                if (pair.WindowOnlyAlive)
                {
                    result.WindowOnlyCompleted++;
                }
                pair.Exact.Dispose();
                pair.WindowOnly?.Dispose();
            }

            try
            {
                while (true)
                {
                    var frameKind = reader.BeginFrame(out var headerBytes, out var frameWindowSize, out var windowDescriptor, out var hasChecksum);
                    if (frameKind == FrameKind.EndOfStream)
                    {
                        break;
                    }
                    if (frameKind == FrameKind.Skippable)
                    {
                        continue;
                    }
                    if (frameWindowSize > 1L << 30)
                    {
                        throw new InvalidDataException($"zstd window {frameWindowSize:N0} too large to probe.");
                    }
                    if (ring.Length < frameWindowSize)
                    {
                        ring = new byte[frameWindowSize];
                    }

                    if (!ZstdFrameHelpers.Feed(dctx, headerBytes, outBuf, out _))
                    {
                        throw new InvalidDataException("zstd frame header rejected.");
                    }

                    var frameStartUncompressed = uncompressedPos;
                    var lastBlock = false;
                    while (!lastBlock)
                    {
                        var boundaryCompressed = reader.Position;

                        // Mid-frame boundaries only: entropy resets at a frame start, where the
                        // existing stateless resume is already exact.
                        if (
                            uncompressedPos > frameStartUncompressed
                            && shouldCapture(boundaryCompressed, uncompressedPos)
                        )
                        {
                            var window = SnapshotWindow(ring, uncompressedPos, frameWindowSize);
                            var state = ZstdExactState.Capture(dctx);
                            var note = $"u={uncompressedPos:N0} c={boundaryCompressed:N0} window={window.Length:N0} {state.Describe()}";
                            result.BoundariesCaptured++;
                            result.CaptureNotes.Add(note);

                            var exact = new ExactResumeShadow(window, windowDescriptor, state);
                            var windowOnly = new ShadowDecoder(window, windowDescriptor);
                            if (!exact.Healthy)
                            {
                                result.ExactFailed++;
                                result.FirstExactDivergence ??= $"resume init failed at {note}";
                                exact.Dispose();
                                windowOnly.Dispose();
                            }
                            else
                            {
                                if (!windowOnly.Healthy)
                                {
                                    result.WindowOnlyFailed++;
                                }
                                active.Add(
                                    new ActivePair
                                    {
                                        Exact = exact,
                                        WindowOnly = windowOnly.Healthy ? windowOnly : null,
                                        WindowOnlyAlive = windowOnly.Healthy,
                                        StartUncompressed = uncompressedPos,
                                        Remaining = compareBudget,
                                        Note = note,
                                    }
                                );
                                if (!windowOnly.Healthy)
                                {
                                    windowOnly.Dispose();
                                }
                            }
                        }

                        var block = reader.ReadBlock(out lastBlock);
                        if (!ZstdFrameHelpers.Feed(dctx, block, outBuf, out var produced))
                        {
                            throw new InvalidDataException($"zstd decode error at uncompressed {uncompressedPos:N0}.");
                        }
                        var truth = outBuf.AsSpan(0, produced);

                        // Rolling window ring (chronological reconstruction at snapshot time).
                        for (var copied = 0; copied < produced; )
                        {
                            var ringPos = (int)((uncompressedPos + copied) % frameWindowSize);
                            var n = (int)Math.Min(produced - copied, frameWindowSize - ringPos);
                            Array.Copy(outBuf, copied, ring, ringPos, n);
                            copied += n;
                        }

                        for (var i = active.Count - 1; i >= 0; i--)
                        {
                            var pair = active[i];
                            var (ok, mismatchAt, shadowProduced) = pair.Exact.FeedAndCompare(block, truth);
                            if (!ok)
                            {
                                result.ExactFailed++;
                                result.FirstExactDivergence ??= mismatchAt >= 0
                                    ? $"first differing byte at absolute uncompressed {uncompressedPos + mismatchAt:N0} "
                                        + $"({uncompressedPos + mismatchAt - pair.StartUncompressed:N0} bytes after the resume point); "
                                        + $"capture was [{pair.Note}]"
                                    : mismatchAt == -2
                                        ? $"length mismatch (shadow produced {shadowProduced}, truth {truth.Length}) in the block at "
                                            + $"uncompressed {uncompressedPos:N0}; capture was [{pair.Note}]"
                                        : $"shadow decode error in the block at uncompressed {uncompressedPos:N0}; capture was [{pair.Note}]";
                                pair.Exact.Dispose();
                                pair.WindowOnly?.Dispose();
                                active.RemoveAt(i);
                                continue;
                            }

                            if (pair.WindowOnlyAlive && !pair.WindowOnly!.FeedAndCompare(block, truth))
                            {
                                pair.WindowOnlyAlive = false;
                                result.WindowOnlyFailed++;
                                pair.WindowOnly.Dispose();
                                pair.WindowOnly = null;
                            }

                            pair.Remaining -= produced;
                            if (pair.Remaining <= 0)
                            {
                                CompletePair(pair);
                                active.RemoveAt(i);
                            }
                        }

                        uncompressedPos += produced;
                    }

                    // Frame end: feed the declared checksum (if any) to the true decoder so it
                    // finishes the frame cleanly; the shadows' synthetic frames declared none and
                    // ended with the last block, so any still-active shadow has fully verified up
                    // to the end of its frame - complete it.
                    if (hasChecksum)
                    {
                        var checksum = new byte[4];
                        var got = 0;
                        while (got < 4)
                        {
                            var n = compressed.Read(checksum, got, 4 - got);
                            if (n <= 0)
                            {
                                throw new InvalidDataException("Truncated zstd stream (checksum).");
                            }
                            got += n;
                        }
                        if (!ZstdFrameHelpers.Feed(dctx, checksum, outBuf, out _))
                        {
                            throw new InvalidDataException("zstd content checksum mismatch in the true decode.");
                        }
                    }

                    foreach (var pair in active)
                    {
                        CompletePair(pair);
                    }
                    active.Clear();
                }
            }
            finally
            {
                Methods.ZSTD_freeDCtx(dctx);
                foreach (var pair in active)
                {
                    pair.Exact.Dispose();
                    pair.WindowOnly?.Dispose();
                }
            }

            result.TotalUncompressed = uncompressedPos;
            return result;
        }

        private static byte[] SnapshotWindow(byte[] ring, long uncompressedPos, long frameWindowSize)
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
    }
}
