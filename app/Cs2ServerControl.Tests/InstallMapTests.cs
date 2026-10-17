using System.IO;
using System.Runtime.CompilerServices;
using Cs2ServerControl.Install;
using Cs2ServerControl.Services;
using Xunit;

namespace Cs2ServerControl.Tests;

public class InstallMapTests : IDisposable {
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "cs16-install-tests-" + Guid.NewGuid().ToString("N"));

    public InstallMapTests() {
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose() {
        FileTree.DeleteFolderQuietly(_tempDir);
    }

    [Fact]
    public void ParsesTheMapListWithQuotesAndMissingModId() {
        string[] lines = [
            "Name,Url,ArchiveName,ModId",
            "fy_snow,https://gamebanana.com/dl/287523,fy_snow.rar,86704",
            "",
            "\"de_rats, the\",\"https://example.com/dl?a=1\",de_rats.rar",
        ];

        IReadOnlyList<MapListEntry> entries = MapList.Parse(lines);

        Assert.Equal([
            new MapListEntry("fy_snow", "https://gamebanana.com/dl/287523", "fy_snow.rar", "86704"),
            new MapListEntry("de_rats, the", "https://example.com/dl?a=1", "de_rats.rar", ""),
        ], entries);
    }

    [Fact]
    public void ReadsThePackageMapList() {
        string mapList = Path.Combine(PackageRoot(), "tools", "maps.csv");
        if (!File.Exists(mapList)) {
            return;
        }

        IReadOnlyList<MapListEntry> entries = MapList.Load(mapList);

        Assert.True(entries.Count > 30);
        Assert.All(entries, entry => {
            Assert.StartsWith("https://", entry.Url);
            Assert.NotEmpty(entry.ArchiveName);
        });
        Assert.Contains(entries, entry => entry.Name == "fy_iceworld" && entry.ModId == "86288");
    }

    [Theory]
    [InlineData("zm_ice_attack", "zombie")]
    [InlineData("deathrun_temple", "deathrun")]
    [InlineData("35hp_2", "classic")]
    [InlineData("35_hp", "classic")]
    [InlineData("he_glass", "classic")]
    [InlineData("ka_matrix", "classic")]
    [InlineData("surf_ski_2", "classic deathmatch")]
    [InlineData("fy_snow", "classic gungame deathmatch")]
    [InlineData("gg_mini_simpsons", "classic gungame deathmatch")]
    [InlineData("cs_rats2", "classic gungame deathmatch")]
    [InlineData("de_dust2_2x2", "classic gungame deathmatch")]
    [InlineData("de_dust2", "classic gungame deathmatch zombie")]
    [InlineData("cs_mansion", "classic gungame deathmatch")]
    [InlineData("cs_italy", "classic deathmatch")]
    [InlineData("de_survivor", "classic zombie")]
    [InlineData("cs_747", "classic")]
    [InlineData("de_westwood", "classic")]
    public void SortsMapsIntoModesByName(string mapName, string expectedModes) {
        Assert.Equal(expectedModes.Split(' '), MapModes.ModesOf(mapName));
    }

    [Fact]
    public void BuildsEmptyCyclesWithoutMaps() {
        IReadOnlyDictionary<string, List<string>> cycles = MapModes.BuildCycles([]);

        Assert.Equal(5, cycles.Count);
        Assert.All(cycles.Values, Assert.Empty);
    }

    [Fact]
    public void RejectsAnArchiveWithoutMap() {
        string archive = CreateFolder("extracted");
        WriteFile(archive, "readme.txt", "no map here");

        InstallException exception = Assert.Throws<InstallException>(() => new MapContentCopier(CreateFolder("cstrike")).Install(archive));

        Assert.Equal("no .bsp file in the archive", exception.Message);
    }

    [Fact]
    public void OverlayCopyMergesFoldersAndReplacesFiles() {
        string overlay = CreateFolder("server-files");
        WriteFile(overlay, "server.cfg", "new settings");
        WriteFile(overlay, @"modes\classic.cfg", "classic");
        Directory.CreateDirectory(Path.Combine(overlay, @"addons\empty"));

        string cstrike = CreateFolder("cstrike");
        WriteFile(cstrike, "server.cfg", "old settings");
        WriteFile(cstrike, "local.cfg", "rcon_password \"keep\"");
        WriteFile(cstrike, @"modes\zombie.cfg", "zombie");

        FileTree.CopyContents(overlay, cstrike);

        Assert.Equal("new settings", ReadFile(cstrike, "server.cfg"));
        Assert.Equal("rcon_password \"keep\"", ReadFile(cstrike, "local.cfg"));
        Assert.Equal("classic", ReadFile(cstrike, @"modes\classic.cfg"));
        Assert.Equal("zombie", ReadFile(cstrike, @"modes\zombie.cfg"));
        Assert.True(Directory.Exists(Path.Combine(cstrike, @"addons\empty")));
    }

    [Fact]
    public void ConfigFilesKeepTheirBytes() {
        string file = Path.Combine(_tempDir, "users.ini");
        byte[] original = [0x3B, 0x20, 0xE4, 0xC3, 0xBC, 0x0D, 0x0A, 0x61, 0x0D, 0x0A];
        File.WriteAllBytes(file, original);

        FileTree.WriteLines(file, FileTree.ReadLines(file));

        Assert.Equal(original, File.ReadAllBytes(file));
    }

    private string CreateFolder(string name) {
        string folder = Path.Combine(_tempDir, name);
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static void WriteFile(string root, string relativePath, string content) {
        string path = Path.Combine(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static string ReadFile(string root, string relativePath) {
        return File.ReadAllText(Path.Combine(root, relativePath));
    }

    internal static string PackageRoot([CallerFilePath] string sourceFile = "") {
        // The test project sits in app\Cs2ServerControl.Tests, two levels below the package.
        return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", ".."));
    }

    [Fact]
    public void ReadsOnlyModelAndSpriteFilesAMapNames() {
        IReadOnlyList<string> files = MapResources.ParseRequiredFiles(
            "{\n\"model\" \"*12\"\n}\n{\n\"model\" \"Models\\Tree.MDL\"\n\"message\" \"ambience/wind.wav\"\n}\n{\n\"model\" \"sprites/glow.spr\"\n}\n{\n\"model\" \"sprites/glow.spr\"\n}\n");

        Assert.Equal(["models/tree.mdl", "sprites/glow.spr"], files);
    }

    /// <summary>A GoldSrc .bsp with only the header and the entity lump, enough for MapResources.</summary>
    private static byte[] BspWithEntities(string entities) {
        const int HeaderSize = 4 + 15 * 8;
        byte[] text = System.Text.Encoding.ASCII.GetBytes(entities);
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.ASCII, leaveOpen: true)) {
            writer.Write(30);
            writer.Write(HeaderSize);
            writer.Write(text.Length);
            for (int lump = 1; lump < 15; lump++) {
                writer.Write(0);
                writer.Write(0);
            }
            writer.Write(text);
        }
        return stream.ToArray();
    }
}
