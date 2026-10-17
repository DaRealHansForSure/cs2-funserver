using System.IO;
using Cs2ServerControl.Services;
using Cs2ServerControl.Stats;
using Xunit;

namespace Cs2ServerControl.Tests;

public class StatsTests {
    private static readonly string[] GameLog = [
        "L 10/17/2026 - 00:34:10: -------- Mapchange to de_dust2 --------",
        "L 10/17/2026 - 00:34:24: \"Peter<3><76561197960287930><CT>\" killed \"bjaren<29><BOT><TERRORIST>\" with \"ak47\"",
        "L 10/17/2026 - 00:34:30: \"Peter<3><76561197960287930><CT>\" killed \"Simon<4><76561197960287931><TERRORIST>\" with \"knife_t\" (headshot)",
        "L 10/17/2026 - 00:34:40: \"Peter<3><76561197960287930><CT>\" killed \"bjaren<29><BOT><TERRORIST>\" with \"ak47\"",
        // CS2's own game log format, with the positions.
        "L 10/17/2026 - 00:35:00: \"bjaren<29><BOT><TERRORIST>\" [81 -1549 -176] killed \"Peter<3><[U:1:22202]><CT>\" [212 -1560 -175] with \"hegrenade\"",
        "L 10/17/2026 - 00:35:10: \"Simon<4><76561197960287931><TERRORIST>\" committed suicide with \"world\"",
        "L 10/17/2026 - 00:35:20: \"Simon<4><76561197960287931><TERRORIST>\" say \"Peter killed me with knife\"",
    ];

    [Fact]
    public void CountsKillsDeathsAndWeapons() {
        IReadOnlyList<PlayerStats> board = StatsReader.BuildLeaderboard(GameLog, null);

        PlayerStats peter = board[0];
        Assert.Equal("Peter", peter.Name);
        Assert.Equal(3, peter.Kills);
        Assert.Equal(1, peter.Deaths);
        Assert.Equal(1, peter.KnifeKills);
        Assert.Equal("ak47", peter.FavoriteWeapon);
        Assert.Equal(3.0, peter.KillDeathRatio);

        PlayerStats bot = board.Single(player => player.Name == "bjaren");
        Assert.True(bot.IsBot);
        Assert.Equal(1, bot.GrenadeKills);

        PlayerStats simon = board.Single(player => player.Name == "Simon");
        Assert.Equal(2, simon.Deaths);
        Assert.Equal(1, simon.Suicides);
        Assert.Equal(0, simon.Kills);
    }

    [Fact]
    public void LeavesOutKillsBeforeThePeriod() {
        IReadOnlyList<PlayerStats> board = StatsReader.BuildLeaderboard(GameLog, new DateTime(2026, 10, 17, 0, 34, 35));

        Assert.Equal(1, board.Single(player => player.Name == "Peter").Kills);
    }

    [Fact]
    public void ListsMapsWithModeAndDuration() {
        string[] amxLog = [
            "L 10/17/2026 - 00:27:57: -------- Mapchange to de_rats --------",
            "L 10/17/2026 - 00:27:57: Active mode: classic",
            "L 10/17/2026 - 00:34:09: -------- Mapchange to fy_iceworld --------",
            "L 10/17/2026 - 00:34:10: Active mode: deathmatch",
        ];
        var now = new DateTime(2026, 10, 17, 0, 50, 9);

        IReadOnlyList<MapPlay> plays = StatsReader.BuildMapsPlayed(amxLog, new DateTime(2026, 10, 16, 12, 0, 0), now, isServerRunning: true);

        Assert.Equal(2, plays.Count);
        Assert.Equal("de_rats", plays[0].Map);
        Assert.Equal("classic", plays[0].ModeId);
        Assert.Equal(TimeSpan.FromSeconds(372), plays[0].Duration);
        Assert.False(plays[0].IsRunning);
        Assert.Equal("deathmatch", plays[1].ModeId);
        Assert.Equal(TimeSpan.FromMinutes(16), plays[1].Duration);
        Assert.True(plays[1].IsRunning);
    }

    [Fact]
    public void LastMapOfAStoppedServerHasNoDuration() {
        string[] amxLog = ["L 10/17/2026 - 00:27:57: -------- Mapchange to de_rats --------"];

        IReadOnlyList<MapPlay> plays = StatsReader.BuildMapsPlayed(amxLog, new DateTime(2026, 10, 16, 12, 0, 0),
            new DateTime(2026, 10, 17, 1, 0, 0), isServerRunning: false);

        Assert.Equal(TimeSpan.Zero, plays[0].Duration);
    }

    [Theory]
    [InlineData("11:28", 11 * 60 + 28)]
    [InlineData("0:05", 5)]
    public void ReadsTimeLeft(string text, int expectedSeconds) {
        Assert.True(StatsViewModel.TryParseTimeLeft(text, out TimeSpan left));
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), left);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("soon")]
    public void RejectsUnreadableTimeLeft(string? text) {
        Assert.False(StatsViewModel.TryParseTimeLeft(text, out _));
    }

    [Theory]
    [InlineData(90, "1 min")]
    [InlineData(3 * 3600 + 5 * 60, "3 h 05 min")]
    public void FormatsDurations(int seconds, string expected) {
        Assert.Equal(expected, StatsViewModel.FormatDuration(TimeSpan.FromSeconds(seconds)));
    }
}

public class LocalSettingsTests : IDisposable {
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"local-{Guid.NewGuid():N}.cfg");

    public void Dispose() {
        File.Delete(_path);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void KeepsUnknownLinesAndReplacesValues() {
        File.WriteAllText(_path, "// my notes\r\nrcon_password \"secret\"\r\nmp_friendlyfire \"1\"\r\n");

        var settings = new LocalSettings(_path);
        settings.Set("hostname", "Peter's LAN");
        settings.Set("rcon_password", "other");
        settings.Save();

        string[] lines = File.ReadAllLines(_path);
        Assert.Equal("// my notes", lines[0]);
        Assert.Contains("rcon_password \"other\"", lines);
        Assert.Contains("mp_friendlyfire \"1\"", lines);
        Assert.Contains("hostname \"Peter's LAN\"", lines);
        Assert.Single(lines, line => line.StartsWith("rcon_password", StringComparison.Ordinal));
    }

    [Fact]
    public void NewFileGetsAHeaderComment() {
        var settings = new LocalSettings(_path);
        settings.Set("sv_password", "");
        settings.Save();

        Assert.StartsWith("//", File.ReadAllLines(_path)[0]);
        Assert.Equal("", new LocalSettings(_path).Get("sv_password"));
    }

    [Fact]
    public void ReadsUnquotedValuesOfTheModeConfigs() {
        File.WriteAllText(_path, "// GunGame\r\nmapcyclefile \"mapcycle_gungame.txt\"\r\nbot_quota 6\r\nmp_forcerespawn 1.5 // seconds\r\n");

        var settings = new LocalSettings(_path);

        Assert.Equal("6", settings.Get("bot_quota"));
        Assert.Equal("1.5", settings.Get("mp_forcerespawn"));
        Assert.Equal("mapcycle_gungame.txt", settings.Get("mapcyclefile"));
        Assert.Null(settings.Get("hostname"));
    }

    [Fact]
    public void DropsQuotesThatWouldBreakTheConfig() {
        var settings = new LocalSettings(_path);
        settings.Set("hostname", "Bad \"name\"");

        Assert.Equal("Bad name", settings.Get("hostname"));
    }
}
