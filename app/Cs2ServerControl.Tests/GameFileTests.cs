using System.IO;
using System.Windows.Media;
using Cs2ServerControl.Services;
using Xunit;

namespace Cs2ServerControl.Tests;

/// <summary>Reads the installed CS2 server's own files; without an installed server there is nothing to check.</summary>
public class GameFileTests {
    [Fact]
    public void ReadsTheWorldCollisionOfDust2WhenInstalled() {
        string? file = TestPaths.FindServerFile("game", "csgo", "maps", "de_dust2.vpk");
        if (file == null) {
            return;
        }

        MapWireframe wireframe = Cs2MapLoader.Load(file, "de_dust2");

        Assert.Equal("de_dust2", wireframe.MapName);
        Assert.InRange(wireframe.Segments.Count, 10_000, 400_000);
        float width = wireframe.BoundsMax.X - wireframe.BoundsMin.X;
        float depth = wireframe.BoundsMax.Y - wireframe.BoundsMin.Y;
        Assert.InRange(width, 3000, 9000);
        Assert.InRange(depth, 3000, 8000);
    }

    [Fact]
    public void DrawsTheKillFeedIconsFromTheGameFilesWhenInstalled() {
        string? package = TestPaths.FindServerFile("game", "csgo", "pak01_dir.vpk");
        if (package == null) {
            return;
        }
        string root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(package)!, "..", "..", ".."));

        RunOnStaThread(() => {
            var icons = new KillIcons(new PackagePaths(root), Colors.White);

            ImageSource? ak47 = icons.ForWeapon("ak47");
            Assert.NotNull(ak47);
            Assert.True(ak47.Width > ak47.Height, "a rifle icon is wider than high");
            Assert.NotNull(icons.ForWeapon("knife_some_new_skin"));
            Assert.NotNull(icons.Headshot);
            Assert.NotNull(icons.Skull);
            Assert.Null(icons.ForWeapon("no_such_weapon"));
        });
    }

    private static void RunOnStaThread(Action action) {
        Exception? failure = null;
        var thread = new Thread(() => {
            try {
                action();
            } catch (Exception exception) {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure != null) {
            throw new Xunit.Sdk.XunitException(failure.ToString());
        }
    }
}
