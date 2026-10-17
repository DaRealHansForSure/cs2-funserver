using Xunit;

namespace Cs2ServerControl.Tests;

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
    [InlineData("--fastdl", AppCommandKind.Window)]
    public void ReadsTheShortcutModes(string argument, AppCommandKind expected) {
        Assert.Equal(expected, AppCommand.Parse([argument]).Kind);
    }
}
