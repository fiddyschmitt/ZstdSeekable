# ZstdSeekable

Random-access (seekable) reading of [zstandard](https://facebook.github.io/zstd/) streams in .NET — plus writing of the official zstd seekable format.

Zstd streams are normally forward-only: reading byte *N* means decompressing everything before it. ZstdSeekable gives you a standard seekable `Stream` over zstd data using whichever of two mechanisms fits your input:

| | Official seekable format | Custom index |
|---|---|---|
| **Input** | Files written in the [zstd seekable format](https://github.com/facebook/zstd/blob/dev/contrib/seekable_format/zstd_seekable_compression_format.md) (e.g. by `t2sz`, zstd's seekable API, or this library's writer): many small frames + a seek table at EOF | Any ordinary zstd stream, including single giant frames (e.g. Clonezilla images) |
| **Setup cost** | None — the seek table is parsed instantly | One full sequential decode to build an index (persist it, and later opens are instant) |
| **Seek cost** | Decode ≤ 1 frame | Decode from the nearest resume point (bounded by the index's span size) |

Everything is managed code ([ZstdSharp](https://github.com/oleg-st/ZstdSharp)) — no native binaries. Targets `netstandard2.0` and `net8.0`.

## Quick start

```csharp
using ZstdSeekable;

// Auto-detect: uses the seek table if present, otherwise loads/builds a custom index
using var stream = ZstdSeekableStream.OpenRead(@"C:\images\sda1.zst", indexPath: null); // index defaults to sda1.zst.zsi

stream.Position = 123_456_789;          // seek anywhere
var buffer = new byte[4096];
var read = stream.Read(buffer, 0, buffer.Length);
```

Both the compressed input and the index can be **files or streams**, in any combination:

```csharp
using var compressed = File.OpenRead("data.zst");
using var indexStream = new MemoryStream();          // or a FileStream, a database blob, ...
using var stream = ZstdSeekableStream.OpenRead(compressed, indexStream);
```

### Writing seekable files

`ZstdSeekableWriter` produces official seekable-format output — readable by any zstd decompressor, and instantly seekable by any seekable-format implementation (including this library):

```csharp
using (var writer = ZstdSeekableWriter.Create("data.zst",
        new ZstdSeekableWriterOptions { MaxFrameSize = 1024 * 1024, CompressionLevel = 3 }))
{
    source.CopyTo(writer);
}   // Dispose (or Finish()) appends the seek table
```

If you control how your files are compressed, prefer this — no index build is ever needed.

### Reading seekable-format files directly

```csharp
using var reader = ZstdSeekableReader.Open("data.zst",
        new ZstdSeekableReaderOptions { VerifyChecksums = true });
Console.WriteLine($"{reader.SeekTable.Entries.Count} frames, {reader.Length:N0} bytes uncompressed");
```

### Indexing ordinary zstd streams

For zstd data that was *not* written in the seekable format, `ZstdIndex` builds a random-access index by decoding the stream once:

```csharp
using var compressed = File.OpenRead("clonezilla-image.zst");

using var index = ZstdIndex.LoadOrBuild(compressed, "clonezilla-image.zst.zsi",
    new ZstdIndexOptions { TargetSpanBytes = 64L * 1024 * 1024 },
    progress: new Progress<ZstdIndexProgress>(p => Console.Write($"\r{p.Phase} {p.Fraction:P0}")));

using var stream = new ZstdIndexedStream(compressed, index);
stream.Position = 40L * 1024 * 1024 * 1024;     // seek into the 40 GB mark
```

#### How the index works (and why it's trustworthy)

A zstd block can depend on decoder state beyond the content window (repeat offsets, entropy tables), so a block boundary is only usable as a resume point if a resumed decode is *bit-identical* to the true decode. The build is a **single sequential pass that verifies as it goes**: two shadow decoders run alongside the true decode — one for the current *candidate* point, and an *insurance* shadow covering the last confirmed point's still-open span. A candidate that stays byte-identical for a whole span is confirmed, which seals its predecessor with its entire serving span proven. A diverging candidate (normal — roughly 12% of block boundaries) is simply re-armed at a later boundary.

Frame starts are stateless and always sound, so the build never fails on valid zstd input — in the theoretical worst case (an insurance divergence deeper than a whole span, never observed on real data) the index degrades to frame-start points only: coarser, never wrong. Readers additionally never serve bytes beyond a point's verified span.

#### Interrupted builds resume

When building to a file or seekable stream (`ZstdIndex.LoadOrBuild`), each sealed point is flushed the moment it's verified — build memory stays flat (~10 MB regardless of stream size), and if the build is interrupted (crash, cancellation, lost connection), the next `LoadOrBuild` **resumes from the last sealed point** instead of starting over.

The index format (`.zsi`, magic `ZSTZRAN2`) stores point records with their zstd-compressed window snapshots inline, loaded lazily on cold seeks. Indexes written by ZstdSeekable 0.1.x (`ZSTZRAN1`) still load.

## Concurrency

Stream instances follow the usual BCL rule: one instance, one thread. For concurrent random access, call `CreateView()` on `ZstdSeekableReader` or `ZstdIndexedStream` — views are cheap independent cursors sharing the underlying source through a lock:

```csharp
Parallel.For(0, 8, _ =>
{
    using var view = stream.CreateView();
    view.Position = ...;    // each view has its own position and cache
});
```

## Notes

- The compressed input must be seekable (`CanSeek == true`) — both mechanisms need random access to the compressed bytes.
- An index loaded lazily from a seekable stream needs that stream to remain open while in use; indexes loaded from non-seekable streams are read eagerly into memory.
- `ZstdIndex` files are compatible with the `.zsi` index files produced by [clonezilla-util](https://github.com/fiddyschmitt/clonezilla-util), where this mechanism originated.

## License

MIT
