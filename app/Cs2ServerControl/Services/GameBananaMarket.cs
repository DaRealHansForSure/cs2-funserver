using System.IO;
using System.Net.Http;
using System.Text.Json;
using Cs2ServerControl.Install;

namespace Cs2ServerControl.Services;

public sealed record MarketCategory(int Id, string Name, int MapCount);

/// <summary>One map on GameBanana, as the market lists it.</summary>
public sealed record MarketMap(int ModId, string Name, string Category, int Likes, int Views, string? ImageUrl, DateTimeOffset Added) {
    public string PageUrl => $"https://gamebanana.com/mods/{ModId}";
}

public sealed record MarketPage(IReadOnlyList<MarketMap> Maps, int TotalCount);

/// <summary>A downloadable file of a map; IsClean is GameBanana's virus scan result.</summary>
public sealed record MarketFile(int FileId, string FileName, long Size, bool IsClean) {
    public string DownloadUrl => $"https://gamebanana.com/dl/{FileId}";
}

public enum MarketSort {
    MostDownloaded,
    MostLiked,
    MostViewed,
    Newest,
    RecentlyUpdated,
}

/// <summary>
/// The Counter-Strike 1.6 maps on GameBanana, through its public apiv11 (the
/// same site the map pack and the pictures come from). Only read requests;
/// the parsers are separate so they can be tested without the network.
/// </summary>
public static class GameBananaMarket {
    public const int GameId = 4254;
    public const int MapsCategoryId = 5474;
    // The most GameBanana sends per request.
    public const int PageSize = 50;

    private const string ApiUrl = "https://gamebanana.com/apiv11";

    private static readonly IReadOnlySet<string> ArchiveExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
        ".zip", ".rar", ".7z",
    };

    private static readonly IReadOnlySet<string> PictureExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
        ".jpg", ".jpeg", ".png",
    };

    public static async Task<IReadOnlyList<MarketCategory>> LoadCategoriesAsync(CancellationToken cancellation) {
        using JsonDocument document = await GetJsonAsync($"{ApiUrl}/Mod/Categories?_idCategoryRow={MapsCategoryId}&_sSort=a_to_z&_bShowEmpty=true", cancellation);
        return ParseCategories(document.RootElement);
    }

    /// <summary>One page of maps, filtered and sorted on GameBanana's side.</summary>
    public static async Task<MarketPage> SearchAsync(int page, int? categoryId, string nameContains, MarketSort sort, CancellationToken cancellation) {
        using JsonDocument document = await GetJsonAsync(BuildSearchUrl(page, categoryId, nameContains, sort), cancellation);
        return ParsePage(document.RootElement);
    }

    public static async Task<IReadOnlyList<MarketFile>> LoadFilesAsync(int modId, CancellationToken cancellation) {
        using JsonDocument document = await GetJsonAsync($"{ApiUrl}/Mod/{modId}/ProfilePage", cancellation);
        return ParseFiles(document.RootElement);
    }

    internal static string BuildSearchUrl(int page, int? categoryId, string nameContains, MarketSort sort) {
        string url = $"{ApiUrl}/Mod/Index?_nPage={page}&_nPerpage={PageSize}"
            + $"&_aFilters[Generic_Category]={categoryId ?? MapsCategoryId}&_sSort={SortName(sort)}";
        // The filter value is "contains,<text>", so a comma in the text would cut it.
        string search = nameContains.Replace(",", " ").Trim();
        if (search.Length > 0) {
            url += "&_aFilters[Generic_Name]=" + Uri.EscapeDataString("contains," + search);
        }
        return url;
    }

    /// <summary>
    /// The file to install: an archive the virus scan passed, the biggest one
    /// when there are several (the full map rather than a patch). Null when
    /// there is none, the market then offers no Install.
    /// </summary>
    public static MarketFile? PickArchive(IReadOnlyList<MarketFile> files) {
        return files
            .Where(file => file.IsClean && ArchiveExtensions.Contains(Path.GetExtension(file.FileName)))
            .OrderByDescending(file => file.Size)
            .FirstOrDefault();
    }

    internal static IReadOnlyList<MarketCategory> ParseCategories(JsonElement root) {
        if (root.ValueKind != JsonValueKind.Array) {
            return [];
        }
        return root.EnumerateArray()
            .Where(category => category.ValueKind == JsonValueKind.Object)
            .Select(category => new MarketCategory(IntOf(category, "_idRow"), StringOf(category, "_sName"), IntOf(category, "_nItemCount")))
            .Where(category => category.Id > 0 && category.Name.Length > 0)
            .ToList();
    }

    internal static MarketPage ParsePage(JsonElement root) {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("_aRecords", out JsonElement records) || records.ValueKind != JsonValueKind.Array) {
            return new MarketPage([], 0);
        }

        int total = 0;
        if (root.TryGetProperty("_aMetadata", out JsonElement metadata) && metadata.ValueKind == JsonValueKind.Object) {
            total = IntOf(metadata, "_nRecordCount");
        }

        var maps = new List<MarketMap>();
        foreach (JsonElement record in records.EnumerateArray()) {
            if (record.ValueKind != JsonValueKind.Object || StringOf(record, "_sModelName") != "Mod") {
                continue;
            }
            int modId = IntOf(record, "_idRow");
            string name = StringOf(record, "_sName");
            if (modId <= 0 || name.Length == 0) {
                continue;
            }
            string category = record.TryGetProperty("_aSubCategory", out JsonElement subCategory) && subCategory.ValueKind == JsonValueKind.Object
                ? StringOf(subCategory, "_sName")
                : "";
            maps.Add(new MarketMap(modId, name, category, IntOf(record, "_nLikeCount"), IntOf(record, "_nViewCount"),
                PickCardImage(record), DateTimeOffset.FromUnixTimeSeconds(LongOf(record, "_tsDateAdded"))));
        }
        return new MarketPage(maps, total);
    }

    internal static IReadOnlyList<MarketFile> ParseFiles(JsonElement profilePage) {
        if (profilePage.ValueKind != JsonValueKind.Object || !profilePage.TryGetProperty("_aFiles", out JsonElement files) || files.ValueKind != JsonValueKind.Array) {
            return [];
        }
        return files.EnumerateArray()
            .Where(file => file.ValueKind == JsonValueKind.Object)
            .Select(file => new MarketFile(IntOf(file, "_idRow"), StringOf(file, "_sFile"), LongOf(file, "_nFilesize"),
                // Older files have no scan result at all; only a finished, clean scan counts.
                StringOf(file, "_sAvResult") == "clean"))
            .Where(file => file.FileId > 0 && file.FileName.Length > 0)
            .ToList();
    }

    /// <summary>The 220 px screenshot for a card, jpg or png only (WPF has no webp codec).</summary>
    private static string? PickCardImage(JsonElement record) {
        bool hasImages = record.TryGetProperty("_aPreviewMedia", out JsonElement media)
            && media.ValueKind == JsonValueKind.Object
            && media.TryGetProperty("_aImages", out JsonElement images)
            && images.ValueKind == JsonValueKind.Array;
        if (!hasImages) {
            return null;
        }

        foreach (JsonElement image in record.GetProperty("_aPreviewMedia").GetProperty("_aImages").EnumerateArray()) {
            if (image.ValueKind != JsonValueKind.Object) {
                continue;
            }
            string baseUrl = StringOf(image, "_sBaseUrl");
            foreach (string fileName in new[] { StringOf(image, "_sFile220"), StringOf(image, "_sFile530"), StringOf(image, "_sFile") }) {
                if (fileName.Length > 0 && PictureExtensions.Contains(Path.GetExtension(fileName))) {
                    return $"{baseUrl}/{fileName}";
                }
            }
        }
        return null;
    }

    private static string SortName(MarketSort sort) {
        switch (sort) {
            case MarketSort.MostLiked:
                return "Generic_MostLiked";
            case MarketSort.MostViewed:
                return "Generic_MostViewed";
            case MarketSort.Newest:
                return "Generic_Newest";
            case MarketSort.RecentlyUpdated:
                return "Generic_LatestUpdated";
            default:
                return "Generic_MostDownloaded";
        }
    }

    private static async Task<JsonDocument> GetJsonAsync(string url, CancellationToken cancellation) {
        using HttpResponseMessage response = await DownloadCache.Client.GetAsync(url, cancellation);
        response.EnsureSuccessStatusCode();
        await using Stream body = await response.Content.ReadAsStreamAsync(cancellation);
        return await JsonDocument.ParseAsync(body, cancellationToken: cancellation);
    }

    private static string StringOf(JsonElement element, string property) {
        return element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    }

    private static int IntOf(JsonElement element, string property) {
        return element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number) ? number : 0;
    }

    private static long LongOf(JsonElement element, string property) {
        return element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long number) ? number : 0;
    }
}
