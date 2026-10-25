using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace Cs2ServerControl.Services;

public sealed record WeaponTally(string Weapon, int Kills, int Headshots);

/// <summary>One other player: how often this player killed them and how often they killed this player.</summary>
public sealed record OpponentTally(string Name, bool IsBot, int Kills, int Deaths);

public sealed record MapTally(string Map, int Kills, int Deaths);

/// <summary>A kill or death of one player, seen from that player: IsKill false means Other killed them.</summary>
public sealed record PlayerEvent(DateTime Time, string Map, bool IsKill, string Other, string Weapon, bool IsHeadshot);

public sealed record PlayerStats(
    string Name, bool IsBot, int Kills, int Deaths, int Suicides, int KnifeKills, int GrenadeKills, string FavoriteWeapon) {
    /// <summary>Kills per death, kills alone when somebody never died.</summary>
    public double KillDeathRatio => Deaths == 0 ? Kills : Math.Round((double)Kills / Deaths, 2);

    public int Headshots { get; init; }

    /// <summary>The most kills in a row without dying in between.</summary>
    public int BestStreak { get; init; }

    public double HeadshotPercent => Kills == 0 ? 0 : Math.Round(100.0 * Headshots / Kills, 0);

    /// <summary>Most kills first.</summary>
    public IReadOnlyList<WeaponTally> Weapons { get; init; } = [];

    /// <summary>The players this one met most first (kills plus deaths).</summary>
    public IReadOnlyList<OpponentTally> Opponents { get; init; } = [];

    /// <summary>In the order the maps were played.</summary>
    public IReadOnlyList<MapTally> Maps { get; init; } = [];

    /// <summary>The newest kills and deaths first, at most RecentEventCount.</summary>
    public IReadOnlyList<PlayerEvent> RecentEvents { get; init; } = [];
}

public sealed record MapPlay(string Map, string? ModeId, DateTime StartedAt, TimeSpan Duration, bool IsRunning);

/// <summary>
/// Builds the evening's statistics from the FunServer plugin's daily log
/// (addons\counterstrikesharp\logs\fun-yyyyMMdd.log): every kill (killer,
/// victim, weapon, BOT marker), every map change and the mode that came with
/// it, in the HLDS log format of the CS 1.6 server.
/// </summary>
public sealed partial class StatsReader(PackagePaths paths) {
    /// <summary>Kills since the given time, or of the running map (since its last map change) when null.</summary>
    public IReadOnlyList<PlayerStats> ReadLeaderboard(DateTime? since) {
        List<FileInfo> logs = FunLogs(since ?? DateTime.MinValue);
        if (since != null) {
            return BuildLeaderboard(logs.SelectMany(ReadLinesShared), since);
        }
        if (logs.Count == 0) {
            return [];
        }
        // One log holds the whole day; the running map starts at its last map change.
        List<string> lines = ReadLinesShared(logs[^1]).ToList();
        int lastMapChange = lines.FindLastIndex(line => MapChangeLine().IsMatch(line));
        return BuildLeaderboard(lastMapChange < 0 ? lines : lines.Skip(lastMapChange), since: null);
    }

    /// <summary>The maps since the given time with how long each one ran.</summary>
    public IReadOnlyList<MapPlay> ReadMapsPlayed(DateTime since, DateTime now, bool isServerRunning) {
        IEnumerable<string> lines = FunLogs(since).SelectMany(ReadLinesShared);
        return BuildMapsPlayed(lines, since, now, isServerRunning);
    }

    /// <summary>The FunServer plugin's daily logs that may hold lines since the given time, oldest first.</summary>
    internal List<FileInfo> FunLogs(DateTime since) {
        if (!Directory.Exists(paths.PluginLogsDir)) {
            return [];
        }
        return new DirectoryInfo(paths.PluginLogsDir).GetFiles("fun-*.log")
            .Where(file => file.LastWriteTime >= since)
            .OrderBy(file => file.Name, StringComparer.Ordinal)
            .ToList();
    }

    internal static IReadOnlyList<PlayerStats> BuildLeaderboard(IEnumerable<string> lines, DateTime? since) {
        var players = new Dictionary<string, PlayerTally>(StringComparer.Ordinal);
        string map = "-";

        foreach (string line in lines) {
            Match mapChange = MapChangeLine().Match(line);
            if (mapChange.Success) {
                map = mapChange.Groups["map"].Value;
                continue;
            }

            Match kill = KillLine().Match(line);
            if (kill.Success) {
                string time = kill.Groups["time"].Value;
                if (!IsInPeriod(time, since)) {
                    continue;
                }
                TryParseLogTime(time, out DateTime killedAt);

                string weapon = kill.Groups["weapon"].Value;
                bool isHeadshot = kill.Groups["headshot"].Success;
                PlayerTally killer = Tally(players, kill.Groups["killer"].Value, kill.Groups["killerAuth"].Value);
                PlayerTally victim = Tally(players, kill.Groups["victim"].Value, kill.Groups["victimAuth"].Value);
                killer.AddKill(new PlayerEvent(killedAt, map, IsKill: true, victim.Name, weapon, isHeadshot), victim.IsBot);
                victim.AddDeath(new PlayerEvent(killedAt, map, IsKill: false, killer.Name, weapon, isHeadshot), killer.IsBot);
                continue;
            }

            Match suicide = SuicideLine().Match(line);
            if (suicide.Success && IsInPeriod(suicide.Groups["time"].Value, since)) {
                PlayerTally player = Tally(players, suicide.Groups["name"].Value, suicide.Groups["auth"].Value);
                player.AddSuicide(map);
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

    /// <summary>A "-------- Mapchange to de_dust2 --------" line with its time.</summary>
    internal static bool TryReadMapChange(string line, out string map, out DateTime startedAt) {
        map = "";
        startedAt = default;
        Match mapChange = MapChangeLine().Match(line);
        if (!mapChange.Success || !TryParseLogTime(mapChange.Groups["time"].Value, out startedAt)) {
            return false;
        }
        map = mapChange.Groups["map"].Value;
        return true;
    }

    internal static IEnumerable<string> ReadLinesShared(FileInfo file) {
        var lines = new List<string>();
        try {
            // The plugin appends to today's log while the window reads it.
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
        // The details dialog lists this many; older ones are dropped while reading.
        private const int RecentEventCount = 30;

        private readonly Dictionary<string, (int Kills, int Headshots)> _weapons = new(StringComparer.Ordinal);
        private readonly Dictionary<string, (bool IsBot, int Kills, int Deaths)> _opponents = new(StringComparer.Ordinal);
        // A list, not a dictionary: the maps stay in the order they were played, a map played twice counts as one.
        private readonly List<(string Map, int Kills, int Deaths)> _maps = [];
        private readonly Queue<PlayerEvent> _recent = new();
        private int _streak;
        private int _bestStreak;
        private int _kills;
        private int _deaths;
        private int _suicides;
        private int _knifeKills;
        private int _grenadeKills;
        private int _headshots;

        public string Name => name;
        public bool IsBot => isBot;

        public void AddKill(PlayerEvent kill, bool isVictimBot) {
            _kills++;
            _streak++;
            _bestStreak = Math.Max(_bestStreak, _streak);
            if (kill.IsHeadshot) {
                _headshots++;
            }
            // CS2 names knife skins (knife_t, bayonet ...) and the HE grenade its own way.
            if (WeaponNames.IsKnife(kill.Weapon)) {
                _knifeKills++;
            }
            if (kill.Weapon is "hegrenade" or "grenade") {
                _grenadeKills++;
            }
            (int weaponKills, int weaponHeadshots) = _weapons.GetValueOrDefault(kill.Weapon);
            _weapons[kill.Weapon] = (weaponKills + 1, weaponHeadshots + (kill.IsHeadshot ? 1 : 0));
            (_, int opponentKills, int opponentDeaths) = _opponents.GetValueOrDefault(kill.Other);
            _opponents[kill.Other] = (isVictimBot, opponentKills + 1, opponentDeaths);
            CountOnMap(kill.Map, kills: 1, deaths: 0);
            Remember(kill);
        }

        public void AddDeath(PlayerEvent death, bool isKillerBot) {
            _deaths++;
            _streak = 0;
            (_, int opponentKills, int opponentDeaths) = _opponents.GetValueOrDefault(death.Other);
            _opponents[death.Other] = (isKillerBot, opponentKills, opponentDeaths + 1);
            CountOnMap(death.Map, kills: 0, deaths: 1);
            Remember(death);
        }

        public void AddSuicide(string map) {
            _deaths++;
            _suicides++;
            _streak = 0;
            CountOnMap(map, kills: 0, deaths: 1);
        }

        private void CountOnMap(string map, int kills, int deaths) {
            int index = _maps.FindIndex(entry => entry.Map == map);
            if (index < 0) {
                _maps.Add((map, kills, deaths));
                return;
            }
            _maps[index] = (map, _maps[index].Kills + kills, _maps[index].Deaths + deaths);
        }

        private void Remember(PlayerEvent playerEvent) {
            _recent.Enqueue(playerEvent);
            if (_recent.Count > RecentEventCount) {
                _recent.Dequeue();
            }
        }

        public PlayerStats ToStats() {
            List<WeaponTally> weapons = _weapons
                .Select(pair => new WeaponTally(pair.Key, pair.Value.Kills, pair.Value.Headshots))
                .OrderByDescending(weapon => weapon.Kills)
                .ThenBy(weapon => weapon.Weapon, StringComparer.Ordinal)
                .ToList();
            string favorite = weapons.Count == 0 ? "-" : weapons[0].Weapon;
            return new PlayerStats(name, isBot, _kills, _deaths, _suicides, _knifeKills, _grenadeKills, favorite) {
                Headshots = _headshots,
                BestStreak = _bestStreak,
                Weapons = weapons,
                Opponents = _opponents
                    .Select(pair => new OpponentTally(pair.Key, pair.Value.IsBot, pair.Value.Kills, pair.Value.Deaths))
                    .OrderByDescending(opponent => opponent.Kills + opponent.Deaths)
                    .ThenBy(opponent => opponent.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                Maps = _maps.Select(entry => new MapTally(entry.Map, entry.Kills, entry.Deaths)).ToList(),
                RecentEvents = _recent.Reverse().ToList(),
            };
        }
    }

    // L 10/17/2026 - 00:34:24: "Peter<33><76561197960287930><CT>" killed "Tom<29><BOT><TERRORIST>" with "sg552" (headshot)
    // CS2's own game log puts the positions behind each name: "Peter<3><[U:1:1]><CT>" [81 -1549 -176] killed ...
    [GeneratedRegex(@"^L (?<time>\d{2}/\d{2}/\d{4} - \d{2}:\d{2}:\d{2}): ""(?<killer>.+?)<\d+><(?<killerAuth>[^>]*)><[^>]*>""(?: \[[^\]]*\])? killed ""(?<victim>.+?)<\d+><(?<victimAuth>[^>]*)><[^>]*>""(?: \[[^\]]*\])? with ""(?<weapon>[^""]+)""(?<headshot> \(headshot\))?")]
    private static partial Regex KillLine();

    // L 10/17/2026 - 00:39:00: "Peter<4><STEAM_ID_LAN><TERRORIST>" committed suicide with "world"
    [GeneratedRegex(@"^L (?<time>\d{2}/\d{2}/\d{4} - \d{2}:\d{2}:\d{2}): ""(?<name>.+?)<\d+><(?<auth>[^>]*)><[^>]*>"" committed suicide with ")]
    private static partial Regex SuicideLine();

    [GeneratedRegex(@"^L (?<time>\d{2}/\d{2}/\d{4} - \d{2}:\d{2}:\d{2}): -------- Mapchange to (?<map>\S+) --------")]
    private static partial Regex MapChangeLine();

    [GeneratedRegex(@"Active mode: (?<mode>\w+)")]
    private static partial Regex ActiveModeLine();
}
