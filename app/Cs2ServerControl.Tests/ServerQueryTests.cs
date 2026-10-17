using System.Text;
using Cs2ServerControl.Services;
using Xunit;

namespace Cs2ServerControl.Tests;

public class ServerQueryTests {
    private static byte[] Packet(char header, params object[] fields) {
        var bytes = new List<byte> { 0xFF, 0xFF, 0xFF, 0xFF, (byte)header };
        foreach (object field in fields) {
            switch (field) {
                case string text:
                    bytes.AddRange(Encoding.UTF8.GetBytes(text));
                    bytes.Add(0);
                    break;
                case byte value:
                    bytes.Add(value);
                    break;
                case short value:
                    bytes.AddRange(BitConverter.GetBytes(value));
                    break;
            }
        }
        return bytes.ToArray();
    }

    [Fact]
    public void ReadsSourceStyleInfoWithBots() {
        byte[] packet = Packet('I', (byte)48, "LAN Fun Server", "fy_iceworld", "cstrike", "Counter-Strike", (short)10, (byte)8, (byte)20, (byte)6);

        ServerInfo? info = QueryParser.ParseInfo(packet);

        Assert.NotNull(info);
        Assert.Equal("LAN Fun Server", info.Name);
        Assert.Equal("fy_iceworld", info.Map);
        Assert.Equal(8, info.Players);
        Assert.Equal(20, info.MaxPlayers);
        Assert.Equal(6, info.Bots);
    }

    [Fact]
    public void ReadsTheChallengeTheServerWantsBack() {
        byte[] packet = [0xFF, 0xFF, 0xFF, 0xFF, (byte)'A', 1, 2, 3, 4];

        Assert.True(QueryParser.TryReadChallenge(packet, out byte[] challenge));
        Assert.Equal([1, 2, 3, 4], challenge);
    }

    [Fact]
    public void ReadsRules() {
        byte[] packet = Packet('E', (short)2, "amx_nextmap", "aim_awp", "amx_timeleft", "12:35");

        IReadOnlyDictionary<string, string>? rules = QueryParser.ParseRules(packet);

        Assert.NotNull(rules);
        Assert.Equal("aim_awp", rules["amx_nextmap"]);
        Assert.Equal("12:35", rules["AMX_TIMELEFT"]);
    }

    [Fact]
    public void ReadsGoldSrcSplitPacketHeader() {
        byte[] part = [0xFE, 0xFF, 0xFF, 0xFF, 9, 0, 0, 0, (1 << 4) | 2, 0xAB, 0xCD];

        Assert.True(QueryParser.TryReadSplitPart(part, out int index, out int total, out byte[] payload));
        Assert.Equal(1, index);
        Assert.Equal(2, total);
        Assert.Equal([0xAB, 0xCD], payload);
    }

    [Fact]
    public void RejectsForeignPackets() {
        Assert.Null(QueryParser.ParseInfo(Encoding.ASCII.GetBytes("hello world")));
        Assert.Null(QueryParser.ParseRules(Packet('I', "x")));
        Assert.False(QueryParser.TryReadSplitPart([0xFF, 0xFF, 0xFF, 0xFF, (byte)'E'], out _, out _, out _));
    }
}
