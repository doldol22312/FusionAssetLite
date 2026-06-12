using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using NUnit.Framework;

namespace FusionAssetLite.Tests;

[TestFixture]
public sealed class FnafGoldenDumpTests
{
    private const string GameRootEnvironmentVariable = "FUSION_ASSET_LITE_FNAF_ROOT";
    private const string OutputRootEnvironmentVariable = "FUSION_ASSET_LITE_TEST_OUTPUT";
    private const string KeepOutputEnvironmentVariable = "FUSION_ASSET_LITE_TEST_KEEP_OUTPUT";
    private const string ParallelismEnvironmentVariable = "FUSION_ASSET_LITE_TEST_PARALLELISM";
    private const string RegenerateReferenceEnvironmentVariable = "FUSION_ASSET_LITE_REGENERATE_REFERENCE";
    private const int DefaultParallelism = 9;

    private static readonly string DefaultGameRoot = Path.Combine(
        "D:\\",
        "toorent",
        "Five Nights at Freddy\u2019s");

    [Test]
    public async Task DumpsMatchNebulaReference()
    {
        SourceReference[] games = LoadSourceReferences().Values.OrderBy(x => x.Game, StringComparer.Ordinal).ToArray();
        string gameRoot = Environment.GetEnvironmentVariable(GameRootEnvironmentVariable) ?? DefaultGameRoot;

        string[] missingInputs = games
            .Select(game => Path.GetFullPath(Path.Combine(gameRoot, game.RelativePath)))
            .Where(path => !File.Exists(path))
            .ToArray();
        if (missingInputs.Length > 0)
        {
            Assert.Ignore(
                $"Missing {missingInputs.Length} test input(s). Set {GameRootEnvironmentVariable} to the folder that contains the FNaF game directories."
                + Environment.NewLine
                + string.Join(Environment.NewLine, missingInputs));
        }

        if (ShouldRegenerateReference())
        {
            await RegenerateNebulaReferenceAsync(games, gameRoot);
            Assert.Pass($"Regenerated {ReferenceSourcePath("nebulafd_sha256.csv")}.");
        }

        Dictionary<string, Dictionary<string, int>> expectedByGame = LoadNebulaFingerprints();
        int parallelism = GetParallelism(games.Length);
        TestContext.Out.WriteLine($"Running {games.Length} golden dump(s) with parallelism {parallelism}.");

        using SemaphoreSlim semaphore = new(parallelism);
        GameResult[] results = await Task.WhenAll(games.Select(async game =>
        {
            await semaphore.WaitAsync(TestContext.CurrentContext.CancellationToken);
            try
            {
                string inputPath = Path.GetFullPath(Path.Combine(gameRoot, game.RelativePath));
                return await DumpAndVerifyAsync(inputPath, game, expectedByGame[game.Game]);
            }
            finally
            {
                semaphore.Release();
            }
        }));

        foreach (GameResult result in results.OrderBy(x => x.Game, StringComparer.Ordinal))
        {
            TestContext.Out.WriteLine(
                $"{result.Game}: {(result.Success ? "passed" : "failed")}, {result.AssetCount} file(s), {result.Elapsed.TotalSeconds:F2}s, {result.OutputRoot}");
        }

        string[] failures = results.Where(x => !x.Success).Select(x => x.Failure).ToArray();
        Assert.That(failures, Is.Empty, string.Join(Environment.NewLine + Environment.NewLine, failures));
    }

    private static async Task RegenerateNebulaReferenceAsync(SourceReference[] games, string gameRoot)
    {
        int parallelism = GetParallelism(games.Length);
        TestContext.Out.WriteLine($"Regenerating reference CSV with {games.Length} dump(s), parallelism {parallelism}.");

        using SemaphoreSlim semaphore = new(parallelism);
        GameReferenceRows[] results = await Task.WhenAll(games.Select(async game =>
        {
            await semaphore.WaitAsync(TestContext.CurrentContext.CancellationToken);
            try
            {
                string inputPath = Path.GetFullPath(Path.Combine(gameRoot, game.RelativePath));
                return await DumpReferenceRowsAsync(inputPath, game);
            }
            finally
            {
                semaphore.Release();
            }
        }));

        string[] failures = results.Where(x => x.Failure.Length > 0).Select(x => x.Failure).ToArray();
        Assert.That(failures, Is.Empty, string.Join(Environment.NewLine + Environment.NewLine, failures));

        string referencePath = ReferenceSourcePath("nebulafd_sha256.csv");
        using StreamWriter writer = new(referencePath, append: false, Encoding.UTF8);
        writer.WriteLine("\"RelativePath\",\"Length\",\"SHA256\",\"Width\",\"Height\",\"RGBA_SHA256\"");
        foreach (ReferenceRow row in results
            .SelectMany(x => x.Rows)
            .OrderBy(x => x.RelativePath, StringComparer.OrdinalIgnoreCase))
        {
            writer.WriteLine(string.Join(",", [
                CsvField(row.RelativePath),
                CsvField(row.Length),
                CsvField(row.Sha256),
                CsvField(row.Width),
                CsvField(row.Height),
                CsvField(row.RgbaSha256)
            ]));
        }
    }

    private static async Task<GameReferenceRows> DumpReferenceRowsAsync(string inputPath, SourceReference game)
    {
        string outputRoot = CreateOutputRoot(game.Game);
        try
        {
            VerifySourceFile(inputPath, game);

            ProcessResult process = await RunDumperProcessAsync(inputPath, outputRoot);
            if (process.ExitCode != 0)
                return GameReferenceRows.Failed(game.Game, $"{game.Game}: dumper exited with {process.ExitCode}.{Environment.NewLine}{process.Output}");

            string[] dumpRoots = Directory.GetDirectories(outputRoot);
            if (dumpRoots.Length != 1)
                return GameReferenceRows.Failed(game.Game, $"{game.Game}: expected one dump root under {outputRoot}, found {dumpRoots.Length}.");

            string dumpRoot = dumpRoots[0];
            string dumpName = Path.GetFileName(dumpRoot);
            List<ReferenceRow> rows = [];
            foreach (string file in Directory.EnumerateFiles(dumpRoot, "*", SearchOption.AllDirectories))
            {
                string relativeToDump = Path.GetRelativePath(dumpRoot, file);
                string category = FindCategory(relativeToDump);
                if (category.Length == 0)
                    continue;

                FileInfo info = new(file);
                string relativePath = Path.Combine(game.Game, dumpName, relativeToDump);
                if (category == "Images")
                {
                    PngFingerprint png = PngValidator.ValidateAndHashRgba(file);
                    rows.Add(new ReferenceRow(relativePath, info.Length.ToString(), Sha256File(file), png.Width.ToString(), png.Height.ToString(), png.RgbaSha256));
                }
                else
                {
                    rows.Add(new ReferenceRow(relativePath, info.Length.ToString(), Sha256File(file), string.Empty, string.Empty, string.Empty));
                }
            }

            if (!KeepTestOutput())
                TryDelete(outputRoot);

            return GameReferenceRows.Passed(game.Game, rows);
        }
        catch (Exception ex)
        {
            return GameReferenceRows.Failed(game.Game, $"{game.Game}: {ex}");
        }
    }

    private static async Task<GameResult> DumpAndVerifyAsync(string inputPath, SourceReference game, Dictionary<string, int> expected)
    {
        string outputRoot = CreateOutputRoot(game.Game);
        Stopwatch sw = Stopwatch.StartNew();

        try
        {
            VerifySourceFile(inputPath, game);

            ProcessResult process = await RunDumperProcessAsync(inputPath, outputRoot);
            if (process.ExitCode != 0)
            {
                return GameResult.Failed(
                    game.Game,
                    outputRoot,
                    sw.Elapsed,
                    $"{game.Game}: dumper exited with {process.ExitCode}.{Environment.NewLine}{process.Output}");
            }

            Dictionary<string, int> actual = BuildActualFingerprints(outputRoot);
            string diff = FormatFingerprintDiff(expected, actual);
            if (diff.Length > 0)
            {
                return GameResult.Failed(
                    game.Game,
                    outputRoot,
                    sw.Elapsed,
                    $"{game.Game}: checksum comparison failed.{Environment.NewLine}{diff}");
            }

            if (!KeepTestOutput())
                TryDelete(outputRoot);

            return GameResult.Passed(game.Game, outputRoot, sw.Elapsed, actual.Values.Sum());
        }
        catch (Exception ex)
        {
            return GameResult.Failed(game.Game, outputRoot, sw.Elapsed, $"{game.Game}: {ex}");
        }
    }

    private static async Task<ProcessResult> RunDumperProcessAsync(string inputPath, string outputRoot)
    {
        string exePath = Path.Combine(TestContext.CurrentContext.TestDirectory, "FusionAssetLite.exe");
        if (!File.Exists(exePath))
            throw new FileNotFoundException("FusionAssetLite.exe was not copied to the test output directory.", exePath);

        ProcessStartInfo startInfo = new()
        {
            FileName = exePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(inputPath);
        startInfo.ArgumentList.Add(outputRoot);

        StringBuilder output = new();
        using Process process = new() { StartInfo = startInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data != null)
                output.AppendLine(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null)
                output.AppendLine(e.Data);
        };

        if (!process.Start())
            throw new InvalidOperationException("Failed to start FusionAssetLite test process.");

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync(TestContext.CurrentContext.CancellationToken);
        return new ProcessResult(process.ExitCode, output.ToString());
    }

    private static int GetParallelism(int gameCount)
    {
        string? value = Environment.GetEnvironmentVariable(ParallelismEnvironmentVariable);
        if (int.TryParse(value, out int requested) && requested > 0)
            return Math.Min(requested, gameCount);

        return Math.Min(DefaultParallelism, gameCount);
    }

    private static void VerifySourceFile(string inputPath, SourceReference expected)
    {
        FileInfo file = new(inputPath);
        Assert.That(file.Length, Is.EqualTo(expected.Length), $"Input file size differs for {expected.Game}: {inputPath}");

        string actualSha256 = Sha256File(inputPath);
        Assert.That(actualSha256, Is.EqualTo(expected.Sha256), $"Input file SHA-256 differs for {expected.Game}: {inputPath}");
    }

    private static Dictionary<string, Dictionary<string, int>> LoadNebulaFingerprints()
    {
        string referencePath = ReferencePath("nebulafd_sha256.csv");
        Dictionary<string, Dictionary<string, int>> byGame = new(StringComparer.OrdinalIgnoreCase);

        foreach (string[] row in ReadCsv(referencePath).Skip(1))
        {
            if (row.Length < 3)
                continue;

            string[] parts = SplitRelativePath(row[0]);
            if (parts.Length < 4)
                throw new InvalidDataException($"Unexpected NebulaFD reference path: {row[0]}");

            string game = parts[0];
            string category = parts[2];
            string fingerprint = category == "Images"
                ? ImageFingerprint(category, row)
                : Fingerprint(category, row[1], row[2]);
            Increment(GetOrAdd(byGame, game), fingerprint);
        }

        return byGame;
    }

    private static Dictionary<string, SourceReference> LoadSourceReferences()
    {
        string referencePath = ReferencePath("source_exe_sha256.csv");
        Dictionary<string, SourceReference> references = new(StringComparer.OrdinalIgnoreCase);

        foreach (string[] row in ReadCsv(referencePath).Skip(1))
        {
            if (row.Length < 4)
                continue;

            references[row[0]] = new SourceReference(row[0], row[1], long.Parse(row[2]), row[3]);
        }

        return references;
    }

    private static Dictionary<string, int> BuildActualFingerprints(string outputRoot)
    {
        Dictionary<string, int> fingerprints = new(StringComparer.OrdinalIgnoreCase);
        foreach (string file in Directory.EnumerateFiles(outputRoot, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(outputRoot, file);
            string category = FindCategory(relative);
            if (category.Length == 0)
                continue;

            FileInfo info = new(file);
            string fingerprint;
            if (category == "Images")
            {
                PngFingerprint png = PngValidator.ValidateAndHashRgba(file);
                fingerprint = ImageFingerprint(category, png.Width.ToString(), png.Height.ToString(), png.RgbaSha256);
            }
            else
            {
                fingerprint = Fingerprint(category, info.Length.ToString(), Sha256File(file));
            }

            Increment(fingerprints, fingerprint);
        }

        return fingerprints;
    }

    private static string FindCategory(string relativePath)
    {
        foreach (string part in SplitRelativePath(relativePath))
        {
            if (part is "Images" or "Sounds" or "Packed Data" or "Shaders")
                return part;
        }

        return string.Empty;
    }

    private static string FormatFingerprintDiff(Dictionary<string, int> expected, Dictionary<string, int> actual)
    {
        List<string> lines = [];
        foreach (string key in expected.Keys.Concat(actual.Keys).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            expected.TryGetValue(key, out int expectedCount);
            actual.TryGetValue(key, out int actualCount);
            if (expectedCount == actualCount)
                continue;

            lines.Add($"{key}: expected {expectedCount}, actual {actualCount}");
            if (lines.Count >= 50)
            {
                lines.Add("Diff truncated after 50 fingerprints.");
                break;
            }
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string CreateOutputRoot(string game)
    {
        string baseRoot = Environment.GetEnvironmentVariable(OutputRootEnvironmentVariable)
            ?? Path.Combine(Path.GetTempPath(), "FusionAssetLite.Tests");
        string path = Path.Combine(baseRoot, $"{SanitizeForPath(game)}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static bool KeepTestOutput() =>
        string.Equals(Environment.GetEnvironmentVariable(KeepOutputEnvironmentVariable), "1", StringComparison.OrdinalIgnoreCase)
        || string.Equals(Environment.GetEnvironmentVariable(KeepOutputEnvironmentVariable), "true", StringComparison.OrdinalIgnoreCase);

    private static bool ShouldRegenerateReference() =>
        string.Equals(Environment.GetEnvironmentVariable(RegenerateReferenceEnvironmentVariable), "1", StringComparison.OrdinalIgnoreCase)
        || string.Equals(Environment.GetEnvironmentVariable(RegenerateReferenceEnvironmentVariable), "true", StringComparison.OrdinalIgnoreCase);

    private static string SanitizeForPath(string value)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        return new string(value.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());
    }

    private static string CsvField(string value) =>
        "\"" + value.Replace("\"", "\"\"") + "\"";

    private static string Fingerprint(string category, string length, string sha256) =>
        $"{category}|{length}|{sha256.ToUpperInvariant()}";

    private static string ImageFingerprint(string category, string[] row)
    {
        if (row.Length < 6 || string.IsNullOrWhiteSpace(row[3]) || string.IsNullOrWhiteSpace(row[4]) || string.IsNullOrWhiteSpace(row[5]))
            throw new InvalidDataException("Image golden rows must include Width, Height, and RGBA_SHA256 columns. Regenerate nebulafd_sha256.csv before changing PNG compression.");

        return ImageFingerprint(category, row[3], row[4], row[5]);
    }

    private static string ImageFingerprint(string category, string width, string height, string rgbaSha256) =>
        $"{category}|{width}x{height}|{rgbaSha256.ToUpperInvariant()}";

    private static string Sha256File(string path)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static Dictionary<string, int> GetOrAdd(Dictionary<string, Dictionary<string, int>> map, string key)
    {
        if (!map.TryGetValue(key, out Dictionary<string, int>? value))
        {
            value = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            map.Add(key, value);
        }

        return value;
    }

    private static void Increment(Dictionary<string, int> map, string key)
    {
        map.TryGetValue(key, out int count);
        map[key] = count + 1;
    }

    private static string[] SplitRelativePath(string path) =>
        path.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);

    private static string ReferencePath(string fileName)
    {
        string path = Path.Combine(TestContext.CurrentContext.TestDirectory, "Reference", fileName);
        if (File.Exists(path))
            return path;

        path = Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", "Reference", fileName));
        if (File.Exists(path))
            return path;

        throw new FileNotFoundException("Missing golden reference file.", fileName);
    }

    private static string ReferenceSourcePath(string fileName)
    {
        string path = Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", "Reference", fileName));
        if (File.Exists(path))
            return path;

        path = Path.Combine(TestContext.CurrentContext.TestDirectory, "Reference", fileName);
        if (File.Exists(path))
            return path;

        throw new FileNotFoundException("Missing golden reference file.", fileName);
    }

    private static IEnumerable<string[]> ReadCsv(string path)
    {
        foreach (string line in File.ReadLines(path))
            yield return ParseCsvLine(line).ToArray();
    }

    private static List<string> ParseCsvLine(string line)
    {
        List<string> fields = [];
        string current = string.Empty;
        bool quoted = false;

        for (int i = 0; i < line.Length; i++)
        {
            char ch = line[i];
            if (ch == '"')
            {
                if (quoted && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current += '"';
                    i++;
                }
                else
                {
                    quoted = !quoted;
                }
            }
            else if (ch == ',' && !quoted)
            {
                fields.Add(current);
                current = string.Empty;
            }
            else
            {
                current += ch;
            }
        }

        fields.Add(current);
        return fields;
    }

    private static void TryDelete(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public sealed record SourceReference(string Game, string RelativePath, long Length, string Sha256);
    private sealed record ProcessResult(int ExitCode, string Output);
    private sealed record PngFingerprint(int Width, int Height, string RgbaSha256);
    private sealed record ReferenceRow(string RelativePath, string Length, string Sha256, string Width, string Height, string RgbaSha256);
    private sealed record GameReferenceRows(string Game, IReadOnlyList<ReferenceRow> Rows, string Failure)
    {
        public static GameReferenceRows Passed(string game, IReadOnlyList<ReferenceRow> rows) =>
            new(game, rows, string.Empty);

        public static GameReferenceRows Failed(string game, string failure) =>
            new(game, [], failure);
    }

    private sealed record GameResult(string Game, bool Success, string OutputRoot, TimeSpan Elapsed, int AssetCount, string Failure)
    {
        public static GameResult Passed(string game, string outputRoot, TimeSpan elapsed, int assetCount) =>
            new(game, true, outputRoot, elapsed, assetCount, string.Empty);

        public static GameResult Failed(string game, string outputRoot, TimeSpan elapsed, string failure) =>
            new(game, false, outputRoot, elapsed, 0, failure);
    }

    private static class PngValidator
    {
        private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];
        private static readonly uint[] CrcTable = BuildCrcTable();

        public static PngFingerprint ValidateAndHashRgba(string path)
        {
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
            Span<byte> signature = stackalloc byte[8];
            stream.ReadExactly(signature);
            if (!signature.SequenceEqual(Signature))
                throw new InvalidDataException($"PNG signature is invalid: {path}");

            int width = 0;
            int height = 0;
            int bytesPerPixel = 0;
            bool sawIhdr = false;
            bool sawIend = false;
            using MemoryStream idat = new();
            Span<byte> header = stackalloc byte[8];
            Span<byte> crcBytes = stackalloc byte[4];
            while (!sawIend)
            {
                stream.ReadExactly(header);
                int length = checked((int)BinaryPrimitives.ReadUInt32BigEndian(header[0..4]));
                byte[] data = new byte[length];
                stream.ReadExactly(data);
                stream.ReadExactly(crcBytes);
                uint expectedCrc = BinaryPrimitives.ReadUInt32BigEndian(crcBytes);
                uint actualCrc = ComputeCrc(header[4..8], data);
                if (actualCrc != expectedCrc)
                    throw new InvalidDataException($"PNG chunk CRC mismatch in {path}.");

                string type = Encoding.ASCII.GetString(header[4..8]);
                switch (type)
                {
                    case "IHDR":
                        if (sawIhdr || length != 13)
                            throw new InvalidDataException($"PNG IHDR is invalid: {path}");

                        width = checked((int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(0, 4)));
                        height = checked((int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(4, 4)));
                        if (width <= 0 || height <= 0)
                            throw new InvalidDataException($"PNG dimensions are invalid: {path}");
                        if (data[8] != 8 || data[10] != 0 || data[11] != 0 || data[12] != 0)
                            throw new InvalidDataException($"PNG is not non-interlaced 8-bit RGB/RGBA: {path}");
                        bytesPerPixel = data[9] switch
                        {
                            2 => 3,
                            6 => 4,
                            _ => throw new InvalidDataException($"PNG color type {data[9]} is not RGB/RGBA: {path}")
                        };

                        sawIhdr = true;
                        break;
                    case "IDAT":
                        if (!sawIhdr)
                            throw new InvalidDataException($"PNG IDAT appears before IHDR: {path}");
                        idat.Write(data);
                        break;
                    case "IEND":
                        sawIend = true;
                        break;
                }
            }

            if (!sawIhdr || idat.Length == 0)
                throw new InvalidDataException($"PNG is missing IHDR or IDAT data: {path}");

            idat.Position = 0;
            return ValidateImageData(idat, width, height, bytesPerPixel, path);
        }

        private static PngFingerprint ValidateImageData(Stream compressed, int width, int height, int bytesPerPixel, string path)
        {
            int rowBytes = checked(width * bytesPerPixel);
            int expectedBytes = checked((rowBytes + 1) * height);
            byte[] filtered = new byte[expectedBytes];
            using ZLibStream zlib = new(compressed, CompressionMode.Decompress, leaveOpen: true);
            zlib.ReadExactly(filtered);
            if (zlib.ReadByte() != -1)
                throw new InvalidDataException($"PNG IDAT has trailing decompressed data: {path}");

            byte[] previous = new byte[rowBytes];
            byte[] current = new byte[rowBytes];
            byte[]? rgbaRow = bytesPerPixel == 3 ? new byte[checked(width * 4)] : null;
            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            int source = 0;
            for (int y = 0; y < height; y++)
            {
                int filter = filtered[source++];
                ReadOnlySpan<byte> encoded = filtered.AsSpan(source, rowBytes);
                source += rowBytes;
                DecodeRow(filter, encoded, previous, current, bytesPerPixel, path);
                AppendRgbaRowHash(hash, current, rgbaRow, bytesPerPixel);
                (previous, current) = (current, previous);
            }

            return new PngFingerprint(width, height, Convert.ToHexString(hash.GetHashAndReset()));
        }

        private static void AppendRgbaRowHash(IncrementalHash hash, byte[] row, byte[]? rgbaRow, int bytesPerPixel)
        {
            if (bytesPerPixel == 4)
            {
                hash.AppendData(row);
                return;
            }

            if (rgbaRow == null)
                throw new ArgumentNullException(nameof(rgbaRow));

            int source = 0;
            int dest = 0;
            while (source < row.Length)
            {
                rgbaRow[dest++] = row[source++];
                rgbaRow[dest++] = row[source++];
                rgbaRow[dest++] = row[source++];
                rgbaRow[dest++] = 255;
            }

            hash.AppendData(rgbaRow);
        }

        private static void DecodeRow(int filter, ReadOnlySpan<byte> encoded, byte[] previous, byte[] current, int bytesPerPixel, string path)
        {
            for (int i = 0; i < encoded.Length; i++)
            {
                int left = i >= bytesPerPixel ? current[i - bytesPerPixel] : 0;
                int up = previous[i];
                int upperLeft = i >= bytesPerPixel ? previous[i - bytesPerPixel] : 0;
                int predictor = filter switch
                {
                    0 => 0,
                    1 => left,
                    2 => up,
                    3 => (left + up) >> 1,
                    4 => Paeth(left, up, upperLeft),
                    _ => throw new InvalidDataException($"PNG row filter {filter} is invalid: {path}")
                };

                current[i] = unchecked((byte)(encoded[i] + predictor));
            }
        }

        private static int Paeth(int left, int up, int upperLeft)
        {
            int p = left + up - upperLeft;
            int pa = Math.Abs(p - left);
            int pb = Math.Abs(p - up);
            int pc = Math.Abs(p - upperLeft);
            if (pa <= pb && pa <= pc)
                return left;
            return pb <= pc ? up : upperLeft;
        }

        private static uint ComputeCrc(ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
        {
            uint crc = 0xFFFFFFFF;
            crc = UpdateCrc(crc, type);
            crc = UpdateCrc(crc, data);
            return ~crc;
        }

        private static uint UpdateCrc(uint crc, ReadOnlySpan<byte> data)
        {
            foreach (byte value in data)
                crc = CrcTable[(crc ^ value) & 0xFF] ^ (crc >> 8);
            return crc;
        }

        private static uint[] BuildCrcTable()
        {
            uint[] table = new uint[256];
            for (uint n = 0; n < table.Length; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++)
                    c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
                table[n] = c;
            }

            return table;
        }
    }
}
