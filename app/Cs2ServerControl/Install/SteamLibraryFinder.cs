using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Cs2ServerControl.Install;

/// <summary>
/// Finds a CS2 the owner already installed through Steam, so the first
/// server install can copy it on the disk instead of downloading about 60 GB;
/// SteamCMD then only fetches what differs. Read only: nothing in the Steam
/// folders is ever changed.
/// </summary>
internal static partial class SteamLibraryFinder {
    private const string Cs2Manifest = "appmanifest_730.acf";
    // StateFlags 4: fully installed. A running update or download has other bits set.
    private const string FullyInstalled = "4";

    [GeneratedRegex(@"^\s*""path""\s+""(?<path>[^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex LibraryPathPattern();

    [GeneratedRegex(@"^\s*""(?<key>[^""]+)""\s+""(?<value>[^""]*)""")]
    private static partial Regex KeyValuePattern();

    /// <summary>The game folder of an installed CS2 (with game\bin\win64\cs2.exe), or null.</summary>
    public static string? FindCs2Install() {
        string? steamPath = ReadSteamPath();
        if (steamPath == null) {
            return null;
        }

        string libraryFile = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
        IReadOnlyList<string> libraries = File.Exists(libraryFile) ? ReadLibraryPaths(File.ReadAllLines(libraryFile)) : [];
        // The apps list in libraryfolders.vdf can name a library that no longer
        // exists, so every library folder is checked for the manifest itself.
        foreach (string library in libraries.Append(steamPath).Distinct(StringComparer.OrdinalIgnoreCase)) {
            string? gameFolder = CheckLibrary(library);
            if (gameFolder != null) {
                return gameFolder;
            }
        }
        return null;
    }

    internal static string? CheckLibrary(string library) {
        string manifest = Path.Combine(library, "steamapps", Cs2Manifest);
        if (!File.Exists(manifest)) {
            return null;
        }
        IReadOnlyDictionary<string, string> values = ReadManifest(File.ReadAllLines(manifest));
        if (!values.TryGetValue("StateFlags", out string? state) || state != FullyInstalled) {
            return null;
        }
        if (!values.TryGetValue("installdir", out string? installDir) || installDir.Length == 0) {
            return null;
        }
        string gameFolder = Path.Combine(library, "steamapps", "common", installDir);
        bool hasServerExe = File.Exists(Path.Combine(gameFolder, "game", "bin", "win64", "cs2.exe"));
        return hasServerExe ? gameFolder : null;
    }

    /// <summary>The "path" entries of libraryfolders.vdf, with the doubled backslashes undone.</summary>
    internal static IReadOnlyList<string> ReadLibraryPaths(IEnumerable<string> vdfLines) {
        var paths = new List<string>();
        foreach (string line in vdfLines) {
            Match match = LibraryPathPattern().Match(line);
            if (match.Success) {
                paths.Add(match.Groups["path"].Value.Replace(@"\\", @"\"));
            }
        }
        return paths;
    }

    /// <summary>The top level "key" "value" pairs of an appmanifest file.</summary>
    internal static IReadOnlyDictionary<string, string> ReadManifest(IEnumerable<string> manifestLines) {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in manifestLines) {
            Match match = KeyValuePattern().Match(line);
            if (match.Success && !values.ContainsKey(match.Groups["key"].Value)) {
                values[match.Groups["key"].Value] = match.Groups["value"].Value;
            }
        }
        return values;
    }

    private static string? ReadSteamPath() {
        try {
            using RegistryKey? steam = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            string? path = steam?.GetValue("SteamPath") as string;
            if (string.IsNullOrEmpty(path)) {
                return null;
            }
            return Path.GetFullPath(path);
        } catch (Exception exception) when (exception is System.Security.SecurityException or IOException or UnauthorizedAccessException) {
            return null;
        }
    }
}
