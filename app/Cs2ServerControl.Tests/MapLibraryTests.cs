using System.IO;
using Cs2ServerControl.Install;
using Cs2ServerControl.Services;
using Xunit;

namespace Cs2ServerControl.Tests;

public class MapLibraryTests : IDisposable {
    private readonly string _rootDir = Path.Combine(Path.GetTempPath(), "cs2-maplibrary-tests-" + Guid.NewGuid().ToString("N"));
    private readonly PackagePaths _paths;

    public MapLibraryTests() {
        _paths = new PackagePaths(_rootDir);
        Directory.CreateDirectory(_paths.MapsDir);
        Directory.CreateDirectory(_paths.FunConfigDir);
    }

    public void Dispose() {
        FileTree.DeleteFolderQuietly(_rootDir);
    }

    [Fact]
    public void ListsEveryPlayableMapWithItsModesAndSize() {
        File.WriteAllBytes(_paths.MapFile("de_dust2"), new byte[2048]);
        File.WriteAllBytes(_paths.MapFile("de_dust2_vanity"), new byte[10]);
        File.WriteAllBytes(_paths.MapFile("ar_shoots"), new byte[10]);
        File.WriteAllBytes(_paths.MapFile("cs_office"), new byte[10]);
        File.WriteAllLines(_paths.MapCycleFile("classic"), ["de_dust2", "ar_shoots"]);
        File.WriteAllLines(_paths.MapCycleFile("gungame"), ["ar_shoots"]);

        IReadOnlyList<MapLibraryEntry> entries = MapLibrary.Scan(_paths);

        Assert.Equal(["ar_shoots", "cs_office", "de_dust2"], entries.Select(entry => entry.Name));
        Assert.Equal(["classic", "gungame"], entries[0].ModeIds);
        Assert.Empty(entries[1].ModeIds);
        Assert.Equal(["classic"], entries[2].ModeIds);
        Assert.Equal(2048, entries[2].SizeBytes);
    }

    [Fact]
    public void AnEmptyPackageHasNoMaps() {
        Assert.Empty(MapLibrary.Scan(new PackagePaths(Path.Combine(_rootDir, "nothing"))));
    }
}
