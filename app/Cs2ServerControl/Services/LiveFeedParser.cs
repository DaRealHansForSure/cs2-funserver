using System.Globalization;
using System.Numerics;
using System.Text;

namespace Cs2ServerControl.Services;

public enum Team {
    Spectator,
    Terrorist,
    CounterTerrorist,
}

public sealed record LivePlayer(
    int UserId,
    string Name,
    Team Team,
    bool IsAlive,
    bool IsBot,
    int Health,
    Vector3 Origin,
    float Yaw,
    int Frags,
    int Deaths,
    string Weapon,
    bool IsHooking,
    Vector3 HookPoint,
    PlayerGlow? Glow = null);

/// <summary>The glow shell color of a player, from amx_glow, streaks, Juggernaut or zombies.</summary>
public readonly record struct PlayerGlow(byte Red, byte Green, byte Blue);

public sealed record LiveSnapshot(
    string MapName,
    string ModeId,
    int RoundSecondsLeft,
    int MaxPlayers,
    IReadOnlyList<LivePlayer> Players);

public sealed record LiveKill(int KillerUserId, int VictimUserId, string Weapon, bool IsHeadshot);

/// <summary>One bullet: from the muzzle of the shooter to where it stopped.</summary>
public sealed record LiveShot(int ShooterUserId, Vector3 Start, Vector3 End, bool HitPlayer);

/// <summary>The bullets fired since the last snapshot.</summary>
public sealed record LiveShotBatch(IReadOnlyList<LiveShot> Shots);

/// <summary>
/// Parses the text packets of fun_livemap.amxx (spec section 10.3). Packets
/// without the CSLIVE1 prefix are rejected, broken lines are skipped and
/// counted, nothing here ever throws on bad input.
/// </summary>
public sealed class LiveFeedParser {
    public const string Prefix = "CSLIVE1|";

    private const int PlayerFieldCount = 18;
    private const int PlayerFieldCountWithGlow = 21;

    public int MalformedLineCount { get; private set; }

    /// <summary>Returns a LiveSnapshot, a LiveKill, a LiveShotBatch, or null for anything else.</summary>
    public object? Parse(byte[] packet) {
        string text;
        try {
            text = Encoding.UTF8.GetString(packet);
        } catch (ArgumentException) {
            MalformedLineCount++;
            return null;
        }
        return Parse(text);
    }

    public object? Parse(string text) {
        if (!text.StartsWith(Prefix, StringComparison.Ordinal)) {
            return null;
        }

        string[] lines = text.Replace("\r", "").Split('\n');
        string[] header = lines[0].Split('|');
        if (header.Length < 2) {
            MalformedLineCount++;
            return null;
        }

        if (header[1] == "K") {
            return ParseKill(header);
        }
        if (header[1] == "S") {
            return ParseSnapshot(header, lines);
        }
        if (header[1] == "T") {
            return ParseShots(lines);
        }

        MalformedLineCount++;
        return null;
    }

    private LiveKill? ParseKill(string[] header) {
        if (header.Length < 6 || !TryInt(header[2], out int killer) || !TryInt(header[3], out int victim)) {
            MalformedLineCount++;
            return null;
        }
        return new LiveKill(killer, victim, header[4], header[5] == "1");
    }

    private LiveShotBatch ParseShots(string[] lines) {
        var shots = new List<LiveShot>();
        foreach (string line in lines.Skip(1)) {
            if (line.Length == 0) {
                continue;
            }

            LiveShot? shot = ParseShot(line);
            if (shot == null) {
                MalformedLineCount++;
                continue;
            }
            shots.Add(shot);
        }
        return new LiveShotBatch(shots);
    }

    private static LiveShot? ParseShot(string line) {
        string[] fields = line.Split('|');
        if (fields.Length < 8) {
            return null;
        }
        bool numbersOk = TryInt(fields[0], out int shooter)
            & TryFloat(fields[1], out float startX)
            & TryFloat(fields[2], out float startY)
            & TryFloat(fields[3], out float startZ)
            & TryFloat(fields[4], out float endX)
            & TryFloat(fields[5], out float endY)
            & TryFloat(fields[6], out float endZ);
        if (!numbersOk) {
            return null;
        }
        return new LiveShot(shooter, new Vector3(startX, startY, startZ), new Vector3(endX, endY, endZ), fields[7] == "1");
    }

    private LiveSnapshot? ParseSnapshot(string[] header, string[] lines) {
        if (header.Length < 6 || !TryInt(header[4], out int secondsLeft) || !TryInt(header[5], out int maxPlayers)) {
            MalformedLineCount++;
            return null;
        }

        var players = new List<LivePlayer>();
        foreach (string line in lines.Skip(1)) {
            if (line.Length == 0) {
                continue;
            }

            LivePlayer? player = ParsePlayer(line);
            if (player == null) {
                MalformedLineCount++;
                continue;
            }
            players.Add(player);
        }
        return new LiveSnapshot(header[2], header[3], secondsLeft, maxPlayers, players);
    }

    private static LivePlayer? ParsePlayer(string line) {
        string[] fields = line.Split('|');
        if (fields.Length < PlayerFieldCount || fields[0] != "P") {
            return null;
        }

        bool numbersOk = TryInt(fields[1], out int userId)
            & TryInt(fields[6], out int health)
            & TryFloat(fields[7], out float x)
            & TryFloat(fields[8], out float y)
            & TryFloat(fields[9], out float z)
            & TryFloat(fields[10], out float yaw)
            & TryInt(fields[11], out int frags)
            & TryInt(fields[12], out int deaths)
            & TryFloat(fields[15], out float hookX)
            & TryFloat(fields[16], out float hookY)
            & TryFloat(fields[17], out float hookZ);
        if (!numbersOk) {
            return null;
        }

        Team team;
        switch (fields[3]) {
            case "T":
                team = Team.Terrorist;
                break;
            case "CT":
                team = Team.CounterTerrorist;
                break;
            case "SPEC":
                team = Team.Spectator;
                break;
            default:
                return null;
        }

        return new LivePlayer(
            userId,
            fields[2],
            team,
            fields[4] == "1",
            fields[5] == "1",
            health,
            new Vector3(x, y, z),
            yaw,
            frags,
            deaths,
            fields[13],
            fields[14] == "1",
            new Vector3(hookX, hookY, hookZ),
            ReadGlow(fields));
    }

    /// <summary>
    /// The three glow fields after the hook point; -1 means no glow. Lines of
    /// older plugin versions end before them and simply have no glow.
    /// </summary>
    private static PlayerGlow? ReadGlow(string[] fields) {
        if (fields.Length < PlayerFieldCountWithGlow) {
            return null;
        }
        bool numbersOk = TryInt(fields[18], out int red) & TryInt(fields[19], out int green) & TryInt(fields[20], out int blue);
        if (!numbersOk || red < 0 || green < 0 || blue < 0) {
            return null;
        }
        return new PlayerGlow((byte)Math.Min(red, 255), (byte)Math.Min(green, 255), (byte)Math.Min(blue, 255));
    }

    private static bool TryInt(string text, out int value) {
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryFloat(string text, out float value) {
        return float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }
}
