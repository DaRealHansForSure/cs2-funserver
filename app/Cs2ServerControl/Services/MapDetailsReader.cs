using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Cs2ServerControl.Services;

public sealed record MapStat(string Label, string Value);

/// <summary>
/// Facts about one map for the details dialog: what the .bsp says (size,
/// spawns, objective), the files next to it, and how it was played according
/// to the server's game logs (one log file per map start).
/// </summary>
public static partial class MapDetailsReader {
    private const int EntitiesLump = 0;
    private const int MaxLogFiles = 500;
    private const double MetersPerUnit = 0.0254;

    public static Task<IReadOnlyList<MapStat>> ReadAsync(PackagePaths paths, string mapName, BspWireframe? wireframe) {
        return Task.Run(() => Read(paths, mapName, wireframe));
    }

    private static IReadOnlyList<MapStat> Read(PackagePaths paths, string mapName, BspWireframe? wireframe) {
        var stats = new List<MapStat>();
        string bspFile = paths.MapFile(mapName);

        stats.Add(new MapStat("Modes", ModesOf(paths, mapName)));

        if (File.Exists(bspFile)) {
            IReadOnlyDictionary<string, int> entities = ReadEntityCounts(bspFile);
            stats.Add(new MapStat("Objective", Objective(entities)));
            stats.Add(new MapStat("Spawns", $"{Count(entities, "info_player_deathmatch")} T, {Count(entities, "info_player_start")} CT"));
            stats.Add(new MapStat("File", $"{new FileInfo(bspFile).Length / 1024.0 / 1024.0:0.0} MB"));
        }

        if (wireframe != null) {
            float width = wireframe.BoundsMax.X - wireframe.BoundsMin.X;
            float depth = wireframe.BoundsMax.Y - wireframe.BoundsMin.Y;
            float height = wireframe.BoundsMax.Z - wireframe.BoundsMin.Z;
            stats.Add(new MapStat("Size", string.Create(CultureInfo.InvariantCulture,
                $"{width:0} x {depth:0} x {height:0} units ({width * MetersPerUnit:0} x {depth * MetersPerUnit:0} m)")));
        }

        string navFile = paths.NavFile(mapName);
        stats.Add(new MapStat("Bot navigation", NavText(navFile)));

        AddPlayStats(paths, mapName, stats);
        return stats;
    }

    private static string ModesOf(PackagePaths paths, string mapName) {
        List<string> modes = GameModes.All
            .Where(mode => MapCycleReader.Read(paths.MapCycleFile(mode.Id)).Contains(mapName, StringComparer.OrdinalIgnoreCase))
            .Select(mode => mode.Title)
            .ToList();
        return modes.Count == 0 ? "-" : string.Join(", ", modes);
    }

    private static string Objective(IReadOnlyDictionary<string, int> entities) {
        if (Count(entities, "func_bomb_target") + Count(entities, "info_bomb_target") > 0) {
            return "Bomb defusal";
        }
        if (Count(entities, "hostage_entity") > 0) {
            return $"Hostage rescue ({Count(entities, "hostage_entity")} hostages)";
        }
        if (Count(entities, "func_vip_safetyzone") > 0) {
            return "VIP escort";
        }
        if (Count(entities, "func_escapezone") > 0) {
            return "Escape";
        }
        return "None (fun map)";
    }

    private static string NavText(string navFile) {
        if (!File.Exists(navFile)) {
            return "Missing, zBot builds it on the first load";
        }
        try {
            NavMesh mesh = NavMeshReader.Load(navFile);
            return $"{mesh.Areas.Count} areas, {mesh.HidingSpots.Count} hiding spots";
        } catch (Exception exception) when (exception is IOException or InvalidDataException) {
            return "Present, unreadable";
        }
    }

    private static int Count(IReadOnlyDictionary<string, int> entities, string className) {
        return entities.TryGetValue(className, out int count) ? count : 0;
    }

    /// <summary>Counts entities per classname in the .bsp entity lump (plain text key/value blocks).</summary>
    internal static IReadOnlyDictionary<string, int> ReadEntityCounts(string bspFile) {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        try {
            using var stream = File.OpenRead(bspFile);
            using var reader = new BinaryReader(stream);
            reader.ReadInt32();
            stream.Seek(4 + EntitiesLump * 8, SeekOrigin.Begin);
            int offset = reader.ReadInt32();
            int length = reader.ReadInt32();
            if (offset <= 0 || length <= 0 || offset + (long)length > stream.Length) {
                return counts;
            }

            stream.Seek(offset, SeekOrigin.Begin);
            string text = Encoding.ASCII.GetString(reader.ReadBytes(length));
            foreach (Match match in ClassNamePattern().Matches(text)) {
                string className = match.Groups["name"].Value;
                counts[className] = Count(counts, className) + 1;
            }
        } catch (IOException) {
            // A map that is being replaced right now simply shows fewer facts.
        }
        return counts;
    }

    private static void AddPlayStats(PackagePaths paths, string mapName, List<MapStat> stats) {
        string logsDir = paths.GameLogsDir;
        if (!Directory.Exists(logsDir)) {
            stats.Add(new MapStat("Played", "No game logs yet"));
            return;
        }

        int timesPlayed = 0;
        DateTime? lastPlayed = null;
        int kills = 0;
        int knifeKills = 0;
        var weapons = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var killers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        IEnumerable<FileInfo> logFiles = new DirectoryInfo(logsDir).EnumerateFiles("L*.log")
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .Take(MaxLogFiles);
        foreach (FileInfo logFile in logFiles) {
            try {
                if (!IsLogOfMap(logFile.FullName, mapName)) {
                    continue;
                }
                timesPlayed++;
                if (lastPlayed == null || logFile.LastWriteTime > lastPlayed) {
                    lastPlayed = logFile.LastWriteTime;
                }

                foreach (string line in File.ReadLines(logFile.FullName)) {
                    Match kill = KillPattern().Match(line);
                    if (!kill.Success) {
                        continue;
                    }
                    kills++;
                    string weapon = kill.Groups["weapon"].Value;
                    weapons[weapon] = (weapons.TryGetValue(weapon, out int weaponCount) ? weaponCount : 0) + 1;
                    string killer = kill.Groups["killer"].Value;
                    killers[killer] = (killers.TryGetValue(killer, out int killerCount) ? killerCount : 0) + 1;
                    if (WeaponNames.IsKnife(weapon)) {
                        knifeKills++;
                    }
                }
            } catch (IOException) {
                // The running server holds today's log open; skip it this time.
            }
        }

        if (timesPlayed == 0) {
            stats.Add(new MapStat("Played", "Never on this server"));
            return;
        }

        string times = timesPlayed == 1 ? "once" : $"{timesPlayed} times";
        stats.Add(new MapStat("Played", $"{times}, last on {lastPlayed:dd.MM.yyyy HH:mm}"));
        stats.Add(new MapStat("Kills", $"{kills} ({knifeKills} with the knife)"));
        if (weapons.Count > 0) {
            KeyValuePair<string, int> topWeapon = weapons.MaxBy(pair => pair.Value);
            stats.Add(new MapStat("Top weapon", $"{WeaponNames.Display(topWeapon.Key)} ({topWeapon.Value} kills)"));
        }
        if (killers.Count > 0) {
            KeyValuePair<string, int> topKiller = killers.MaxBy(pair => pair.Value);
            stats.Add(new MapStat("Best player", $"{topKiller.Key} ({topKiller.Value} kills)"));
        }
    }

    private static bool IsLogOfMap(string logFile, string mapName) {
        // The map is named in the first lines of every log file.
        foreach (string line in File.ReadLines(logFile).Take(4)) {
            Match match = LoadingMapPattern().Match(line);
            if (match.Success) {
                return string.Equals(match.Groups["map"].Value, mapName, StringComparison.OrdinalIgnoreCase);
            }
        }
        return false;
    }

    [GeneratedRegex("\"classname\"\\s+\"(?<name>[^\"]+)\"")]
    private static partial Regex ClassNamePattern();

    [GeneratedRegex("Loading map \"(?<map>[^\"]+)\"")]
    private static partial Regex LoadingMapPattern();

    // "Tom<4><STEAM_ID_LAN><CT>" killed "Mike<5><STEAM_ID_LAN><TERRORIST>" with "awp"
    [GeneratedRegex("\"(?<killer>[^\"<]+)<\\d+><[^>]*><[^>]*>\" killed \"[^\"]+\" with \"(?<weapon>[^\"]+)\"")]
    private static partial Regex KillPattern();
}
