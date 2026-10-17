using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using SteamDatabase.ValvePak;

namespace Cs2ServerControl.Services;

/// <summary>A Workshop map the host added, with what Steam says about it.</summary>
public sealed record WorkshopItem(string Id, string Title, long SizeBytes, string PreviewUrl);

/// <summary>A Workshop item the server downloaded, and the maps inside it.</summary>
public sealed record WorkshopDownload(string Id, string PackageFile, IReadOnlyList<string> MapNames, long SizeBytes);

/// <summary>
/// The host's Workshop maps: the list from the Maps screen (tools\workshop.json)
/// and what the server downloaded into its Workshop folder. A Workshop item
/// is a package with the map package inside (maps\&lt;name&gt;.vpk); the map
/// name, which the game uses everywhere, is only known after the download.
/// </summary>
public static partial class WorkshopLibrary {
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly ConcurrentDictionary<string, (DateTime Timestamp, WorkshopDownload? Download)> ScanCache = new(StringComparer.OrdinalIgnoreCase);

    // Parts of a map that are no map of their own: the 3D skybox and cosmetic additions.
    private static readonly string[] SideMapSuffixes = ["_3dsky", "_skybox", "_vanity"];

    [GeneratedRegex(@"(?:[?&]id=|^)(?<id>\d{6,12})(?:\D|$)")]
    private static partial Regex WorkshopIdPattern();

    /// <summary>The item id from a Workshop link (…filedetails/?id=123) or a bare number; null for anything else.</summary>
    public static string? ParseId(string input) {
        Match match = WorkshopIdPattern().Match(input.Trim());
        return match.Success ? match.Groups["id"].Value : null;
    }

    public static IReadOnlyList<WorkshopItem> LoadItems(PackagePaths paths) {
        try {
            if (!File.Exists(paths.WorkshopListFile)) {
                return [];
            }
            return JsonSerializer.Deserialize<List<WorkshopItem>>(File.ReadAllText(paths.WorkshopListFile)) ?? [];
        } catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException) {
            return [];
        }
    }

    public static void SaveItems(PackagePaths paths, IReadOnlyList<WorkshopItem> items) {
        Directory.CreateDirectory(paths.ToolsDir);
        File.WriteAllText(paths.WorkshopListFile, JsonSerializer.Serialize(items, JsonOptions));
    }

    /// <summary>Every item in the server's Workshop folder; a folder is only read again after it changed.</summary>
    public static IReadOnlyList<WorkshopDownload> ScanDownloads(PackagePaths paths) {
        if (!Directory.Exists(paths.WorkshopContentDir)) {
            return [];
        }
        var downloads = new List<WorkshopDownload>();
        foreach (string folder in Directory.EnumerateDirectories(paths.WorkshopContentDir)) {
            WorkshopDownload? download = ReadDownload(folder);
            if (download != null) {
                downloads.Add(download);
            }
        }
        return downloads;
    }

    public static WorkshopDownload? FindDownload(PackagePaths paths, string mapName) {
        return ScanDownloads(paths).FirstOrDefault(download => download.MapNames.Contains(mapName, StringComparer.OrdinalIgnoreCase));
    }

    public static WorkshopDownload? FindDownloadById(PackagePaths paths, string id) {
        string folder = Path.Combine(paths.WorkshopContentDir, id);
        return Directory.Exists(folder) ? ReadDownload(folder) : null;
    }

    private static WorkshopDownload? ReadDownload(string folder) {
        string id = Path.GetFileName(folder);
        string packageFile = Path.Combine(folder, id + "_dir.vpk");
        if (!File.Exists(packageFile)) {
            // A single file item has no _dir part.
            packageFile = Path.Combine(folder, id + ".vpk");
            if (!File.Exists(packageFile)) {
                return null;
            }
        }

        DateTime timestamp = File.GetLastWriteTimeUtc(packageFile);
        if (ScanCache.TryGetValue(packageFile, out var cached) && cached.Timestamp == timestamp) {
            return cached.Download;
        }

        WorkshopDownload? download = null;
        try {
            using var package = new Package();
            package.Read(packageFile);
            List<string> mapNames = [];
            if (package.Entries.TryGetValue("vpk", out List<PackageEntry>? entries)) {
                mapNames = entries
                    .Where(entry => entry.DirectoryName.Equals("maps", StringComparison.OrdinalIgnoreCase))
                    .Select(entry => entry.FileName.ToLowerInvariant())
                    .Where(name => !SideMapSuffixes.Any(suffix => name.EndsWith(suffix, StringComparison.Ordinal)))
                    .ToList();
            }
            long size = Directory.EnumerateFiles(folder).Sum(file => new FileInfo(file).Length);
            download = new WorkshopDownload(id, packageFile, mapNames, size);
        } catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException) {
            // Still downloading or damaged; the next scan tries again.
            return null;
        }
        ScanCache[packageFile] = (timestamp, download);
        return download;
    }
}
