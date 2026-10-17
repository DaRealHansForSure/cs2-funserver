using System.IO;
using Cs2ServerControl.Services;
using Xunit;

namespace Cs2ServerControl.Tests;

public class FastDownloadServerTests : IDisposable {
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"cstrike-{Guid.NewGuid():N}");

    public FastDownloadServerTests() {
        Directory.CreateDirectory(Path.Combine(_root, "maps"));
        Directory.CreateDirectory(Path.Combine(_root, "addons", "amxmodx", "configs"));
        File.WriteAllText(Path.Combine(_root, "maps", "de_rats.bsp"), "map");
        File.WriteAllText(Path.Combine(_root, "maps", "secret.cfg"), "no");
        File.WriteAllText(Path.Combine(_root, "halflife.wad"), "wad");
        File.WriteAllText(Path.Combine(_root, "server.cfg"), "rcon");
        File.WriteAllText(Path.Combine(_root, "addons", "amxmodx", "configs", "users.ini"), "admin");
    }

    public void Dispose() {
        Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    private FastDownloadServer Server => new(_root, 27080);

    [Theory]
    [InlineData("/maps/de_rats.bsp")]
    [InlineData("/maps/de%5Frats.bsp")]
    [InlineData("/halflife.wad")]
    [InlineData("/maps/de_rats.bsp?version=2")]
    public void ServesMapFiles(string request) {
        Assert.NotNull(Server.ResolveRequestedFile(request));
    }

    [Theory]
    [InlineData("/server.cfg")]
    [InlineData("/maps/secret.cfg")]
    [InlineData("/addons/amxmodx/configs/users.ini")]
    [InlineData("/maps/../server.cfg")]
    [InlineData("/maps/..%2F..%2Fserver.cfg")]
    [InlineData("/maps/missing.bsp")]
    [InlineData("/")]
    public void RefusesEverythingElse(string request) {
        Assert.Null(Server.ResolveRequestedFile(request));
    }
}

public class AppCommandTests {
    [Fact]
    public void NoArgumentsOpensTheWindow() {
        Assert.Equal(AppCommandKind.Window, AppCommand.Parse([]).Kind);
    }

    [Theory]
    [InlineData("--install", AppCommandKind.Install)]
    [InlineData("--UPDATE-MAPS", AppCommandKind.UpdateMaps)]
    [InlineData("--start", AppCommandKind.Start)]
    [InlineData("--something", AppCommandKind.Window)]
    public void ReadsTheBatModes(string argument, AppCommandKind expected) {
        AppCommand command = AppCommand.Parse([argument]);

        Assert.Equal(expected, command.Kind);
        Assert.True(command.HasWindow);
    }

    [Fact]
    public void ReadsTheHiddenDownloadServer() {
        AppCommand command = AppCommand.Parse(["--fastdl", "--root", @"C:\x\cstrike", "--port", "27080", "--watch-pid", "1234"]);

        Assert.Equal(AppCommandKind.FastDownload, command.Kind);
        Assert.False(command.HasWindow);
        Assert.Equal(@"C:\x\cstrike", command.Root);
        Assert.Equal(27080, command.Port);
        Assert.Equal(1234, command.WatchProcessId);
    }

    [Fact]
    public void DownloadServerWithoutRootFallsBackToTheWindow() {
        Assert.Equal(AppCommandKind.Window, AppCommand.Parse(["--fastdl", "--port", "27080"]).Kind);
    }
}
