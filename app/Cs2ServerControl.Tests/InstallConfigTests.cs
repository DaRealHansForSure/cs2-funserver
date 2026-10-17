using System.Text.Json;
using Cs2ServerControl.Install;
using Xunit;

namespace Cs2ServerControl.Tests;

public class InstallConfigTests {
    // The SearchPaths part of a real CS2 gameinfo.gi, tabs as Valve writes them.
    private static readonly string[] GameInfoLines = [
        "\tFileSystem",
        "\t{",
        "\t\tSearchPaths",
        "\t\t{",
        "\t\t\tGame_LowViolence\tcsgo_lv // Perfect World content override",
        "\t\t\tGame\tcsgo",
        "\t\t\tGame\tcsgo_imported",
        "\t\t\tGame\tcsgo_core",
        "\t\t\tGame\tcore",
        "\t\t}",
        "\t}",
    ];

    [Fact]
    public void PutsTheMetamodSearchPathRightAboveGameCsgo() {
        List<string> edited = ConfigEdits.AddMetamodSearchPath(GameInfoLines);

        int metamod = edited.IndexOf("\t\t\tGame\tcsgo/addons/metamod");
        Assert.True(metamod > 0);
        Assert.Equal("\t\t\tGame\tcsgo", edited[metamod + 1]);
        Assert.Equal(GameInfoLines.Length + 1, edited.Count);
        Assert.True(ConfigEdits.HasMetamodSearchPath(edited));
        Assert.False(ConfigEdits.HasMetamodSearchPath(GameInfoLines));
    }

    [Fact]
    public void ASecondRunChangesNothing() {
        List<string> once = ConfigEdits.AddMetamodSearchPath(GameInfoLines);

        List<string> twice = ConfigEdits.AddMetamodSearchPath(once);

        Assert.Equal(once, twice);
    }

    [Fact]
    public void ABrokenGameInfoIsReported() {
        Assert.Throws<InstallException>(() => ConfigEdits.AddMetamodSearchPath(["\"GameInfo\"", "{", "}"]));
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
