using System.IO;
using Cs2ServerControl.Install;
using Xunit;

namespace Cs2ServerControl.Tests;

public class SteamLibraryFinderTests : IDisposable {
    private readonly string _library = Path.Combine(Path.GetTempPath(), "cs2-steamlib-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose() {
        FileTree.DeleteFolderQuietly(_library);
    }

    [Fact]
    public void ReadsTheLibraryPathsWithSingleBackslashes() {
        string[] vdf = [
            "\"libraryfolders\"",
            "{",
            "\t\"0\"",
            "\t{",
            "\t\t\"path\"\t\t\"C:\\\\Program Files (x86)\\\\Steam\"",
            "\t\t\"apps\"",
            "\t\t{",
            "\t\t\t\"730\"\t\t\"33070076096\"",
            "\t\t}",
            "\t}",
            "\t\"1\"",
            "\t{",
            "\t\t\"path\"\t\t\"D:\\\\SteamLibrary\"",
            "\t}",
            "}",
        ];

        Assert.Equal([@"C:\Program Files (x86)\Steam", @"D:\SteamLibrary"], SteamLibraryFinder.ReadLibraryPaths(vdf));
    }

    [Fact]
    public void FindsAFullyInstalledCs2InALibrary() {
        WriteManifest(stateFlags: "4");
        string game = Path.Combine(_library, "steamapps", "common", "Counter-Strike Global Offensive");
        Directory.CreateDirectory(Path.Combine(game, "game", "bin", "win64"));
        File.WriteAllText(Path.Combine(game, "game", "bin", "win64", "cs2.exe"), "exe");

        Assert.Equal(game, SteamLibraryFinder.CheckLibrary(_library));
    }

    [Fact]
    public void IgnoresACs2ThatIsStillDownloadingOrMissing() {
        Assert.Null(SteamLibraryFinder.CheckLibrary(_library));

        WriteManifest(stateFlags: "1026");
        Assert.Null(SteamLibraryFinder.CheckLibrary(_library));

        // Fully installed by the manifest, but the folder has no cs2.exe.
        WriteManifest(stateFlags: "4");
        Assert.Null(SteamLibraryFinder.CheckLibrary(_library));
    }

    private void WriteManifest(string stateFlags) {
        Directory.CreateDirectory(Path.Combine(_library, "steamapps"));
        File.WriteAllLines(Path.Combine(_library, "steamapps", "appmanifest_730.acf"), [
            "\"AppState\"",
            "{",
            "\t\"appid\"\t\t\"730\"",
            $"\t\"StateFlags\"\t\t\"{stateFlags}\"",
            "\t\"installdir\"\t\t\"Counter-Strike Global Offensive\"",
            "}",
        ]);
    }
}
