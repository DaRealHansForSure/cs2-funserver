using System.Globalization;
using System.IO;

namespace Cs2ServerControl.Services;

public sealed record MapStat(string Label, string Value);

/// <summary>
/// Facts about one map for the details dialog: its modes, the map package,
/// its size from the geometry, and how it was played according to the
/// server's game logs (one log file per map start).
/// </summary>
public static class MapDetailsReader {
    private const int MaxLogFiles = 500;
    private const double MetersPerUnit = 0.0254;

    public static Task<IReadOnlyList<MapStat>> ReadAsync(PackagePaths paths, string mapName, MapWireframe? wireframe) {
        return Task.Run(() => Read(paths, mapName, wireframe));
    }

    private static IReadOnlyList<MapStat> Read(PackagePaths paths, string mapName, MapWireframe? wireframe) {
        var stats = new List<MapStat>();
        string mapFile = paths.MapFile(mapName);

        stats.Add(new MapStat("Modes", ModesOf(paths, mapName)));

        if (File.Exists(mapFile)) {
            stats.Add(new MapStat("File", $"{new FileInfo(mapFile).Length / 1024.0 / 1024.0:0.0} MB"));
        }

        if (wireframe != null) {
            float width = wireframe.BoundsMax.X - wireframe.BoundsMin.X;
            float depth = wireframe.BoundsMax.Y - wireframe.BoundsMin.Y;
            float height = wireframe.BoundsMax.Z - wireframe.BoundsMin.Z;
            stats.Add(new MapStat("Size", string.Create(CultureInfo.InvariantCulture,
                $"{width:0} x {depth:0} x {height:0} units ({width * MetersPerUnit:0} x {depth * MetersPerUnit:0} m)")));
        }

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

    /// <summary>
    /// How often the map ran and its kills, from the FunServer plugin's daily
    /// logs: every line after a map change to this map, up to the next one.
    /// </summary>
    private static void AddPlayStats(PackagePaths paths, string mapName, List<MapStat> stats) {
        List<FileInfo> logs = new StatsReader(paths).FunLogs(DateTime.MinValue);
        if (logs.Count == 0) {
            stats.Add(new MapStat("Played", "No game logs yet"));
            return;
        }

        int timesPlayed = 0;
        DateTime? lastPlayed = null;
        bool isOnMap = false;
        var killLines = new List<string>();
        foreach (string line in logs.TakeLast(MaxLogFiles).SelectMany(StatsReader.ReadLinesShared)) {
            if (StatsReader.TryReadMapChange(line, out string map, out DateTime startedAt)) {
                isOnMap = string.Equals(map, mapName, StringComparison.OrdinalIgnoreCase);
                if (isOnMap) {
                    timesPlayed++;
                    lastPlayed = startedAt;
                }
                continue;
            }
            if (isOnMap) {
                killLines.Add(line);
            }
        }

        if (timesPlayed == 0) {
            stats.Add(new MapStat("Played", "Never on this server"));
            return;
        }

        IReadOnlyList<PlayerStats> players = StatsReader.BuildLeaderboard(killLines, since: null);
        int kills = players.Sum(player => player.Kills);
        int knifeKills = players.Sum(player => player.KnifeKills);
        string times = timesPlayed == 1 ? "once" : $"{timesPlayed} times";
        stats.Add(new MapStat("Played", $"{times}, last on {lastPlayed:dd.MM.yyyy HH:mm}"));
        stats.Add(new MapStat("Kills", $"{kills} ({knifeKills} with the knife)"));
        PlayerStats? best = players.FirstOrDefault(player => player.Kills > 0);
        if (best != null) {
            stats.Add(new MapStat("Best player", $"{best.Name} ({best.Kills} kills, mostly {WeaponNames.Display(best.FavoriteWeapon)})"));
        }
    }
}
