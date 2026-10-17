using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Cs2ServerControl.Services;

/// <summary>One admin: a name for the list and the 64 bit SteamID that identifies them.</summary>
public sealed record Admin(string Name, ulong SteamId);

/// <summary>
/// The admins of the FunServer plugin, kept in CounterStrikeSharp's own
/// file (addons\counterstrikesharp\configs\admins.json, the host's file in
/// the server folder, never overwritten by the installer). Every admin gets
/// @css/root. As long as the file lists nobody, the plugin treats everybody
/// on the LAN as admin.
/// </summary>
public static partial class AdminList {
    private const ulong SteamId64Base = 76561197960265728UL;
    private const string RootFlag = "@css/root";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    [GeneratedRegex(@"(?<!\d)(?<id>7656119\d{10})(?!\d)")]
    private static partial Regex SteamId64Pattern();

    [GeneratedRegex(@"^STEAM_[0-5]:(?<y>[01]):(?<z>\d+)$", RegexOptions.IgnoreCase)]
    private static partial Regex SteamId2Pattern();

    [GeneratedRegex(@"^\[?U:1:(?<account>\d+)\]?$", RegexOptions.IgnoreCase)]
    private static partial Regex SteamId3Pattern();

    public static string FilePath(PackagePaths paths) {
        return Path.Combine(paths.AddonsDir, "counterstrikesharp", "configs", "admins.json");
    }

    /// <summary>
    /// The 64 bit SteamID from what a host pastes: the number itself, a
    /// steamcommunity.com/profiles/ link, STEAM_0:1:2345 or [U:1:4691].
    /// Null for anything else, a /id/name link included (that needs Steam's
    /// web API to resolve).
    /// </summary>
    public static ulong? ParseSteamId(string input) {
        string text = input.Trim();
        Match steamId64 = SteamId64Pattern().Match(text);
        if (steamId64.Success) {
            return ulong.Parse(steamId64.Groups["id"].Value, CultureInfo.InvariantCulture);
        }
        Match steamId2 = SteamId2Pattern().Match(text);
        if (steamId2.Success) {
            ulong y = ulong.Parse(steamId2.Groups["y"].Value, CultureInfo.InvariantCulture);
            ulong z = ulong.Parse(steamId2.Groups["z"].Value, CultureInfo.InvariantCulture);
            return SteamId64Base + z * 2 + y;
        }
        Match steamId3 = SteamId3Pattern().Match(text);
        if (steamId3.Success) {
            return SteamId64Base + ulong.Parse(steamId3.Groups["account"].Value, CultureInfo.InvariantCulture);
        }
        return null;
    }

    public static IReadOnlyList<Admin> Load(PackagePaths paths) {
        string file = FilePath(paths);
        try {
            if (!File.Exists(file)) {
                return [];
            }
            return Parse(File.ReadAllText(file));
        } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException) {
            return [];
        }
    }

    /// <summary>CounterStrikeSharp's format: { "name": { "identity": "7656...", "flags": [...] } }.</summary>
    internal static IReadOnlyList<Admin> Parse(string json) {
        var admins = new List<Admin>();
        if (JsonNode.Parse(json) is not JsonObject root) {
            return admins;
        }
        foreach ((string name, JsonNode? entry) in root) {
            string? identity = entry?["identity"]?.GetValue<string>();
            if (identity != null && ulong.TryParse(identity, NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong steamId)) {
                admins.Add(new Admin(name, steamId));
            }
        }
        return admins;
    }

    public static void Save(PackagePaths paths, IReadOnlyList<Admin> admins) {
        string file = FilePath(paths);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, Format(admins));
    }

    internal static string Format(IReadOnlyList<Admin> admins) {
        var root = new JsonObject();
        foreach (Admin admin in admins) {
            // The name is the key; two admins with one name would overwrite each other.
            string key = root.ContainsKey(admin.Name) ? $"{admin.Name} ({admin.SteamId})" : admin.Name;
            root[key] = new JsonObject {
                ["identity"] = admin.SteamId.ToString(CultureInfo.InvariantCulture),
                ["flags"] = new JsonArray(RootFlag),
            };
        }
        return root.ToJsonString(JsonOptions);
    }
}
