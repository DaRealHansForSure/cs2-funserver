using System.Text;
using Cs2ServerControl.Services;
using Xunit;

namespace Cs2ServerControl.Tests;

public class RconResponseTests {
    [Fact]
    public void ReadsChallengeNumber() {
        Assert.Equal("1234567890", RconReply.ReadChallenge("challenge rcon 1234567890\n"));
        Assert.Null(RconReply.ReadChallenge("Bad challenge.\n"));
    }

    [Fact]
    public void StripsHeaderPrintMarkerAndTrailingZeros() {
        byte[] packet = [0xFF, 0xFF, 0xFF, 0xFF, (byte)'l', .. Encoding.UTF8.GetBytes("hostname: LAN\n"), 0, 0];

        Assert.Equal("hostname: LAN\n", RconReply.ReadPacketText(packet));
    }

    [Fact]
    public void KeepsTextThatStartsWithLWithoutHeader() {
        byte[] packet = Encoding.UTF8.GetBytes("loaded");

        Assert.Equal("loaded", RconReply.ReadPacketText(packet));
    }

    [Fact]
    public void DetectsBadChallengeAndBadPassword() {
        Assert.True(RconReply.IsBadChallenge("Bad challenge.\n"));
        Assert.True(RconReply.IsBadPassword("Bad rcon_password.\n"));
        Assert.False(RconReply.IsBadPassword("\"rcon_password\" is \"\"\n"));
    }

    [Fact]
    public void SplitsReplyIntoLines() {
        Assert.Equal(["a", "b"], RconReply.SplitLines("a\r\n\r\nb\n"));
    }

    [Theory]
    [InlineData("Rcon from 192.168.89.63:51756:", true)]
    [InlineData("rcon 376501098 \"secret\" amx_nextmap", true)]
    [InlineData("L 10/17/2026 - 12:18:34: Rcon: \"rcon 376501098 \"secret\" amx_nextmap", true)]
    [InlineData("\" from \"192.168.89.63:51756\"", true)]
    [InlineData("L 10/17/2026 - 12:18:34: \"Tom<4><STEAM_ID_LAN><CT>\" say \"rcon is cool\"", false)]
    [InlineData("Executing AMX Mod X Configuration File", false)]
    public void HidesRconEchoLines(string line, bool isEcho) {
        Assert.Equal(isEcho, ConsoleLine.IsRconEcho(line));
    }

    [Theory]
    [InlineData("L 10/17/2026 - 20:14:02: [fun_modes.amxx] Active mode: classic", ConsoleLineKind.Plugin)]
    [InlineData("[Fun] Admin Peter launches Ulf as a rocket.", ConsoleLineKind.Plugin)]
    [InlineData("L 10/17/2026 - 20:21:10: \"Tom<4><STEAM_ID_LAN><CT>\" killed \"Mike<5><STEAM_ID_LAN><TERRORIST>\" with \"awp\"", ConsoleLineKind.Kill)]
    [InlineData("Executing AMX Mod X Configuration File", ConsoleLineKind.Normal)]
    public void ClassifiesConsoleLines(string line, ConsoleLineKind expected) {
        Assert.Equal(expected, ConsoleLine.FromServer(line).Kind);
    }
}
