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

A zstd block can depend on decoder state beyond the content window: the three repeat offsets and the entropy tables (Huffman literals table, FSE sequence tables) that a block can reuse via `Repeat_Mode`. Since 0.4.0 each mid-frame resume point snapshots **that state exactly** — the window plus the decoder's entropy/rep state and table-selector classification — and a resume reinstates all of it, so a resumed decode is bit-identical *by construction*. Every block boundary at the target spacing is accepted (**100% boundary acceptance**): there are no dropped candidates, no trial decodes, and no divergence fallback, and the build is a single decode pass — roughly half the work of the old verify-as-you-go build.

Earlier versions (≤ 0.3.x) resumed from the window alone, which required trial-verifying every candidate span and dropped the ~12% of boundaries where the encoder reused entropy state across the boundary. Indexes built that way still load and serve exactly as before (their spans were verified at build time). A 0.4.0 build still byte-verifies a small sample of spans (`ZstdIndexOptions.VerifiedSampleSpans`, default 3) with a shadow decoder as an integration check — a sampled mismatch is treated as a bug and fails the build rather than ever being healed around. Frame starts remain stateless, always-sound points.

#### Fill spans: empty space costs nothing to read

Disk images are mostly empty space (zeros — or `0xFF` in flash dumps). During the build, runs of a single repeated byte longer than `ZstdIndexOptions.FillSpanThreshold` (default 1 MiB) are recorded as **fill spans**: reads inside them are served by filling the buffer directly — no window load, no decoder, no compressed I/O — roughly two orders of magnitude faster than a resume-decode. Fill spans are derived from the build's true decode, so they can never be wrong, and they're exposed as `ZstdIndex.FillSpans` — ready-made sparse-extent metadata for consumers (e.g. mounted filesystem images).

#### Interrupted builds resume

When building to a file or seekable stream (`ZstdIndex.LoadOrBuild`), each record is flushed the moment it's sealed — build memory stays flat (~10 MB regardless of stream size), and if the build is interrupted (crash, cancellation, lost connection), the next `LoadOrBuild` **resumes by exact-restoring the last sealed point** — no re-decoding from the last frame start. The header fingerprints the compressed stream (length + CRC32 of its first 64 KiB), so a stale partial index is never resumed against the wrong input.

The index format (`.zsi`, magic `ZSTZRAN4`) is a sequence of typed records — frame-start points, exact mid-frame points with their zstd-compressed entropy-state and window snapshots inline (loaded lazily on cold seeks; selector pointers stored symbolically, never as addresses), and fill spans — appended in the order they are sealed. Indexes written by older versions (`ZSTZRAN1`, `ZSTZRAN2`, `ZSTZRAN3`) still load and serve.

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
