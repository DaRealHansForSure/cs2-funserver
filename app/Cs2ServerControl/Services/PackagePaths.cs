using System.IO;

namespace Cs2ServerControl.Services;

/// <summary>
/// All file locations of the package. The root is found by walking up from
/// the exe until a folder holds both server\ and tools\, so the exe works in
/// the package root and from a bin\ folder during development.
///
/// The CS2 dedicated server (SteamCMD app 730) lives in server\: the program
/// in game\bin\win64, the game folder in game\csgo with its maps (.vpk),
/// cfg\ and addons\ (Metamod, CounterStrikeSharp and our plugins).
/// </summary>
public sealed class PackagePaths {
    public PackagePaths(string rootDir) {
        RootDir = rootDir;
    }

    public string RootDir { get; }
    public string ServerDir => Path.Combine(RootDir, "server");
    public string ToolsDir => Path.Combine(RootDir, "tools");
    public string GameDir => Path.Combine(ServerDir, "game", "csgo");
    public string MapsDir => Path.Combine(GameDir, "maps");
    public string ConfigDir => Path.Combine(GameDir, "cfg");
    public string AddonsDir => Path.Combine(GameDir, "addons");
    public string PreviewsDir => Path.Combine(ToolsDir, "previews");
    // Rendered by the Maps screen; a real screenshot in PreviewsDir always wins.
    public string GeneratedPreviewsDir => Path.Combine(PreviewsDir, "auto");
    public string Cs2Exe => Path.Combine(ServerDir, "game", "bin", "win64", "cs2.exe");
    // -condebug writes the console into the game folder (not verified on a real server yet).
    public string ConsoleLogFile => Path.Combine(GameDir, "console.log");

    // The package's own configs live in cfg\fun\, so they never mix with Valve's.
    public string FunConfigDir => Path.Combine(ConfigDir, "fun");
    public string LocalConfigFile => Path.Combine(FunConfigDir, "local.cfg");
    // Written by the window, read by the mode plugin: CS2 has no localinfo to carry it.
    public string ActiveModeFile => Path.Combine(FunConfigDir, "active_mode.txt");
    // The host's bot settings; only what differs from the defaults in base.cfg.
    public string LocalBotConfigFile => Path.Combine(FunConfigDir, "local_bots.cfg");

    /// <summary>The game rules the host set for one mode; the mode plugin runs it after local.cfg in that mode only.</summary>
    public string LocalModeConfigFile(string modeId) {
        return Path.Combine(FunConfigDir, $"local_{modeId}.cfg");
    }

    public string ModeConfigFile(string modeId) {
        return Path.Combine(FunConfigDir, "modes", modeId + ".cfg");
    }

    public string GameLogsDir => Path.Combine(GameDir, "logs");
    public string PluginLogsDir => Path.Combine(AddonsDir, "counterstrikesharp", "logs");
    public string BotProfileFile => Path.Combine(GameDir, "botprofile.db");
    public string ServerBaseConfigFile => Path.Combine(FunConfigDir, "modes", "base.cfg");
    public string SourceBaseConfigFile => Path.Combine(RootDir, "server-files", "csgo", "cfg", "fun", "modes", "base.cfg");

    public bool IsServerInstalled => File.Exists(Cs2Exe);

    public string MapCycleFile(string modeId) {
        return Path.Combine(FunConfigDir, $"mapcycle_{modeId}.txt");
    }

    /// <summary>A Valve map: game\csgo\maps\&lt;name&gt;.vpk. Workshop maps live in the Steam workshop folder instead.</summary>
    public string MapFile(string mapName) {
        return Path.Combine(MapsDir, mapName + ".vpk");
    }

    /// <summary>A loose nav mesh next to the map; most CS2 maps carry theirs inside the .vpk instead.</summary>
    public string NavFile(string mapName) {
        return Path.Combine(MapsDir, mapName + ".nav");
    }

    // Radar images; Valve's are packed into the game's .vpk files, loose ones only for own maps.
    public string OverviewsDir => Path.Combine(GameDir, "resource", "overviews");

    public static PackagePaths Locate() {
        string? candidate = AppContext.BaseDirectory;
        while (candidate != null) {
            bool looksLikePackage = Directory.Exists(Path.Combine(candidate, "server"))
                && Directory.Exists(Path.Combine(candidate, "tools"));
            if (looksLikePackage) {
                return new PackagePaths(candidate);
            }
            candidate = Path.GetDirectoryName(candidate.TrimEnd(Path.DirectorySeparatorChar));
        }

        // Before the first install there is no server\ yet. Walking up for
        // tools\ alone still finds the package, so the window can say so.
        candidate = AppContext.BaseDirectory;
        while (candidate != null) {
            if (Directory.Exists(Path.Combine(candidate, "tools"))) {
                return new PackagePaths(candidate);
            }
            candidate = Path.GetDirectoryName(candidate.TrimEnd(Path.DirectorySeparatorChar));
        }
        return new PackagePaths(AppContext.BaseDirectory);
    }
}
