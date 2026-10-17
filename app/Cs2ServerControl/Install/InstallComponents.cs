namespace Cs2ServerControl.Install;

/// <summary>Download locations, pinned versions and fixed lists of the installer.</summary>
internal static class InstallComponents {
    public const string BrowserUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64)";

    // The CS2 dedicated server is the game's own app; about 60 GB.
    public const int Cs2AppId = 730;
    // SteamCMD sometimes stops half way on a first big download; a second run resumes it.
    public const int ServerInstallAttempts = 3;

    // Pinned versions, so every LAN party gets the same tested setup. After a
    // CS2 update these may need newer builds: Metamod from mms.alliedmods.net
    // (2.0 branch), CounterStrikeSharp from its GitHub releases ("with-runtime",
    // so the server needs no .NET installed).
    public const string SteamCmdUrl = "https://steamcdn-a.akamaihd.net/client/installer/steamcmd.zip";
    public const string MetamodUrl = "https://mms.alliedmods.net/mmsdrop/2.0/mmsource-2.0.0-git1481-windows.zip";
    public const string CounterStrikeSharpUrl = "https://github.com/roflmuffin/CounterStrikeSharp/releases/download/v1.0.376/counterstrikesharp-with-runtime-windows-1.0.376.zip";

    // Our own plugins, built with the package (plugins\ in the package root).
    public static readonly IReadOnlyList<string> FunPlugins = ["FunServer"];
}
