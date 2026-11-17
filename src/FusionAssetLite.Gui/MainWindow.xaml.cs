using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace FusionAssetLite.Gui;

public partial class MainWindow : Window
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaBorderColor = 34;
    private const int DwmwcpRound = 2;

    private static readonly Geometry MoonIconGeometry = Geometry.Parse("M 12,3 A 9,9 0 1 0 21,12 A 7,7 0 0 1 12,3 Z");
    private static readonly Geometry SunIconGeometry = Geometry.Parse("M 12,3 V 5 M 12,19 V 21 M 4.2,4.2 L 5.6,5.6 M 18.4,18.4 L 19.8,19.8 M 3,12 H 5 M 19,12 H 21 M 4.2,19.8 L 5.6,18.4 M 18.4,5.6 L 19.8,4.2 M 16,12 A 4,4 0 1 1 8,12 A 4,4 0 0 1 16,12 Z");
    private static readonly Geometry MaximizeIconGeometry = Geometry.Parse("M 0,0 H 10 V 10 H 0 Z");
    private static readonly Geometry RestoreIconGeometry = Geometry.Parse("M 0,3 H 7 V 10 H 0 Z M 3,3 V 0 H 10 V 7 H 7");

    private enum LogKind
    {
        Normal,
        Header,
        Progress,
        Success,
        Warning,
        Error
    }

    private readonly record struct LogEntry(string Text, LogKind Kind);

    private readonly GuiSettings _settings;
    private readonly List<LogEntry> _logEntries = [];
    private readonly Stopwatch _runStopwatch = new();
    private readonly DispatcherTimer _elapsedTimer;
    private readonly DispatcherTimer _copyResetTimer;

    private Process? _runningProcess;
    private bool _cancelRequested;
    private bool _isInitializing = true;
    private string? _lastOutputPath;
    private string? _detectedAppName;
    private string? _detectedPackageMeta;
    private bool _outputWasAutoFilled;
    private bool _settingAutoOutput;
    private int _issueCount;

    public MainWindow() : this(GuiSettings.Load(), null)
    {
    }

    internal MainWindow(GuiSettings settings, string? initialPackage)
    {
        _settings = settings;
        InitializeComponent();

        _elapsedTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _elapsedTimer.Tick += (_, _) =>
        {
            if (_runStopwatch.IsRunning)
                ElapsedTextBlock.Text = $"{_runStopwatch.Elapsed.TotalSeconds:0.00}s";
        };

        _copyResetTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1400) };
        _copyResetTimer.Tick += (_, _) =>
        {
            _copyResetTimer.Stop();
            RestoreCopyButtonText();
        };

        ApplySettingsToUi();
        _isInitializing = false;

        if (!string.IsNullOrWhiteSpace(initialPackage) && File.Exists(initialPackage))
            LoadPackagePath(initialPackage);
        else
            UpdatePackagePreview();

        UpdateOutputPreview();
        UpdateBankSelectionState();
        RefreshRecentPackagesUi();
        UpdateThemeIcon();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ApplyDwmWindowAttributes();
    }

    private void ApplyDwmWindowAttributes()
    {
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
            return;

        int corner = DwmwcpRound;
        _ = DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref corner, sizeof(int));

        int dark = _settings.IsDarkTheme ? 1 : 0;
        _ = DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int));

        // Subtle border tint matching theme (Win32 COLORREF = 0x00BBGGRR)
        int borderColor = _settings.IsDarkTheme ? 0x00372C23 : 0x00D7E0D9;
        _ = DwmSetWindowAttribute(hwnd, DwmwaBorderColor, ref borderColor, sizeof(int));
    }

    private void ApplySettingsToUi()
    {
        if (_settings.ZipOutput)
            ZipOutputRadio.IsChecked = true;
        else
            FolderOutputRadio.IsChecked = true;

        ImagesCheckBox.IsChecked = _settings.DumpImages;
        SoundsCheckBox.IsChecked = _settings.DumpSounds;
        PackedDataCheckBox.IsChecked = _settings.DumpPackData;
        ShadersCheckBox.IsChecked = _settings.DumpShaders;

        for (int i = 0; i < PngCompressionComboBox.Items.Count; i++)
        {
            if (PngCompressionComboBox.Items[i] is ComboBoxItem item &&
                string.Equals(item.Tag as string, _settings.PngCompression, StringComparison.OrdinalIgnoreCase))
            {
                PngCompressionComboBox.SelectedIndex = i;
                break;
            }
        }
    }

    private void SaveSettingsFromUi()
    {
        if (_isInitializing)
            return;

        _settings.ZipOutput = ZipOutputRadio.IsChecked == true;
        _settings.DumpImages = ImagesCheckBox.IsChecked == true;
        _settings.DumpSounds = SoundsCheckBox.IsChecked == true;
        _settings.DumpPackData = PackedDataCheckBox.IsChecked == true;
        _settings.DumpShaders = ShadersCheckBox.IsChecked == true;
        _settings.PngCompression = GetSelectedCompressionTag();
        _settings.Save();
    }

    private string GetSelectedCompressionTag() =>
        (PngCompressionComboBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "fastest";

    private void ThemeToggle_Click(object sender, RoutedEventArgs e)
    {
        _settings.IsDarkTheme = !_settings.IsDarkTheme;
        if (Application.Current is App app)
            app.ApplyTheme(_settings.IsDarkTheme);

        _settings.Save();
        ApplyDwmWindowAttributes();
        UpdateThemeIcon();
        RebuildLogDocument();
        UpdateStatusVisualsForCurrentState();
    }

    private void UpdateThemeIcon()
    {
        ThemeIconPath.Data = _settings.IsDarkTheme ? SunIconGeometry : MoonIconGeometry;
        ThemeToggleButton.ToolTip = _settings.IsDarkTheme ? "Switch to Light theme" : "Switch to Dark theme";
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        bool maximized = WindowState == WindowState.Maximized;
        WindowRootBorder.Margin = maximized ? new Thickness(7) : new Thickness(0);
        WindowRootBorder.BorderThickness = maximized ? new Thickness(0) : new Thickness(1);
        MaximizeIconPath.Data = maximized ? RestoreIconGeometry : MaximizeIconGeometry;
        MaximizeButton.ToolTip = maximized ? "Restore" : "Maximize";
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.O && Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (_runningProcess == null)
                BrowseInput_Click(this, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (e.Key == Key.O && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            if (_runningProcess == null)
                BrowseOutput_Click(this, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (e.Key == Key.F5 || (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control))
        {
            if (_runningProcess == null && ExtractButton.IsEnabled)
                Extract_Click(this, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && _runningProcess != null)
        {
            Cancel_Click(this, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (e.Key == Key.L && Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (CopyLogButton.IsEnabled)
                CopyLog_Click(this, new RoutedEventArgs());
            e.Handled = true;
        }
    }

    private void BrowseInput_Click(object sender, RoutedEventArgs e)
    {
        OpenFileDialog dialog = new()
        {
            Title = "Select Fusion game package",
            Filter = "Fusion packages (*.exe;*.ccn)|*.exe;*.ccn|Executable files (*.exe)|*.exe|CCN files (*.ccn)|*.ccn|All files (*.*)|*.*",
            CheckFileExists = true
        };

        if (File.Exists(InputPathTextBox.Text))
            dialog.InitialDirectory = Path.GetDirectoryName(InputPathTextBox.Text);

        if (dialog.ShowDialog(this) != true)
            return;

        LoadPackagePath(dialog.FileName);
    }

    private void PackageInfoBar_Click(object sender, MouseButtonEventArgs e)
    {
        if (_runningProcess != null)
            return;

        BrowseInput_Click(sender, e);
    }

    private void ClearInput_Click(object sender, RoutedEventArgs e)
    {
        if (_runningProcess != null)
            return;

        InputPathTextBox.Text = string.Empty;
        if (_outputWasAutoFilled)
        {
            _settingAutoOutput = true;
            OutputPathTextBox.Text = string.Empty;
            _settingAutoOutput = false;
            _outputWasAutoFilled = false;
        }
        UpdatePackagePreview();
        UpdateOutputPreview();
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
        UpdateOutputPreview();
    }

    private void LoadPackagePath(string path)
    {
        InputPathTextBox.Text = path;
        AutoFillOutputPath(path);
        UpdatePackagePreview();
        UpdateOutputPreview();
    }

    private async void Extract_Click(object sender, RoutedEventArgs e)
    {
        if (_runningProcess != null)
            return;

        string inputPath = InputPathTextBox.Text.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(inputPath))
        {
            BrowseInput_Click(sender, e);
            inputPath = InputPathTextBox.Text.Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(inputPath))
                return;
        }

        if (!File.Exists(inputPath))
        {
            SetStatus("Input missing", "StatusErrorBrush");
            StageTextBlock.Text = "Package not found";
            RunDetailTextBlock.Text = $"Could not find \"{inputPath}\".";
            return;
        }

        if (!HasAnyBankSelected())
        {
            SetStatus("No banks", "StatusWarningBrush");
            BankWarningTextBlock.Visibility = Visibility.Visible;
            return;
        }

        string outputPath = OutputPathTextBox.Text.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            outputPath = GetDefaultOutputPath(inputPath);
            SetAutoOutputPath(outputPath);
        }

        string? cliPath = FindCliExecutable();
        if (cliPath == null)
        {
            SetStatus("CLI missing", "StatusErrorBrush");
            StageTextBlock.Text = "Extractor binary missing";
            RunDetailTextBlock.Text = "Build FusionAssetLite before running the GUI.";
            AppendLog("GUI: Could not locate FusionAssetLite.exe or FusionAssetLite.dll.", LogKind.Error);
            return;
        }

        _settings.AddRecentPackage(inputPath);
        SaveSettingsFromUi();
        RefreshRecentPackagesUi();

        ResetRunState(inputPath, outputPath);
        SetRunning(true);
        _runStopwatch.Restart();
        _elapsedTimer.Start();

        try
        {
            ProcessStartInfo startInfo = CreateStartInfo(cliPath, inputPath, outputPath);
            Process process = new() { StartInfo = startInfo, EnableRaisingEvents = true };
            _runningProcess = process;

            process.OutputDataReceived += (_, args) => DispatchLine(args.Data, isError: false);
            process.ErrorDataReceived += (_, args) => DispatchLine(args.Data, isError: true);

            if (!process.Start())
                throw new InvalidOperationException("The extractor process did not start.");

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            await process.WaitForExitAsync();

            _runStopwatch.Stop();
            _elapsedTimer.Stop();
            if (ElapsedTextBlock.Text == "0.00s" || !_hasCliElapsed)
                ElapsedTextBlock.Text = $"{_runStopwatch.Elapsed.TotalSeconds:0.00}s";

            int exitCode = process.ExitCode;
            if (_cancelRequested)
            {
                StageTextBlock.Text = "Extraction Cancelled";
                RunDetailTextBlock.Text = "The extractor process was stopped by user request.";
                ExtractionProgressBar.IsIndeterminate = false;
                ProgressPercentTextBlock.Text = "Stopped";
                SetStatus("Cancelled", "StatusWarningBrush");
                MarkUnfinishedCards("Cancelled");
            }
            else if (exitCode == 0)
            {
                string targetLabel = _detectedAppName ?? Path.GetFileNameWithoutExtension(inputPath);
                string metaSuffix = string.IsNullOrEmpty(_detectedPackageMeta) ? string.Empty : $" · {_detectedPackageMeta}";
                StageTextBlock.Text = "Extraction Complete";
                RunDetailTextBlock.Text = $"{targetLabel}{metaSuffix}";
                ExtractionProgressBar.IsIndeterminate = false;
                ExtractionProgressBar.Value = 100;
                ProgressPercentTextBlock.Text = "100%";
                SetStatus("Complete", "StatusSuccessBrush");
                MarkUnfinishedCards("Done");
            }
            else
            {
                StageTextBlock.Text = "Extraction Failed";
                RunDetailTextBlock.Text = $"Extractor exited with code {exitCode}. Inspect the log for details.";
                ExtractionProgressBar.IsIndeterminate = false;
                ProgressPercentTextBlock.Text = "Failed";
                SetStatus("Failed", "StatusErrorBrush");
                MarkUnfinishedCards("Stopped");
            }
        }
        catch (Exception ex)
        {
            _runStopwatch.Stop();
            _elapsedTimer.Stop();
            AppendLog("GUI: " + ex.Message, LogKind.Error);
            StageTextBlock.Text = "Launch Failed";
            RunDetailTextBlock.Text = ex.Message;
            ExtractionProgressBar.IsIndeterminate = false;
            ProgressPercentTextBlock.Text = "Error";
            SetStatus("Failed", "StatusErrorBrush");
        }
        finally
        {
            _runningProcess?.Dispose();
            _runningProcess = null;
            SetRunning(false);
            RefreshOpenOutputButton();
            UpdateOutputPreview();
        }
    }

    private bool _hasCliElapsed;
    private string _currentStatusKey = "StatusIdleBrush";

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (_runningProcess == null || _runningProcess.HasExited)
            return;

        _cancelRequested = true;
        AppendLog("GUI: cancellation requested.", LogKind.Warning);

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
        if (string.IsNullOrWhiteSpace(path))
            return;

        if (File.Exists(path))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{path}\"",
                UseShellExecute = true
            });
            return;
        }

        if (Directory.Exists(path))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
        }
    }

    private void CopyLog_Click(object sender, RoutedEventArgs e)
    {
        if (_logEntries.Count == 0)
            return;

        StringBuilder sb = new();
        foreach (LogEntry entry in _logEntries)
            sb.AppendLine(entry.Text);

        Clipboard.SetText(sb.ToString().TrimEnd());

        if (CopyLogButton.Content is StackPanel panel && panel.Children.Count > 1 && panel.Children[1] is TextBlock tb)
            tb.Text = "Copied";

        _copyResetTimer.Stop();
        _copyResetTimer.Start();
    }

    private void RestoreCopyButtonText()
    {
        if (CopyLogButton.Content is StackPanel panel && panel.Children.Count > 1 && panel.Children[1] is TextBlock tb)
            tb.Text = "Copy";
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e)
    {
        if (_runningProcess != null)
            return;

        _logEntries.Clear();
        _issueCount = 0;
        IssuesCountBadge.Visibility = Visibility.Collapsed;
        IssuesCountTextBlock.Text = "0";
        LogRichTextBox.Document.Blocks.Clear();
        UpdateLogEmptyStateAndCounts();
    }

    private void LogFilter_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing)
            return;

        RebuildLogDocument();
    }

    private void OutputMode_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing)
            return;

        SaveSettingsFromUi();
        UpdateOutputPreview();
    }

    private void AssetBankCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing)
            return;

        SaveSettingsFromUi();
        UpdateBankSelectionState();
    }

    private void ToggleAllBanks_Click(object sender, RoutedEventArgs e)
    {
        ImagesCheckBox.IsChecked = true;
        SoundsCheckBox.IsChecked = true;
        PackedDataCheckBox.IsChecked = true;
        ShadersCheckBox.IsChecked = true;
        SaveSettingsFromUi();
        UpdateBankSelectionState();
    }

    private void PngCompressionComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        SaveSettingsFromUi();
    }

    private void InputPathTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isInitializing)
            return;

        string trimmed = InputPathTextBox.Text.Trim().Trim('"');
        ClearInputButton.Visibility = string.IsNullOrEmpty(trimmed) ? Visibility.Collapsed : Visibility.Visible;

        if (File.Exists(trimmed) && (string.IsNullOrWhiteSpace(OutputPathTextBox.Text) || _outputWasAutoFilled))
            AutoFillOutputPath(trimmed);

        UpdatePackagePreview();
        UpdateOutputPreview();
    }

    private void OutputPathTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_settingAutoOutput)
            _outputWasAutoFilled = false;

        if (!_isInitializing)
            UpdateOutputPreview();
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        if (_runningProcess == null && TryGetDroppedPackage(e, out string? path))
        {
            e.Effects = DragDropEffects.Copy;
            DropOverlayFileTextBlock.Text = Path.GetFileName(path);
            DropOverlay.Visibility = Visibility.Visible;
        }
        else
        {
            e.Effects = DragDropEffects.None;
            DropOverlay.Visibility = Visibility.Collapsed;
        }

        e.Handled = true;
    }

    private void Window_DragLeave(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
        if (_runningProcess != null || !TryGetDroppedPackage(e, out string? path))
            return;

        LoadPackagePath(path);
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
        string dotnetExecutable = ResolveDotnetHost();

        ProcessStartInfo startInfo = new()
        {
            FileName = runDllWithDotnet ? dotnetExecutable : cliPath,
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
        if (ZipOutputRadio.IsChecked == true)
            startInfo.ArgumentList.Add("--zip");

        string compression = GetSelectedCompressionTag();
        if (!string.IsNullOrWhiteSpace(compression))
            startInfo.Environment["FUSION_ASSET_LITE_PNG_COMPRESSION"] = compression;

        return startInfo;
    }

    private static string ResolveDotnetHost()
    {
        string userDotnet = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".dotnet",
            "dotnet.exe");
        return File.Exists(userDotnet) ? userDotnet : "dotnet";
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
            Path.GetFullPath(Path.Combine(baseDirectory, "..", "..", "..", "..", "FusionAssetLite", "bin", configuration, "net10.0", "FusionAssetLite.exe")),
            Path.GetFullPath(Path.Combine(baseDirectory, "..", "..", "..", "..", "FusionAssetLite", "bin", configuration, "net10.0", "FusionAssetLite.dll"))
        ];

        return candidates.FirstOrDefault(File.Exists);
    }

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

    private void AutoFillOutputPath(string inputPath) =>
        SetAutoOutputPath(GetDefaultOutputPath(inputPath));

    private void SetAutoOutputPath(string outputPath)
    {
        _settingAutoOutput = true;
        OutputPathTextBox.Text = outputPath;
        _settingAutoOutput = false;
        _outputWasAutoFilled = true;
    }

    private void UpdatePackagePreview()
    {
        string path = InputPathTextBox.Text.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(path))
        {
            PackageTypeBadge.Visibility = Visibility.Collapsed;
            PackageInfoTitleTextBlock.Text = "Drop .exe or .ccn anywhere";
            PackageInfoSubTextBlock.Text = "Or click here to browse for a game file";
            return;
        }

        string ext = Path.GetExtension(path).TrimStart('.').ToUpperInvariant();
        if (string.IsNullOrEmpty(ext))
            ext = "FILE";

        PackageTypeTextBlock.Text = ext;
        PackageTypeBadge.Visibility = Visibility.Visible;

        if (File.Exists(path))
        {
            FileInfo info = new(path);
            string kind = ext == "EXE" ? "PE Executable" : ext == "CCN" ? "Fusion CCN Stream" : "Package File";
            PackageInfoTitleTextBlock.Text = info.Name;
            PackageInfoSubTextBlock.Text = $"{FormatBytes(info.Length)} · {kind}";
        }
        else
        {
            PackageInfoTitleTextBlock.Text = Path.GetFileName(path);
            PackageInfoSubTextBlock.Text = "File not found on disk";
        }
    }

    private void UpdateOutputPreview()
    {
        string inputPath = InputPathTextBox.Text.Trim().Trim('"');
        string outputRoot = OutputPathTextBox.Text.Trim().Trim('"');
        bool zip = ZipOutputRadio.IsChecked == true;

        string appStem = !string.IsNullOrWhiteSpace(_detectedAppName)
            ? _detectedAppName
            : File.Exists(inputPath)
                ? Path.GetFileNameWithoutExtension(inputPath)
                : "<App Name>";

        if (string.IsNullOrWhiteSpace(outputRoot))
        {
            outputRoot = File.Exists(inputPath) ? GetDefaultOutputPath(inputPath) : "extracted_assets_lite";
        }

        string preview = zip
            ? Path.Combine(outputRoot, appStem + ".zip")
            : Path.Combine(outputRoot, appStem) + Path.DirectorySeparatorChar;

        OutputTargetPreviewTextBlock.Text = zip
            ? $"Archive: {preview}"
            : $"Folder: {preview}";
    }

    private bool HasAnyBankSelected() =>
        ImagesCheckBox.IsChecked == true ||
        SoundsCheckBox.IsChecked == true ||
        PackedDataCheckBox.IsChecked == true ||
        ShadersCheckBox.IsChecked == true;

    private void UpdateBankSelectionState()
    {
        bool allSelected =
            ImagesCheckBox.IsChecked == true &&
            SoundsCheckBox.IsChecked == true &&
            PackedDataCheckBox.IsChecked == true &&
            ShadersCheckBox.IsChecked == true;

        ToggleAllBanksButton.Visibility = allSelected ? Visibility.Collapsed : Visibility.Visible;

        bool anySelected = HasAnyBankSelected();
        BankWarningTextBlock.Visibility = anySelected ? Visibility.Collapsed : Visibility.Visible;
        if (_runningProcess == null)
            ExtractButton.IsEnabled = anySelected;
    }

    private void RefreshRecentPackagesUi()
    {
        RecentPackagesWrapPanel.Children.Clear();
        List<string> existing = _settings.RecentPackages.Where(File.Exists).Take(4).ToList();
        if (existing.Count == 0)
        {
            RecentPackagesPanel.Visibility = Visibility.Collapsed;
            return;
        }

        foreach (string path in existing)
        {
            Button chip = new()
            {
                Content = Path.GetFileName(path),
                ToolTip = path,
                Height = 26,
                Padding = new Thickness(10, 0, 10, 0),
                Margin = new Thickness(3),
                FontSize = 11,
                Background = new SolidColorBrush(Color.FromRgb(0x16, 0x1E, 0x27)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x28, 0x35, 0x43)),
                Foreground = new SolidColorBrush(Color.FromRgb(0xCB, 0xD5, 0xE1))
            };
            chip.Click += (_, _) =>
            {
                if (_runningProcess == null)
                    LoadPackagePath(path);
            };
            RecentPackagesWrapPanel.Children.Add(chip);
        }

        RecentPackagesPanel.Visibility = Visibility.Visible;
    }

    private void ResetRunState(string inputPath, string outputPath)
    {
        _cancelRequested = false;
        _hasCliElapsed = false;
        _lastOutputPath = outputPath;
        _detectedAppName = null;
        _detectedPackageMeta = null;

        EnginePill.Visibility = Visibility.Collapsed;
        StageTextBlock.Text = "Scanning Package";
        RunDetailTextBlock.Text = $"Opening {Path.GetFileName(inputPath)}...";
        ElapsedTextBlock.Text = "0.00s";
        RamTextBlock.Text = "—";
        ProgressPercentTextBlock.Text = "...";

        _logEntries.Clear();
        _issueCount = 0;
        IssuesCountBadge.Visibility = Visibility.Collapsed;
        IssuesCountTextBlock.Text = "0";
        LogRichTextBox.Document.Blocks.Clear();
        UpdateLogEmptyStateAndCounts();

        ResetBankCard(ImagesSummaryTextBlock, ImagesStateTextBlock, ImagesDetailTextBlock, ImagesCheckBox.IsChecked == true, "PNG RGBA / RGB");
        ResetBankCard(SoundsSummaryTextBlock, SoundsStateTextBlock, SoundsDetailTextBlock, SoundsCheckBox.IsChecked == true, "WAV / OGG / MP3");
        ResetBankCard(PackedSummaryTextBlock, PackedStateTextBlock, PackedDetailTextBlock, PackedDataCheckBox.IsChecked == true, "Embedded files");
        ResetBankCard(ShadersSummaryTextBlock, ShadersStateTextBlock, ShadersDetailTextBlock, ShadersCheckBox.IsChecked == true, "FX / FXC + XML");

        ExtractionProgressBar.Value = 0;
        ExtractionProgressBar.IsIndeterminate = true;
        OpenOutputButton.IsEnabled = false;
        OpenOutputTextBlock.Text = ZipOutputRadio.IsChecked == true ? "Reveal .zip" : "Open Output";
        SetStatus("Running", "StatusSuccessBrush");
    }

    private static void ResetBankCard(TextBlock valueBlock, TextBlock stateBlock, TextBlock detailBlock, bool enabled, string defaultDetail)
    {
        valueBlock.Text = enabled ? "0" : "—";
        stateBlock.Text = enabled ? "Queued" : "Skipped";
        detailBlock.Text = enabled ? defaultDetail : "Disabled in options";
    }

    private void MarkUnfinishedCards(string terminalState)
    {
        MarkIfQueued(ImagesStateTextBlock, terminalState);
        MarkIfQueued(SoundsStateTextBlock, terminalState);
        MarkIfQueued(PackedStateTextBlock, terminalState);
        MarkIfQueued(ShadersStateTextBlock, terminalState);
    }

    private static void MarkIfQueued(TextBlock stateBlock, string terminalState)
    {
        if (stateBlock.Text is "Queued" or "Active")
            stateBlock.Text = terminalState;
    }

    private void SetRunning(bool running)
    {
        ExtractButton.IsEnabled = !running && HasAnyBankSelected();
        CancelButton.IsEnabled = running;
        InputPathTextBox.IsEnabled = !running;
        ClearInputButton.IsEnabled = !running;
        OutputPathTextBox.IsEnabled = !running;
        FolderOutputRadio.IsEnabled = !running;
        ZipOutputRadio.IsEnabled = !running;
        ImagesCheckBox.IsEnabled = !running;
        SoundsCheckBox.IsEnabled = !running;
        PackedDataCheckBox.IsEnabled = !running;
        ShadersCheckBox.IsEnabled = !running;
        PngCompressionComboBox.IsEnabled = !running;
        ToggleAllBanksButton.IsEnabled = !running;
        ClearLogButton.IsEnabled = !running && _logEntries.Count > 0;
    }

    private void RefreshOpenOutputButton()
    {
        bool exists = !string.IsNullOrWhiteSpace(_lastOutputPath) &&
                      (Directory.Exists(_lastOutputPath) || File.Exists(_lastOutputPath));
        OpenOutputButton.IsEnabled = exists;
        OpenOutputTextBlock.Text = !string.IsNullOrWhiteSpace(_lastOutputPath) &&
                                   _lastOutputPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
            ? "Reveal .zip"
            : "Open Output";
    }

    private void DispatchLine(string? line, bool isError)
    {
        if (line == null)
            return;

        Dispatcher.Invoke(() =>
        {
            LogKind kind = ClassifyLine(line, isError);
            AppendLog(line, kind);
            ReadProcessLine(line);
        });
    }

    private static LogKind ClassifyLine(string line, bool isError)
    {
        if (isError)
        {
            return line.StartsWith("Warning:", StringComparison.OrdinalIgnoreCase)
                ? LogKind.Warning
                : LogKind.Error;
        }

        if (line.Contains("failed", StringComparison.OrdinalIgnoreCase) &&
            !line.Contains("0 failed", StringComparison.OrdinalIgnoreCase))
            return LogKind.Error;

        if (line.StartsWith("Warning:", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("skipping", StringComparison.OrdinalIgnoreCase))
            return LogKind.Warning;

        if (line == "Done." ||
            line.StartsWith("Elapsed:", StringComparison.OrdinalIgnoreCase) ||
            line.StartsWith("Peak RAM:", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("0 failed", StringComparison.OrdinalIgnoreCase))
            return LogKind.Success;

        if (line.StartsWith("Input:", StringComparison.OrdinalIgnoreCase) ||
            line.StartsWith("Package:", StringComparison.OrdinalIgnoreCase) ||
            line.StartsWith("App:", StringComparison.OrdinalIgnoreCase) ||
            line.StartsWith("Extended header:", StringComparison.OrdinalIgnoreCase) ||
            line.StartsWith("Output:", StringComparison.OrdinalIgnoreCase))
            return LogKind.Header;

        if (ProgressLineRegex().IsMatch(line))
            return LogKind.Progress;

        return LogKind.Normal;
    }

    private void AppendLog(string line, LogKind kind)
    {
        LogEntry entry = new(line, kind);
        _logEntries.Add(entry);

        if (kind is LogKind.Warning or LogKind.Error)
        {
            _issueCount++;
            IssuesCountTextBlock.Text = _issueCount.ToString(CultureInfo.InvariantCulture);
            IssuesCountBadge.Visibility = Visibility.Visible;
        }

        bool showEntry = LogFilterAllRadio.IsChecked == true || kind is LogKind.Warning or LogKind.Error;
        if (showEntry)
        {
            AppendEntryToRichTextBox(entry);
            LogRichTextBox.ScrollToEnd();
        }

        UpdateLogEmptyStateAndCounts();
    }

    private void RebuildLogDocument()
    {
        LogRichTextBox.Document.Blocks.Clear();
        bool issuesOnly = LogFilterIssuesRadio.IsChecked == true;
        foreach (LogEntry entry in _logEntries)
        {
            if (!issuesOnly || entry.Kind is LogKind.Warning or LogKind.Error)
                AppendEntryToRichTextBox(entry);
        }

        if (_logEntries.Count > 0)
            LogRichTextBox.ScrollToEnd();

        UpdateLogEmptyStateAndCounts();
    }

    private void AppendEntryToRichTextBox(LogEntry entry)
    {
        Paragraph p = new();
        Run run = new(entry.Text)
        {
            Foreground = GetBrushForLogKind(entry.Kind)
        };
        if (entry.Kind == LogKind.Header)
            run.FontWeight = FontWeights.SemiBold;
        p.Inlines.Add(run);
        LogRichTextBox.Document.Blocks.Add(p);
    }

    private Brush GetBrushForLogKind(LogKind kind)
    {
        string key = kind switch
        {
            LogKind.Header => "LogHeaderBrush",
            LogKind.Progress => "LogMutedBrush",
            LogKind.Success => "LogSuccessBrush",
            LogKind.Warning => "LogWarnBrush",
            LogKind.Error => "LogErrorBrush",
            _ => "LogTextBrush"
        };
        return (Brush)FindResource(key);
    }

    private void UpdateLogEmptyStateAndCounts()
    {
        int count = _logEntries.Count;
        LogEmptyState.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
        LogCountTextBlock.Text = count == 1 ? "1 line" : $"{count:N0} lines";
        CopyLogButton.IsEnabled = count > 0;
        ClearLogButton.IsEnabled = count > 0 && _runningProcess == null;
    }

    private void ReadProcessLine(string line)
    {
        Match progressMatch = ProgressLineRegex().Match(line);
        if (progressMatch.Success)
        {
            string stage = progressMatch.Groups["stage"].Value;
            int percent = int.Parse(progressMatch.Groups["percent"].Value, CultureInfo.InvariantCulture);
            int done = int.Parse(progressMatch.Groups["done"].Value, CultureInfo.InvariantCulture);
            int total = int.Parse(progressMatch.Groups["total"].Value, CultureInfo.InvariantCulture);
            string ram = progressMatch.Groups["ram"].Value;

            ExtractionProgressBar.IsIndeterminate = false;
            ExtractionProgressBar.Value = percent;
            ProgressPercentTextBlock.Text = $"{percent}%";
            StageTextBlock.Text = $"Extracting {stage}";
            RunDetailTextBlock.Text = $"{done:N0} of {total:N0} {stage.ToLowerInvariant()} exported";
            RamTextBlock.Text = $"{ram} MB";

            if (stage == "Images")
            {
                ImagesSummaryTextBlock.Text = done.ToString("N0", CultureInfo.InvariantCulture);
                ImagesStateTextBlock.Text = $"{percent}%";
                ImagesDetailTextBlock.Text = $"of {total:N0} images";
            }
            else if (stage == "Sounds")
            {
                SoundsSummaryTextBlock.Text = done.ToString("N0", CultureInfo.InvariantCulture);
                SoundsStateTextBlock.Text = $"{percent}%";
                SoundsDetailTextBlock.Text = $"of {total:N0} sounds";
            }

            return;
        }

        Match summaryMatch = SummaryLineRegex().Match(line);
        if (summaryMatch.Success)
        {
            string stage = summaryMatch.Groups["stage"].Value;
            int written = int.Parse(summaryMatch.Groups["written"].Value, CultureInfo.InvariantCulture);
            int failed = int.Parse(summaryMatch.Groups["failed"].Value, CultureInfo.InvariantCulture);

            string valueText = written.ToString("N0", CultureInfo.InvariantCulture);
            string stateText = failed == 0 ? "Done" : $"{failed:N0} err";
            string detailText = failed == 0 ? "0 failed" : $"{failed:N0} failed";

            switch (stage)
            {
                case "Images":
                    ImagesSummaryTextBlock.Text = valueText;
                    ImagesStateTextBlock.Text = ImagesCheckBox.IsChecked == true ? stateText : "Skipped";
                    if (ImagesCheckBox.IsChecked == true && !ImagesDetailTextBlock.Text.StartsWith("Mode ", StringComparison.Ordinal))
                        ImagesDetailTextBlock.Text = detailText;
                    break;
                case "Sounds":
                    SoundsSummaryTextBlock.Text = valueText;
                    SoundsStateTextBlock.Text = SoundsCheckBox.IsChecked == true ? stateText : "Skipped";
                    if (SoundsCheckBox.IsChecked == true)
                        SoundsDetailTextBlock.Text = detailText;
                    break;
                case "Packed data":
                    PackedSummaryTextBlock.Text = valueText;
                    PackedStateTextBlock.Text = PackedDataCheckBox.IsChecked == true ? stateText : "Skipped";
                    if (PackedDataCheckBox.IsChecked == true)
                        PackedDetailTextBlock.Text = detailText;
                    break;
                case "Shader files":
                    ShadersSummaryTextBlock.Text = valueText;
                    ShadersStateTextBlock.Text = ShadersCheckBox.IsChecked == true ? stateText : "Skipped";
                    if (ShadersCheckBox.IsChecked == true)
                        ShadersDetailTextBlock.Text = detailText;
                    break;
            }

            return;
        }

        Match packageMatch = PackageLineRegex().Match(line);
        if (packageMatch.Success)
        {
            string header = packageMatch.Groups["header"].Value;
            string fusionVer = packageMatch.Groups["ver"].Value;
            string build = packageMatch.Groups["build"].Value;
            _detectedPackageMeta = $"{header} · Fusion {fusionVer} · Build {build}";
            EnginePillTextBlock.Text = _detectedPackageMeta;
            EnginePill.Visibility = Visibility.Visible;
            StageTextBlock.Text = "Reading Package";
            return;
        }

        if (line.StartsWith("App: ", StringComparison.OrdinalIgnoreCase))
        {
            _detectedAppName = line["App: ".Length..].Trim();
            RunDetailTextBlock.Text = _detectedAppName;
            UpdateOutputPreview();
            return;
        }

        if (line.StartsWith("Image modes: ", StringComparison.OrdinalIgnoreCase))
        {
            string rawModes = line["Image modes: ".Length..].Trim();
            string formatted = string.Join(" · ", rawModes
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(part =>
                {
                    int eq = part.IndexOf('=');
                    if (eq <= 0)
                        return part;
                    string mode = part[..eq];
                    string countStr = int.TryParse(part[(eq + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int c)
                        ? c.ToString("N0", CultureInfo.InvariantCulture)
                        : part[(eq + 1)..];
                    return $"M{mode}: {countStr}";
                }));
            if (!string.IsNullOrWhiteSpace(formatted))
                ImagesDetailTextBlock.Text = formatted;
            return;
        }

        if (line.StartsWith("Elapsed: ", StringComparison.OrdinalIgnoreCase))
        {
            _hasCliElapsed = true;
            ElapsedTextBlock.Text = line["Elapsed: ".Length..].Trim();
            return;
        }

        if (line.StartsWith("Peak RAM: ", StringComparison.OrdinalIgnoreCase))
        {
            RamTextBlock.Text = line["Peak RAM: ".Length..].Trim();
            return;
        }

        if (line.StartsWith("Output: ", StringComparison.OrdinalIgnoreCase))
        {
            _lastOutputPath = line["Output: ".Length..].Trim();
            RefreshOpenOutputButton();
            return;
        }

        if (line.StartsWith("ImageBank:", StringComparison.OrdinalIgnoreCase))
        {
            StageTextBlock.Text = "Extracting Images";
            ImagesStateTextBlock.Text = "Active";
        }
        else if (line.StartsWith("SoundBank:", StringComparison.OrdinalIgnoreCase))
        {
            StageTextBlock.Text = "Extracting Sounds";
            SoundsStateTextBlock.Text = "Active";
        }
        else if (line.StartsWith("PackData:", StringComparison.OrdinalIgnoreCase))
        {
            StageTextBlock.Text = "Reading Packed Data";
            if (line.Contains("skipping", StringComparison.OrdinalIgnoreCase) || line.Contains("none detected", StringComparison.OrdinalIgnoreCase))
                PackedStateTextBlock.Text = "None";
            else
                PackedStateTextBlock.Text = "Active";
        }
        else if (line.StartsWith("Extended header:", StringComparison.OrdinalIgnoreCase))
        {
            RunDetailTextBlock.Text = string.IsNullOrEmpty(_detectedAppName) ? line : $"{_detectedAppName} · {line}";
        }
        else if (line == "Done.")
        {
            StageTextBlock.Text = "Finalizing";
        }
    }

    private void SetStatus(string text, string brushKey)
    {
        _currentStatusKey = brushKey;
        StatusTextBlock.Text = text;
        StatusDot.Fill = (Brush)FindResource(brushKey);
    }

    private void UpdateStatusVisualsForCurrentState()
    {
        StatusDot.Fill = (Brush)FindResource(_currentStatusKey);
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes:N0} B" : $"{value:0.#} {units[unit]}";
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    [GeneratedRegex(
        @"^(?<stage>Images|Sounds): (?<percent>\d+)% \((?<done>\d+)/(?<total>\d+)\), RAM (?<ram>\d+) MB$",
        RegexOptions.CultureInvariant)]
    private static partial Regex ProgressLineRegex();

    [GeneratedRegex(
        @"^(?<stage>Images|Sounds|Packed data|Shader files): (?<written>\d+) written, (?<failed>\d+) failed$",
        RegexOptions.CultureInvariant)]
    private static partial Regex SummaryLineRegex();

    [GeneratedRegex(
        @"^Package: (?<header>\w+), Fusion (?<ver>[\d.]+), build (?<build>\d+)$",
        RegexOptions.CultureInvariant)]
    private static partial Regex PackageLineRegex();
}
