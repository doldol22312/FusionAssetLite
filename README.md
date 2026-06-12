# FusionAssetLite

A fast, low-memory asset dumper for Windows Clickteam Fusion 2.5 games.

Point it at a game EXE (or raw CCN data) and it extracts images, sounds,
packed runtime files, and shaders. It is built around two goals:

- **Speed** — a parallel decode/encode pipeline, libdeflate for both
  decompression and PNG compression, SIMD pixel translation, and single-write
  in-memory PNG assembly. A 509 MB FNaF-class game dumps in about 2 seconds.
- **Low memory** — asset banks are streamed from the game file through bounded
  queues of compressed payloads; the whole game is never loaded into RAM.

This is an asset extractor, not a decompiler. Events, frames, and object data
are out of scope.

## What it extracts

| Asset | Details |
| --- | --- |
| Images | Graphic modes 4 (24-bit masked), 6 (15-bit), 7 (16-bit), and 8 (32-bit RGBA), including RLE variants and the Fusion 2.5+ optimized LZ4 image format. Written as PNG — RGB for fully opaque images, RGBA otherwise. |
| Sounds | Standard (compressed) Windows sound banks. File type detected from content: `wav`, `ogg`, `aiff`, `mp3`, `it`, `xm`, `s3m`, `mod`, or `bin` fallback. |
| Packed data | Embedded runtime files (`Packed Data/`), inflated when zlib-compressed. |
| Shaders | `.fx` source or `.fxc` compiled blobs plus an `.xml` parameter manifest per shader. |

Input formats: Fusion 2.5 `PAME`/`PAMU` payloads inside a Windows EXE, or a
raw CCN/data stream. The payload is located automatically after the PE
sections (with a fallback scan for the package header).

Known limitations:

- Fusion 1.x legacy pack data is detected but skipped.
- Mobile/Flash/HTML sound banks are detected but skipped.
- Graphic modes other than 4/6/7/8 are reported as failures per image.

## Quick start

```powershell
FusionAssetLite.exe "C:\Games\SomeGame.exe"
```

Assets land in `extracted_assets_lite\<App Name>\` next to the game by
default. Pass a second argument to choose the output root:

```powershell
FusionAssetLite.exe "C:\Games\SomeGame.exe" "D:\dumps"
```

### Options

| Flag | Effect |
| --- | --- |
| `--zip` | Write everything into a single stored (uncompressed) `.zip` instead of one file per asset. Fastest mode on most systems — it replaces thousands of file creations with one, which also sidesteps antivirus per-file scan overhead. |
| `--no-images` | Skip the image bank. |
| `--no-sounds` | Skip the sound bank. |
| `--no-pack` | Skip packed runtime files. |
| `--no-shaders` | Skip shader banks. |

### PNG compression

Controlled by the `FUSION_ASSET_LITE_PNG_COMPRESSION` environment variable
(case-insensitive). The default is libdeflate level 1, which is the best
speed/size tradeoff; a managed zlib path is used automatically if the native
libdeflate binary is unavailable.

| Value | Meaning |
| --- | --- |
| *(unset)*, `fastest`, `libdeflate` | libdeflate level 1 (default) |
| `optimal` | libdeflate level 6 — smaller PNGs, slower |
| `zlib_fastest` | force the managed zlib path, fastest level |
| `zlib_optimal` | force the managed zlib path, optimal level |
| `none` / `stored` | no compression — very fast, but expect ~3× the output size |

Unknown values print a warning and fall back to the default.

## Performance

Measured on `Five Nights at Candy's 3.exe` (509 MB, 3,270 images, 138 sounds,
16 packed files, 16 shaders), Release build:

| Tool | Peak RAM | Dump time |
| --- | ---: | ---: |
| CTFAK | ~8.7 GB | 46 s |
| NebulaFD | ~7.0 GB | 2 m 13 s |
| FusionAssetLite (folder output) | ~856 MB | ~2.1 s |
| FusionAssetLite (`--zip`) | ~856 MB | ~1.8 s |

How: images are decoded and PNG-encoded by one worker per core fed from a
bounded channel; compressed banks stream straight off disk; source inflation
and PNG deflation both go through libdeflate; pixel translation is SSSE3
vectorized and writes directly into pre-filtered PNG scanlines; each PNG is
assembled in pooled memory and written with a single preallocated write.

Tip for folder output on Windows: exclude the output directory from
Defender real-time scanning, or just use `--zip` — per-file scan overhead is
the single largest external cost.

The optimization history (13.3 s → 1.8 s on the same game) is charted in
`docs/fnac3-optimization-timeline.svg`, with the working plan in
`docs/performance-plan.md`.

## Building from source

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download) on Windows
(pinned by `global.json`).

```powershell
dotnet build .\FusionAssetLite.sln -c Release
```

Projects:

| Path | Description |
| --- | --- |
| `src/FusionAssetLite` | The CLI dumper (single-file source, `net10.0`). |
| `src/FusionAssetLite.Gui` | WPF desktop front-end (`net10.0-windows`). |
| `tests/FusionAssetLite.Tests` | Golden-dump integration tests. |

Run the GUI from source:

```powershell
dotnet run --project .\src\FusionAssetLite.Gui\FusionAssetLite.Gui.csproj -c Release
```

Dependencies: [LibDeflate.NET](https://www.nuget.org/packages/LibDeflate.NET)
(fast inflate/deflate), [K4os.Compression.LZ4](https://www.nuget.org/packages/K4os.Compression.LZ4)
(Fusion 2.5+ optimized images), `System.IO.Hashing` (hardware CRC-32 for PNG
chunks).

## Tests

The integration tests dump nine reference FNaF EXEs and compare the results
against golden fingerprints in `tests/FusionAssetLite.Tests/Reference`. Every
emitted PNG is structurally validated and fingerprinted by its **decoded RGBA
pixels** (so compressor changes don't break the goldens); other assets are
fingerprinted by SHA-256.

The tests need the game files locally. Point them at your set:

```powershell
$env:FUSION_ASSET_LITE_FNAF_ROOT = "D:\path\to\fnaf\games"
dotnet test .\FusionAssetLite.sln -c Release
```

Test environment variables:

| Variable | Effect |
| --- | --- |
| `FUSION_ASSET_LITE_FNAF_ROOT` | Root folder containing the reference games. |
| `FUSION_ASSET_LITE_TEST_KEEP_OUTPUT=1` | Keep temporary dumps from passing tests for inspection. |
| `FUSION_ASSET_LITE_TEST_PARALLELISM` | Lower the parallel dump count if disk or antivirus is the bottleneck. |

## Output layout

```text
extracted_assets_lite\
└── <App Name>\
    ├── Images\        00000.png, 00001.png, ...
    ├── Sounds\        <name>.<wav|ogg|...>
    ├── Packed Data\   original relative paths, sanitized
    └── Shaders\       <name>.fx|.fxc + <name>.xml
```

With `--zip`, the same layout is written as entries of
`extracted_assets_lite\<App Name>.zip`.
