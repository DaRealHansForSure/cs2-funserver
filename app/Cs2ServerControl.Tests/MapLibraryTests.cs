using System.IO;
using Cs2ServerControl.Install;
using Cs2ServerControl.Services;
using Xunit;

namespace Cs2ServerControl.Tests;

public class MapLibraryTests : IDisposable {
    private readonly string _rootDir = Path.Combine(Path.GetTempPath(), "cs16-maplibrary-tests-" + Guid.NewGuid().ToString("N"));
    private readonly PackagePaths _paths;

    public MapLibraryTests() {
        _paths = new PackagePaths(_rootDir);
        Directory.CreateDirectory(_paths.MapsDir);
        Directory.CreateDirectory(_paths.FunConfigDir);
        Directory.CreateDirectory(_paths.ToolsDir);
    }

    public void Dispose() {
        FileTree.DeleteFolderQuietly(_rootDir);
    }

    [Fact]
    public void ListsEveryInstalledMapWithItsModesNavAndSource() {
        File.WriteAllBytes(_paths.MapFile("de_dust2"), new byte[2048]);
        File.WriteAllBytes(_paths.MapFile("fy_snow"), new byte[10]);
        File.WriteAllBytes(_paths.MapFile("my_own_map"), new byte[10]);
        File.WriteAllText(_paths.NavFile("de_dust2"), "nav");
        File.WriteAllLines(_paths.MapCycleFile("classic"), ["de_dust2", "fy_snow"]);
        File.WriteAllLines(_paths.MapCycleFile("gungame"), ["fy_snow"]);
        File.WriteAllLines(Path.Combine(_paths.ToolsDir, "maps.csv"), ["Name,Url,ArchiveName,ModId", "fy_snow,https://example.com,fy_snow.rar,1"]);

        IReadOnlyList<MapLibraryEntry> entries = MapLibrary.Scan(_paths);

        Assert.Equal(["de_dust2", "fy_snow", "my_own_map"], entries.Select(entry => entry.Name));
        MapLibraryEntry dust2 = entries[0];
        Assert.Equal(MapSource.Stock, dust2.Source);
        Assert.True(dust2.HasNav);
        Assert.Equal(["classic"], dust2.ModeIds);
        Assert.Equal(2048, dust2.SizeBytes);

        MapLibraryEntry snow = entries[1];
        Assert.Equal(MapSource.MapPack, snow.Source);
        Assert.False(snow.HasNav);
        Assert.Equal(["classic", "gungame"], snow.ModeIds);

        MapLibraryEntry own = entries[2];
        Assert.Equal(MapSource.Custom, own.Source);
        Assert.Empty(own.ModeIds);
    }

    [Fact]
    public void CustomMapsListsArchivesAndOnlyMapPackagesNotInstalledYet() {
        string customDir = Path.Combine(_rootDir, "custom-maps");
        Directory.CreateDirectory(customDir);
        File.WriteAllText(Path.Combine(customDir, "PUT_MAPS_HERE.txt"), "hint");
        File.WriteAllBytes(Path.Combine(customDir, "de_dust2.vpk"), [1]);
        File.WriteAllBytes(Path.Combine(customDir, "new_map.vpk"), [1]);
        File.WriteAllBytes(Path.Combine(customDir, "pack.RAR"), [1]);
        File.WriteAllBytes(_paths.MapFile("de_dust2"), [1]);

        IReadOnlyList<WaitingMap> waiting = MapLibrary.ScanCustomMaps(customDir, _paths.MapsDir);

        Assert.Equal([new WaitingMap("new_map.vpk", IsArchive: false), new WaitingMap("pack.RAR", IsArchive: true)], waiting);
    }

    [Fact]
    public void AnEmptyPackageHasNoMaps() {
        var empty = new PackagePaths(Path.Combine(_rootDir, "nothing"));

        Assert.Empty(MapLibrary.Scan(empty));
        Assert.Empty(MapLibrary.ScanCustomMaps(Path.Combine(_rootDir, "nothing"), empty.MapsDir));
    }
}
