using System.IO;
using System.Text.Json;
using Cs2ServerControl.Services;
using Xunit;

namespace Cs2ServerControl.Tests;

public class WorkshopTests {
    [Theory]
    [InlineData("3070244462", "3070244462")]
    [InlineData(" https://steamcommunity.com/sharedfiles/filedetails/?id=3070244462 ", "3070244462")]
    [InlineData("https://steamcommunity.com/sharedfiles/filedetails/?l=german&id=3070244462&searchtext=", "3070244462")]
    [InlineData("https://steamcommunity.com/workshop/filedetails/?id=123", null)]
    [InlineData("de_dust2", null)]
    [InlineData("", null)]
    public void ReadsTheItemIdFromALinkOrANumber(string input, string? expected) {
        Assert.Equal(expected, WorkshopLibrary.ParseId(input));
    }

    [Fact]
    public void ReadsTitleSizeAndPreviewFromSteamsAnswer() {
        const string json = """
            {"response":{"result":1,"resultcount":1,"publishedfiledetails":[{"publishedfileid":"3070244462","result":1,
             "consumer_app_id":730,"file_size":"124752240","preview_url":"https://images.example/a.jpg","title":"Aim Botz - Aim Training (CS2)"}]}}
            """;
        using JsonDocument document = JsonDocument.Parse(json);

        WorkshopItem item = WorkshopClient.ParseDetails(document.RootElement, "3070244462");

        Assert.Equal(new WorkshopItem("3070244462", "Aim Botz - Aim Training (CS2)", 124752240, "https://images.example/a.jpg"), item);
    }

    [Theory]
    [InlineData("""{"response":{"publishedfiledetails":[{"publishedfileid":"1","result":9}]}}""", "was not found or is not public")]
    [InlineData("""{"response":{"publishedfiledetails":[{"publishedfileid":"1","result":1,"consumer_app_id":440}]}}""", "is not for Counter-Strike 2")]
    [InlineData("""{"response":{}}""", "no details")]
    public void ExplainsWhatIsWrongWithAnItem(string json, string message) {
        using JsonDocument document = JsonDocument.Parse(json);

        WorkshopException exception = Assert.Throws<WorkshopException>(() => WorkshopClient.ParseDetails(document.RootElement, "1"));

        Assert.Contains(message, exception.Message);
    }

    [Fact]
    public void FindsTheMapInsideADownloadedItemWhenThere() {
        string? package = TestPaths.FindServerFile("game", "bin", "win64", "steamapps", "workshop", "content", "730", "3070244462", "3070244462_dir.vpk");
        if (package == null) {
            return;
        }
        string root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(package)!, "..", "..", "..", "..", "..", "..", "..", "..", ".."));
        var paths = new PackagePaths(root);

        WorkshopDownload? download = WorkshopLibrary.FindDownloadById(paths, "3070244462");

        Assert.NotNull(download);
        Assert.Equal(["aim_botz"], download.MapNames);
        Assert.Equal(package, paths.FindMapPackage("aim_botz"));
        MapWireframe wireframe = Cs2MapLoader.Load(package, "aim_botz");
        Assert.True(wireframe.Segments.Count > 100);
    }
}
