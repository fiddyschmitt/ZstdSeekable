using System;
using System.Runtime.InteropServices;
using ZstdSeekable.Internal;
using ZstdSharp.Unsafe;

namespace ZstdSeekable
{
    /// <summary>
    /// A decoder resumed at a candidate point (window preloaded via refPrefix, synthetic frame
    /// header), fed the same blocks as the true decode and byte-compared against it. The window
    /// array is pinned for the shadow's lifetime (refPrefix references it directly).
    /// </summary>
    internal sealed unsafe class ShadowDecoder : IDisposable
    {
        ZSTD_DCtx_s* dctx;
        GCHandle windowPin;
        readonly byte[] scratch = new byte[1 << 20];

        public bool Healthy { get; }

        public ShadowDecoder(byte[] windowRaw, byte windowDescriptor)
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
            if (ok) ok = ZstdFrameHelpers.Feed(dctx, ZstdFrameHelpers.SyntheticFrameHeader(windowDescriptor), scratch, out _);
            Healthy = ok;
        }

        public bool FeedAndCompare(ReadOnlySpan<byte> block, ReadOnlySpan<byte> truth)
        {
            if (!ZstdFrameHelpers.Feed(dctx, block, scratch, out var produced)) return false;
            return produced == truth.Length && scratch.AsSpan(0, produced).SequenceEqual(truth);
        }

        public void Dispose()
        {
            if (dctx != null)
            {
                Methods.ZSTD_freeDCtx(dctx);
                dctx = null;
            }
            if (windowPin.IsAllocated) windowPin.Free();
        }
    }
}
