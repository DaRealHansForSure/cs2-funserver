using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace Cs2ServerControl.Services;

public sealed class WorkshopException(string message) : Exception(message);

/// <summary>
/// Steam's public Workshop details (ISteamRemoteStorage/GetPublishedFileDetails):
/// title, size and preview picture of one item, without a Web API key. A
/// search would need a key, so the window opens the Workshop in the browser
/// and the host pastes a link.
/// </summary>
public static class WorkshopClient {
    private const string DetailsUrl = "https://api.steampowered.com/ISteamRemoteStorage/GetPublishedFileDetails/v1/";
    private const int Cs2AppId = 730;
    public const string BrowseUrl = "https://steamcommunity.com/workshop/browse/?appid=730&requiredtags%5B%5D=CS2&requiredtags%5B%5D=Map&actualsort=trend&browsesort=trend";

    public static string PageUrl(string id) {
        return "https://steamcommunity.com/sharedfiles/filedetails/?id=" + id;
    }

    public static async Task<WorkshopItem> GetDetailsAsync(string id, CancellationToken cancellation = default) {
        using var form = new FormUrlEncodedContent([
            new KeyValuePair<string, string>("itemcount", "1"),
            new KeyValuePair<string, string>("publishedfileids[0]", id),
        ]);
        using HttpResponseMessage response = await Install.DownloadCache.Client.PostAsync(DetailsUrl, form, cancellation);
        response.EnsureSuccessStatusCode();
        await using Stream body = await response.Content.ReadAsStreamAsync(cancellation);
        using JsonDocument document = await JsonDocument.ParseAsync(body, cancellationToken: cancellation);
        return ParseDetails(document.RootElement, id);
    }

    /// <summary>Reads one item of the answer; throws WorkshopException with a message for the host.</summary>
    internal static WorkshopItem ParseDetails(JsonElement root, string id) {
        bool hasItem = root.TryGetProperty("response", out JsonElement response)
            && response.TryGetProperty("publishedfiledetails", out JsonElement items)
            && items.ValueKind == JsonValueKind.Array
            && items.GetArrayLength() > 0;
        if (!hasItem) {
            throw new WorkshopException("Steam sent no details for this item.");
        }

        JsonElement item = response.GetProperty("publishedfiledetails")[0];
        // result 1 is OK; 9 means not found, private items answer the same way.
        if (Int(item, "result") != 1) {
            throw new WorkshopException($"Workshop item {id} was not found or is not public.");
        }
        if (Int(item, "consumer_app_id") != Cs2AppId) {
            throw new WorkshopException($"Workshop item {id} is not for Counter-Strike 2.");
        }

        string title = Text(item, "title");
        long size = long.TryParse(Text(item, "file_size"), NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed) ? parsed : 0;
        return new WorkshopItem(id, title.Length == 0 ? id : title, size, Text(item, "preview_url"));
    }

    /// <summary>Saves the preview picture once; a picture that is already there stays.</summary>
    public static async Task DownloadPreviewAsync(WorkshopItem item, string targetFile, CancellationToken cancellation = default) {
        if (item.PreviewUrl.Length == 0 || File.Exists(targetFile)) {
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(targetFile)!);
        await Install.DownloadCache.DownloadFileAsync(item.PreviewUrl, targetFile, cancellation);
    }

    private static int Int(JsonElement element, string name) {
        if (!element.TryGetProperty(name, out JsonElement value)) {
            return 0;
        }
        return value.ValueKind == JsonValueKind.Number ? value.GetInt32() : 0;
    }

    private static string Text(JsonElement element, string name) {
        if (element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String) {
            return value.GetString() ?? "";
        }
        return "";
    }
}
