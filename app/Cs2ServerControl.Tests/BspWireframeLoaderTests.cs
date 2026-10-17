using Cs2ServerControl.Services;
using Xunit;

namespace Cs2ServerControl.Tests;

public class BspWireframeLoaderTests {
    [Fact]
    public void LoadsDust2WorldGeometry() {
        string? file = TestPaths.FindServerFile("cstrike", "maps", "de_dust2.bsp");
        if (file == null) {
            // Only runs on a PC with the server installed.
            return;
        }

        BspWireframe wireframe = BspWireframeLoader.Load(file);

        Assert.NotEmpty(wireframe.Segments);
        Assert.InRange(wireframe.BoundsMin.X, -3000, -1500);
        Assert.InRange(wireframe.BoundsMax.X, 1200, 2500);
        Assert.InRange(wireframe.BoundsMin.Y, -2000, -1000);
        Assert.InRange(wireframe.BoundsMax.Y, 3300, 4200);
        Assert.True(wireframe.MaxZ > wireframe.MinZ);
    }

    [Fact]
    public void CachesByFileAndTimestamp() {
        string? file = TestPaths.FindServerFile("cstrike", "maps", "de_dust2.bsp");
        if (file == null) {
            return;
        }

        Assert.Same(BspWireframeLoader.Load(file), BspWireframeLoader.Load(file));
    }

    [Fact]
    public void RejectsOtherBspVersions() {
        byte[] data = new byte[4 + 15 * 8];
        BitConverter.GetBytes(29).CopyTo(data, 0);

        var exception = Assert.Throws<BspFormatException>(() => BspWireframeLoader.Parse(data, "quake1"));
        Assert.Contains("29", exception.Message);
    }

    [Fact]
    public void RejectsTruncatedFiles() {
        Assert.Throws<BspFormatException>(() => BspWireframeLoader.Parse([30, 0, 0, 0], "short"));
    }

    [Fact]
    public void RejectsLumpsOutsideTheFile() {
        byte[] data = new byte[4 + 15 * 8];
        BitConverter.GetBytes(30).CopyTo(data, 0);
        BitConverter.GetBytes(1000).CopyTo(data, 4);
        BitConverter.GetBytes(50).CopyTo(data, 8);

        Assert.Throws<BspFormatException>(() => BspWireframeLoader.Parse(data, "broken"));
    }
}
