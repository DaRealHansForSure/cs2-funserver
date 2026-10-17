using System.Text;
using Cs2ServerControl.Services;
using Xunit;

namespace Cs2ServerControl.Tests;

public class LiveFeedParserTests {
    [Fact]
    public void ParsesSnapshotWithPlayers() {
        string packet = string.Join('\n',
            "CSLIVE1|S|fy_iceworld|classic|102|20",
            "P|3|Peter|T|1|0|100|-512|300|64|90|5|1|ak47|0|0|0|0",
            "P|7|Gunner|CT|1|1|35|560|60|140|-45|4|4|awp|1|900|120|300");

        var parser = new LiveFeedParser();
        var snapshot = Assert.IsType<LiveSnapshot>(parser.Parse(packet));

        Assert.Equal("fy_iceworld", snapshot.MapName);
        Assert.Equal("classic", snapshot.ModeId);
        Assert.Equal(102, snapshot.RoundSecondsLeft);
        Assert.Equal(20, snapshot.MaxPlayers);
        Assert.Equal(2, snapshot.Players.Count);

        LivePlayer gunner = snapshot.Players[1];
        Assert.Equal(7, gunner.UserId);
        Assert.Equal(Team.CounterTerrorist, gunner.Team);
        Assert.True(gunner.IsBot);
        Assert.True(gunner.IsHooking);
        Assert.Equal(35, gunner.Health);
        Assert.Equal(560, gunner.Origin.X);
        Assert.Equal(-45, gunner.Yaw);
        Assert.Equal("awp", gunner.Weapon);
        Assert.Equal(300, gunner.HookPoint.Z);
        Assert.Equal(0, parser.MalformedLineCount);
    }

    [Fact]
    public void ParsesKillEvent() {
        var kill = Assert.IsType<LiveKill>(new LiveFeedParser().Parse("CSLIVE1|K|3|9|knife|1"));

        Assert.Equal(3, kill.KillerUserId);
        Assert.Equal(9, kill.VictimUserId);
        Assert.Equal("knife", kill.Weapon);
        Assert.True(kill.IsHeadshot);
    }

    [Theory]
    [InlineData("")]
    [InlineData("hello")]
    [InlineData("CSLIVE2|S|de_dust2|classic|-1|20")]
    [InlineData("ÿÿÿÿinfo")]
    public void RejectsPacketsWithoutPrefix(string packet) {
        Assert.Null(new LiveFeedParser().Parse(packet));
    }

    [Fact]
    public void SkipsAndCountsBrokenLinesWithoutThrowing() {
        string packet = string.Join('\n',
            "CSLIVE1|S|de_dust2|gungame|-1|20",
            "P|1|Ok|T|1|0|100|0|0|0|0|0|0|knife|0|0|0|0",
            "P|2|TooShort|CT|1",
            "P|x|BadNumber|CT|1|0|100|0|0|0|0|0|0|knife|0|0|0|0",
            "P|4|BadTeam|BLUE|1|0|100|0|0|0|0|0|0|knife|0|0|0|0",
            "garbage");

        var parser = new LiveFeedParser();
        var snapshot = Assert.IsType<LiveSnapshot>(parser.Parse(packet));

        Assert.Single(snapshot.Players);
        Assert.Equal(-1, snapshot.RoundSecondsLeft);
        Assert.Equal(4, parser.MalformedLineCount);
    }

    [Fact]
    public void BrokenHeaderReturnsNull() {
        var parser = new LiveFeedParser();

        Assert.Null(parser.Parse("CSLIVE1|S|de_dust2"));
        Assert.Null(parser.Parse("CSLIVE1|K|abc|1|knife|0"));
        Assert.Null(parser.Parse("CSLIVE1|X"));
        Assert.Equal(3, parser.MalformedLineCount);
    }

    [Fact]
    public void KeepsOddCharactersInNames() {
        // The plugin already replaced | and newlines with /, everything else stays.
        string packet = "CSLIVE1|S|de_dust2|classic|60|20\nP|5|[FUN] Ülf / 1337 <3 ★|SPEC|0|0|0|0|0|0|0|0|0||0|0|0|0";

        var snapshot = Assert.IsType<LiveSnapshot>(new LiveFeedParser().Parse(Encoding.UTF8.GetBytes(packet)));

        Assert.Equal("[FUN] Ülf / 1337 <3 ★", snapshot.Players[0].Name);
        Assert.Equal(Team.Spectator, snapshot.Players[0].Team);
        Assert.Equal("", snapshot.Players[0].Weapon);
    }

    [Fact]
    public void ReadsTheGlowColor() {
        string packet = string.Join('\n',
            "CSLIVE1|S|de_dust2|classic|60|20",
            "P|1|Pinky|T|1|0|100|0|0|0|0|0|0|ak47|0|0|0|0|255|0|200",
            "P|2|Plain|CT|1|0|100|0|0|0|0|0|0|m4a1|0|0|0|0|-1|-1|-1",
            "P|3|OldPlugin|CT|1|0|100|0|0|0|0|0|0|m4a1|0|0|0|0");

        var parser = new LiveFeedParser();
        var snapshot = Assert.IsType<LiveSnapshot>(parser.Parse(packet));

        Assert.Equal(new PlayerGlow(255, 0, 200), snapshot.Players[0].Glow);
        Assert.Null(snapshot.Players[1].Glow);
        Assert.Null(snapshot.Players[2].Glow);
        Assert.Equal(0, parser.MalformedLineCount);
        Assert.Equal("pink", GlowNames.Describe(snapshot.Players[0].Glow));
        Assert.Equal("gold (kill streak)", GlowNames.Describe(new PlayerGlow(255, 200, 0)));
        Assert.Equal("custom", GlowNames.Describe(new PlayerGlow(1, 2, 3)));
    }

    [Fact]
    public void ToleratesWindowsLineEnds() {
        string packet = "CSLIVE1|S|de_dust2|classic|60|20\r\nP|1|A|T|1|0|100|1|2|3|4|0|0|knife|0|0|0|0\r\n";

        var snapshot = Assert.IsType<LiveSnapshot>(new LiveFeedParser().Parse(packet));

        Assert.Single(snapshot.Players);
    }

    [Fact]
    public void ReadsShotBatchesAndSkipsBrokenLines() {
        var parser = new LiveFeedParser();
        string packet = "CSLIVE1|T\n7|10|20|30|400|-50|28|1\nbroken|line\n8|0|0|0|1|1|1|0\n";

        var batch = Assert.IsType<LiveShotBatch>(parser.Parse(packet));

        Assert.Equal(2, batch.Shots.Count);
        Assert.Equal(new LiveShot(7, new System.Numerics.Vector3(10, 20, 30), new System.Numerics.Vector3(400, -50, 28), HitPlayer: true), batch.Shots[0]);
        Assert.False(batch.Shots[1].HitPlayer);
        Assert.Equal(1, parser.MalformedLineCount);
    }
}
