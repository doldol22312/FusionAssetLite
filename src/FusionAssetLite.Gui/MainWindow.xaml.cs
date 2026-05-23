using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;

namespace FusionAssetLite.Gui;

public partial class MainWindow : Window
{
    private static readonly Regex ProgressLineRegex = new(
        @"^(?<stage>Images|Sounds): (?<percent>\d+)% \((?<done>\d+)/(?<total>\d+)\), RAM (?<ram>\d+) MB$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex SummaryLineRegex = new(
        @"^(?<stage>Images|Sounds|Packed data|Shader files): (?<written>\d+) written, (?<failed>\d+) failed$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private Process? _runningProcess;
    private bool _cancelRequested;
    private string? _lastOutputPath;
    private bool _outputWasAutoFilled;
    private bool _settingAutoOutput;

    public MainWindow()
    {
        InitializeComponent();
    }

    private void BrowseInput_Click(object sender, RoutedEventArgs e)
    {
        OpenFileDialog dialog = new()
        {
            Title = "Select Fusion game package",
            Filter = "Fusion packages (*.exe;*.ccn)|*.exe;*.ccn|Executable files (*.exe)|*.exe|CCN files (*.ccn)|*.ccn|All files (*.*)|*.*",
            CheckFileExists = true
        };

        if (dialog.ShowDialog(this) != true)
            return;

        InputPathTextBox.Text = dialog.FileName;
        AutoFillOutputPath(dialog.FileName);
    }

    private void BrowseOutput_Click(object sender, RoutedEventArgs e)
    {
        OpenFolderDialog dialog = new()
        {
            Title = "Select output folder",
            Multiselect = false
        };

        if (!string.IsNullOrWhiteSpace(OutputPathTextBox.Text))
            dialog.InitialDirectory = OutputPathTextBox.Text;
        else if (File.Exists(InputPathTextBox.Text))
            dialog.InitialDirectory = Path.GetDirectoryName(InputPathTextBox.Text);

        if (dialog.ShowDialog(this) != true)
            return;

        OutputPathTextBox.Text = dialog.FolderName;
        _outputWasAutoFilled = false;
    }

    private async void Extract_Click(object sender, RoutedEventArgs e)
    {
        if (_runningProcess != null)
            return;

        string inputPath = InputPathTextBox.Text.Trim();
        if (!File.Exists(inputPath))
        {
            SetStatus("Input missing", "#DC2626");
            MessageBox.Show(this, "Select an existing .exe or .ccn file.", "FusionAssetLite", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        string outputPath = OutputPathTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            outputPath = GetDefaultOutputPath(inputPath);
            SetAutoOutputPath(outputPath);
        }

        string? cliPath = FindCliExecutable();
        if (cliPath == null)
        {
            SetStatus("CLI missing", "#DC2626");
            MessageBox.Show(this, "Build FusionAssetLite before running the GUI.", "FusionAssetLite", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        ResetRunState(outputPath);
        SetRunning(true);

        try
        {
            ProcessStartInfo startInfo = CreateStartInfo(cliPath, inputPath, outputPath);
            Process process = new() { StartInfo = startInfo, EnableRaisingEvents = true };
            _runningProcess = process;

            process.OutputDataReceived += (_, args) => DispatchLine(args.Data);
            process.ErrorDataReceived += (_, args) => DispatchLine(args.Data);

            if (!process.Start())
                throw new InvalidOperationException("The extractor process did not start.");

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            await process.WaitForExitAsync();

            int exitCode = process.ExitCode;
            if (_cancelRequested)
            {
                StageTextBlock.Text = "Cancelled";
                RunDetailTextBlock.Text = "The extractor process was stopped.";
                ExtractionProgressBar.IsIndeterminate = false;
                SetStatus("Cancelled", "#B45309");
            }
            else if (exitCode == 0)
            {
                StageTextBlock.Text = "Complete";
                RunDetailTextBlock.Text = "Extraction finished.";
                ExtractionProgressBar.IsIndeterminate = false;
                ExtractionProgressBar.Value = 100;
                SetStatus("Complete", "#0E7C66");
            }
            else
            {
                StageTextBlock.Text = "Failed";
                RunDetailTextBlock.Text = $"Extractor exited with code {exitCode}.";
                ExtractionProgressBar.IsIndeterminate = false;
                SetStatus("Failed", "#DC2626");
            }
        }
        catch (Exception ex)
        {
            AppendLog("GUI: " + ex.Message);
            StageTextBlock.Text = "Failed";
            RunDetailTextBlock.Text = ex.Message;
            ExtractionProgressBar.IsIndeterminate = false;
            SetStatus("Failed", "#DC2626");
        }
        finally
        {
            _runningProcess?.Dispose();
            _runningProcess = null;
            SetRunning(false);
            OpenOutputButton.IsEnabled = Directory.Exists(_lastOutputPath);
            CopyLogButton.IsEnabled = LogTextBox.Text.Length > 0;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (_runningProcess == null || _runningProcess.HasExited)
            return;

        _cancelRequested = true;
        AppendLog("GUI: cancellation requested.");

        try
        {
            _runningProcess.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
    }

    private void OpenOutput_Click(object sender, RoutedEventArgs e)
    {
        string? path = _lastOutputPath;
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            return;

        Process.Start(new ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true
        });
    }

    private void CopyLog_Click(object sender, RoutedEventArgs e)
    {
        if (LogTextBox.Text.Length == 0)
            return;

        Clipboard.SetText(LogTextBox.Text);
        SetStatus("Log copied", "#0E7C66");
    }

    private void InputPathTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (File.Exists(InputPathTextBox.Text) && (string.IsNullOrWhiteSpace(OutputPathTextBox.Text) || _outputWasAutoFilled))
            AutoFillOutputPath(InputPathTextBox.Text);
    }

    private void OutputPathTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (!_settingAutoOutput)
            _outputWasAutoFilled = false;
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = HasPackageDrop(e) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (!TryGetDroppedPackage(e, out string? path))
            return;

        InputPathTextBox.Text = path;
        AutoFillOutputPath(path);
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_runningProcess == null || _runningProcess.HasExited)
            return;

        _cancelRequested = true;
        try
        {
            _runningProcess.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
    }

    private ProcessStartInfo CreateStartInfo(string cliPath, string inputPath, string outputPath)
    {
        bool runDllWithDotnet = Path.GetExtension(cliPath).Equals(".dll", StringComparison.OrdinalIgnoreCase);
        ProcessStartInfo startInfo = new()
        {
            FileName = runDllWithDotnet ? "dotnet" : cliPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(cliPath) ?? AppContext.BaseDirectory
        };

        if (runDllWithDotnet)
            startInfo.ArgumentList.Add(cliPath);

        startInfo.ArgumentList.Add(inputPath);
        startInfo.ArgumentList.Add(outputPath);

        if (ImagesCheckBox.IsChecked != true)
            startInfo.ArgumentList.Add("--no-images");
        if (SoundsCheckBox.IsChecked != true)
            startInfo.ArgumentList.Add("--no-sounds");
        if (PackedDataCheckBox.IsChecked != true)
            startInfo.ArgumentList.Add("--no-pack");
        if (ShadersCheckBox.IsChecked != true)
            startInfo.ArgumentList.Add("--no-shaders");

        return startInfo;
    }

    private static string? FindCliExecutable()
    {
        string baseDirectory = AppContext.BaseDirectory;
        string configuration =
#if DEBUG
            "Debug";
#else
            "Release";
#endif

        string[] candidates =
        [
            Path.Combine(baseDirectory, "FusionAssetLite.exe"),
            Path.Combine(baseDirectory, "FusionAssetLite.dll"),
            Path.GetFullPath(Path.Combine(baseDirectory, "..", "..", "..", "..", "FusionAssetLite", "bin", configuration, "net8.0-windows", "FusionAssetLite.exe")),
            Path.GetFullPath(Path.Combine(baseDirectory, "..", "..", "..", "..", "FusionAssetLite", "bin", configuration, "net8.0-windows", "FusionAssetLite.dll"))
        ];

        return candidates.FirstOrDefault(File.Exists);
    }

    private static bool HasPackageDrop(DragEventArgs e) => TryGetDroppedPackage(e, out _);

    private static bool TryGetDroppedPackage(DragEventArgs e, [NotNullWhen(true)] out string? path)
    {
        path = null;
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
            return false;

        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files)
            return false;

        path = files.FirstOrDefault(file =>
            File.Exists(file) &&
            (Path.GetExtension(file).Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
             Path.GetExtension(file).Equals(".ccn", StringComparison.OrdinalIgnoreCase)));

        return path != null;
    }

    private static string GetDefaultOutputPath(string inputPath) =>
        Path.Combine(Path.GetDirectoryName(inputPath) ?? Environment.CurrentDirectory, "extracted_assets_lite");

    private void AutoFillOutputPath(string inputPath)
    {
        SetAutoOutputPath(GetDefaultOutputPath(inputPath));
    }

    private void SetAutoOutputPath(string outputPath)
    {
        _settingAutoOutput = true;
        OutputPathTextBox.Text = outputPath;
        _settingAutoOutput = false;
        _outputWasAutoFilled = true;
    }

    private void ResetRunState(string outputPath)
    {
        _cancelRequested = false;
        _lastOutputPath = outputPath;
        StageTextBlock.Text = "Starting";
        RunDetailTextBlock.Text = "Extractor process is launching.";
        OutputDetailTextBlock.Text = outputPath;
        LogTextBox.Clear();
        ImagesSummaryTextBlock.Text = "-";
        SoundsSummaryTextBlock.Text = "-";
        PackedSummaryTextBlock.Text = "-";
        ShadersSummaryTextBlock.Text = "-";
        ExtractionProgressBar.Value = 0;
        ExtractionProgressBar.IsIndeterminate = true;
        CopyLogButton.IsEnabled = false;
        OpenOutputButton.IsEnabled = false;
        SetStatus("Running", "#0E7C66");
    }

    private void SetRunning(bool running)
    {
        ExtractButton.IsEnabled = !running;
        CancelButton.IsEnabled = running;
        InputPathTextBox.IsEnabled = !running;
        OutputPathTextBox.IsEnabled = !running;
        ImagesCheckBox.IsEnabled = !running;
        SoundsCheckBox.IsEnabled = !running;
        PackedDataCheckBox.IsEnabled = !running;
        ShadersCheckBox.IsEnabled = !running;
    }

    private void DispatchLine(string? line)
    {
        if (line == null)
            return;

        Dispatcher.Invoke(() =>
        {
            AppendLog(line);
            ReadProcessLine(line);
        });
    }

    private void AppendLog(string line)
    {
        LogTextBox.AppendText(line + Environment.NewLine);
        LogTextBox.ScrollToEnd();
        CopyLogButton.IsEnabled = true;
    }

    private void ReadProcessLine(string line)
    {
        Match progressMatch = ProgressLineRegex.Match(line);
        if (progressMatch.Success)
        {
            string stage = progressMatch.Groups["stage"].Value;
            int percent = int.Parse(progressMatch.Groups["percent"].Value);
            string done = progressMatch.Groups["done"].Value;
            string total = progressMatch.Groups["total"].Value;
            string ram = progressMatch.Groups["ram"].Value;

            ExtractionProgressBar.IsIndeterminate = false;
            ExtractionProgressBar.Value = percent;
            StageTextBlock.Text = stage;
            RunDetailTextBlock.Text = $"{percent}% ({done}/{total}), RAM {ram} MB";
            return;
        }

        Match summaryMatch = SummaryLineRegex.Match(line);
        if (summaryMatch.Success)
        {
            string stage = summaryMatch.Groups["stage"].Value;
            string written = summaryMatch.Groups["written"].Value;
            string failed = summaryMatch.Groups["failed"].Value;
            string value = failed == "0" ? written : $"{written} / {failed} failed";

            switch (stage)
            {
                case "Images":
                    ImagesSummaryTextBlock.Text = value;
                    break;
                case "Sounds":
                    SoundsSummaryTextBlock.Text = value;
                    break;
                case "Packed data":
                    PackedSummaryTextBlock.Text = value;
                    break;
                case "Shader files":
                    ShadersSummaryTextBlock.Text = value;
                    break;
            }

            return;
        }

        if (line.StartsWith("Output: ", StringComparison.OrdinalIgnoreCase))
        {
            _lastOutputPath = line["Output: ".Length..].Trim();
            OutputDetailTextBlock.Text = _lastOutputPath;
            OpenOutputButton.IsEnabled = Directory.Exists(_lastOutputPath);
            return;
        }

        if (line.StartsWith("ImageBank:", StringComparison.OrdinalIgnoreCase))
            StageTextBlock.Text = "Images";
        else if (line.StartsWith("SoundBank:", StringComparison.OrdinalIgnoreCase))
            StageTextBlock.Text = "Sounds";
        else if (line.StartsWith("PackData:", StringComparison.OrdinalIgnoreCase))
            StageTextBlock.Text = "Packed Data";
        else if (line.StartsWith("Package:", StringComparison.OrdinalIgnoreCase))
            StageTextBlock.Text = "Package";
        else if (line.StartsWith("Extended header:", StringComparison.OrdinalIgnoreCase))
            RunDetailTextBlock.Text = line;
        else if (line == "Done.")
            StageTextBlock.Text = "Finalizing";
    }

    private void SetStatus(string text, string color)
    {
        StatusTextBlock.Text = text;
        StatusDot.Fill = (SolidColorBrush)new BrushConverter().ConvertFromString(color)!;
    }
}
