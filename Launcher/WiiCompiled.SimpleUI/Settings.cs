using System.Text.Json;

namespace WiiCompiled.SimpleUI;

/// <summary>What the user picked last time, so the UI doesn't ask again on every launch.</summary>
internal sealed class Settings
{
    public string? SetupExePath { get; set; }
    public string? GamePath { get; set; }
    public string? InstallDirectory { get; set; }
    public bool IncludeRetroRewind { get; set; }
    public string? RetroDirectory { get; set; }
    public bool DownloadRetroWfcPayload { get; set; } = true;
    public bool EnableLegacyWfc { get; set; }
    public bool EnableCheats { get; set; } = true;

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WiiCompiled", "SimpleUI", "settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath));
                if (loaded is not null) return loaded;
            }
        }
        catch (Exception) { /* A corrupt or unreadable settings file just starts fresh. */ }
        return new Settings();
    }

    public void Save()
    {
        var path = FilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>
    /// Finds WiiCompiled-Setup.exe: next to this UI (built/deployed together), the remembered
    /// choice, or the default install location from a previous install. Returns null when none of
    /// those exist and the caller must ask the user to browse for one (e.g. Launcher/dist).
    /// </summary>
    public string? ResolveSetupExe()
    {
        var candidates = new List<string?>
        {
            Path.Combine(AppContext.BaseDirectory, "WiiCompiled-Setup.exe"),
            SetupExePath,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs", "WiiCompiled", "WiiCompiled-Setup.exe")
        };
        return candidates.FirstOrDefault(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path));
    }

    public static string DefaultInstallDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs", "WiiCompiled");
}
