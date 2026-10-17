using System.Net;
using Cs2ServerControl.Services;
using Xunit;

namespace Cs2ServerControl.Tests;

public class Cs2ServerTests {
    [Fact]
    public void TheCommandLineStartsADedicatedLanServerInTheModesGameType() {
        GameMode armsRace = GameModes.Find("gungame")!;

        string arguments = ServerProcess.BuildArguments(armsRace, "ar_shoots", 16, IPAddress.Parse("192.168.1.50"), "secret");

        Assert.StartsWith("-dedicated -console -usercon", arguments);
        Assert.Contains("-port 27015", arguments);
        Assert.Contains("-ip 192.168.1.50", arguments);
        Assert.Contains("-maxplayers 16", arguments);
        Assert.Contains("+sv_lan 1", arguments);
        Assert.Contains("+sv_hibernate_when_empty 0", arguments);
        Assert.Contains("+game_type 1 +game_mode 0", arguments);
        Assert.Contains("+rcon_password secret", arguments);
        Assert.EndsWith("+map ar_shoots", arguments);
    }

    [Theory]
    [InlineData("game_type 0, game_mode 0", "classic", 0, 0)]
    [InlineData("Arms Race", "gungame", 1, 0)]
    [InlineData("Deathmatch", "deathmatch", 1, 2)]
    public void EveryModeHasItsValveGameTypeAndMode(string because, string modeId, int gameType, int gameMode) {
        GameMode mode = GameModes.Find(modeId)!;

        Assert.True(mode.GameType == gameType && mode.GameModeNumber == gameMode, because);
    }

    [Theory]
    [InlineData("de_dust2", true)]
    [InlineData("de_dust2_vanity", false)]
    [InlineData("lobby_mapveto", false)]
    [InlineData("graphics_settings", false)]
    public void OnlyRealMapsCountAsMaps(string package, bool isMap) {
        Assert.Equal(isMap, MapLibrary.IsPlayableMapPackage(package));
    }
}
