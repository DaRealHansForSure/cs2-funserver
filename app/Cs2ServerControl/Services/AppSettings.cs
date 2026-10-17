using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace Cs2ServerControl.Services;

/// <summary>
/// Per user settings in %APPDATA%\Cs2ServerControl\settings.json. A missing
/// or broken file gives the defaults, it never stops the app from starting.
/// </summary>
public sealed class AppSettings {
    private const string PasswordAlphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public string RconPassword { get; set; } = "";
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public double WindowWidth { get; set; } = 1440;
    public double WindowHeight { get; set; } = 900;
    public bool WindowMaximized { get; set; }
    public string LastScreen { get; set; } = "LiveMap";
    public bool ShowNames { get; set; } = true;
    public bool ShowTrails { get; set; } = true;
    public bool ShowWeapons { get; set; } = true;
    public bool ShowShots { get; set; } = true;
    public int MaxPlayers { get; set; } = ServerProcess.DefaultMaxPlayers;
    public bool FirewallAsked { get; set; }
    /// <summary>The LAN address the server binds to; empty picks the adapter with the default gateway.</summary>
    public string LanAddress { get; set; } = "";
    /// <summary>The screen the window opens on; empty opens the one used last.</summary>
    public string StartScreen { get; set; } = "";

    /// <summary>Where Save writes; tests point it at a temp file so they never touch the real settings.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string FilePath { get; init; } = SettingsFile;

    public static string SettingsFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Cs2ServerControl", "settings.json");

    public static AppSettings Load() {
        AppSettings settings = ReadFile() ?? new AppSettings();
        if (string.IsNullOrEmpty(settings.RconPassword)) {
            settings.RconPassword = CreatePassword();
            settings.Save();
        }
        return settings;
    }

    public void Save() {
        try {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
        } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
            // Losing the window position is not worth an error dialog.
        }
    }

    internal static string CreatePassword() {
        return RandomNumberGenerator.GetString(PasswordAlphabet, 24);
    }

    private static AppSettings? ReadFile() {
        try {
            if (!File.Exists(SettingsFile)) {
                return null;
            }
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsFile));
        } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException) {
            return null;
        }
    }
}
