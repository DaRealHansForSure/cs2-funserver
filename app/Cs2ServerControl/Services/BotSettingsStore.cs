using System.IO;

namespace Cs2ServerControl.Services;

/// <summary>
/// The bot settings in two layers. The package's defaults are the marked block
/// at the end of modes\base.cfg; the window only reads it. What the host
/// changes goes into server\cstrike\local_bots.cfg, only the values that
/// differ from the defaults. fun_modes runs that file right after base.cfg and
/// before the mode config, so a mode can still switch bots off (Deathrun) and
/// an install never touches the host's choices. No file means the defaults.
/// </summary>
public static class BotSettingsStore {
    public const string StartMarker = "// --- Bots (Server Control) ---";
    public const string EndMarker = "// --- End Bots ---";

    /// <summary>The package defaults: the installed base.cfg, else the one in server-files.</summary>
    public static IReadOnlyDictionary<string, string> ReadDefaults(PackagePaths paths) {
        IReadOnlyDictionary<string, string> values = ReadBlock(paths.ServerBaseConfigFile);
        if (values.Count == 0) {
            values = ReadBlock(paths.SourceBaseConfigFile);
        }
        return values;
    }

    /// <summary>The host's values for the bot cvars the defaults know, empty without local_bots.cfg.</summary>
    public static IReadOnlyDictionary<string, string> ReadCustom(PackagePaths paths, IEnumerable<string> cvars) {
        var custom = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(paths.LocalBotConfigFile)) {
            return custom;
        }
        var settings = new LocalSettings(paths.LocalBotConfigFile);
        foreach (string cvar in cvars) {
            string? value = settings.Get(cvar);
            if (value != null) {
                custom[cvar] = value;
            }
        }
        return custom;
    }

    /// <summary>Writes the values that differ from the defaults; with none left the file goes, the defaults apply again.</summary>
    public static void SaveCustom(PackagePaths paths, IReadOnlyList<KeyValuePair<string, string>> cvars, IReadOnlyDictionary<string, string> defaults) {
        IReadOnlyList<KeyValuePair<string, string>> differences = Differences(cvars, defaults);
        if (differences.Count == 0) {
            if (File.Exists(paths.LocalBotConfigFile)) {
                File.Delete(paths.LocalBotConfigFile);
            }
            return;
        }

        var settings = new LocalSettings(paths.LocalBotConfigFile);
        foreach (KeyValuePair<string, string> cvar in cvars) {
            settings.Remove(cvar.Key);
        }
        foreach (KeyValuePair<string, string> cvar in differences) {
            settings.Set(cvar.Key, cvar.Value);
        }
        settings.Save();
    }

    internal static IReadOnlyList<KeyValuePair<string, string>> Differences(IReadOnlyList<KeyValuePair<string, string>> cvars, IReadOnlyDictionary<string, string> defaults) {
        return cvars
            .Where(cvar => !defaults.TryGetValue(cvar.Key, out string? defaultValue) || !string.Equals(defaultValue, cvar.Value, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    /// <summary>Reads the cvars inside the marked block, empty when there is none.</summary>
    public static IReadOnlyDictionary<string, string> ReadBlock(string configFile) {
        if (!File.Exists(configFile)) {
            return new Dictionary<string, string>();
        }
        return ParseBlock(File.ReadAllLines(configFile));
    }

    internal static IReadOnlyDictionary<string, string> ParseBlock(IReadOnlyList<string> lines) {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int start = IndexOfMarker(lines, StartMarker);
        if (start < 0) {
            return values;
        }

        for (int index = start + 1; index < lines.Count; index++) {
            string line = lines[index].Trim();
            if (line == EndMarker) {
                break;
            }
            int space = line.IndexOf(' ');
            if (space <= 0 || line.StartsWith("//", StringComparison.Ordinal)) {
                continue;
            }
            values[line[..space]] = line[(space + 1)..].Trim().Trim('"');
        }
        return values;
    }

    private static int IndexOfMarker(IReadOnlyList<string> lines, string marker) {
        for (int index = 0; index < lines.Count; index++) {
            if (lines[index].Trim() == marker) {
                return index;
            }
        }
        return -1;
    }
}
