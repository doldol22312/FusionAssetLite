using System.IO;
using System.Windows;

namespace FusionAssetLite.Gui;

public partial class App : Application
{
    internal static readonly Uri DarkThemeUri = new("Themes/DarkTheme.xaml", UriKind.Relative);
    internal static readonly Uri LightThemeUri = new("Themes/LightTheme.xaml", UriKind.Relative);

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        GuiSettings settings = GuiSettings.Load();
        ApplyTheme(settings.IsDarkTheme);

        string? initialPackage = e.Args.FirstOrDefault(arg =>
            !string.IsNullOrWhiteSpace(arg) &&
            !arg.StartsWith("-", StringComparison.Ordinal) &&
            File.Exists(arg));

        MainWindow window = new(settings, initialPackage);
        MainWindow = window;
        window.Show();
    }

    internal void ApplyTheme(bool isDark)
    {
        Uri targetUri = isDark ? DarkThemeUri : LightThemeUri;
        ResourceDictionary dict = new() { Source = targetUri };

        var merged = Resources.MergedDictionaries;
        if (merged.Count > 0)
            merged[0] = dict;
        else
            merged.Add(dict);
    }
}
