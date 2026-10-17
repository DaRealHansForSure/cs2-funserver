using System.Globalization;
using System.Text.RegularExpressions;

namespace Cs2ServerControl.Install;

/// <summary>Which modes a map fits, judged by its name, and the per mode map cycles built from that.</summary>
internal static partial class MapModes {
    /// <summary>The modes in the order their map lists are reported.</summary>
    public static readonly IReadOnlyList<string> AllModes = ["classic", "gungame", "deathmatch", "zombie", "deathrun"];

    private static readonly IReadOnlySet<string> GoodDeathmatchMaps = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
        "de_dust2", "de_inferno", "de_nuke", "de_aztec", "de_train", "cs_assault", "cs_italy", "cs_office", "cs_mansion",
    };

    private static readonly IReadOnlySet<string> GoodZombieMaps = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
        "de_dust2", "de_inferno", "de_nuke", "de_aztec", "de_train", "de_survivor", "de_vertigo",
    };

    private static readonly IReadOnlySet<string> BigGunGameMaps = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
        "de_dust2", "cs_assault", "cs_mansion",
    };

    [GeneratedRegex("^(35_?hp|he_|ka_)", RegexOptions.IgnoreCase)]
    private static partial Regex SpecialWeaponMapPattern();

    [GeneratedRegex("^(fy_|aim_|awp_|gg_|fun_)", RegexOptions.IgnoreCase)]
    private static partial Regex SmallFunMapPattern();

    public static IReadOnlyList<string> ModesOf(string mapName) {
        if (HasPrefix(mapName, "zm_")) {
            return ["zombie"];
        }

        // Deathrun maps only make sense with the one-trapper rules of their mode.
        if (HasPrefix(mapName, "deathrun_")) {
            return ["deathrun"];
        }

        List<string> modes = ["classic"];

        // Knife and grenade maps force their own weapons, which fights GunGame
        // levels and Deathmatch spawn weapons, so they only run in classic.
        if (SpecialWeaponMapPattern().IsMatch(mapName)) {
            return modes;
        }

        // Surf with instant respawn and random weapons is a classic of its own.
        if (HasPrefix(mapName, "surf_")) {
            modes.Add("deathmatch");
            return modes;
        }

        bool isSmallFunMap = SmallFunMapPattern().IsMatch(mapName)
            || mapName.Contains("rats", StringComparison.OrdinalIgnoreCase)
            || HasPrefix(mapName, "de_dust2_2x2");
        if (isSmallFunMap || BigGunGameMaps.Contains(mapName)) {
            modes.Add("gungame");
        }
        if (isSmallFunMap || GoodDeathmatchMaps.Contains(mapName)) {
            modes.Add("deathmatch");
        }
        if (GoodZombieMaps.Contains(mapName)) {
            modes.Add("zombie");
        }
        return modes;
    }

    /// <summary>
    /// The map cycle of every mode, keyed like <see cref="AllModes"/>. Fun maps
    /// come first, so a fresh server starts on something fun instead of
    /// cycling through all the stock maps.
    /// </summary>
    public static IReadOnlyDictionary<string, List<string>> BuildCycles(IEnumerable<string> installedMaps) {
        // Sorted like Sort-Object -Unique did: case-insensitive, by culture rules.
        List<string> sortedMaps = [.. installedMaps
            .Select(map => map.ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Create(CultureInfo.InvariantCulture, ignoreCase: true))];

        IEnumerable<string> orderedMaps = sortedMaps.Where(map => !InstallComponents.StockMaps.Contains(map))
            .Concat(sortedMaps.Where(map => InstallComponents.StockMaps.Contains(map)));

        Dictionary<string, List<string>> mapsByMode = AllModes.ToDictionary(mode => mode, _ => new List<string>());
        foreach (string mapName in orderedMaps) {
            foreach (string mode in ModesOf(mapName)) {
                mapsByMode[mode].Add(mapName);
            }
        }
        return mapsByMode;
    }

    private static bool HasPrefix(string mapName, string prefix) {
        return mapName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }
}
