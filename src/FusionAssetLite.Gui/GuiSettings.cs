using System.IO;
using System.Text.Json;

namespace FusionAssetLite.Gui;

internal sealed class GuiSettings
{
    private const int MaxRecentPackages = 5;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public bool IsDarkTheme { get; set; } = true;
    public bool ZipOutput { get; set; }
    public string PngCompression { get; set; } = "fastest";
    public bool DumpImages { get; set; } = true;
    public bool DumpSounds { get; set; } = true;
    public bool DumpPackData { get; set; } = true;
    public bool DumpShaders { get; set; } = true;
    public List<string> RecentPackages { get; set; } = [];

    private static string GetSettingsPath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FusionAssetLite",
            "gui-settings.json");

    public static GuiSettings Load()
    {
        try
        {
            string path = GetSettingsPath();
            if (!File.Exists(path))
                return new GuiSettings();

            string json = File.ReadAllText(path);
            GuiSettings? settings = JsonSerializer.Deserialize<GuiSettings>(json, JsonOptions);
            if (settings == null)
                return new GuiSettings();

            settings.RecentPackages ??= [];
            return settings;
        }
        catch
        {
            return new GuiSettings();
        }
    }

    public void Save()
    {
        try
        {
            string path = GetSettingsPath();
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch
        {
        }
    }

    public void AddRecentPackage(string packagePath)
    {
        if (string.IsNullOrWhiteSpace(packagePath))
            return;

        string fullPath = Path.GetFullPath(packagePath);
        RecentPackages.RemoveAll(existing => string.Equals(existing, fullPath, StringComparison.OrdinalIgnoreCase));
        RecentPackages.Insert(0, fullPath);
        if (RecentPackages.Count > MaxRecentPackages)
            RecentPackages.RemoveRange(MaxRecentPackages, RecentPackages.Count - MaxRecentPackages);
    }
}
