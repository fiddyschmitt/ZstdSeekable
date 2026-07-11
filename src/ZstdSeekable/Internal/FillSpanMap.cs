using System.Collections.Generic;

namespace ZstdSeekable.Internal
{
    /// <summary>Binary searches over a sorted, non-overlapping fill-span list.</summary>
    internal static class FillSpanMap
    {
        /// <summary>Index of the span containing <paramref name="position"/>, or -1.</summary>
        public static int Find(IReadOnlyList<ZstdFillSpan> spans, long position)
        {
            var lo = 0;
            var hi = spans.Count - 1;
            while (lo <= hi)
            {
                var mid = lo + (hi - lo) / 2;
                var span = spans[mid];
                if (position < span.UncompressedOffset) hi = mid - 1;
                else if (position >= span.UncompressedOffset + span.Length) lo = mid + 1;
                else return mid;
            }
            return -1;
        }

        /// <summary>Start of the first span beginning after <paramref name="position"/>, or
        /// long.MaxValue when none does.</summary>
        public static long NextStart(IReadOnlyList<ZstdFillSpan> spans, long position)
        {
            var lo = 0;
            var hi = spans.Count - 1;
            var result = long.MaxValue;
            while (lo <= hi)
            {
                var mid = lo + (hi - lo) / 2;
                if (spans[mid].UncompressedOffset > position)
                {
                    result = spans[mid].UncompressedOffset;
                    hi = mid - 1;
                }
                else
                {
                    lo = mid + 1;
                }
            }
            return result;
        }
    }
}
