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

    // Map files we copy out of downloaded archives. Anything else (exe, dll,
    // readme files) stays in the archive.
    public static readonly IReadOnlySet<string> MapContentExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
        ".vpk", ".txt", ".nav", ".png", ".jpg",
    };

    public static readonly IReadOnlyList<string> MapContentFolders = ["maps"];

    // The resource folders of a loose archive: everything above except maps.
    public static readonly IReadOnlyList<string> LooseResourceFolders = [];

    // Valve's maps that come with the server, for the "Source" column of the Maps screen.
    public static readonly IReadOnlySet<string> StockMaps = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
        "ar_baggage", "ar_pool_day", "ar_shoots", "cs_italy", "cs_office", "de_ancient", "de_anubis",
        "de_dust2", "de_inferno", "de_mirage", "de_nuke", "de_overpass", "de_train", "de_vertigo",
    };
}
