using System.IO;

namespace Cs2ServerControl.Services;

/// <summary>One installed map: which modes play it, how big it is, and its Workshop item when it came from there.</summary>
public sealed record MapLibraryEntry(string Name, IReadOnlyList<string> ModeIds, long SizeBytes, string? WorkshopId = null);

/// <summary>
/// Everything the Maps screen lists, read from the files on disk: the map
/// packages (.vpk) in game\csgo\maps, the Workshop maps the server downloaded
/// and the map list of every mode.
/// </summary>
public static class MapLibrary {
    /// <summary>
    /// The maps folder also holds packages that are no playable map: the
    /// lobby and vote screens, the cosmetic "_vanity" parts of each map and
    /// the Workshop preview maps of the main menu.
    /// </summary>
    internal static bool IsPlayableMapPackage(string mapName) {
        return !mapName.EndsWith("_vanity", StringComparison.OrdinalIgnoreCase)
            && !mapName.StartsWith("lobby_", StringComparison.OrdinalIgnoreCase)
            && !mapName.StartsWith("workshop_preview_", StringComparison.OrdinalIgnoreCase)
            && !mapName.Equals("graphics_settings", StringComparison.OrdinalIgnoreCase);
    }

    public static IReadOnlyList<MapLibraryEntry> Scan(PackagePaths paths) {
        if (!Directory.Exists(paths.MapsDir)) {
            return [];
        }

        Dictionary<string, List<string>> modesByMap = new(StringComparer.OrdinalIgnoreCase);
        foreach (GameMode mode in GameModes.All) {
            foreach (string map in MapCycleReader.Read(paths.MapCycleFile(mode.Id))) {
                if (!modesByMap.TryGetValue(map, out List<string>? modes)) {
                    modes = [];
                    modesByMap[map] = modes;
                }
                modes.Add(mode.Id);
            }
        }

        var entries = new List<MapLibraryEntry>();
        foreach (string mapFile in Directory.EnumerateFiles(paths.MapsDir, "*.vpk")) {
            string name = Path.GetFileNameWithoutExtension(mapFile).ToLowerInvariant();
            if (!IsPlayableMapPackage(name)) {
                continue;
            }
            entries.Add(new MapLibraryEntry(
                name,
                modesByMap.TryGetValue(name, out List<string>? modes) ? modes : [],
                new FileInfo(mapFile).Length));
        }
        foreach (WorkshopDownload download in WorkshopLibrary.ScanDownloads(paths)) {
            foreach (string name in download.MapNames) {
                entries.Add(new MapLibraryEntry(name, [], download.SizeBytes, download.Id));
            }
        }
        return entries.OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
