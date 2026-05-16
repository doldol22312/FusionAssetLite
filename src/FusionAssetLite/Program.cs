using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using K4os.Compression.LZ4;

namespace FusionAssetLite;

internal static class Program
{
    private static int Main(string[] args)
    {
        static bool HasArg(string[] values, string value) =>
            values.Any(arg => string.Equals(arg, value, StringComparison.OrdinalIgnoreCase));

        if (args.Length == 0 || HasArg(args, "--help") || HasArg(args, "-h"))
        {
            Console.WriteLine("Usage: FusionAssetLite <game.exe|game.ccn> [output-root] [--no-images] [--no-sounds] [--no-pack] [--no-shaders]");
            return args.Length == 0 ? 1 : 0;
        }

        string inputPath = Path.GetFullPath(args[0]);
        if (!File.Exists(inputPath))
        {
            Console.Error.WriteLine("Input file not found: " + inputPath);
            return 1;
        }

        string? outputArg = args.Skip(1).FirstOrDefault(arg => !arg.StartsWith("-", StringComparison.Ordinal));
        string outputRoot = outputArg != null
            ? Path.GetFullPath(outputArg)
            : Path.Combine(Path.GetDirectoryName(inputPath)!, "extracted_assets_lite");

        DumperOptions options = new()
        {
            DumpImages = !HasArg(args, "--no-images"),
            DumpSounds = !HasArg(args, "--no-sounds"),
            DumpPackData = !HasArg(args, "--no-pack"),
            DumpShaders = !HasArg(args, "--no-shaders")
        };

        try
        {
            FusionAssetDumper dumper = new(inputPath, outputRoot, options);
            dumper.Run();
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }
}

internal sealed class DumperOptions
{
    public bool DumpImages { get; init; } = true;
    public bool DumpSounds { get; init; } = true;
    public bool DumpPackData { get; init; } = true;
    public bool DumpShaders { get; init; } = true;
}

internal sealed class FusionAssetDumper
{
    private const uint MagicPame = 0x454D4150; // "PAME"
    private const uint MagicPamu = 0x554D4150; // "PAMU"
    private const uint MagicPackData = 0x77777777;
    private const ushort LegacyLastChunk = 0x7F7F;
    private const ushort LegacyAltChunk = 0x222C;
    private const ushort ChunkAppName = 0x2224;
    private const ushort ChunkShaders = 0x2243;
    private const ushort ChunkExtendedHeader = 0x2245;
    private const ushort ChunkShadersAlt = 0x225A;
    private const ushort ChunkPlus = 0x2253;
    private const ushort ChunkImageBank = 0x6666;
    private const ushort ChunkSoundBank = 0x6668;
    private const ushort ChunkSeeded = 0x7EEE;
    private const ushort ChunkLast = 0x7F7F;
    private const int MaxPackFiles = 100_000;
    private const int MaxImages = 1_000_000;
    private const int MaxSounds = 100_000;
    private const int MaxShaders = 10_000;
    private const int MaxNameLength = 4096;
    private const int MaxShaderSourceBytes = 16 * 1024 * 1024;

    private readonly string _inputPath;
    private readonly string _outputRoot;
    private readonly DumperOptions _options;
    private readonly Stopwatch _timer = new();

    private string _appName;
    private string _dumpDir = string.Empty;
    private bool? _unicode;
    private float _fusion = 2.5f;
    private int _build;
    private bool _windows;
    private bool _flash;
    private bool _android;
    private bool _ios;
    private bool _html;
    private bool _plus;
    private bool _seeded;
    private bool _premultipliedAlpha;
    private bool _optimizeImageSize;

    private int _imagesWritten;
    private int _imageFailures;
    private int _soundsWritten;
    private int _soundFailures;
    private int _packFilesWritten;
    private int _packFailures;
    private int _shaderFilesWritten;
    private int _shaderFailures;
    private readonly Dictionary<byte, int> _imageModes = new();
    private readonly HashSet<string> _usedPaths = new(StringComparer.OrdinalIgnoreCase);

    public FusionAssetDumper(string inputPath, string outputRoot, DumperOptions options)
    {
        _inputPath = inputPath;
        _outputRoot = outputRoot;
        _options = options;
        _appName = Path.GetFileNameWithoutExtension(inputPath);
        _dumpDir = Path.Combine(_outputRoot, Sanitizer.FileName(_appName));
    }

    public void Run()
    {
        _timer.Start();
        Directory.CreateDirectory(_dumpDir);

        using FileStream fs = new(_inputPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
        using BinaryReader reader = new(fs, Encoding.UTF8, leaveOpen: true);

        if (LooksLikeExe(reader))
        {
            long entryPoint = CalculateEntryPoint(reader);
            fs.Position = entryPoint;
            Console.WriteLine($"Input: normal EXE, payload at 0x{entryPoint:X}");
        }
        else
        {
            fs.Position = 0;
            Console.WriteLine("Input: raw CCN/data stream");
        }

        ReadPackDataIfPresent(reader);
        SeekToPackageHeaderIfNeeded(reader);
        ReadPackage(reader);

        Console.WriteLine();
        Console.WriteLine("Done.");
        Console.WriteLine($"Output: {_dumpDir}");
        Console.WriteLine($"Images: {_imagesWritten} written, {_imageFailures} failed");
        Console.WriteLine($"Sounds: {_soundsWritten} written, {_soundFailures} failed");
        Console.WriteLine($"Packed data: {_packFilesWritten} written, {_packFailures} failed");
        Console.WriteLine($"Shader files: {_shaderFilesWritten} written, {_shaderFailures} failed");
        if (_imageModes.Count > 0)
            Console.WriteLine("Image modes: " + string.Join(", ", _imageModes.OrderBy(x => x.Key).Select(x => $"{x.Key}={x.Value}")));
        Console.WriteLine($"Elapsed: {_timer.Elapsed:mm\\:ss}");
        Console.WriteLine($"Peak RAM: {Process.GetCurrentProcess().PeakWorkingSet64 / 1024 / 1024} MB");
    }

    private static bool LooksLikeExe(BinaryReader reader)
    {
        reader.BaseStream.Position = 0;
        return reader.BaseStream.Length >= 2 && BinaryText.ReadAscii(reader, 2) == "MZ";
    }

    private static long CalculateEntryPoint(BinaryReader reader)
    {
        Stream stream = reader.BaseStream;
        stream.Position = 0;
        if (BinaryText.ReadAscii(reader, 2) != "MZ")
            return 0;

        if (stream.Length < 0x40)
            throw new InvalidDataException("EXE is too small to contain a PE header.");

        stream.Position = 0x3C;
        uint peHeader = reader.ReadUInt32();
        if (peHeader > stream.Length - 24)
            throw new InvalidDataException($"Invalid PE header offset 0x{peHeader:X}.");

        stream.Position = peHeader;
        if (BinaryText.ReadAscii(reader, 4) != "PE")
            throw new InvalidDataException($"Missing PE signature at 0x{peHeader:X}.");

        _ = reader.ReadUInt16(); // machine
        ushort sectionCount = reader.ReadUInt16();
        stream.Seek(12, SeekOrigin.Current);
        ushort optionalHeaderSize = reader.ReadUInt16();
        stream.Seek(2, SeekOrigin.Current);

        long sectionTable = peHeader + 4 + 20 + optionalHeaderSize;
        long sectionTableEnd = sectionTable + sectionCount * 40L;
        if (sectionTableEnd > stream.Length)
            throw new InvalidDataException("PE section table extends past end of file.");

        stream.Position = sectionTable;

        long payloadOffset = 0;
        for (int i = 0; i < sectionCount; i++)
        {
            stream.Seek(16, SeekOrigin.Current);
            uint sizeOfRawData = reader.ReadUInt32();
            uint pointerToRawData = reader.ReadUInt32();
            stream.Seek(16, SeekOrigin.Current);

            if (sizeOfRawData == 0)
                continue;

            long sectionEnd = pointerToRawData + (long)sizeOfRawData;
            if (sectionEnd <= stream.Length)
                payloadOffset = Math.Max(payloadOffset, sectionEnd);
        }

        if (payloadOffset <= 0 || payloadOffset >= stream.Length)
            throw new InvalidDataException("Could not locate a Fusion payload after PE sections.");

        return payloadOffset;
    }

    private void ReadPackDataIfPresent(BinaryReader reader)
    {
        if (!reader.HasBytes(4))
            return;

        uint marker = reader.PeekUInt32();
        if (marker is MagicPame or MagicPamu)
            return;

        if (marker == MagicPackData)
        {
            reader.BaseStream.Seek(28, SeekOrigin.Current);
        }
        else if ((marker & 0xFFFF) is LegacyLastChunk or LegacyAltChunk)
        {
            if (reader.MatchesBytesAt(4, 0x49, 0x87, 0x47, 0x12))
                reader.BaseStream.Seek(28, SeekOrigin.Current);
            else
            {
                _fusion = 1.5f;
                _unicode = false;
                Console.WriteLine("PackData: legacy format not implemented in lite mode, skipping.");
                return;
            }
        }
        else if ((ushort)(marker & 0xFFFF) == 1)
        {
            _fusion = 1.1f;
            _unicode = false;
            Console.WriteLine("PackData: legacy format not implemented in lite mode, skipping.");
            return;
        }
        else
        {
            Console.WriteLine("PackData: none detected");
            return;
        }

        uint count = reader.ReadUInt32();
        if (count > MaxPackFiles)
            throw new InvalidDataException($"PackData file count out of range: {count}");

        Console.WriteLine($"PackData: {count} files");
        if (count > 0)
            _unicode = DetectLengthPrefixedStringUnicode(reader);

        string packDir = Path.Combine(_dumpDir, "Packed Data");
        if (_options.DumpPackData)
            Directory.CreateDirectory(packDir);

        for (uint i = 0; i < count; i++)
        {
            try
            {
                int nameLength = ValidateLength(reader.ReadInt16(), "PackData name");
                string name = ReadUniversal(reader, nameLength);
                _ = reader.ReadInt32();
                int dataSize = ValidateRemainingSize(reader, reader.ReadInt32(), "PackData file");

                if (!_options.DumpPackData)
                {
                    reader.SkipBytesExact(dataSize);
                    continue;
                }

                byte[] data = reader.ReadBytesExact(dataSize);
                if (Compression.IsZlib(data))
                    data = Compression.DecompressBlock(data);

                string outPath = UniquePath(Path.Combine(packDir, Sanitizer.RelativePath(name)));
                Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
                File.WriteAllBytes(outPath, data);
                _packFilesWritten++;
            }
            catch (Exception ex)
            {
                _packFailures++;
                Console.WriteLine($"PackData file {i:D5} failed: {ex.Message}");
                if (!reader.HasBytes(1))
                    break;
            }
        }
    }

    private static bool DetectLengthPrefixedStringUnicode(BinaryReader reader)
    {
        long pos = reader.BaseStream.Position;
        try
        {
            if (!reader.HasBytes(4))
                return false;

            short length = reader.ReadInt16();
            return length > 0 && reader.HasBytes(2) && reader.BaseStream.ReadByte() >= 0 && reader.BaseStream.ReadByte() == 0;
        }
        finally
        {
            reader.BaseStream.Position = pos;
        }
    }

    private void SeekToPackageHeaderIfNeeded(BinaryReader reader)
    {
        if (!reader.HasBytes(4))
            return;

        uint marker = reader.PeekUInt32();
        if (marker is MagicPame or MagicPamu)
            return;

        long start = reader.BaseStream.Position;
        long header = FindPackageHeader(reader.BaseStream, start);
        if (header < 0)
        {
            reader.BaseStream.Position = start;
            return;
        }

        reader.BaseStream.Position = header;
        Console.WriteLine($"Package: skipped {header - start:N0} bytes to header at 0x{header:X}");
    }

    private static long FindPackageHeader(Stream stream, long start)
    {
        const int BufferSize = 1 << 20;
        byte[] buffer = new byte[BufferSize + 3];
        int overlap = 0;
        stream.Position = start;

        while (stream.Position < stream.Length)
        {
            long readStart = stream.Position - overlap;
            int read = stream.Read(buffer, overlap, BufferSize);
            if (read == 0)
                break;

            int total = overlap + read;
            for (int i = 0; i <= total - 4; i++)
            {
                if (buffer[i] == (byte)'P' &&
                    buffer[i + 1] == (byte)'A' &&
                    buffer[i + 2] == (byte)'M' &&
                    (buffer[i + 3] == (byte)'E' || buffer[i + 3] == (byte)'U'))
                {
                    return readStart + i;
                }
            }

            overlap = Math.Min(3, total);
            Array.Copy(buffer, total - overlap, buffer, 0, overlap);
        }

        return -1;
    }

    private void ReadPackage(BinaryReader reader)
    {
        string header = BinaryText.ReadAscii(reader, 4);
        if (header is not ("PAME" or "PAMU"))
            throw new InvalidDataException($"Unsupported package header '{header}' at 0x{reader.BaseStream.Position - 4:X}.");

        short runtimeVersion = reader.ReadInt16();
        _ = reader.ReadInt16(); // runtime subversion
        int productVersion = reader.ReadInt32();
        _build = reader.ReadInt32();

        if (runtimeVersion == 769)
            _fusion = 1.5f;
        else if (_build < 280)
            _fusion = 2.0f + (productVersion == 1 ? 0.1f : 0);
        else
            _fusion = 2.5f;

        _unicode = header != "PAME";
        Console.WriteLine($"Package: {header}, Fusion {_fusion:0.0}, build {_build}");

        bool stopAfterChunk = false;
        while (reader.HasBytes(8) && !stopAfterChunk)
        {
            long chunkStart = reader.BaseStream.Position;
            short id = reader.ReadInt16();
            short flag = reader.ReadInt16();
            int size = reader.ReadInt32();
            long payloadStart = reader.BaseStream.Position;
            long payloadEnd = payloadStart + size;

            if (size < 0 || payloadEnd > reader.BaseStream.Length)
                throw new InvalidDataException($"Bad chunk size at 0x{chunkStart:X}: id=0x{(ushort)id:X4}, size={size}");

            try
            {
                switch ((ushort)id)
                {
                    case ChunkAppName:
                        ReadAppNameChunk(reader, size, flag);
                        break;
                    case ChunkShaders when _options.DumpShaders:
                        ReadShaderBankChunk(reader, size, flag, "Shaders");
                        break;
                    case ChunkExtendedHeader:
                        ReadExtendedHeaderChunk(reader, size, flag);
                        break;
                    case ChunkShadersAlt when _options.DumpShaders:
                        ReadShaderBankChunk(reader, size, flag, "Shaders");
                        break;
                    case ChunkPlus:
                        _plus = true;
                        break;
                    case ChunkImageBank when _options.DumpImages:
                        DumpImageBank(reader, size, flag);
                        break;
                    case ChunkSoundBank when _options.DumpSounds:
                        DumpSoundBank(reader, size, flag);
                        break;
                    case ChunkSeeded:
                        _seeded = true;
                        break;
                    case ChunkLast:
                        stopAfterChunk = true;
                        break;
                }
            }
            finally
            {
                reader.BaseStream.Position = payloadEnd;
            }
        }
    }

    private void ReadAppNameChunk(BinaryReader reader, int size, short flag)
    {
        using BinaryReader chunk = OpenChunkReader(reader, size, flag);
        string appName = ReadUniversal(chunk);
        if (string.IsNullOrWhiteSpace(appName))
            return;

        _appName = appName.TrimEnd('\0');
        string desiredDumpDir = Path.Combine(_outputRoot, Sanitizer.FileName(_appName));
        if (!Path.GetFullPath(desiredDumpDir).Equals(Path.GetFullPath(_dumpDir), StringComparison.OrdinalIgnoreCase) &&
            !Directory.EnumerateFileSystemEntries(_dumpDir).Any())
        {
            Directory.Delete(_dumpDir);
            _dumpDir = desiredDumpDir;
            Directory.CreateDirectory(_dumpDir);
        }

        Console.WriteLine("App: " + _appName);
    }

    private void ReadExtendedHeaderChunk(BinaryReader reader, int size, short flag)
    {
        using BinaryReader chunk = OpenChunkReader(reader, size, flag);
        uint flags = chunk.ReadUInt32();
        byte buildType = chunk.ReadByte();
        _windows = buildType is 0 or 1 or 2;
        _flash = buildType == 10;
        _android = buildType is 12 or 20 or 34;
        _ios = buildType is 13 or 14 or 15;
        _html = buildType is 27 or 28;
        chunk.BaseStream.Seek(3, SeekOrigin.Current);
        uint compressionFlags = chunk.ReadUInt32();

        _premultipliedAlpha = BitFlag.IsSet(flags, 29);
        _optimizeImageSize = BitFlag.IsSet(compressionFlags, 9);

        Console.WriteLine($"Extended header: buildType={buildType}, optimizeImageSize={_optimizeImageSize}");
    }

    private void DumpImageBank(BinaryReader reader, int size, short flag)
    {
        Console.WriteLine($"ImageBank: {(flag == 0 ? "streaming" : "buffered compressed chunk")}, {size:N0} bytes");
        using BinaryReader bank = OpenChunkReader(reader, size, flag);
        int imageCount = ValidateCount((_android || _ios || _flash || _html) ? ReadMobileCount(bank) : bank.ReadInt32(), "Image", MaxImages);
        string imageDir = Path.Combine(_dumpDir, "Images");
        Directory.CreateDirectory(imageDir);

        ProgressMeter progress = new("Images", imageCount);
        for (int i = 0; i < imageCount; i++)
        {
            LiteImage? image = null;
            try
            {
                image = _optimizeImageSize && _fusion > 2.0f && !_flash && !_android && !_ios && !_html
                    ? ReadImage25Plus(bank)
                    : ReadImage25(bank);

                _imageModes[image.GraphicMode] = _imageModes.GetValueOrDefault(image.GraphicMode) + 1;

                string outPath = Path.Combine(imageDir, $"{image.Handle:D5}.png");
                SavePng(image, outPath);
                _imagesWritten++;
            }
            catch (Exception ex)
            {
                _imageFailures++;
                string handle = image == null ? i.ToString("D5") : image.Handle.ToString("D5");
                Console.WriteLine($"Image {handle} failed: {ex.Message}");
            }
            finally
            {
                image?.Clear();
            }

            progress.Step(i + 1);
        }

        progress.Done();
    }

    private static int ReadMobileCount(BinaryReader reader)
    {
        reader.BaseStream.Seek(2, SeekOrigin.Current);
        return reader.ReadInt16();
    }

    private LiteImage ReadImage25(BinaryReader reader)
    {
        uint rawHandle = reader.ReadUInt32();
        uint handle = NormalizeHandle(rawHandle);
        int decompressedSize = ValidateNonNegative(reader.ReadInt32(), "Image decompressed size");
        int compressedSize = ValidateRemainingSize(reader, reader.ReadInt32(), "Image compressed data");
        byte[] compressed = reader.ReadBytesExact(compressedSize);
        byte[] decompressed = Compression.DecompressBlock(compressed, decompressedSize);

        using MemoryStream ms = new(decompressed, writable: false);
        using BinaryReader imageReader = new(ms);
        LiteImage image = new()
        {
            Handle = handle,
            Checksum = imageReader.ReadInt32(),
            References = imageReader.ReadInt32()
        };

        int dataSize = ValidateRemainingSize(imageReader, imageReader.ReadInt32(), "Image data");
        image.Width = imageReader.ReadInt16();
        image.Height = imageReader.ReadInt16();
        ValidateImageDimensions(image.Width, image.Height);
        image.GraphicMode = imageReader.ReadByte();
        image.Flags = imageReader.ReadByte();
        imageReader.BaseStream.Seek(2, SeekOrigin.Current);
        image.HotspotX = imageReader.ReadInt16();
        image.HotspotY = imageReader.ReadInt16();
        image.ActionPointX = imageReader.ReadInt16();
        image.ActionPointY = imageReader.ReadInt16();
        image.TransparentColor = BinaryText.ReadColor(imageReader);

        if (image.Flag(ImageFlags.Lzx))
        {
            _ = imageReader.ReadInt32();
            int remaining = checked((int)(imageReader.BaseStream.Length - imageReader.BaseStream.Position));
            image.ImageData = Compression.DecompressBlock(imageReader.ReadBytesExact(remaining));
        }
        else
        {
            image.ImageData = imageReader.ReadBytesExact(dataSize);
        }

        return image;
    }

    private LiteImage ReadImage25Plus(BinaryReader reader)
    {
        LiteImage image = new()
        {
            Handle = NormalizeHandle(reader.ReadUInt32()),
            Checksum = reader.ReadInt32(),
            References = reader.ReadInt32()
        };
        reader.BaseStream.Seek(4, SeekOrigin.Current);
        int dataSize = ValidateRemainingSize(reader, reader.ReadInt32(), "Image data");
        image.Width = reader.ReadInt16();
        image.Height = reader.ReadInt16();
        ValidateImageDimensions(image.Width, image.Height);
        image.GraphicMode = reader.ReadByte();
        image.Flags = reader.ReadByte();
        reader.BaseStream.Seek(2, SeekOrigin.Current);
        image.HotspotX = reader.ReadInt16();
        image.HotspotY = reader.ReadInt16();
        image.ActionPointX = reader.ReadInt16();
        image.ActionPointY = reader.ReadInt16();
        image.TransparentColor = BinaryText.ReadColor(reader);

        int decompressedSize = ValidateImageBufferSize(reader.ReadInt32(), image.Width, image.Height);
        if (dataSize < 4)
            throw new InvalidDataException($"Optimized image data size out of range: {dataSize}");

        byte[] compressedImage = reader.ReadBytesExact(Math.Max(0, dataSize - 4));
        image.ImageData = new byte[decompressedSize];
        int decoded = LZ4Codec.Decode(compressedImage, image.ImageData);
        if (decoded != decompressedSize)
            throw new InvalidDataException($"LZ4 image decoded {decoded} bytes, expected {decompressedSize}.");
        return image;
    }

    private void SavePng(LiteImage image, string path)
    {
        byte[] bgra = ImageTranslator.ToBgra(image, new TranslatorContext
        {
            Build = _build,
            Fusion = _fusion,
            Plus = _plus,
            Seeded = _seeded,
            PremultipliedAlpha = _premultipliedAlpha
        });

        using Bitmap bitmap = new(image.Width, image.Height, PixelFormat.Format32bppArgb);
        BitmapData data = bitmap.LockBits(new Rectangle(0, 0, image.Width, image.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            int rowBytes = image.Width * 4;
            if (data.Stride == rowBytes)
            {
                Marshal.Copy(bgra, 0, data.Scan0, bgra.Length);
            }
            else
            {
                for (int y = 0; y < image.Height; y++)
                {
                    IntPtr dest = IntPtr.Add(data.Scan0, y * data.Stride);
                    Marshal.Copy(bgra, y * rowBytes, dest, rowBytes);
                }
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        bitmap.Save(path, ImageFormat.Png);
    }

    private void DumpSoundBank(BinaryReader reader, int size, short flag)
    {
        Console.WriteLine($"SoundBank: {(flag == 0 ? "streaming" : "buffered compressed chunk")}, {size:N0} bytes");
        using BinaryReader bank = OpenChunkReader(reader, size, flag);
        int count = ValidateCount((_android || _ios || _flash || _html) ? ReadMobileCount(bank) : bank.ReadInt32(), "Sound", MaxSounds);
        if (_android || _ios || _flash || _html)
        {
            Console.WriteLine("SoundBank: mobile/Flash/HTML sound banks are not implemented in lite mode, skipping.");
            return;
        }

        string soundDir = Path.Combine(_dumpDir, "Sounds");
        Directory.CreateDirectory(soundDir);

        ProgressMeter progress = new("Sounds", count);
        for (int i = 0; i < count; i++)
        {
            long soundStart = bank.BaseStream.Position;
            try
            {
                SoundAsset sound = ReadSound(bank);
                string safeName = string.IsNullOrWhiteSpace(sound.Name) ? $"sound_{sound.Handle:D5}" : Sanitizer.FileName(sound.Name);
                string ext = sound.GetExtension();
                string outPath = UniquePath(Path.Combine(soundDir, $"{safeName}.{ext}"));
                File.WriteAllBytes(outPath, sound.Data);
                _soundsWritten++;
            }
            catch (Exception ex)
            {
                _soundFailures++;
                Console.WriteLine($"Sound {i:D5} failed: {ex.Message}");
                if (bank.BaseStream.Position <= soundStart || !bank.HasBytes(1))
                    break;
            }
            finally
            {
                progress.Step(i + 1);
            }
        }

        progress.Done();
    }

    private SoundAsset ReadSound(BinaryReader reader)
    {
        SoundAsset sound = new();
        uint rawHandle = reader.ReadUInt32();
        sound.Handle = NormalizeHandle(rawHandle);
        sound.Checksum = reader.ReadInt32();
        sound.References = reader.ReadUInt32();
        int decompressedSize = ValidateNonNegative(reader.ReadInt32(), "Sound decompressed size");
        sound.Flags = reader.ReadUInt32();
        sound.Frequency = reader.ReadInt32();
        int nameLength = ValidateLength(reader.ReadInt32(), "Sound name");

        bool playFromDisk = BitFlag.IsSet(sound.Flags, 5);
        byte[] payload;
        if (!playFromDisk)
        {
            int compressedSize = ValidateRemainingSize(reader, reader.ReadInt32(), "Sound compressed data");
            payload = Compression.DecompressBlock(reader.ReadBytesExact(compressedSize), decompressedSize);
        }
        else
        {
            payload = reader.ReadBytesExact(ValidateRemainingSize(reader, decompressedSize, "Sound data"));
        }

        using MemoryStream ms = new(payload, writable: false);
        using BinaryReader soundReader = new(ms);
        sound.Name = ReadUniversalStop(soundReader, nameLength);
        sound.Data = soundReader.ReadBytesExact(checked((int)(soundReader.BaseStream.Length - soundReader.BaseStream.Position)));

        return sound;
    }

    private void ReadShaderBankChunk(BinaryReader reader, int size, short flag, string folderName)
    {
        using BinaryReader chunk = OpenChunkReader(reader, size, flag);
        if (!chunk.HasBytes(4))
            return;

        int count = chunk.ReadInt32();
        if (count < 0 || count > MaxShaders)
            return;

        int[] offsets = new int[count];
        for (int i = 0; i < count; i++)
            offsets[i] = chunk.ReadInt32();

        string shaderDir = Path.Combine(_dumpDir, folderName);
        Directory.CreateDirectory(shaderDir);

        for (int i = 0; i < count; i++)
        {
            if (offsets[i] == 0)
                continue;

            try
            {
                if (offsets[i] < 0 || offsets[i] >= chunk.BaseStream.Length)
                    throw new InvalidDataException($"Shader offset out of range: {offsets[i]}");

                chunk.BaseStream.Position = offsets[i];
                ShaderAsset shader = ReadShader(chunk, i);
                if (shader.FxData.Length == 0)
                    continue;

                string baseName = Sanitizer.FileName(Path.GetFileNameWithoutExtension(shader.Name));
                if (string.IsNullOrWhiteSpace(baseName))
                    baseName = $"shader_{i:D3}";

                string fxPath = UniquePath(Path.Combine(shaderDir, baseName + (shader.Compiled ? ".fxc" : ".fx")));
                File.WriteAllBytes(fxPath, shader.FxData);
                _shaderFilesWritten++;

                string xmlPath = UniquePath(Path.Combine(shaderDir, baseName + ".xml"));
                File.WriteAllText(xmlPath, shader.ToXml(), Encoding.UTF8);
                _shaderFilesWritten++;
            }
            catch (Exception ex)
            {
                _shaderFailures++;
                Console.WriteLine($"Shader {i:D3} failed: {ex.Message}");
            }
        }
    }

    private ShaderAsset ReadShader(BinaryReader reader, int index)
    {
        long start = reader.BaseStream.Position;
        int nameOffset = reader.ReadInt32();
        int fxDataOffset = reader.ReadInt32();
        int parameterOffset = reader.ReadInt32();
        _ = reader.ReadInt32(); // options offset
        int fxDataSize = ValidateNonNegative(reader.ReadInt32(), "Shader data size");

        ShaderAsset shader = new() { Handle = index };
        if (_build >= 296 && Math.Abs(_fusion - 2.5f) < 0.01f)
        {
            shader.Name = $"Shader_{index}.fx";
        }
        else if (nameOffset != 0)
        {
            SeekRelativeOffset(reader, start, nameOffset, "Shader name");
            shader.Name = BinaryText.ReadAsciiZ(reader, MaxNameLength);
        }

        if (fxDataOffset != 0)
        {
            SeekRelativeOffset(reader, start, fxDataOffset, "Shader data");
            string header = BinaryText.ReadAscii(reader, 4);
            shader.Compiled = header == "DXBC";
            reader.BaseStream.Seek(-4, SeekOrigin.Current);
            if (shader.Compiled)
            {
                int byteCount = ValidateRemainingSize(reader, Math.Max(0, fxDataSize - 1), "Compiled shader data");
                shader.FxData = reader.ReadBytesExact(byteCount);
            }
            else
            {
                shader.FxData = Encoding.ASCII.GetBytes(BinaryText.ReadAsciiZ(reader, MaxShaderSourceBytes));
            }
        }

        if (parameterOffset != 0)
        {
            SeekRelativeOffset(reader, start, parameterOffset, "Shader parameters");
            int paramCount = reader.ReadInt32();
            if (paramCount is > 0 and < 1024)
            {
                int typeOffset = reader.ReadInt32();
                int nameOffset2 = reader.ReadInt32();
                byte[] types = new byte[paramCount];

                SeekRelativeOffset(reader, start + parameterOffset, typeOffset, "Shader parameter types");
                for (int i = 0; i < paramCount; i++)
                    types[i] = reader.ReadByte();

                SeekRelativeOffset(reader, start + parameterOffset, nameOffset2, "Shader parameter names");
                for (int i = 0; i < paramCount; i++)
                    shader.Parameters.Add(new ShaderParameter(types[i], BinaryText.ReadAsciiZ(reader, MaxNameLength)));
            }
        }

        return shader;
    }

    private uint NormalizeHandle(uint rawHandle) =>
        _build >= 284 && rawHandle > 0 ? rawHandle - 1 : rawHandle;

    private static int ValidateCount(int count, string label, int max)
    {
        if (count < 0 || count > max)
            throw new InvalidDataException($"{label} count out of range: {count}");

        return count;
    }

    private static int ValidateLength(int length, string label)
    {
        if (length < 0 || length > MaxNameLength)
            throw new InvalidDataException($"{label} length out of range: {length}");

        return length;
    }

    private static int ValidateNonNegative(int value, string label)
    {
        if (value < 0)
            throw new InvalidDataException($"{label} is negative: {value}");

        return value;
    }

    private static int ValidateRemainingSize(BinaryReader reader, int size, string label)
    {
        ValidateNonNegative(size, label);
        if (!reader.HasBytes(size))
            throw new EndOfStreamException($"{label} needs {size} bytes but only {reader.BaseStream.Length - reader.BaseStream.Position} remain.");

        return size;
    }

    private static void ValidateImageDimensions(short width, short height)
    {
        if (width <= 0 || height <= 0)
            throw new InvalidDataException($"Image dimensions out of range: {width}x{height}");

        long pixels = width * (long)height;
        if (pixels > 100_000_000)
            throw new InvalidDataException($"Image dimensions are too large: {width}x{height}");
    }

    private static int ValidateImageBufferSize(int size, short width, short height)
    {
        ValidateImageDimensions(width, height);
        ValidateNonNegative(size, "Image buffer size");

        long maxReasonable = width * (long)height * 8 + height * 8L + 4096;
        if (size > maxReasonable)
            throw new InvalidDataException($"Image buffer size {size} is too large for {width}x{height}.");

        return size;
    }

    private static void SeekRelativeOffset(BinaryReader reader, long start, int offset, string label)
    {
        if (offset < 0)
            throw new InvalidDataException($"{label} offset is negative: {offset}");

        long position = start + offset;
        if (position < 0 || position >= reader.BaseStream.Length)
            throw new InvalidDataException($"{label} offset out of range: {offset}");

        reader.BaseStream.Position = position;
    }

    private BinaryReader OpenChunkReader(BinaryReader reader, int size, short flag)
    {
        if (flag == 0)
            return new BinaryReader(new WindowedReadStream(reader.BaseStream, reader.BaseStream.Position, size), Encoding.UTF8);

        byte[] payload = ReadChunkPayload(reader, size, flag);
        return new BinaryReader(new MemoryStream(payload, writable: false), Encoding.UTF8);
    }

    private static byte[] ReadChunkPayload(BinaryReader reader, int size, short flag)
    {
        if (flag == 0)
            return reader.ReadBytesExact(size);

        if (flag == 1)
        {
            if (size < 8)
                throw new InvalidDataException($"Compressed chunk is too small: {size} bytes.");

            long start = reader.BaseStream.Position;
            int decompressedSize = ValidateNonNegative(reader.ReadInt32(), "Chunk decompressed size");
            int compressedSize = ValidateRemainingSize(reader, reader.ReadInt32(), "Compressed chunk data");
            if (compressedSize > size - 8)
                throw new InvalidDataException($"Compressed chunk data exceeds chunk size: {compressedSize} > {size - 8}");

            byte[] compressed = reader.ReadBytesExact(compressedSize);
            reader.BaseStream.Position = start + size;
            return Compression.DecompressBlock(compressed, decompressedSize);
        }

        throw new NotSupportedException($"Chunk flag {flag} is not supported by the lite dumper.");
    }

    private string ReadUniversal(BinaryReader reader, int length = -1)
    {
        return _unicode == true
            ? BinaryText.ReadUtf16(reader, length)
            : BinaryText.ReadAscii(reader, length);
    }

    private string ReadUniversalStop(BinaryReader reader, int length)
    {
        return _unicode == true
            ? BinaryText.ReadUtf16Stop(reader, length)
            : BinaryText.ReadAsciiStop(reader, length);
    }

    private string UniquePath(string path)
    {
        path = Path.GetFullPath(path);
        if (!File.Exists(path) && _usedPaths.Add(path))
            return path;

        string dir = Path.GetDirectoryName(path)!;
        string name = Path.GetFileNameWithoutExtension(path);
        string ext = Path.GetExtension(path);
        for (int i = 1; ; i++)
        {
            string candidate = Path.Combine(dir, $"{name}_{i}{ext}");
            if (!File.Exists(candidate) && _usedPaths.Add(candidate))
                return candidate;
        }
    }
}

internal sealed class LiteImage
{
    public uint Handle;
    public int Checksum;
    public int References;
    public short Width;
    public short Height;
    public byte GraphicMode;
    public byte Flags;
    public short HotspotX;
    public short HotspotY;
    public short ActionPointX;
    public short ActionPointY;
    public Color TransparentColor = Color.Black;
    public byte[] ImageData = Array.Empty<byte>();

    public bool Flag(int bit) => BitFlag.IsSet(Flags, bit);
    public void Clear() => ImageData = Array.Empty<byte>();
}

internal static class ImageFlags
{
    public const int Rle = 0;
    public const int Rlew = 1;
    public const int Rlet = 2;
    public const int Lzx = 3;
    public const int Alpha = 4;
    public const int Rgba = 7;
}

internal readonly record struct TranslatorContext(int Build, float Fusion, bool Plus, bool Seeded, bool PremultipliedAlpha);

internal static class ImageTranslator
{
    public static byte[] ToBgra(LiteImage image, TranslatorContext context)
    {
        if (image.Width <= 0 || image.Height <= 0)
            throw new InvalidDataException("Image has invalid dimensions.");

        return image.GraphicMode switch
        {
            4 => Normal24BitMaskedToBgra(image, context),
            6 => Normal15BitToBgra(image, context),
            7 => Normal16BitToBgra(image, context),
            8 => TwoFivePlusToBgra(image, context),
            _ => throw new NotSupportedException($"Graphic mode {image.GraphicMode} is not implemented.")
        };
    }

    private static int GetPadding(LiteImage image, TranslatorContext context)
    {
        int colorModeSize = 3;
        int modSize = 2;
        switch (image.GraphicMode)
        {
            case 3:
                colorModeSize = 1;
                modSize = 4;
                break;
            case 6:
            case 7:
                colorModeSize = 2;
                break;
            case 0:
            case 8:
                colorModeSize = 4;
                break;
        }

        if (!image.Flag(ImageFlags.Rlet) || context.Plus || context.Fusion < 2.0f)
            return image.Width * colorModeSize % modSize;
        if (context.Build < 280)
            return image.Width * colorModeSize % modSize * colorModeSize;
        return image.Width % modSize * colorModeSize;
    }

    private static int GetAlphaPadding(LiteImage image) => (4 - image.Width % 4) % 4;

    private static bool IsRle(LiteImage image) =>
        image.Flag(ImageFlags.Rle) || image.Flag(ImageFlags.Rlew) || image.Flag(ImageFlags.Rlet);

    private static byte PeekImageByte(LiteImage image, int position)
    {
        EnsureImageBytes(image, position, 1);
        return image.ImageData[position];
    }

    private static byte ReadImageByte(LiteImage image, ref int position)
    {
        EnsureImageBytes(image, position, 1);
        return image.ImageData[position++];
    }

    private static ushort ReadImageUInt16(LiteImage image, ref int position)
    {
        EnsureImageBytes(image, position, 2);
        ushort value = (ushort)(image.ImageData[position] | image.ImageData[position + 1] << 8);
        position += 2;
        return value;
    }

    private static void SkipImageBytes(LiteImage image, ref int position, int count)
    {
        EnsureImageBytes(image, position, count);
        position += count;
    }

    private static void EnsureImageBytes(LiteImage image, int position, int count)
    {
        if (position < 0 || count < 0 || position > image.ImageData.Length - count)
            throw new InvalidDataException($"Image data is truncated at offset {position}; needed {count} bytes, length is {image.ImageData.Length}.");
    }

    private static byte[] Normal24BitMaskedToBgra(LiteImage image, TranslatorContext context)
    {
        byte[] output = new byte[checked(image.Width * image.Height * 4)];
        int stride = image.Width * 4;
        int pad = GetPadding(image, context);
        int position = 0;
        int command = PeekImageByte(image, position);
        bool rleLoop = false;
        bool rleCommander = false;
        bool rle = IsRle(image);

        if (command > 128)
        {
            command -= 128;
            rleCommander = true;
        }
        else if (command == 0)
        {
            rleLoop = true;
        }

        if (rle)
            position++;

        byte r = 0;
        byte g = 0;
        byte b = 0;
        for (int y = 0; y < image.Height; y++)
        {
            for (int x = 0; x < image.Width; x++)
            {
                if (!rle || !rleLoop || rleCommander)
                {
                    r = ReadImageByte(image, ref position);
                    g = ReadImageByte(image, ref position);
                    b = ReadImageByte(image, ref position);
                    rleLoop = true;
                }

                int newPos = y * stride + x * 4;
                if (Math.Abs(context.Fusion - 3.0f) < 0.01f && !context.Seeded)
                {
                    output[newPos + 0] = b;
                    output[newPos + 1] = g;
                    output[newPos + 2] = r;
                }
                else
                {
                    output[newPos + 0] = r;
                    output[newPos + 1] = g;
                    output[newPos + 2] = b;
                }

                output[newPos + 3] = 255;
                if (!image.Flag(ImageFlags.Alpha) &&
                    output[newPos + 0] == image.TransparentColor.B &&
                    output[newPos + 1] == image.TransparentColor.G &&
                    output[newPos + 2] == image.TransparentColor.R)
                {
                    output[newPos + 3] = 0;
                }

                if (rle && --command == 0)
                {
                    command = ReadImageByte(image, ref position);
                    rleCommander = false;
                    rleLoop = false;

                    if (command > 128)
                    {
                        command -= 128;
                        rleCommander = true;
                    }
                    else if (command == 0)
                    {
                        rleLoop = true;
                    }
                }
            }

            SkipImageBytes(image, ref position, pad * 3);
        }

        if (image.Flag(ImageFlags.Alpha))
        {
            int alphaPad = GetAlphaPadding(image);
            for (int y = 0; y < image.Height; y++)
            {
                for (int x = 0; x < image.Width; x++)
                    output[y * stride + x * 4 + 3] = ReadImageByte(image, ref position);
                SkipImageBytes(image, ref position, alphaPad);
            }
        }

        return output;
    }

    private static byte[] Normal16BitToBgra(LiteImage image, TranslatorContext context)
    {
        byte[] output = new byte[checked(image.Width * image.Height * 4)];
        int stride = image.Width * 4;
        int pad = GetPadding(image, context);
        int position = 0;
        int command = PeekImageByte(image, position);
        bool rleLoop = false;
        bool rleCommander = false;
        bool rle = IsRle(image);

        if (command > 128)
        {
            command -= 128;
            rleCommander = true;
        }
        else if (command == 0)
        {
            rleLoop = true;
        }

        if (rle)
            position++;

        byte r = 0;
        byte g = 0;
        byte b = 0;
        for (int y = 0; y < image.Height; y++)
        {
            for (int x = 0; x < image.Width; x++)
            {
                if (!rle || !rleLoop || rleCommander)
                {
                    ushort value = ReadImageUInt16(image, ref position);
                    r = (byte)((value & 63488) >> 11);
                    g = (byte)((value & 2016) >> 5);
                    b = (byte)(value & 31);
                    r = (byte)(r << 3);
                    g = (byte)(g << 2);
                    b = (byte)(b << 3);
                    rleLoop = true;
                }

                int newPos = y * stride + x * 4;
                output[newPos + 2] = r;
                output[newPos + 1] = g;
                output[newPos + 0] = b;
                output[newPos + 3] = 255;
                if (!image.Flag(ImageFlags.Alpha) &&
                    output[newPos + 2] == image.TransparentColor.R &&
                    output[newPos + 1] == image.TransparentColor.G &&
                    output[newPos + 0] == image.TransparentColor.B)
                {
                    output[newPos + 3] = 0;
                }

                if (rle && --command == 0)
                {
                    command = ReadImageByte(image, ref position);
                    rleCommander = false;
                    rleLoop = false;
                    if (command > 128)
                    {
                        command -= 128;
                        rleCommander = true;
                    }
                    else if (command == 0)
                    {
                        rleLoop = true;
                    }
                }
            }

            SkipImageBytes(image, ref position, pad * 2);
        }

        ApplyTrailingAlpha(image, output, ref position, stride);
        return output;
    }

    private static byte[] Normal15BitToBgra(LiteImage image, TranslatorContext context)
    {
        byte[] output = new byte[checked(image.Width * image.Height * 4)];
        int stride = image.Width * 4;
        int pad = GetPadding(image, context);
        int position = 0;
        int command = PeekImageByte(image, position);
        bool rleLoop = false;
        bool rleCommander = false;
        bool rle = IsRle(image);

        if (command > 128)
        {
            command -= 128;
            rleCommander = true;
        }
        else if (command == 0)
        {
            rleLoop = true;
        }

        if (rle)
            position++;

        byte r = 0;
        byte g = 0;
        byte b = 0;
        for (int y = 0; y < image.Height; y++)
        {
            for (int x = 0; x < image.Width; x++)
            {
                if (!rle || !rleLoop || rleCommander)
                {
                    ushort value = ReadImageUInt16(image, ref position);
                    r = (byte)((value & 31744) >> 10);
                    g = (byte)((value & 992) >> 5);
                    b = (byte)(value & 31);
                    r = (byte)(r << 3);
                    g = (byte)(g << 3);
                    b = (byte)(b << 3);
                    rleLoop = true;
                }

                int newPos = y * stride + x * 4;
                output[newPos + 2] = r;
                output[newPos + 1] = g;
                output[newPos + 0] = b;
                output[newPos + 3] = 255;
                if (!image.Flag(ImageFlags.Alpha) &&
                    output[newPos + 2] == image.TransparentColor.R &&
                    output[newPos + 1] == image.TransparentColor.G &&
                    output[newPos + 0] == image.TransparentColor.B)
                {
                    output[newPos + 3] = 0;
                }

                if (rle && --command == 0)
                {
                    command = ReadImageByte(image, ref position);
                    rleCommander = false;
                    rleLoop = false;
                    if (command > 128)
                    {
                        command -= 128;
                        rleCommander = true;
                    }
                    else if (command == 0)
                    {
                        rleLoop = true;
                    }
                }
            }

            SkipImageBytes(image, ref position, pad * 2);
        }

        ApplyTrailingAlpha(image, output, ref position, stride);
        return output;
    }

    private static byte[] TwoFivePlusToBgra(LiteImage image, TranslatorContext context)
    {
        byte[] output = new byte[checked(image.Width * image.Height * 4)];
        int stride = image.Width * 4;
        int pad = GetPadding(image, context);
        int position = 0;
        for (int y = 0; y < image.Height; y++)
        {
            for (int x = 0; x < image.Width; x++)
            {
                int newPos = y * stride + x * 4;
                EnsureImageBytes(image, position, 4);
                if (Math.Abs(context.Fusion - 3.0f) < 0.01f && !context.Seeded)
                {
                    output[newPos + 0] = image.ImageData[position + 2];
                    output[newPos + 1] = image.ImageData[position + 1];
                    output[newPos + 2] = image.ImageData[position + 0];
                }
                else
                {
                    output[newPos + 0] = image.ImageData[position + 0];
                    output[newPos + 1] = image.ImageData[position + 1];
                    output[newPos + 2] = image.ImageData[position + 2];
                }

                output[newPos + 3] = 255;
                if (image.Flag(ImageFlags.Alpha) || image.Flag(ImageFlags.Rgba))
                {
                    if (context.PremultipliedAlpha && image.ImageData[position + 3] != 0)
                    {
                        float alpha = image.ImageData[position + 3] / 255f;
                        output[newPos + 0] = (byte)Math.Clamp(output[newPos + 0] / alpha, 0, 255);
                        output[newPos + 1] = (byte)Math.Clamp(output[newPos + 1] / alpha, 0, 255);
                        output[newPos + 2] = (byte)Math.Clamp(output[newPos + 2] / alpha, 0, 255);
                    }

                    output[newPos + 3] = image.ImageData[position + 3];
                }
                else if (output[newPos + 2] == image.TransparentColor.R &&
                         output[newPos + 1] == image.TransparentColor.G &&
                         output[newPos + 0] == image.TransparentColor.B)
                {
                    output[newPos + 3] = 0;
                }

                position += 4;
            }

            SkipImageBytes(image, ref position, pad * 4);
        }

        if (position != image.ImageData.Length && image.Flag(ImageFlags.Alpha) && !image.Flag(ImageFlags.Rgba))
            ApplyTrailingAlpha(image, output, ref position, stride);

        return output;
    }

    private static void ApplyTrailingAlpha(LiteImage image, byte[] output, ref int position, int stride)
    {
        if (!image.Flag(ImageFlags.Alpha))
            return;

        int alphaPad = GetAlphaPadding(image);
        for (int y = 0; y < image.Height; y++)
        {
            for (int x = 0; x < image.Width; x++)
                output[y * stride + x * 4 + 3] = ReadImageByte(image, ref position);
            SkipImageBytes(image, ref position, alphaPad);
        }
    }
}

internal sealed class SoundAsset
{
    public uint Handle;
    public int Checksum;
    public uint References;
    public uint Flags;
    public int Frequency;
    public string Name = string.Empty;
    public byte[] Data = Array.Empty<byte>();

    public string GetExtension()
    {
        if (Data.Length < 4)
            return "bin";

        string header = Encoding.ASCII.GetString(Data, 0, 4);
        if (header == "RIFF")
            return "wav";
        if (header == "FORM")
            return "aiff";
        if (header == "OggS")
            return "ogg";
        if (Data[0] == 'I' && Data[1] == 'D' && Data[2] == '3')
            return "mp3";
        if (Data.Length >= 2 && Data[0] == 0xFF && (Data[1] & 0xE0) == 0xE0)
            return "mp3";
        if (header == "IMPM")
            return "it";
        if (Data.Length >= 17 && Encoding.ASCII.GetString(Data, 0, 17) == "Extended Module: ")
            return "xm";
        if (Data.Length > 0x2F &&
            Data[0x2C] == 'S' && Data[0x2D] == 'C' && Data[0x2E] == 'R' && Data[0x2F] == 'M')
            return "s3m";
        if (Data.Length > 0x43B)
        {
            string modSignature = Encoding.ASCII.GetString(Data, 0x438, 4);
            if (ModSignatures.Contains(modSignature))
                return "mod";
        }

        return "bin";
    }

    private static readonly HashSet<string> ModSignatures = new(StringComparer.Ordinal)
    {
        "2CHN", "M.K.", "6CHN", "8CHN", "10CH", "12CH", "14CH", "16CH",
        "18CH", "20CH", "22CH", "24CH", "26CH", "28CH", "30CH", "32CH",
        "M!K!", "FLT4", "OCTA"
    };
}

internal sealed class ShaderAsset
{
    public int Handle;
    public string Name = string.Empty;
    public bool Compiled;
    public byte[] FxData = Array.Empty<byte>();
    public List<ShaderParameter> Parameters { get; } = new();

    public string ToXml()
    {
        StringBuilder sb = new();
        sb.AppendLine("<effect>");
        sb.AppendLine($"  <name>{XmlEscape(Path.GetFileNameWithoutExtension(Name))}</name>");
        sb.AppendLine($"  <handle>{Handle}</handle>");
        sb.AppendLine($"  <compiled>{Compiled.ToString().ToLowerInvariant()}</compiled>");
        foreach (ShaderParameter parameter in Parameters)
            sb.AppendLine($"  <parameter type=\"{parameter.Type}\">{XmlEscape(parameter.Name)}</parameter>");
        sb.AppendLine("</effect>");
        return sb.ToString();
    }

    private static string XmlEscape(string? value) =>
        string.IsNullOrEmpty(value)
            ? string.Empty
            : value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
}

internal readonly record struct ShaderParameter(byte Type, string Name);

internal sealed class ProgressMeter
{
    private readonly string _label;
    private readonly int _total;
    private int _lastPercent = -1;

    public ProgressMeter(string label, int total)
    {
        _label = label;
        _total = Math.Max(1, total);
    }

    public void Step(int value)
    {
        int percent = value * 100 / _total;
        if (percent == _lastPercent || (percent % 10 != 0 && percent != 100))
            return;

        _lastPercent = percent;
        long ram = Process.GetCurrentProcess().WorkingSet64 / 1024 / 1024;
        Console.WriteLine($"{_label}: {percent}% ({value}/{_total}), RAM {ram} MB");
    }

    public void Done() => Step(_total);
}

internal sealed class WindowedReadStream : Stream
{
    // This stream is a non-owning view over the package FileStream.
    private readonly Stream _baseStream;
    private readonly long _start;
    private readonly long _length;
    private long _position;

    public WindowedReadStream(Stream baseStream, long start, long length)
    {
        _baseStream = baseStream;
        _start = start;
        _length = length;
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _length;

    public override long Position
    {
        get => _position;
        set => Seek(value, SeekOrigin.Begin);
    }

    public override void Flush() { }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_position >= _length)
            return 0;

        count = (int)Math.Min(count, _length - _position);
        _baseStream.Position = _start + _position;
        int read = _baseStream.Read(buffer, offset, count);
        _position += read;
        return read;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        long newPosition = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => _length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };

        if (newPosition < 0 || newPosition > _length)
            throw new IOException("Seek outside chunk window.");

        _position = newPosition;
        return _position;
    }

    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

internal static class Compression
{
    private const int MaxDecompressedBlockBytes = 1024 * 1024 * 1024;

    public static byte[] DecompressBlock(byte[] data, int maxOutputSize = MaxDecompressedBlockBytes)
    {
        if (maxOutputSize < 0)
            throw new InvalidDataException($"Invalid decompressed size: {maxOutputSize}");

        using MemoryStream input = new(data, writable: false);
        using Stream stream = IsZlib(data)
            ? new ZLibStream(input, CompressionMode.Decompress)
            : new DeflateStream(input, CompressionMode.Decompress);
        int capacity = maxOutputSize is > 0 and < MaxDecompressedBlockBytes ? maxOutputSize : 0;
        using MemoryStream output = capacity > 0 ? new MemoryStream(capacity) : new MemoryStream();
        byte[] buffer = new byte[81920];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (output.Length + read > maxOutputSize)
                throw new InvalidDataException($"Decompressed block exceeds limit of {maxOutputSize} bytes.");

            output.Write(buffer, 0, read);
        }

        return output.ToArray();
    }

    public static bool IsZlib(byte[] data)
    {
        if (data.Length < 2 || data[0] != 0x78)
            return false;

        return data[1] is 0x01 or 0x5E or 0x9C or 0xDA;
    }
}

internal static class BinaryReaderExtensions
{
    public static bool HasBytes(this BinaryReader reader, long count) =>
        reader.BaseStream.Length - reader.BaseStream.Position >= count;

    public static int PeekInt32(this BinaryReader reader)
    {
        long pos = reader.BaseStream.Position;
        int value = reader.ReadInt32();
        reader.BaseStream.Position = pos;
        return value;
    }

    public static uint PeekUInt32(this BinaryReader reader)
    {
        long pos = reader.BaseStream.Position;
        uint value = reader.ReadUInt32();
        reader.BaseStream.Position = pos;
        return value;
    }

    public static long PeekInt64(this BinaryReader reader)
    {
        long pos = reader.BaseStream.Position;
        long value = reader.ReadInt64();
        reader.BaseStream.Position = pos;
        return value;
    }

    public static string GetAsciiAt(this BinaryReader reader, long relativePosition, int length)
    {
        long pos = reader.BaseStream.Position;
        reader.BaseStream.Seek(relativePosition, SeekOrigin.Current);
        string value = BinaryText.ReadAscii(reader, length);
        reader.BaseStream.Position = pos;
        return value;
    }

    public static bool MatchesBytesAt(this BinaryReader reader, long relativePosition, params byte[] expected)
    {
        long pos = reader.BaseStream.Position;
        try
        {
            reader.BaseStream.Seek(relativePosition, SeekOrigin.Current);
            if (!reader.HasBytes(expected.Length))
                return false;

            for (int i = 0; i < expected.Length; i++)
            {
                if (reader.ReadByte() != expected[i])
                    return false;
            }

            return true;
        }
        finally
        {
            reader.BaseStream.Position = pos;
        }
    }

    public static byte[] ReadBytesExact(this BinaryReader reader, int count)
    {
        if (count < 0)
            throw new InvalidDataException($"Cannot read a negative byte count: {count}");
        if (!reader.HasBytes(count))
            throw new EndOfStreamException($"Needed {count} bytes, got {reader.BaseStream.Length - reader.BaseStream.Position} remaining.");

        byte[] data = reader.ReadBytes(count);
        if (data.Length != count)
            throw new EndOfStreamException($"Needed {count} bytes, got {data.Length}.");
        return data;
    }

    public static void SkipBytesExact(this BinaryReader reader, int count)
    {
        if (count < 0)
            throw new InvalidDataException($"Cannot skip a negative byte count: {count}");
        if (!reader.HasBytes(count))
            throw new EndOfStreamException($"Needed to skip {count} bytes, got {reader.BaseStream.Length - reader.BaseStream.Position} remaining.");

        reader.BaseStream.Seek(count, SeekOrigin.Current);
    }
}

internal static class BinaryText
{
    public static string ReadAscii(BinaryReader reader, int length = -1)
    {
        if (length >= 0)
            return Encoding.ASCII.GetString(reader.ReadBytesExact(length)).TrimEnd('\0');

        StringBuilder sb = new();
        while (reader.HasBytes(1))
        {
            byte b = reader.ReadByte();
            if (b == 0)
                break;
            sb.Append((char)b);
        }

        return sb.ToString();
    }

    public static string ReadAsciiZ(BinaryReader reader, int maxBytes)
    {
        if (maxBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(maxBytes));

        StringBuilder sb = new();
        for (int i = 0; i < maxBytes && reader.HasBytes(1); i++)
        {
            byte b = reader.ReadByte();
            if (b == 0)
                return sb.ToString();

            sb.Append((char)b);
        }

        if (reader.HasBytes(1))
            throw new InvalidDataException($"ASCII string exceeds {maxBytes} bytes.");

        return sb.ToString();
    }

    public static string ReadAsciiStop(BinaryReader reader, int length)
    {
        long start = reader.BaseStream.Position;
        StringBuilder sb = new();
        for (int i = 0; i < length && reader.HasBytes(1); i++)
        {
            byte b = reader.ReadByte();
            if (b == 0)
                break;
            sb.Append((char)b);
        }

        reader.BaseStream.Position = start + length;
        return sb.ToString();
    }

    public static string ReadUtf16(BinaryReader reader, int length = -1)
    {
        StringBuilder sb = new();
        if (length >= 0)
        {
            for (int i = 0; i < length && reader.HasBytes(2); i++)
            {
                ushort ch = reader.ReadUInt16();
                if (ch != 0)
                    sb.Append((char)ch);
            }

            return sb.ToString();
        }

        while (reader.HasBytes(2))
        {
            ushort ch = reader.ReadUInt16();
            if (ch == 0)
                break;
            sb.Append((char)ch);
        }

        return sb.ToString();
    }

    public static string ReadUtf16Stop(BinaryReader reader, int length)
    {
        long start = reader.BaseStream.Position;
        StringBuilder sb = new();
        for (int i = 0; i < length && reader.HasBytes(2); i++)
        {
            ushort ch = reader.ReadUInt16();
            if (ch == 0)
                break;
            sb.Append((char)ch);
        }

        reader.BaseStream.Position = start + length * 2L;
        return sb.ToString();
    }

    public static Color ReadColor(BinaryReader reader)
    {
        byte r = reader.ReadByte();
        byte g = reader.ReadByte();
        byte b = reader.ReadByte();
        byte a = reader.ReadByte();
        return Color.FromArgb(a, r, g, b);
    }
}

internal static class BitFlag
{
    public static bool IsSet(byte value, int bit)
    {
        Debug.Assert(bit is >= 0 and < 8);
        return bit is >= 0 and < 8 && (value & (1 << bit)) != 0;
    }

    public static bool IsSet(uint value, int bit)
    {
        Debug.Assert(bit is >= 0 and < 32);
        return bit is >= 0 and < 32 && (value & (1u << bit)) != 0;
    }
}

internal static class Sanitizer
{
    private static readonly char[] InvalidFileNameChars = Path.GetInvalidFileNameChars();
    private static readonly char[] InvalidPathChars = Path.GetInvalidPathChars();

    public static string FileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "unnamed";

        char[]? chars = null;
        for (int i = 0; i < name.Length; i++)
        {
            char ch = name[i];
            if (!InvalidFileNameChars.Contains(ch) && !char.IsControl(ch))
                continue;

            chars ??= name.ToCharArray();
            chars[i] = '_';
        }

        string cleaned = chars == null ? name : new string(chars);
        cleaned = cleaned.Trim().TrimEnd('.');
        return string.IsNullOrWhiteSpace(cleaned) ? "unnamed" : cleaned;
    }

    public static string RelativePath(string path)
    {
        string[] parts = path.Replace('/', '\\')
            .Split('\\', StringSplitOptions.RemoveEmptyEntries)
            .Select(FileName)
            .ToArray();

        if (parts.Length == 0)
            return "unnamed";

        string combined = Path.Combine(parts);
        foreach (char ch in InvalidPathChars)
            combined = combined.Replace(ch, '_');
        return combined;
    }
}
