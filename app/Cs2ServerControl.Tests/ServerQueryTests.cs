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
    public void RejectsForeignPackets() {
        Assert.Null(QueryParser.ParseInfo(Encoding.ASCII.GetBytes("hello world")));
        Assert.Null(QueryParser.ParseInfo(Packet('E', (short)1, "a", "b")));
    }
}
