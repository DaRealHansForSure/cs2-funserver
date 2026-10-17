using Cs2ServerControl.Services;
using Xunit;

namespace Cs2ServerControl.Tests;

public class AdminListTests {
    [Theory]
    [InlineData("76561197960287930", 76561197960287930UL)]
    [InlineData("https://steamcommunity.com/profiles/76561197960287930/", 76561197960287930UL)]
    [InlineData("STEAM_0:0:11101", 76561197960287930UL)]
    [InlineData("STEAM_1:0:11101", 76561197960287930UL)]
    [InlineData("[U:1:22202]", 76561197960287930UL)]
    [InlineData("U:1:22202", 76561197960287930UL)]
    public void ReadsEveryCommonSteamIdForm(string input, ulong expected) {
        Assert.Equal(expected, AdminList.ParseSteamId(input));
    }

    [Theory]
    [InlineData("https://steamcommunity.com/id/gabelogannewell")]
    [InlineData("12345")]
    [InlineData("")]
    public void RejectsWhatIsNoSteamId(string input) {
        Assert.Null(AdminList.ParseSteamId(input));
    }

    [Fact]
    public void WritesAndReadsCounterStrikeSharpsFormat() {
        Admin[] admins = [new("Host", 76561197960287930UL), new("Host", 76561197960287931UL), new("Tom", 76561197960287932UL)];

        string json = AdminList.Format(admins);
        IReadOnlyList<Admin> read = AdminList.Parse(json);

        Assert.Contains("\"identity\": \"76561197960287930\"", json);
        Assert.Contains("@css/root", json);
        Assert.Equal(3, read.Count);
        Assert.Equal(76561197960287931UL, read[1].SteamId);
        Assert.Equal("Tom", read[2].Name);
    }
}
