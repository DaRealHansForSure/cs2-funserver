using System.Text.Json;
using Cs2ServerControl.Install;
using Xunit;

namespace Cs2ServerControl.Tests;

public class InstallConfigTests {
    [Fact]
    public void PointsOnlyTheGameDllLineAtMetamod() {
        string[] libList = [
            "game \"Counter-Strike\"",
            "gamedll \"dlls\\mp.dll\"",
            "gamedll_linux \"dlls/cs.so\"",
            "  GameDll \"dlls\\other.dll\"",
        ];

        List<string> updated = ConfigEdits.PointGameDllToMetamod(libList);

        Assert.Equal([
            "game \"Counter-Strike\"",
            "gamedll \"addons\\metamod\\metamod.dll\"",
            "gamedll_linux \"dlls/cs.so\"",
            "gamedll \"addons\\metamod\\metamod.dll\"",
        ], updated);
    }

    [Fact]
    public void ReplacesTheBotEnableLineOfGameInit() {
        List<string> updated = ConfigEdits.EnableZBot(["// ReGameDLL", "bot_enable \"0\"", "mp_round_infinite 0"]);

        Assert.Equal(["// ReGameDLL", "bot_enable \"1\"", "mp_round_infinite 0"], updated);
    }

    [Fact]
    public void AddsBotEnableWhenGameInitHasNone() {
        Assert.Equal(["// nothing", "bot_enable \"1\""], ConfigEdits.EnableZBot(["// nothing"]));
        Assert.Equal(["bot_enable \"1\""], ConfigEdits.EnableZBot([]));
    }

    [Fact]
    public void WritesTheMetamodLineRelativeToCstrike() {
        string line = ConfigEdits.MetamodPluginLine(@"C:\cs\server\cstrike", @"C:\cs\server\cstrike\addons\amxmodx\dlls\amxmodx_mm.dll");

        Assert.Equal("win32 addons/amxmodx/dlls/amxmodx_mm.dll", line);
    }

    [Fact]
    public void MergesTheFunPluginsIntoPluginsIni() {
        string[] stock = ["; AMX Mod X plugins", "admin.amxx", ";miscstats.amxx\t; Misc. Stats", "statsx.amxx"];

        List<string> merged = ConfigEdits.MergeAmxPluginList(stock, ["fun_modes.amxx", "fun_hook.amxx"]);

        Assert.Equal([
            "; AMX Mod X plugins",
            "admin.amxx",
            "miscstats.amxx\t; Misc. Stats",
            "statsx.amxx",
            "",
            "; --- LAN Fun Server ---",
            "fun_modes.amxx",
            "fun_hook.amxx",
        ], merged);
    }

    [Fact]
    public void PluginsIniMergeIsStableAndAddsNewPluginsOnce() {
        List<string> firstRun = ConfigEdits.MergeAmxPluginList(["admin.amxx"], ["fun_modes.amxx"]);
        List<string> secondRun = ConfigEdits.MergeAmxPluginList(firstRun, ["fun_modes.amxx"]);
        List<string> laterVersion = ConfigEdits.MergeAmxPluginList(secondRun, ["fun_modes.amxx", "fun_livemap.amxx"]);

        Assert.Equal(firstRun, secondRun);
        Assert.Equal([.. firstRun, "fun_livemap.amxx"], laterVersion);
        Assert.Equal(laterVersion, ConfigEdits.MergeAmxPluginList(laterVersion, ["fun_modes.amxx", "fun_livemap.amxx"]));
    }

    [Fact]
    public void AddsTheAdminLineOnceWithTheKickFlag() {
        string[] users = ["; Users configuration file", "; \"loopback\" \"\" \"abcdefghijklmnopqrstu\" \"de\""];

        List<string> updated = ConfigEdits.AddAdminAccount(users, new AdminAccount("Benji", "secret"));

        Assert.False(ConfigEdits.HasPackageAdmin(users));
        Assert.True(ConfigEdits.HasPackageAdmin(updated));
        Assert.Equal([.. users, "", "; --- LAN Fun Server ---", "\"Benji\" \"secret\" \"abcdefghijklmnopqrstu\" \"a\""], updated);
        Assert.Equal(updated, ConfigEdits.AddAdminAccount(updated, new AdminAccount("Other", "pw")));
    }

    [Fact]
    public void StoresAnUmlautNameAsItsUtf8Bytes() {
        List<string> updated = ConfigEdits.AddAdminAccount([], new AdminAccount("J\u00fcrg", "pw"));

        // The UTF-8 bytes C3 BC of the umlaut, one char per byte in the Latin-1 written file.
        Assert.Equal("\"J\u00c3\u00bcrg\" \"pw\" \"abcdefghijklmnopqrstu\" \"a\"", updated[^1]);
    }

    [Theory]
    [InlineData("Benji", "secret", null)]
    [InlineData("", "secret", "The admin name is empty.")]
    [InlineData("Benji", " ", "The admin password is empty.")]
    [InlineData("Be\"nji", "secret", "The admin name must not contain quotes.")]
    [InlineData("Benji", "sec ret", "The admin password must not contain spaces or quotes.")]
    [InlineData("Benji", "sec\"ret", "The admin password must not contain spaces or quotes.")]
    public void ChecksTheAdminAccount(string name, string password, string? problem) {
        Assert.Equal(problem, ConfigEdits.AdminAccountProblem(new AdminAccount(name, password)));
    }

    [Fact]
    public void PicksThe530PixelJpgOrPngPreview() {
        const string json = """
            {"_aPreviewMedia": {"_aImages": [
                {"_sBaseUrl": "https://images.gamebanana.com/img/ss/mods", "_sFile": "a.webp", "_sFile530": "530-90_a.webp"},
                {"_sBaseUrl": "https://images.gamebanana.com/img/ss/mods", "_sFile": "b.jpg", "_sFile530": "530-90_b.jpg", "_sFile220": "220-90_b.jpg"}
            ]}}
            """;
        using JsonDocument document = JsonDocument.Parse(json);

        Assert.Equal("https://images.gamebanana.com/img/ss/mods/530-90_b.jpg", MapInstaller.PickPreviewUrl(document.RootElement));
    }

    [Fact]
    public void FallsBackToTheFullSizePreviewAndToNothing() {
        using JsonDocument fullOnly = JsonDocument.Parse("""{"_aPreviewMedia": {"_aImages": [{"_sBaseUrl": "https://x", "_sFile": "c.PNG"}]}}""");
        using JsonDocument noImages = JsonDocument.Parse("""{"_aPreviewMedia": []}""");
        using JsonDocument webpOnly = JsonDocument.Parse("""{"_aPreviewMedia": {"_aImages": [{"_sBaseUrl": "https://x", "_sFile": "d.webp"}]}}""");

        Assert.Equal("https://x/c.PNG", MapInstaller.PickPreviewUrl(fullOnly.RootElement));
        Assert.Null(MapInstaller.PickPreviewUrl(noImages.RootElement));
        Assert.Null(MapInstaller.PickPreviewUrl(webpOnly.RootElement));
    }
}
