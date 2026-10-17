using System.IO;
using System.Text.Json;
using Cs2ServerControl.Install;
using Cs2ServerControl.Services;
using Xunit;

namespace Cs2ServerControl.Tests;

public class GameBananaMarketTests {
    [Fact]
    public void ReadsAPageOfMapsWithTheCardPicture() {
        const string json = """
            {
              "_aMetadata": { "_nRecordCount": 1751, "_bIsComplete": false, "_nPerpage": 2 },
              "_aRecords": [
                {
                  "_idRow": 86562, "_sModelName": "Mod", "_sName": "fy_pool_day", "_nLikeCount": 64, "_nViewCount": 325507,
                  "_tsDateAdded": 1322855532,
                  "_aSubCategory": { "_sName": "Fight Yard" },
                  "_aPreviewMedia": { "_aImages": [
                    { "_sBaseUrl": "https://images.gamebanana.com/img/ss/mods", "_sFile": "a.webp", "_sFile220": "220-90_a.webp" },
                    { "_sBaseUrl": "https://images.gamebanana.com/img/ss/mods", "_sFile": "b.jpg", "_sFile220": "220-90_b.jpg" }
                  ] }
                },
                { "_idRow": 5, "_sModelName": "Tool", "_sName": "not a map" },
                { "_idRow": 7, "_sModelName": "Mod", "_sName": "aim_no_picture" }
              ]
            }
            """;

        MarketPage page = GameBananaMarket.ParsePage(JsonDocument.Parse(json).RootElement);

        Assert.Equal(1751, page.TotalCount);
        Assert.Equal(["fy_pool_day", "aim_no_picture"], page.Maps.Select(map => map.Name));
        MarketMap pool = page.Maps[0];
        Assert.Equal(86562, pool.ModId);
        Assert.Equal("Fight Yard", pool.Category);
        Assert.Equal(64, pool.Likes);
        Assert.Equal("https://images.gamebanana.com/img/ss/mods/220-90_b.jpg", pool.ImageUrl);
        Assert.Equal("https://gamebanana.com/mods/86562", pool.PageUrl);
        Assert.Null(page.Maps[1].ImageUrl);
    }

    [Fact]
    public void AnErrorAnswerIsAnEmptyPage() {
        MarketPage page = GameBananaMarket.ParsePage(JsonDocument.Parse("""{ "_sErrorCode": "INPUT_ERRORS" }""").RootElement);

        Assert.Empty(page.Maps);
        Assert.Equal(0, page.TotalCount);
    }

    [Fact]
    public void ReadsTheCategories() {
        const string json = """
            [ { "_idRow": 5485, "_sName": "Fight Yard", "_nItemCount": 1751 }, { "_idRow": 0, "_sName": "broken" } ]
            """;

        IReadOnlyList<MarketCategory> categories = GameBananaMarket.ParseCategories(JsonDocument.Parse(json).RootElement);

        Assert.Equal([new MarketCategory(5485, "Fight Yard", 1751)], categories);
    }

    [Fact]
    public void InstallsOnlyACleanArchiveAndPrefersTheBiggest() {
        const string json = """
            { "_aFiles": [
              { "_idRow": 1, "_sFile": "readme.txt", "_nFilesize": 10, "_sAvResult": "clean" },
              { "_idRow": 2, "_sFile": "map_patch.zip", "_nFilesize": 1000, "_sAvResult": "clean" },
              { "_idRow": 3, "_sFile": "map_full.rar", "_nFilesize": 900000, "_sAvResult": "clean" },
              { "_idRow": 4, "_sFile": "map_bigger.7z", "_nFilesize": 9000000, "_sAvResult": "suspicious" },
              { "_idRow": 5, "_sFile": "old.zip", "_nFilesize": 99999999 }
            ] }
            """;

        IReadOnlyList<MarketFile> files = GameBananaMarket.ParseFiles(JsonDocument.Parse(json).RootElement);
        MarketFile? picked = GameBananaMarket.PickArchive(files);

        Assert.Equal(5, files.Count);
        Assert.NotNull(picked);
        Assert.Equal(3, picked.FileId);
        Assert.Equal("https://gamebanana.com/dl/3", picked.DownloadUrl);
        Assert.Null(GameBananaMarket.PickArchive([files[0], files[3], files[4]]));
    }

    [Fact]
    public void BuildsTheSearchWithCategorySortAndNameFilter() {
        string url = GameBananaMarket.BuildSearchUrl(2, 5485, " pool, day ", MarketSort.MostLiked);

        Assert.Contains("_nPage=2", url);
        Assert.Contains("_aFilters[Generic_Category]=5485", url);
        Assert.Contains("_sSort=Generic_MostLiked", url);
        Assert.Contains("_aFilters[Generic_Name]=contains%2Cpool%20%20day", url);

        string everything = GameBananaMarket.BuildSearchUrl(1, null, "", MarketSort.MostDownloaded);
        Assert.Contains($"_aFilters[Generic_Category]={GameBananaMarket.MapsCategoryId}", everything);
        Assert.DoesNotContain("Generic_Name", everything);
    }

    [Fact]
    public void TheMarketListKeepsAllMapNamesAndQuotesCommas() {
        string file = Path.Combine(Path.GetTempPath(), "cs16-market-" + Guid.NewGuid().ToString("N") + ".csv");
        try {
            MapList.Save(file, [new MapListEntry("de_a", "https://gamebanana.com/dl/1", "pack, final.zip", "42", "de_a;de_b")]);

            MapListEntry entry = Assert.Single(MapList.LoadIfExists(file));
            Assert.Equal("pack, final.zip", entry.ArchiveName);
            Assert.Equal(["de_a", "de_b"], entry.MapNames);
            Assert.Empty(MapList.LoadIfExists(file + ".missing"));
        } finally {
            File.Delete(file);
        }
    }
}
