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
        if (args.Length == 0 || args.Contains("--help") || args.Contains("-h"))
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

        string outputRoot = args.Length > 1 && !args[1].StartsWith("--", StringComparison.Ordinal)
            ? Path.GetFullPath(args[1])
            : Path.Combine(Path.GetDirectoryName(inputPath)!, "extracted_assets_lite");

        DumperOptions options = new()
        {
            DumpImages = !args.Contains("--no-images"),
            DumpSounds = !args.Contains("--no-sounds"),
            DumpPackData = !args.Contains("--no-pack"),
            DumpShaders = !args.Contains("--no-shaders")
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
    private int _packFilesWritten;
    private int _shaderFilesWritten;
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
        ReadPackage(reader);

        Console.WriteLine();
        Console.WriteLine("Done.");
        Console.WriteLine($"Output: {_dumpDir}");
        Console.WriteLine($"Images: {_imagesWritten} written, {_imageFailures} failed");
        Console.WriteLine($"Sounds: {_soundsWritten} written");
        Console.WriteLine($"Packed data: {_packFilesWritten} written");
        Console.WriteLine($"Shader files: {_shaderFilesWritten} written");
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

        stream.Position = 60;
        ushort peHeader = reader.ReadUInt16();
        stream.Position = peHeader + 6;
        ushort sectionCount = reader.ReadUInt16();
        stream.Seek(240, SeekOrigin.Current);

        uint position = 0;
        uint relocFallback = 0;
        for (int i = 0; i < sectionCount; i++)
        {
            string sectionName = BinaryText.ReadAsciiStop(reader, 8);
            stream.Seek(8, SeekOrigin.Current);
            uint sectionStart = reader.ReadUInt32();
            uint sectionSize = reader.ReadUInt32();
            stream.Seek(16, SeekOrigin.Current);

            if (position == 0)
                position = sectionStart + sectionSize;
            else
                position += sectionStart;

            if (sectionName == ".reloc")
                relocFallback = sectionStart + sectionSize;
        }

        if (position >= stream.Length && relocFallback != position && relocFallback != 0)
            return relocFallback;

        return position;
    }

    private void ReadPackDataIfPresent(BinaryReader reader)
    {
        if (!reader.HasBytes(4))
            return;

        int marker = reader.PeekInt32();
        if (marker is 1162690896 or 1431126352) // PAME/PAMU
            return;

        if (marker == 2004318071)
        {
            reader.BaseStream.Seek(28, SeekOrigin.Current);
        }
        else if (marker is 32639 or 8748)
        {
            if (reader.GetAsciiAt(4, 4) == "I\u0087G\u0012")
                reader.BaseStream.Seek(28, SeekOrigin.Current);
            else
            {
                _fusion = 1.5f;
                _unicode = false;
            }
        }
        else if ((short)(marker & 0xFFFF) == 1)
        {
            _fusion = 1.1f;
            _unicode = false;
        }
        else
        {
            reader.BaseStream.Seek(28, SeekOrigin.Current);
        }

        if (_fusion <= 1.5f)
        {
            Console.WriteLine("PackData: legacy format not implemented in lite mode, skipping.");
            return;
        }

        uint count = reader.ReadUInt32();
        Console.WriteLine($"PackData: {count} files");

        string packDir = Path.Combine(_dumpDir, "Packed Data");
        if (_options.DumpPackData)
            Directory.CreateDirectory(packDir);

        for (uint i = 0; i < count; i++)
        {
            short nameLength = reader.ReadInt16();
            string name = ReadUniversal(reader, nameLength);
            _ = reader.ReadInt32();
            int dataSize = reader.ReadInt32();
            byte[] data = reader.ReadBytesExact(dataSize);

            if (data.Length >= 2 && data[0] == 0x78 && data[1] == 0xDA)
                data = Compression.DecompressBlock(data);

            if (_options.DumpPackData)
            {
                string outPath = UniquePath(Path.Combine(packDir, Sanitizer.RelativePath(name)));
                Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
                File.WriteAllBytes(outPath, data);
                _packFilesWritten++;
            }
        }
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

        bool pastLast = false;
        while (reader.HasBytes(8) && !(pastLast && reader.PeekInt64() == 0))
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
                    case 0x2224:
                        ReadAppNameChunk(reader, size, flag);
                        break;
                    case 0x2243 when _options.DumpShaders:
                        ReadShaderBankChunk(reader, size, flag, "Shaders");
                        break;
                    case 0x2245:
                        ReadExtendedHeaderChunk(reader, size, flag);
                        break;
                    case 0x225A when _options.DumpShaders:
                        ReadShaderBankChunk(reader, size, flag, "Shaders");
                        break;
                    case 0x2253:
                        _plus = true;
                        break;
                    case 0x6666 when _options.DumpImages:
                        DumpImageBank(reader, size, flag);
                        break;
                    case 0x6668 when _options.DumpSounds:
                        DumpSoundBank(reader, size, flag);
                        break;
                    case 0x7EEE:
                        _seeded = true;
                        break;
                    case 0x7F7F:
                        pastLast = true;
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
        int imageCount = (_android || _ios || _flash || _html) ? ReadMobileCount(bank) : bank.ReadInt32();
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
            if ((i + 1) % 128 == 0)
                GC.Collect(0, GCCollectionMode.Optimized, blocking: false);
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
        uint handle = _build >= 284 ? rawHandle - 1 : rawHandle;
        _ = reader.ReadInt32(); // decompressed size
        int compressedSize = reader.ReadInt32();
        byte[] compressed = reader.ReadBytesExact(compressedSize);
        byte[] decompressed = Compression.DecompressBlock(compressed);

        using MemoryStream ms = new(decompressed, writable: false);
        using BinaryReader imageReader = new(ms);
        LiteImage image = new()
        {
            Handle = handle,
            Checksum = imageReader.ReadInt32(),
            References = imageReader.ReadInt32()
        };

        int dataSize = imageReader.ReadInt32();
        image.Width = imageReader.ReadInt16();
        image.Height = imageReader.ReadInt16();
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
            Handle = reader.ReadUInt32() - 1,
            Checksum = reader.ReadInt32(),
            References = reader.ReadInt32()
        };
        reader.BaseStream.Seek(4, SeekOrigin.Current);
        int dataSize = reader.ReadInt32();
        image.Width = reader.ReadInt16();
        image.Height = reader.ReadInt16();
        image.GraphicMode = reader.ReadByte();
        image.Flags = reader.ReadByte();
        reader.BaseStream.Seek(2, SeekOrigin.Current);
        image.HotspotX = reader.ReadInt16();
        image.HotspotY = reader.ReadInt16();
        image.ActionPointX = reader.ReadInt16();
        image.ActionPointY = reader.ReadInt16();
        image.TransparentColor = BinaryText.ReadColor(reader);

        int decompressedSize = reader.ReadInt32();
        byte[] compressedImage = reader.ReadBytesExact(Math.Max(0, dataSize - 4));
        image.ImageData = new byte[decompressedSize];
        LZ4Codec.Decode(compressedImage, image.ImageData);
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
        int count = (_android || _ios || _flash || _html) ? ReadMobileCount(bank) : bank.ReadInt32();
        string soundDir = Path.Combine(_dumpDir, "Sounds");
        Directory.CreateDirectory(soundDir);

        ProgressMeter progress = new("Sounds", count);
        for (int i = 0; i < count; i++)
        {
            SoundAsset sound = ReadSound(bank);
            string safeName = string.IsNullOrWhiteSpace(sound.Name) ? $"sound_{sound.Handle:D5}" : Sanitizer.FileName(sound.Name);
            string ext = sound.GetExtension();
            string outPath = UniquePath(Path.Combine(soundDir, $"{safeName}.{ext}"));
            File.WriteAllBytes(outPath, sound.Data);
            _soundsWritten++;
            progress.Step(i + 1);
        }

        progress.Done();
    }

    private SoundAsset ReadSound(BinaryReader reader)
    {
        if (_android || _ios || _flash || _html)
            throw new NotSupportedException("Mobile/Flash/HTML sound banks are not implemented in lite mode.");

        SoundAsset sound = new();
        uint rawHandle = reader.ReadUInt32();
        sound.Handle = _fusion >= 2.5f ? rawHandle - 1 : rawHandle;
        sound.Checksum = reader.ReadInt32();
        sound.References = reader.ReadUInt32();
        int decompressedSize = reader.ReadInt32();
        sound.Flags = reader.ReadUInt32();
        sound.Frequency = reader.ReadInt32();
        int nameLength = reader.ReadInt32();

        bool playFromDisk = BitFlag.IsSet(sound.Flags, 5);
        byte[] payload;
        if (!playFromDisk)
        {
            int compressedSize = reader.ReadInt32();
            payload = Compression.DecompressBlock(reader.ReadBytesExact(compressedSize));
        }
        else
        {
            payload = reader.ReadBytesExact(decompressedSize);
        }

        using MemoryStream ms = new(payload, writable: false);
        using BinaryReader soundReader = new(ms);
        sound.Name = ReadUniversalStop(soundReader, nameLength);
        if (playFromDisk)
            soundReader.BaseStream.Position = 0;
        sound.Data = soundReader.ReadBytesExact(checked((int)(soundReader.BaseStream.Length - soundReader.BaseStream.Position)));

        return sound;
    }

    private void ReadShaderBankChunk(BinaryReader reader, int size, short flag, string folderName)
    {
        using BinaryReader chunk = OpenChunkReader(reader, size, flag);
        if (!chunk.HasBytes(4))
            return;

        int count = chunk.ReadInt32();
        if (count < 0 || count > 10000)
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
    }

    private ShaderAsset ReadShader(BinaryReader reader, int index)
    {
        long start = reader.BaseStream.Position;
        int nameOffset = reader.ReadInt32();
        int fxDataOffset = reader.ReadInt32();
        int parameterOffset = reader.ReadInt32();
        _ = reader.ReadInt32(); // options offset
        int fxDataSize = reader.ReadInt32();

        ShaderAsset shader = new() { Handle = index };
        if (_build >= 296 && Math.Abs(_fusion - 2.5f) < 0.01f)
        {
            shader.Name = $"Shader_{index}.fx";
        }
        else if (nameOffset != 0)
        {
            reader.BaseStream.Position = start + nameOffset;
            shader.Name = BinaryText.ReadAscii(reader);
        }

        if (fxDataOffset != 0)
        {
            reader.BaseStream.Position = start + fxDataOffset;
            string header = BinaryText.ReadAscii(reader, 4);
            shader.Compiled = header == "DXBC";
            reader.BaseStream.Seek(-4, SeekOrigin.Current);
            if (shader.Compiled)
                shader.FxData = reader.ReadBytesExact(Math.Max(0, fxDataSize - 1));
            else
                shader.FxData = Encoding.ASCII.GetBytes(BinaryText.ReadAscii(reader));
        }

        if (parameterOffset != 0)
        {
            reader.BaseStream.Position = start + parameterOffset;
            int paramCount = reader.ReadInt32();
            if (paramCount is > 0 and < 1024)
            {
                int typeOffset = reader.ReadInt32();
                int nameOffset2 = reader.ReadInt32();
                byte[] types = new byte[paramCount];

                reader.BaseStream.Position = start + parameterOffset + typeOffset;
                for (int i = 0; i < paramCount; i++)
                    types[i] = reader.ReadByte();

                reader.BaseStream.Position = start + parameterOffset + nameOffset2;
                for (int i = 0; i < paramCount; i++)
                    shader.Parameters.Add(new ShaderParameter(types[i], BinaryText.ReadAscii(reader)));
            }
        }

        return shader;
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
            long start = reader.BaseStream.Position;
            _ = reader.ReadInt32();
            int compressedSize = reader.ReadInt32();
            byte[] compressed = reader.ReadBytesExact(compressedSize);
            reader.BaseStream.Position = start + size;
            return Compression.DecompressBlock(compressed);
        }

        throw new NotSupportedException($"Chunk flag {flag} is not supported by the lite dumper.");
    }

    private string ReadUniversal(BinaryReader reader, int length = -1)
    {
        if (_unicode == null && (reader.BaseStream.Length - reader.BaseStream.Position > 1 || length > 1))
        {
            long pos = reader.BaseStream.Position;
            reader.BaseStream.Seek(1, SeekOrigin.Current);
            _unicode = reader.ReadByte() == 0;
            reader.BaseStream.Position = pos;
        }

        return _unicode == true
            ? BinaryText.ReadUtf16(reader, length)
            : BinaryText.ReadAscii(reader, length);
    }

    private string ReadUniversalStop(BinaryReader reader, int length)
    {
        if (_unicode == null && (reader.BaseStream.Length - reader.BaseStream.Position > 2 || length > 2))
        {
            long pos = reader.BaseStream.Position;
            reader.BaseStream.Seek(1, SeekOrigin.Current);
            _unicode = reader.ReadByte() == 0 && reader.ReadByte() != 0;
            reader.BaseStream.Position = pos;
        }

        return _unicode == true
            ? BinaryText.ReadUtf16Stop(reader, length)
            : BinaryText.ReadAsciiStop(reader, length);
    }

    private string UniquePath(string path)
    {
        path = Path.GetFullPath(path);
        if (_usedPaths.Add(path) && !File.Exists(path))
            return path;

        string dir = Path.GetDirectoryName(path)!;
        string name = Path.GetFileNameWithoutExtension(path);
        string ext = Path.GetExtension(path);
        for (int i = 1; ; i++)
        {
            string candidate = Path.Combine(dir, $"{name}_{i}{ext}");
            if (_usedPaths.Add(candidate) && !File.Exists(candidate))
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

    private static byte[] Normal24BitMaskedToBgra(LiteImage image, TranslatorContext context)
    {
        byte[] output = new byte[checked(image.Width * image.Height * 4)];
        int stride = image.Width * 4;
        int pad = GetPadding(image, context);
        int position = 0;
        int command = image.ImageData[position];
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
                    r = image.ImageData[position++];
                    g = image.ImageData[position++];
                    b = image.ImageData[position++];
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
                    command = image.ImageData[position++];
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

            position += pad * 3;
        }

        if (image.Flag(ImageFlags.Alpha))
        {
            int alphaPad = GetAlphaPadding(image);
            for (int y = 0; y < image.Height; y++)
            {
                for (int x = 0; x < image.Width; x++)
                    output[y * stride + x * 4 + 3] = image.ImageData[position++];
                position += alphaPad;
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
        int command = image.ImageData[position];
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
                    ushort value = (ushort)(image.ImageData[position++] | image.ImageData[position++] << 8);
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
                    command = image.ImageData[position++];
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

            position += pad * 2;
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
        int command = image.ImageData[position];
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
                    ushort value = (ushort)(image.ImageData[position++] | image.ImageData[position++] << 8);
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
                    command = image.ImageData[position++];
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

            position += pad;
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
                else if (image.ImageData[newPos + 2] == image.TransparentColor.R &&
                         image.ImageData[newPos + 1] == image.TransparentColor.G &&
                         image.ImageData[newPos + 0] == image.TransparentColor.B)
                {
                    output[newPos + 3] = 0;
                }

                position += 4;
            }

            position += pad * 4;
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
                output[y * stride + x * 4 + 3] = image.ImageData[position++];
            position += alphaPad;
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
        if (Data.Length >= 4)
        {
            string header = Encoding.ASCII.GetString(Data, 0, 4);
            return header switch
            {
                "RIFF" => "wav",
                "AIFF" => "aiff",
                "OggS" => "ogg",
                _ => "mod"
            };
        }

        return "bin";
    }
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
        if (percent == _lastPercent || percent % 10 != 0 && percent != 100)
            return;

        _lastPercent = percent;
        long ram = Process.GetCurrentProcess().WorkingSet64 / 1024 / 1024;
        Console.WriteLine($"{_label}: {percent}% ({value}/{_total}), RAM {ram} MB");
    }

    public void Done() => Step(_total);
}

internal sealed class WindowedReadStream : Stream
{
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
    public static byte[] DecompressBlock(byte[] data)
    {
        using MemoryStream input = new(data, writable: false);
        using Stream stream = IsZlib(data)
            ? new ZLibStream(input, CompressionMode.Decompress)
            : new DeflateStream(input, CompressionMode.Decompress);
        using MemoryStream output = new();
        stream.CopyTo(output);
        return output.ToArray();
    }

    private static bool IsZlib(byte[] data)
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

    public static byte[] ReadBytesExact(this BinaryReader reader, int count)
    {
        byte[] data = reader.ReadBytes(count);
        if (data.Length != count)
            throw new EndOfStreamException($"Needed {count} bytes, got {data.Length}.");
        return data;
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
    public static bool IsSet(byte value, int bit) => (value & (1 << bit)) != 0;
    public static bool IsSet(uint value, int bit) => (value & (1u << bit)) != 0;
}

internal static class Sanitizer
{
    private static readonly char[] InvalidFileNameChars = Path.GetInvalidFileNameChars();
    private static readonly char[] InvalidPathChars = Path.GetInvalidPathChars();

    public static string FileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "unnamed";

        string cleaned = new(name.Select(ch => InvalidFileNameChars.Contains(ch) || char.IsControl(ch) ? '_' : ch).ToArray());
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
