# FusionAssetLite

FusionAssetLite is a lightweight asset dumper for Windows Clickteam Fusion games.
It is built for low memory use: large image and sound banks are streamed from the
game file and decoded one asset at a time.

This is not a full Fusion decompiler. It only targets asset extraction.

## Supported So Far

- Windows Clickteam Fusion 2.5 EXE payloads
- `PAME` / `PAMU` package data
- Standard image banks using graphic mode `4`
- Standard compressed sound banks
- Packed runtime files
- Shader banks

Other exporters and older Clickteam formats may need more readers.

## Requirements

- Windows
- .NET 8 SDK or newer (if building from source)

Prebuilt self-contained releases do not require users to install .NET.

## Build

```powershell
dotnet build .\FusionAssetLite.sln -c Release
```

## Run

```powershell
.\src\FusionAssetLite\bin\Release\net8.0-windows\FusionAssetLite.exe "C:\Path\To\Game.exe" "C:\Path\To\Output"
```

The output folder will contain subfolders like:

- `Images`
- `Sounds`
- `Packed Data`
- `Shaders`

## Memory Usage

Measured on `Five Nights at Candy's 3.exe`, a 509 MB Windows Fusion 2.5 game:

| Tool | Peak RAM | Dump Time | Result |
| --- | ---: | ---: | --- |
| CTFAK | ~7 GB | Not measured | Dumped assets, but used excessive RAM |
| NebulaFD | ~6.8 GB | ~2m 12s | Dumped assets after local sequential-dump patches |
| FusionAssetLite | 136 MB | 47s | Dumped the same 3270 images, 138 sounds, packed data, and shaders |

FusionAssetLite stays lower because it streams large asset banks from the game
file and decodes one asset at a time instead of loading the whole game model.

## Options

```text
--no-images
--no-sounds
--no-pack
--no-shaders
```

Example:

```powershell
.\src\FusionAssetLite\bin\Release\net8.0-windows\FusionAssetLite.exe "Game.exe" ".\out" --no-shaders
```
