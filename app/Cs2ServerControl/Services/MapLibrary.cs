using System.IO;
using Cs2ServerControl.Install;

namespace Cs2ServerControl.Services;

public enum MapSource {
    Stock,
    MapPack,
    Custom,
}

/// <summary>One installed map: where it came from, which modes play it, what it still lacks. HasNav means a usable one.</summary>
public sealed record MapLibraryEntry(
    string Name,
    MapSource Source,
    IReadOnlyList<string> ModeIds,
    bool HasNav,
    long SizeBytes,
    bool IsNavOutdated = false,
    IReadOnlyList<string>? MissingFiles = null) {
    /// <summary>A model or sprite the map names is not on the server: loading it would stop the server.</summary>
    public bool IsPlayable => MissingFiles == null || MissingFiles.Count == 0;
}

/// <summary>A file in custom-maps that "Update maps" has not installed yet.</summary>
public sealed record WaitingMap(string FileName, bool IsArchive);

/// <summary>
/// Everything the Maps screen lists, read from the files on disk: the map
/// packages (.vpk) in game\csgo\maps, the mapcycle of every mode, loose nav
/// files, tools\maps.csv for the map pack and the custom-maps folder.
/// </summary>
public static class MapLibrary {
    private static readonly string[] ArchiveExtensions = [".zip", ".rar", ".7z"];

    /// <summary>
    /// The maps folder also holds packages that are no playable map: the
    /// lobby and vote screens and the cosmetic "_vanity" parts of each map.
    /// </summary>
    internal static bool IsPlayableMapPackage(string mapName) {
        return !mapName.EndsWith("_vanity", StringComparison.OrdinalIgnoreCase)
            && !mapName.StartsWith("lobby_", StringComparison.OrdinalIgnoreCase)
            && !mapName.Equals("graphics_settings", StringComparison.OrdinalIgnoreCase);
    }

    public static IReadOnlyList<MapLibraryEntry> Scan(PackagePaths paths) {
        if (!Directory.Exists(paths.MapsDir)) {
            return [];
        }

        Dictionary<string, List<string>> modesByMap = new(StringComparer.OrdinalIgnoreCase);
        foreach (string modeId in MapModes.AllModes) {
            foreach (string map in MapCycleReader.Read(paths.MapCycleFile(modeId))) {
                if (!modesByMap.TryGetValue(map, out List<string>? modes)) {
                    modes = [];
                    modesByMap[map] = modes;
                }
                modes.Add(modeId);
            }
        }

        IReadOnlySet<string> packMaps = ReadPackMapNames(Path.Combine(paths.ToolsDir, "maps.csv"));
        var entries = new List<MapLibraryEntry>();
        foreach (string mapFile in Directory.EnumerateFiles(paths.MapsDir, "*.vpk")) {
            string name = Path.GetFileNameWithoutExtension(mapFile).ToLowerInvariant();
            if (!IsPlayableMapPackage(name)) {
                continue;
            }
            NavFileState nav = NavFileScanner.Check(paths, name);
            entries.Add(new MapLibraryEntry(
                name,
                SourceOf(name, packMaps),
                modesByMap.TryGetValue(name, out List<string>? modes) ? modes : [],
                nav.IsUsable,
                new FileInfo(mapFile).Length,
                nav.IsOutdated,
                MapResources.FindMissing(mapFile, paths.GameDir)));
        }
        return entries.OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Loose map packages (.vpk) that are not installed yet, and every archive: what
    /// an archive holds is only known after unpacking, which Update maps does.
    /// </summary>
    public static IReadOnlyList<WaitingMap> ScanCustomMaps(string customMapsDir, string mapsDir) {
        if (!Directory.Exists(customMapsDir)) {
            return [];
        }

        var waiting = new List<WaitingMap>();
        foreach (string file in Directory.EnumerateFiles(customMapsDir)) {
            string extension = Path.GetExtension(file).ToLowerInvariant();
            string fileName = Path.GetFileName(file);
            if (ArchiveExtensions.Contains(extension)) {
                waiting.Add(new WaitingMap(fileName, IsArchive: true));
                continue;
            }
            bool isNewMap = extension == ".vpk" && !File.Exists(Path.Combine(mapsDir, fileName));
            if (isNewMap) {
                waiting.Add(new WaitingMap(fileName, IsArchive: false));
            }
        }
        return waiting.OrderBy(map => map.FileName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    internal static MapSource SourceOf(string mapName, IReadOnlySet<string> packMaps) {
        if (InstallComponents.StockMaps.Contains(mapName)) {
            return MapSource.Stock;
        }
        if (packMaps.Contains(mapName)) {
            return MapSource.MapPack;
        }
        return MapSource.Custom;
    }

    private static IReadOnlySet<string> ReadPackMapNames(string mapListFile) {
        try {
            if (!File.Exists(mapListFile)) {
                return new HashSet<string>();
            }
            return MapList.Load(mapListFile)
                .Select(entry => entry.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        } catch (IOException) {
            return new HashSet<string>();
        }
    }
}
