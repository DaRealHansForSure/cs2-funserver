using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace Cs2ServerControl.Services;

public sealed record PlayerStats(
    string Name, bool IsBot, int Kills, int Deaths, int Suicides, int KnifeKills, int GrenadeKills, string FavoriteWeapon) {
    /// <summary>Kills per death, kills alone when somebody never died.</summary>
    public double KillDeathRatio => Deaths == 0 ? Kills : Math.Round((double)Kills / Deaths, 2);
}

public sealed record MapPlay(string Map, string? ModeId, DateTime StartedAt, TimeSpan Duration, bool IsRunning);

/// <summary>
/// Builds the evening's statistics from the plain text logs the server
/// writes anyway: HLDS logs every kill (killer, victim, weapon, BOT marker),
/// AMX Mod X every map change and the mode that came with it.
/// </summary>
public sealed partial class StatsReader(PackagePaths paths) {
    /// <summary>Kills since the given time, or of the newest log (the running map) when null.</summary>
    public IReadOnlyList<PlayerStats> ReadLeaderboard(DateTime? since) {
        if (!Directory.Exists(paths.GameLogsDir)) {
            return [];
        }

        List<FileInfo> logs = new DirectoryInfo(paths.GameLogsDir).GetFiles("L*.log")
            .OrderBy(file => file.LastWriteTime)
            .ToList();
        if (since == null) {
            // HLDS starts a new log file on every map.
            logs = logs.Count == 0 ? [] : [logs[^1]];
        } else {
            logs = logs.Where(file => file.LastWriteTime >= since.Value).ToList();
        }

        return BuildLeaderboard(logs.SelectMany(ReadLinesShared), since);
    }

    /// <summary>The maps since the given time with how long each one ran.</summary>
    public IReadOnlyList<MapPlay> ReadMapsPlayed(DateTime since, DateTime now, bool isServerRunning) {
        if (!Directory.Exists(paths.PluginLogsDir)) {
            return [];
        }

        IEnumerable<string> lines = new DirectoryInfo(paths.PluginLogsDir).GetFiles("L*.log")
            .Where(file => file.LastWriteTime >= since)
            .OrderBy(file => file.LastWriteTime)
            .SelectMany(ReadLinesShared);
        return BuildMapsPlayed(lines, since, now, isServerRunning);
    }

    internal static IReadOnlyList<PlayerStats> BuildLeaderboard(IEnumerable<string> lines, DateTime? since) {
        var players = new Dictionary<string, PlayerTally>(StringComparer.Ordinal);

        foreach (string line in lines) {
            Match kill = KillLine().Match(line);
            if (kill.Success) {
                if (!IsInPeriod(kill.Groups["time"].Value, since)) {
                    continue;
                }

                string weapon = kill.Groups["weapon"].Value;
                PlayerTally killer = Tally(players, kill.Groups["killer"].Value, kill.Groups["killerAuth"].Value);
                PlayerTally victim = Tally(players, kill.Groups["victim"].Value, kill.Groups["victimAuth"].Value);
                killer.Kills++;
                killer.CountWeapon(weapon);
                if (weapon == "knife") {
                    killer.KnifeKills++;
                }
                if (weapon == "grenade") {
                    killer.GrenadeKills++;
                }
                victim.Deaths++;
                continue;
            }

            Match suicide = SuicideLine().Match(line);
            if (suicide.Success && IsInPeriod(suicide.Groups["time"].Value, since)) {
                PlayerTally player = Tally(players, suicide.Groups["name"].Value, suicide.Groups["auth"].Value);
                player.Deaths++;
                player.Suicides++;
            }
        }

        return players.Values
            .Select(tally => tally.ToStats())
            .OrderByDescending(stats => stats.Kills)
            .ThenBy(stats => stats.Deaths)
            .ThenBy(stats => stats.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    internal static IReadOnlyList<MapPlay> BuildMapsPlayed(IEnumerable<string> lines, DateTime since, DateTime now, bool isServerRunning) {
        var starts = new List<(string Map, DateTime StartedAt, string? Mode)>();

        foreach (string line in lines) {
            Match mapChange = MapChangeLine().Match(line);
            if (mapChange.Success && TryParseLogTime(mapChange.Groups["time"].Value, out DateTime startedAt)) {
                starts.Add((mapChange.Groups["map"].Value, startedAt, null));
                continue;
            }

            // fun_modes logs the mode right after the map change it belongs to.
            Match mode = ActiveModeLine().Match(line);
            if (mode.Success && starts.Count > 0 && starts[^1].Mode == null) {
                starts[^1] = (starts[^1].Map, starts[^1].StartedAt, mode.Groups["mode"].Value);
            }
        }

        var plays = new List<MapPlay>();
        for (int i = 0; i < starts.Count; i++) {
            if (starts[i].StartedAt < since) {
                continue;
            }

            bool isLast = i == starts.Count - 1;
            bool isRunning = isLast && isServerRunning;
            // A stopped server leaves no end time, the last map then has none either.
            TimeSpan duration;
            if (!isLast) {
                duration = starts[i + 1].StartedAt - starts[i].StartedAt;
            } else if (isRunning) {
                duration = now - starts[i].StartedAt;
            } else {
                duration = TimeSpan.Zero;
            }
            plays.Add(new MapPlay(starts[i].Map, starts[i].Mode, starts[i].StartedAt, duration, isRunning));
        }
        return plays;
    }

    private static PlayerTally Tally(Dictionary<string, PlayerTally> players, string name, string auth) {
        if (!players.TryGetValue(name, out PlayerTally? tally)) {
            tally = new PlayerTally(name, auth.Equals("BOT", StringComparison.OrdinalIgnoreCase));
            players[name] = tally;
        }
        return tally;
    }

    private static bool IsInPeriod(string logTime, DateTime? since) {
        if (since == null) {
            return true;
        }
        return TryParseLogTime(logTime, out DateTime time) && time >= since.Value;
    }

    private static bool TryParseLogTime(string text, out DateTime time) {
        return DateTime.TryParseExact(text, "MM/dd/yyyy - HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out time);
    }

    private static IEnumerable<string> ReadLinesShared(FileInfo file) {
        var lines = new List<string>();
        try {
            // HLDS keeps the newest log open, so it has to be shared for reading.
            using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            string? line;
            while ((line = reader.ReadLine()) != null) {
                lines.Add(line);
            }
        } catch (IOException) {
            // A log that can not be read only leaves a gap in the statistics.
        }
        return lines;
    }

    private sealed class PlayerTally(string name, bool isBot) {
        private readonly Dictionary<string, int> _killsByWeapon = new(StringComparer.Ordinal);

        public int Kills { get; set; }
        public int Deaths { get; set; }
        public int Suicides { get; set; }
        public int KnifeKills { get; set; }
        public int GrenadeKills { get; set; }

        public void CountWeapon(string weapon) {
            _killsByWeapon[weapon] = _killsByWeapon.GetValueOrDefault(weapon) + 1;
        }

        public PlayerStats ToStats() {
            string favorite = _killsByWeapon.Count == 0 ? "-" : _killsByWeapon.MaxBy(pair => pair.Value).Key;
            return new PlayerStats(name, isBot, Kills, Deaths, Suicides, KnifeKills, GrenadeKills, favorite);
        }
    }

    // L 10/17/2026 - 00:34:24: "Peter<33><STEAM_ID_LAN><CT>" killed "Tom<29><BOT><TERRORIST>" with "sg552"
    [GeneratedRegex(@"^L (?<time>\d{2}/\d{2}/\d{4} - \d{2}:\d{2}:\d{2}): ""(?<killer>.+?)<\d+><(?<killerAuth>[^>]*)><[^>]*>"" killed ""(?<victim>.+?)<\d+><(?<victimAuth>[^>]*)><[^>]*>"" with ""(?<weapon>[^""]+)""")]
    private static partial Regex KillLine();

    // L 10/17/2026 - 00:39:00: "Peter<4><STEAM_ID_LAN><TERRORIST>" committed suicide with "world"
    [GeneratedRegex(@"^L (?<time>\d{2}/\d{2}/\d{4} - \d{2}:\d{2}:\d{2}): ""(?<name>.+?)<\d+><(?<auth>[^>]*)><[^>]*>"" committed suicide with ")]
    private static partial Regex SuicideLine();

    [GeneratedRegex(@"^L (?<time>\d{2}/\d{2}/\d{4} - \d{2}:\d{2}:\d{2}): -------- Mapchange to (?<map>\S+) --------")]
    private static partial Regex MapChangeLine();

    [GeneratedRegex(@"\[fun_modes\.amxx\] Active mode: (?<mode>\w+)")]
    private static partial Regex ActiveModeLine();
}
