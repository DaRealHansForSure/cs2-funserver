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
    public string CstrikeDir => Package.CstrikeDir;
    public string MapsDir => Package.MapsDir;
    public string PreviewsDir => Package.PreviewsDir;
    public string HldsExe => Package.HldsExe;

    public string OverlayDir => Path.Combine(Package.RootDir, "server-files", "cstrike");
    public string OwnMapsDir => Path.Combine(Package.RootDir, "custom-maps");
    public string DownloadDir => Path.Combine(Package.ToolsDir, "downloads");
    public string WorkDir => Path.Combine(Package.ToolsDir, "work");
    public string SteamCmdDir => Path.Combine(Package.ToolsDir, "steamcmd");
    public string SteamCmdExe => Path.Combine(SteamCmdDir, "steamcmd.exe");
    public string MapListFile => Path.Combine(Package.ToolsDir, "maps.csv");
    // Maps installed from the market; created at runtime, not part of the package.
    public string MarketListFile => Path.Combine(Package.ToolsDir, "market.csv");

    public string LibListFile => Path.Combine(CstrikeDir, "liblist.gam");
    public string GameInitFile => Path.Combine(CstrikeDir, "game_init.cfg");
    public string AddonsDir => Path.Combine(CstrikeDir, "addons");
    public string MetamodDir => Path.Combine(AddonsDir, "metamod");
    public string MetamodPluginsFile => Path.Combine(MetamodDir, "plugins.ini");
    public string AmxDir => Path.Combine(AddonsDir, "amxmodx");
    public string AmxConfigsDir => Path.Combine(AmxDir, "configs");
    public string AmxUsersFile => Path.Combine(AmxConfigsDir, "users.ini");
    public string AmxPluginListFile => Path.Combine(AmxConfigsDir, "plugins.ini");
    public string AmxMapsFile => Path.Combine(AmxConfigsDir, "maps.ini");
    public string AmxScriptingDir => Path.Combine(AmxDir, "scripting");
    public string AmxPluginsDir => Path.Combine(AmxDir, "plugins");
    public string AmxCompilerExe => Path.Combine(AmxScriptingDir, "amxxpc.exe");
}
