using System.Text.Json;
using Microsoft.Win32;

namespace Whatts;

/// <summary>Per-user preferences, stored in %LOCALAPPDATA%\Whatts\settings.json.</summary>
sealed class Settings
{
    public int? X { get; set; }
    public int? Y { get; set; }
    public bool TopMost { get; set; }
    public List<string> Collapsed { get; set; } = ["details0", "details1"];

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Whatts", "settings.json");

    public static Settings Load()
    {
        try
        {
            return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), Json) ?? new();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return new(); // first run, or an unreadable file: start from defaults
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Preferences are a convenience; losing one write isn't worth interrupting the user.
        }
    }
}

/// <summary>"Start with Windows" via the per-user Run key (the installer writes the same value).</summary>
static class Autostart
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "Whatts";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string;
        }
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
            key.SetValue(ValueName, $"\"{Environment.ProcessPath}\" --tray");
        else
            key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
