namespace Cs2ServerControl.Install;

internal sealed record QuakeSoundPack(string Url, string FileName, string Folder);

/// <summary>A miscstats sound (path under cstrike\sound) and where it comes from.</summary>
internal sealed record QuakeSound(string TargetName, string PackName, string SourceName);

/// <summary>Download locations, pinned versions and fixed lists of the installer.</summary>
internal static class InstallComponents {
    public const string BrowserUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64)";
    public const int HldsInstallAttempts = 3;
    public const string FunPluginsMarker = "; --- LAN Fun Server ---";

    // Pinned versions, so every LAN party gets the same tested setup.
    // To update, replace the URL with a newer release from the same project.
    public const string SteamCmdUrl = "https://steamcdn-a.akamaihd.net/client/installer/steamcmd.zip";
    public const string ReHldsUrl = "https://github.com/rehlds/ReHLDS/releases/download/3.15.0.896/rehlds-bin-3.15.0.896.zip";
    public const string ReGameDllUrl = "https://github.com/rehlds/ReGameDLL_CS/releases/download/5.30.0.814/regamedll-bin-5.30.0.814.zip";
    public const string MetamodUrl = "https://github.com/rehlds/Metamod-R/releases/download/1.3.0.149/metamod-bin-1.3.0.149.zip";
    public const string AmxBaseUrl = "https://github.com/alliedmodders/amxmodx/releases/download/1.10.0.5487/amxmodx-1.10.0-git5487-base-windows.zip";
    public const string AmxCstrikeUrl = "https://github.com/alliedmodders/amxmodx/releases/download/1.10.0.5487/amxmodx-1.10.0-git5487-cstrike-windows.zip";
    // zBot is built into ReGameDLL, only its data files ship separately.
    // Keep the tag in this URL equal to the ReGameDll version above.
    public const string ZBotProfilesUrl = "https://raw.githubusercontent.com/rehlds/ReGameDLL_CS/5.30.0.814/regamedll/extra/zBot/bot_profiles.zip";

    // Quake style announcer sounds for the stock miscstats plugin, from two
    // GameBanana packs (both 22 kHz mono, which the HL1 engine plays reliably).
    // Blackshot: "Blackshot Killstreak Quakesound!" by Apicvector (Sound 24481).
    // UtRecreation: "Kill sounds pack (UT recreation)" by Mike Isakov
    // (Sound 90672, CC BY-NC-ND 4.0, renaming allowed by its readme).
    public static readonly IReadOnlyDictionary<string, QuakeSoundPack> QuakeSoundPacks = new Dictionary<string, QuakeSoundPack> {
        ["Blackshot"] = new("https://gamebanana.com/dl/179793", @"sounds\blackshot_killstreak_quakesounds.rar", @"Blackshot_Killstreak_Quakesounds\Quakesounds"),
        ["UtRecreation"] = new("https://gamebanana.com/dl/1738096", @"sounds\kill_sounds_pack_ut_recreation.rar", @"Kill sounds pack (UT recreation)\22khz 16bit mono"),
    };

    // Neither pack has "one and only", "may the force" or a grenade line, so
    // those use the closest fitting announcements.
    public static readonly IReadOnlyList<QuakeSound> QuakeSounds = [
        new(@"misc\firstblood.wav", "Blackshot", "firstblood.wav"),
        new(@"misc\headshot.wav", "Blackshot", "headshot.wav"),
        new(@"misc\doublekill.wav", "Blackshot", "doublekill.wav"),
        new(@"misc\multikill.wav", "Blackshot", "multikill.wav"),
        new(@"misc\ultrakill.wav", "Blackshot", "ultrakill.wav"),
        new(@"misc\killingspree.wav", "Blackshot", "killingspree.wav"),
        new(@"misc\rampage.wav", "Blackshot", "rampage.wav"),
        new(@"misc\unstoppable.wav", "Blackshot", "unstoppable.wav"),
        new(@"misc\prepare.wav", "Blackshot", "prepare.wav"),
        new(@"misc\humiliation.wav", "Blackshot", "knife.wav"),
        new(@"misc\oneandonly.wav", "Blackshot", "holyshit.wav"),
        new(@"misc\monsterkill.wav", "UtRecreation", "MonsterKill.wav"),
        new(@"misc\godlike.wav", "UtRecreation", "Godlike.wav"),
        new(@"misc\maytheforce.wav", "UtRecreation", "WickedSick.wav"),
        new(@"djeyl\grenade.wav", "UtRecreation", "Ownage.wav"),
    ];

    // Map files we copy out of downloaded archives. Anything else (exe, dll,
    // readme files) stays in the archive.
    public static readonly IReadOnlySet<string> MapContentExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
        ".bsp", ".wad", ".res", ".txt", ".nav", ".wav", ".mp3", ".mdl", ".spr", ".tga", ".bmp",
    };

    public static readonly IReadOnlyList<string> MapContentFolders = ["maps", "sound", "models", "sprites", "gfx", "overviews"];

    // The resource folders of a loose archive: everything above except maps.
    public static readonly IReadOnlyList<string> LooseResourceFolders = ["sound", "models", "sprites", "gfx", "overviews"];

    public static readonly IReadOnlySet<string> StockMaps = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
        "as_oilrig", "cs_747", "cs_assault", "cs_backalley", "cs_estate", "cs_havana", "cs_italy",
        "cs_militia", "cs_office", "cs_siege", "de_airstrip", "de_aztec", "de_cbble", "de_chateau",
        "de_dust", "de_dust2", "de_inferno", "de_nuke", "de_piranesi", "de_prodigy", "de_storm",
        "de_survivor", "de_torn", "de_train", "de_vertigo",
    };

    public static readonly IReadOnlyList<string> FunPlugins = [
        "fun_modes.amxx",
        "fun_hook.amxx",
        "fun_grab.amxx",
        "fun_parachute.amxx",
        "fun_admincmds.amxx",
        "fun_gungame.amxx",
        "fun_zombie.amxx",
        "fun_menu.amxx",
        "fun_rounds.amxx",
        "fun_deathrun.amxx",
        "fun_spawns.amxx",
        "fun_streaks.amxx",
        "fun_livemap.amxx",
    ];

    // The legacy HLDS build ships an old steamclient.dll that the Steam
    // network no longer accepts, see ServerComponentsInstaller.
    public static readonly IReadOnlyList<string> SteamClientFiles = ["steamclient.dll", "tier0_s.dll", "vstdlib_s.dll", "SDL3.dll"];
}
