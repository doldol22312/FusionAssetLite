# FusionAssetLite Performance Plan

Date: 2026-06-12 · Baseline: FNaC3 dump (3,270 images / 794 MB PNG output), ~2.5–4s wall.

## Evidence (dotnet-trace, three runs on 2026-06-12)

All three traces show the same profile shape:

| Cost | Share of CPU samples | Source |
| --- | --- | --- |
| Worker threads waiting (semaphores, `Monitor.Wait`, `Thread.Sleep`, IO poller) | ~40–50% | Pipeline starvation |
| zlib deflate (`Deflater.Deflate` + native frames) | ~18–26% | PNG IDAT compression in `FastPngWriter` |
| `FileStream` ctor (`CreateFile`) | ~15–19% | One file creation per PNG (~3,270) |
| File write syscalls | ~9–10% | Incremental zlib flushes through `FileStream` |
| Image translation (`ImageTranslator`) | ~2–5% | Already optimized |
| IDAT length seek-back (`set_Position`) | ~1–3% | Two-pass chunk write |

Key negative result: the "defender-excluded" runs show **no drop** in `CreateFile`
cost vs. the unexcluded run (15.2–18.5% across all three), while the recorded
timeline shows a working exclusion is worth ~35% of wall time (3.92s → 2.55s).
Either the exclusion was not active or another filter driver is involved.
`RealTimeProtectionEnabled` was `True` when checked on 2026-06-12.

## Measurement protocol (applies to every phase)

- Same machine, same input, Release build, 3+ runs, report median wall time
  (`Measure-Command { .\FusionAssetLite.exe <fnac3> }`).
- Re-capture a `dotnet-trace` profile after each phase and compare shares.
- Run `FnafGoldenDumpTests` after every change.

## Phase 0 — Make golden tests compressor-independent (prerequisite)

Image fingerprints currently use **PNG file length** (`FnafGoldenDumpTests.cs`,
`BuildActualFingerprints`). Any compressor change alters lengths and fails the
goldens even when pixels are identical.

- Change the image fingerprint to a hash of **decoded RGBA pixels** (decode via
  the existing `PngValidator` chunk walker + `ZLibStream` inflate, or
  `BitmapSource` in the windows test target). Keep structural PNG validation.
- Regenerate golden CSVs once, before any compressor work.

Exit: goldens pass on current code with pixel-based fingerprints.

## Phase 1 — In-memory PNG + one-shot compression (attacks ~30–35%)

Today `FastPngWriter` streams rows through `ZLibStream` → CRC stream →
`FileStream`, then seeks back to patch the IDAT length.

1. Build the filtered scanline buffer (filter byte 0 + row bytes) into one
   pooled buffer per image.
2. Compress IDAT in a single shot into a second pooled buffer.
3. Assemble signature/IHDR/IDAT/IEND in memory — lengths are known up front, so
   the `set_Position` patch-up disappears.
4. Write the file once: `File.OpenHandle(..., preallocationSize)` +
   `RandomAccess.Write`.
5. Compressor, in two steps:
   a. Managed one-shot `ZLibStream` over the buffer (no new dependency) — measure.
   b. Evaluate libdeflate (e.g. `LibDeflate.NET`) at level 1: typically 2–3×
      faster than zlib-ng's fastest with equal-or-better ratio, and it can
      return the zlib checksum cheaply. Keep `FUSION_ASSET_LITE_PNG_COMPRESSION`
      working; fall back to managed path if the native lib is unavailable.

Risk: libdeflate adds a native binary to deployment — keep it optional.
Exit: deflate + write + seek shares drop materially; goldens pass.

## Phase 2 — Fix pipeline starvation (~40% of samples are waits)

1. **Stream `flag == 1` bank decompression.** `OpenChunkReader` currently calls
   `Compression.DecompressExact` on the whole image bank before any worker
   starts. Wrap a `ZLibStream` over the `WindowedReadStream` instead (the
   8-byte size header is already parsed). Workers start immediately; peak RAM
   drops by the bank size.
2. Re-trace. If waits persist:
   - Raise the export queue capacity (currently clamped to 32–64 jobs); payload
     buffers are pooled, so a larger bound is cheap. Consider sizing by bytes
     in flight rather than job count.
   - Swap `BlockingCollection` for `Channel<ImageExportJob>` (less semaphore
     churn — the traces show heavy `SemaphoreSlim`/lifo-semaphore activity).
3. Check producer throughput: the reader thread is single-threaded by design
   (stream order), so the fix is keeping it ahead, not parallelizing it.

Exit: wait share < ~15% in trace; wall time drops accordingly.

## Phase 3 — File-creation cost (~15–19%)

1. **Ops first (no code):** from an admin shell, verify
   `Get-MpPreference | Select -Expand ExclusionPath` actually contains the
   output folder; add it if missing. A/B with real-time protection toggled off
   for one run. If `CreateFile` share still doesn't move, Defender is not the
   culprit (other filter drivers / OneDrive / indexer / NTFS).
2. **`--zip` output mode (code):** write Images/Sounds/etc. into a single
   `.zip` with **stored** (uncompressed) entries via `ZipArchive`. PNGs are
   already compressed, so stored entries are nearly free, and ~3,270
   `CreateFile` calls become 1. Opt-in flag; default layout unchanged.
   This also sidesteps any filter-driver tax permanently.

Exit: with exclusion verified or `--zip` used, `CreateFile` share < 5%.

## Phase 4 — Smaller items (only if still visible after re-trace)

- Mode-4 RLE path (`Normal24BitMaskedToRgba`, ~5%): emit runs as span fills
  instead of per-pixel writes (the non-RLE path already had this treatment).
- Fuse `IsFullyOpaque` into the translators (saves one full pass over the RGBA
  buffer per image; track "saw alpha ≠ 255" while writing).
- Replace `lock (_imageModes)` with `Interlocked.Increment` on an `int[256]`
  (`Monitor.Enter_Slowpath` ~1.3%).
- Sound bank: drop the second full payload copy in `ReadSound` (slice instead
  of re-reading through a `MemoryStream`) and parallelize sound export if
  WAV-heavy games matter.

## Targets

- FNaC3 (with working exclusion or `--zip`): **≤ ~1.2s** median wall time.
- Trace shape: waits < 15%, deflate < 15%, `CreateFile` < 5%.

## Implementation status (2026-06-12)

- Phase 0 complete: image goldens now use decoded RGBA pixel hashes, and
  `nebulafd_sha256.csv` was regenerated with `Width`, `Height`, and
  `RGBA_SHA256` columns.
- Phase 1 partially complete: PNGs are assembled in memory, IDAT length
  seek-back is gone, file output uses `File.OpenHandle` + `RandomAccess.Write`,
  and default PNG compression is libdeflate level 1 with managed zlib fallback.
- Phase 2 complete for the planned first pass: `flag == 1` image/sound banks now
  stream through `ZLibStream`/`DeflateStream`, and image export uses a bounded
  `Channel<ImageExportJob>` with a larger queue.
- Phase 3 code complete: `--zip` writes stored entries through `ZipArchive`,
  reducing per-asset `CreateFile` work to one archive file.
- Measured on `C:\Users\agalq\Downloads\123123213\Five Nights at Candy's 3.exe`
  after the implementation:
  - Folder output, default libdeflate: 2.455s, 2.512s, 2.701s; median 2.512s;
    output ~579.9 MB.
  - `--zip`, default libdeflate: 1.998s, 2.095s, 2.917s; median 2.095s;
    output ~580.3 MB.
  - Managed zlib-fastest comparison before libdeflate default: folder median
    ~2.572s, `--zip` median ~2.155s, output ~816 MB.
  - `stored` PNG mode was not completed because it filled the temp volume during
    the third folder run; it is not practical for this input without a very large
    output volume.
- Current final zip trace:
  `traces/fnac3-final-threadlibdeflate-zip-20260612-153551.nettrace`.
  `dotnet-trace report topN` shows libdeflate compression is now the top
  exclusive cost (~28%), `CreateFile` is no longer in the top 20 for `--zip`,
  and zip serialization (`Monitor.Enter_Slowpath`) remains visible (~6%).
  The 1.2s target was not reached in this pass.

## Next pass (review of the 15:35 final zip trace)

Profile after libdeflate + `--zip` + channel pipeline (median ~2.1s):
compress 28.4% (libdeflate L1, near floor), source inflate ~12% + ~11%
native frames (`DecompressExact` via `DeflateStream`), mode-4 translation
12.1%, zip lock (`Monitor.Enter_Slowpath`) 6.3%, idle ~29%
(`GetQueuedCompletionStatus`/lifo semaphore). `CreateFile` and write
syscalls are solved (out of top 20 / ~1%).

Ranked next steps:

1. **libdeflate for inflate.** Route `Compression.DecompressExact` (exact
   output size known) through libdeflate one-shot inflate; expected to halve
   the ~12–23% combined inflate cost.
2. **Dedicated zip-writer thread.** Workers currently contend on the
   `ZipArchive` lock; queue finished in-memory PNGs through a channel to a
   single writer. Removes the 6.3% lock and converts blocking into pipelining.
3. **SIMD mode-4 translator.** `Vector128` shuffle for BGR→RGBA + vectorized
   transparent-color compare in `Normal24BitMaskedRowsToRgba` (12.1%).
4. **Parallelize the sound phase.** Part of the ~29% idle is the sequential
   sound/shader tail where image workers sit parked.

Expected: `--zip` median ~1.4–1.6s after items 1–3; the 1.2s target likely
needs item 4 as well.

## Next-pass implementation status (2026-06-12)

- Item 1 complete: `Compression.DecompressExact` now tries libdeflate one-shot
  zlib/deflate inflate with thread-local decompressor reuse, then falls back to
  the managed stream path only if the native library is unavailable.
- Item 2 complete: zip output now uses a bounded `Channel<ZipWriteJob>` and a
  dedicated writer task. Image workers transfer completed rented PNG buffers to
  the writer instead of locking `ZipArchive` directly.
- Item 3 partially complete: the mode-4 non-RLE row translator has an SSSE3
  `Vector128` shuffle path for RGB/BGR-to-RGBA expansion. The transparent-color
  alpha check is still scalar to keep the patch conservative.
- Item 4 complete for sounds: sound bank reading now queues sound payload jobs,
  and worker tasks perform decompression, name parsing, extension detection, and
  output writes in parallel.
- Measured on `C:\Users\agalq\Downloads\123123213\Five Nights at Candy's 3.exe`
  after this pass:
  - Folder output: 2.485s, 2.493s, 2.681s; median 2.493s; output ~579.9 MB.
  - `--zip`: 1.913s, 1.943s, 2.154s; median 1.943s; output ~580.3 MB.
- Current trace: `traces/fnac3-nextsteps-zip-20260612-154403.nettrace`.
  `dotnet-trace report topN` shows libdeflate compression remains top exclusive
  cost (~33%), libdeflate inflate is now visible at ~7.8%, the archive lock is
  gone from the top costs, and the vectorized mode-4 translator is ~6.5%
  inclusive. The 1.2s target is still not reached.

## Tail/fused-scanline status (2026-06-12)

- Shader tail: shader payloads are still parsed sequentially from the shared
  chunk reader, but shader file/XML writes now run through `Parallel.ForEach`.
- Image scanline fusion: mode-4 non-RLE images now translate directly into
  filtered RGBA scanlines with filter bytes already placed. Opaque images are
  packed to filtered RGB before compression to preserve the current PNG size
  behavior. The transparent-color check inside the SSSE3 path is vectorized.
- FNaC3 `--zip` timing after this pass: 1.796s, 1.844s, 1.899s; median 1.844s;
  output ~580.3 MB.
- Current trace: `traces/fnac3-fused2-zip-20260612-155107.nettrace`.
  Top exclusive costs: libdeflate compression ~34.7%, idle IOCP wait ~20.7%,
  libdeflate inflate ~8.5%, mode-4 vectorized row copy ~6.2%, and RGB packing
  ~1.8%. `Buffer.MemmoveInternal` is down to ~0.9%, so the full-buffer copy
  targeted by this pass is no longer a major cost.

## Out of scope

- Parallelizing the bank reader (stream format is sequential).
- PNG filter heuristics (filter 0 is the right speed/io tradeoff here).
- GUI changes.
