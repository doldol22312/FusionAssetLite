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
- .NET 8 SDK or newer

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
