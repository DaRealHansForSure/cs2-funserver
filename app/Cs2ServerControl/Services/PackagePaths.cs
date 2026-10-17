using System.IO;

namespace Cs2ServerControl.Services;

/// <summary>
/// All file locations of the package. The root is found by walking up from
/// the exe until a folder holds both server\ and tools\, so the exe works in
/// the package root and from a bin\ folder during development.
/// </summary>
public sealed class PackagePaths {
    public PackagePaths(string rootDir) {
        RootDir = rootDir;
    }

    public string RootDir { get; }
    public string ServerDir => Path.Combine(RootDir, "server");
    public string ToolsDir => Path.Combine(RootDir, "tools");
    public string CstrikeDir => Path.Combine(ServerDir, "cstrike");
    public string MapsDir => Path.Combine(CstrikeDir, "maps");
    public string OverviewsDir => Path.Combine(CstrikeDir, "overviews");
    public string PreviewsDir => Path.Combine(ToolsDir, "previews");
    // Rendered by the Maps screen; a real screenshot in PreviewsDir always wins.
    public string GeneratedPreviewsDir => Path.Combine(PreviewsDir, "auto");
    public string HldsExe => Path.Combine(ServerDir, "hlds.exe");
    // -condebug writes into the engine folder, not into the mod folder.
    public string ConsoleLogFile => Path.Combine(ServerDir, "qconsole.log");
    public string LocalConfigFile => Path.Combine(CstrikeDir, "local.cfg");
    // The host's bot settings; only what differs from the defaults in base.cfg.
    public string LocalBotConfigFile => Path.Combine(CstrikeDir, "local_bots.cfg");

    /// <summary>The game rules the host set for one mode; fun_modes runs it after local.cfg in that mode only.</summary>
    public string LocalModeConfigFile(string modeId) {
        return Path.Combine(CstrikeDir, $"local_{modeId}.cfg");
    }

    public string ModeConfigFile(string modeId) {
        return Path.Combine(CstrikeDir, "modes", modeId + ".cfg");
    }
    public string GameLogsDir => Path.Combine(CstrikeDir, "logs");
    public string AmxLogsDir => Path.Combine(CstrikeDir, "addons", "amxmodx", "logs");
    public string BotProfileFile => Path.Combine(CstrikeDir, "BotProfile.db");
    public string ServerBaseConfigFile => Path.Combine(CstrikeDir, "modes", "base.cfg");
    public string SourceBaseConfigFile => Path.Combine(RootDir, "server-files", "cstrike", "modes", "base.cfg");

    public bool IsServerInstalled => File.Exists(HldsExe);

    public string MapCycleFile(string modeId) {
        return Path.Combine(CstrikeDir, $"mapcycle_{modeId}.txt");
    }

    public string BspFile(string mapName) {
        return Path.Combine(MapsDir, mapName + ".bsp");
    }

    public string NavFile(string mapName) {
        return Path.Combine(MapsDir, mapName + ".nav");
    }

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
