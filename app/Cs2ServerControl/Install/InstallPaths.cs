using System.IO;
using Cs2ServerControl.Services;

namespace Cs2ServerControl.Install;

/// <summary>The locations only the installer needs, on top of <see cref="PackagePaths"/>.</summary>
internal sealed class InstallPaths {
    public InstallPaths(PackagePaths package) {
        Package = package;
    }

    public PackagePaths Package { get; }

    public string ServerDir => Package.ServerDir;
    public string GameDir => Package.GameDir;
    public string MapsDir => Package.MapsDir;
    public string PreviewsDir => Package.PreviewsDir;
    public string Cs2Exe => Package.Cs2Exe;

    // The package's own files for game\csgo (configs), copied over on every install.
    public string OverlayDir => Path.Combine(Package.RootDir, "server-files", "csgo");
    // Our built plugins, one folder per plugin, copied into CounterStrikeSharp's plugins folder.
    public string PluginsSourceDir => Path.Combine(Package.RootDir, "plugins");
    public string OwnMapsDir => Path.Combine(Package.RootDir, "custom-maps");
    public string DownloadDir => Path.Combine(Package.ToolsDir, "downloads");
    public string WorkDir => Path.Combine(Package.ToolsDir, "work");
    public string SteamCmdDir => Path.Combine(Package.ToolsDir, "steamcmd");
    public string SteamCmdExe => Path.Combine(SteamCmdDir, "steamcmd.exe");
    public string MapListFile => Path.Combine(Package.ToolsDir, "maps.csv");
    // Maps installed from the market; created at runtime, not part of the package.
    public string MarketListFile => Path.Combine(Package.ToolsDir, "market.csv");

    public string GameInfoFile => Path.Combine(GameDir, "gameinfo.gi");
    public string AddonsDir => Package.AddonsDir;
    public string MetamodDir => Path.Combine(AddonsDir, "metamod");
    public string CounterStrikeSharpDir => Path.Combine(AddonsDir, "counterstrikesharp");
    public string CounterStrikeSharpPluginsDir => Path.Combine(CounterStrikeSharpDir, "plugins");
}
