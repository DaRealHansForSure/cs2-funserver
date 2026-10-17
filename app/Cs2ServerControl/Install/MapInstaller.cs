using System.IO;
using System.Net.Http;
using System.Text.Json;
using Cs2ServerControl.Services;

namespace Cs2ServerControl.Install;

/// <summary>
/// Everything a map update touches: announcer sounds, the map pack from
/// tools\maps.csv, the map pictures, the custom-maps folder and the per mode
/// map cycles.
/// </summary>
internal sealed class MapInstaller {
    // Two requests per map, a short pause keeps us a polite API user.
    private static readonly TimeSpan PreviewPause = TimeSpan.FromMilliseconds(300);

    private static readonly IReadOnlySet<string> OwnArchiveExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
        ".zip", ".rar", ".7z",
    };

    private static readonly IReadOnlyList<string> ReadablePictureExtensions = [".jpg", ".jpeg", ".png"];

    private readonly InstallPaths _paths;
    private readonly InstallLog _log;
    private readonly DownloadCache _downloads;
    private readonly ArchiveExtractor _extractor;
    private readonly MapContentCopier _copier;

    public MapInstaller(InstallPaths paths, InstallLog log, DownloadCache downloads, ArchiveExtractor extractor) {
        _paths = paths;
        _log = log;
        _downloads = downloads;
        _extractor = extractor;
        _copier = new MapContentCopier(paths.GameDir);
    }

    /// <summary>
    /// Downloads the announcer sound packs and copies the files miscstats plays
    /// into cstrike\sound under the names it expects. Files that already exist
    /// are kept, so sounds the owner put there by hand win. A failure here only
    /// costs the sounds, never the install.
    /// </summary>
    public async Task InstallQuakeSoundsAsync(CancellationToken cancellation) {
        _log.Step("Quake announcer sounds");

        Dictionary<string, string> extractedPacks = [];
        foreach ((string packName, QuakeSoundPack pack) in InstallComponents.QuakeSoundPacks) {
            try {
                string archive = await _downloads.GetAsync(pack.Url, pack.FileName, cancellation);
                string extracted = await Task.Run(() => _extractor.Expand(archive), cancellation);
                extractedPacks[packName] = Path.Combine(extracted, pack.Folder);
            } catch (Exception exception) when (IsRecoverable(exception)) {
                _log.Problem($"Sound pack {packName} failed: {exception.Message}");
            }
        }

        int installedCount = 0;
        foreach (QuakeSound sound in InstallComponents.QuakeSounds) {
            if (!extractedPacks.TryGetValue(sound.PackName, out string? packFolder)) {
                continue;
            }

            string targetPath = Path.Combine(_paths.GameDir, "sound", sound.TargetName);
            if (File.Exists(targetPath)) {
                installedCount++;
                continue;
            }

            string sourcePath = Path.Combine(packFolder, sound.SourceName);
            if (!File.Exists(sourcePath)) {
                _log.Problem($"{sound.SourceName} is missing in sound pack {sound.PackName}.");
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            File.Copy(sourcePath, targetPath);
            installedCount++;
        }

        _log.Note($"{installedCount} of {InstallComponents.QuakeSounds.Count} sounds in place");
    }

    public async Task InstallMapPackAsync(CancellationToken cancellation) {
        _log.Step("Downloading fun maps");

        if (!File.Exists(_paths.MapListFile)) {
            _log.Problem("maps.csv is missing, skipping the map download.");
            return;
        }

        List<string> failedMaps = [];
        foreach (MapListEntry map in AllListedMaps()) {
            try {
                string archive = await _downloads.GetAsync(map.Url, Path.Combine("maps", map.ArchiveName), cancellation);
                IReadOnlyList<string> installedMaps = await Task.Run(() => _copier.Install(_extractor.Expand(archive)), cancellation);
                _log.Note($"{map.Name}: {string.Join(", ", installedMaps)}");
            } catch (Exception exception) when (IsRecoverable(exception)) {
                failedMaps.Add(map.Name);
                _log.Problem($"{map.Name} failed: {exception.Message}");
            }
        }

        if (failedMaps.Count > 0) {
            _log.Problem($"Not installed: {string.Join(", ", failedMaps)}. Try again later with Update maps.");
        }
    }

    /// <summary>
    /// Downloads one screenshot per map from GameBanana for the map tiles of the
    /// Server Control window, as tools\previews\&lt;map&gt;.jpg. The ModId column
    /// of maps.csv names the GameBanana page of each map. A picture that is
    /// already there is kept, so pictures the owner put there by hand win and
    /// repeated runs cost nothing. Stock maps need no download, the window shows
    /// their radar images. A failure only costs the picture.
    /// </summary>
    public async Task InstallMapPreviewsAsync(CancellationToken cancellation) {
        _log.Step("Map pictures for the Server Control window");

        if (!File.Exists(_paths.MapListFile)) {
            return;
        }
        Directory.CreateDirectory(_paths.PreviewsDir);

        int downloadedCount = 0;
        int existingCount = 0;
        foreach (MapListEntry map in AllListedMaps()) {
            if (map.ModId.Length == 0) {
                continue;
            }

            if (HasPreview(map.Name)) {
                existingCount++;
                continue;
            }

            try {
                string? imageUrl = await FindPreviewUrlAsync(map.ModId, cancellation);
                if (imageUrl == null) {
                    _log.Problem($"{map.Name}: no jpg or png picture on GameBanana.");
                    continue;
                }

                string extension = Path.GetExtension(new Uri(imageUrl).AbsolutePath).ToLowerInvariant();
                string targetPath = Path.Combine(_paths.PreviewsDir, map.Name + extension);
                await DownloadCache.DownloadFileAsync(imageUrl, targetPath, cancellation);
                downloadedCount++;
            } catch (Exception exception) when (IsRecoverable(exception)) {
                _log.Problem($"{map.Name} picture failed: {exception.Message}");
            }

            await Task.Delay(PreviewPause, cancellation);
        }

        _log.Note($"{downloadedCount} pictures downloaded, {existingCount} already there");
    }

    private bool HasPreview(string mapName) {
        // A ".part" file is an aborted download, not a picture.
        return Directory.EnumerateFiles(_paths.PreviewsDir, mapName + ".*")
            .Any(file => !file.EndsWith(".part", StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<string?> FindPreviewUrlAsync(string modId, CancellationToken cancellation) {
        string apiUrl = $"https://gamebanana.com/apiv11/Mod/{Uri.EscapeDataString(modId)}/ProfilePage";
        await using Stream body = await DownloadCache.Client.GetStreamAsync(apiUrl, cancellation);
        using JsonDocument profilePage = await JsonDocument.ParseAsync(body, cancellationToken: cancellation);
        return PickPreviewUrl(profilePage.RootElement);
    }

    /// <summary>
    /// The URL of the first jpg or png screenshot of a GameBanana mod, in the
    /// 530 px size when there is one (big enough for a tile, a fraction of the
    /// full size). The window reads jpg and png, webp would need an extra codec.
    /// </summary>
    internal static string? PickPreviewUrl(JsonElement profilePage) {
        bool hasImages = profilePage.ValueKind == JsonValueKind.Object
            && profilePage.TryGetProperty("_aPreviewMedia", out JsonElement previewMedia)
            && previewMedia.ValueKind == JsonValueKind.Object
            && previewMedia.TryGetProperty("_aImages", out JsonElement images)
            && images.ValueKind == JsonValueKind.Array;
        if (!hasImages) {
            return null;
        }

        foreach (JsonElement image in profilePage.GetProperty("_aPreviewMedia").GetProperty("_aImages").EnumerateArray()) {
            if (image.ValueKind != JsonValueKind.Object) {
                continue;
            }

            string baseUrl = StringProperty(image, "_sBaseUrl");
            foreach (string fileName in new[] { StringProperty(image, "_sFile530"), StringProperty(image, "_sFile220"), StringProperty(image, "_sFile") }) {
                bool isReadableFormat = fileName.Length > 0
                    && ReadablePictureExtensions.Contains(Path.GetExtension(fileName).ToLowerInvariant());
                if (isReadableFormat) {
                    return $"{baseUrl}/{fileName}";
                }
            }
        }
        return null;
    }

    private static string StringProperty(JsonElement element, string name) {
        if (element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String) {
            return value.GetString() ?? "";
        }
        return "";
    }

    public async Task InstallOwnMapsAsync(CancellationToken cancellation) {
        _log.Step("Custom maps from the custom-maps folder");

        Directory.CreateDirectory(_paths.OwnMapsDir);

        bool hasLooseMaps = FileTree.FilesWithExtension(_paths.OwnMapsDir, ".bsp", recursive: true).Any();
        if (hasLooseMaps) {
            try {
                IReadOnlyList<string> installedMaps = await Task.Run(() => _copier.Install(_paths.OwnMapsDir), cancellation);
                _log.Note($"Loose files: {string.Join(", ", installedMaps)}");
            } catch (Exception exception) when (IsRecoverable(exception)) {
                _log.Problem($"Loose files in custom-maps: {exception.Message}");
            }
        }

        IEnumerable<string> archives = Directory.EnumerateFiles(_paths.OwnMapsDir)
            .Where(file => OwnArchiveExtensions.Contains(Path.GetExtension(file)))
            .Order(StringComparer.OrdinalIgnoreCase);
        foreach (string archive in archives) {
            try {
                IReadOnlyList<string> installedMaps = await Task.Run(() => _copier.Install(_extractor.Expand(archive)), cancellation);
                _log.Note($"{Path.GetFileName(archive)}: {string.Join(", ", installedMaps)}");
            } catch (Exception exception) when (IsRecoverable(exception)) {
                _log.Problem($"{Path.GetFileName(archive)} failed: {exception.Message}");
            }
        }
    }

    /// <summary>The map pack of the package and the maps installed from the market.</summary>
    private IEnumerable<MapListEntry> AllListedMaps() {
        return MapList.Load(_paths.MapListFile).Concat(MapList.LoadIfExists(_paths.MarketListFile));
    }

    /// <summary>
    /// One map from the market: the archive GameBanana marked clean goes
    /// through the same steps as the map pack (cache, unpack, copy, missing
    /// sprites and models), then its picture and the map lists. Returns the
    /// installed map names. The entry in market.csv makes Update maps restore
    /// it like a map of the pack.
    /// </summary>
    public async Task<IReadOnlyList<string>> InstallMarketMapAsync(MarketMap map, CancellationToken cancellation) {
        _log.Step($"Installing {map.Name} from GameBanana");

        IReadOnlyList<MarketFile> files = await GameBananaMarket.LoadFilesAsync(map.ModId, cancellation);
        MarketFile? archiveFile = GameBananaMarket.PickArchive(files);
        if (archiveFile == null) {
            throw new InstallException($"{map.Name} has no .zip, .rar or .7z file that passed GameBanana's virus scan.");
        }
        _log.Note($"{archiveFile.FileName} ({archiveFile.Size / 1024 / 1024.0:0.0} MB)");

        string archive = await _downloads.GetAsync(archiveFile.DownloadUrl, Path.Combine("maps", archiveFile.FileName), cancellation);
        IReadOnlyList<string> installedMaps = await Task.Run(() => _copier.Install(_extractor.Expand(archive)), cancellation);
        _log.Note($"Installed: {string.Join(", ", installedMaps)}");

        var entry = new MapListEntry(installedMaps[0], archiveFile.DownloadUrl, archiveFile.FileName,
            map.ModId.ToString(System.Globalization.CultureInfo.InvariantCulture), string.Join(';', installedMaps));
        List<MapListEntry> market = [.. MapList.LoadIfExists(_paths.MarketListFile).Where(existing => existing.ModId != entry.ModId), entry];
        MapList.Save(_paths.MarketListFile, market);

        await DownloadMarketPictureAsync(entry, cancellation);
        UpdateMapCycles();
        return installedMaps;
    }

    /// <summary>
    /// Deletes the maps a market install brought (.bsp, .nav, .txt, .res and
    /// their pictures) and its market.csv entry. Maps that are also stock or
    /// part of the map pack stay, shared sounds and models stay too.
    /// </summary>
    public IReadOnlyList<string> RemoveMarketMap(int modId) {
        string id = modId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        List<MapListEntry> market = [.. MapList.LoadIfExists(_paths.MarketListFile)];
        MapListEntry? entry = market.FirstOrDefault(existing => existing.ModId == id);
        if (entry == null) {
            throw new InstallException("This map was not installed from the market.");
        }
        _log.Step($"Removing {entry.Name}");

        var packMaps = MapList.Load(_paths.MapListFile).SelectMany(pack => pack.MapNames).ToHashSet(StringComparer.OrdinalIgnoreCase);
        List<string> removed = [];
        foreach (string mapName in entry.MapNames) {
            bool isShared = InstallComponents.StockMaps.Contains(mapName) || packMaps.Contains(mapName);
            if (isShared) {
                _log.Note($"{mapName} is part of the package, kept");
                continue;
            }
            foreach (string extension in new[] { ".bsp", ".nav", ".txt", ".res" }) {
                DeleteAndReport(Path.Combine(_paths.MapsDir, mapName + extension));
            }
            foreach (string picture in Directory.Exists(_paths.PreviewsDir) ? Directory.EnumerateFiles(_paths.PreviewsDir, mapName + ".*") : []) {
                DeleteAndReport(picture);
            }
            DeleteAndReport(Path.Combine(_paths.Package.GeneratedPreviewsDir, mapName + ".png"));
            removed.Add(mapName);
        }

        market.Remove(entry);
        MapList.Save(_paths.MarketListFile, market);
        _log.Note(removed.Count == 0 ? "No map files removed" : $"Removed: {string.Join(", ", removed)}");
        UpdateMapCycles();
        return removed;
    }

    private void DeleteAndReport(string file) {
        try {
            File.Delete(file);
        } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
            _log.Problem($"Could not delete {Path.GetFileName(file)}: {exception.Message}");
        }
    }

    private async Task DownloadMarketPictureAsync(MapListEntry entry, CancellationToken cancellation) {
        if (!Directory.Exists(_paths.PreviewsDir)) {
            Directory.CreateDirectory(_paths.PreviewsDir);
        }
        if (HasPreview(entry.Name)) {
            return;
        }
        try {
            string? imageUrl = await FindPreviewUrlAsync(entry.ModId, cancellation);
            if (imageUrl == null) {
                _log.Note("No jpg or png picture on GameBanana; Generate missing pictures can draw one.");
                return;
            }
            string extension = Path.GetExtension(new Uri(imageUrl).AbsolutePath).ToLowerInvariant();
            await DownloadCache.DownloadFileAsync(imageUrl, Path.Combine(_paths.PreviewsDir, entry.Name + extension), cancellation);
        } catch (Exception exception) when (IsRecoverable(exception)) {
            _log.Problem($"Picture failed: {exception.Message}");
        }
    }

    public void UpdateMapCycles() {
        _log.Step("Building the map lists for the modes");

        // A map that names a model or sprite the server does not have stops
        // the server when it loads (an engine error box, so it just hangs).
        // It stays installed but out of every map list, so no vote or
        // rotation can ever pick it.
        List<string> playableMaps = [];
        foreach (string bspFile in FileTree.FilesWithExtension(_paths.MapsDir, ".bsp", recursive: false)) {
            string mapName = Path.GetFileNameWithoutExtension(bspFile);
            IReadOnlyList<string> missing = MapResources.FindMissing(bspFile, _paths.GameDir);
            if (missing.Count > 0) {
                _log.Problem($"{mapName} left out of the map lists, it needs {string.Join(", ", missing)}.");
                continue;
            }
            playableMaps.Add(mapName);
        }
        IReadOnlyDictionary<string, List<string>> mapsByMode = MapModes.BuildCycles(playableMaps);

        foreach (string mode in MapModes.AllModes) {
            List<string> cycle = mapsByMode[mode];
            FileTree.WriteLines(_paths.Package.MapCycleFile(mode), cycle);
            _log.Note(string.Format("{0,-11} {1,3} Maps", mode, cycle.Count));
        }

        // The engine starts with mapcycle.txt, which therefore mirrors classic.
        File.Copy(_paths.Package.MapCycleFile("classic"), Path.Combine(_paths.GameDir, "mapcycle.txt"), overwrite: true);
    }

    /// <summary>One map or sound pack going wrong must not stop the others; a cancel still does.</summary>
    private static bool IsRecoverable(Exception exception) {
        return exception is InstallException or IOException or UnauthorizedAccessException
            or HttpRequestException or JsonException or InvalidOperationException or UriFormatException
            || (exception is TaskCanceledException && exception.InnerException is TimeoutException);
    }
}
