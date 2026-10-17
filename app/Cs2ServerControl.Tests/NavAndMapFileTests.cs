using System.IO;
using Cs2ServerControl.Services;
using Xunit;

namespace Cs2ServerControl.Tests;

public class NavAndMapFileTests {
    [Fact]
    public void ReadsAVersionFiveNavFile() {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.ASCII, leaveOpen: true)) {
            writer.Write(0xFEEDFACEu);
            writer.Write(5u);
            writer.Write(123456u); // bsp size
            writer.Write((ushort)1); // place directory
            writer.Write((ushort)5);
            writer.Write("BombA"u8.ToArray());
            writer.Write(2u); // areas
            WriteArea(writer, id: 1, connectsTo: 2, hidingSpots: 1);
            WriteArea(writer, id: 2, connectsTo: 1, hidingSpots: 0);
        }
        stream.Position = 0;

        NavMesh mesh = NavMeshReader.Read(new BinaryReader(stream));

        Assert.Equal(2, mesh.Areas.Count);
        Assert.Equal(2, mesh.Connections.Count);
        Assert.Single(mesh.HidingSpots);
        Assert.Equal(new System.Numerics.Vector3(64, 0, 10), mesh.Areas[0].NorthEast);
    }

    private static void WriteArea(BinaryWriter writer, uint id, uint connectsTo, int hidingSpots) {
        writer.Write(id);
        writer.Write((byte)0);
        writer.Write(0f); writer.Write(0f); writer.Write(0f);   // low corner
        writer.Write(64f); writer.Write(64f); writer.Write(0f); // high corner
        writer.Write(10f); // north east z
        writer.Write(0f);  // south west z
        for (int direction = 0; direction < 4; direction++) {
            writer.Write(direction == 0 ? 1u : 0u);
            if (direction == 0) {
                writer.Write(connectsTo);
            }
        }
        writer.Write((byte)hidingSpots);
        for (int spot = 0; spot < hidingSpots; spot++) {
            writer.Write(100u + (uint)spot);
            writer.Write(8f); writer.Write(8f); writer.Write(0f);
            writer.Write((byte)1);
        }
        writer.Write((byte)0); // approach areas
        writer.Write(0u);      // encounter paths
        writer.Write((ushort)1); // place
    }

    [Fact]
    public void RejectsWrongMagicAndShortFiles() {
        Assert.Throws<InvalidDataException>(() => NavMeshReader.Read(new BinaryReader(new MemoryStream([1, 2, 3, 4, 5, 0, 0, 0]))));
        Assert.Throws<InvalidDataException>(() => NavMeshReader.Read(new BinaryReader(new MemoryStream([0xCE, 0xFA, 0xED, 0xFE, 5, 0, 0, 0]))));
    }

    [Fact]
    public void ReadsTheRealNavFileWhenInstalled() {
        string? file = TestPaths.FindServerFile("cstrike", "maps", "de_dust2.nav");
        if (file == null) {
            return;
        }

        NavMesh mesh = NavMeshReader.Load(file);

        Assert.True(mesh.Areas.Count > 100);
        Assert.NotEmpty(mesh.Connections);
    }

    [Theory]
    [InlineData("  Merging navigation areas...", "Merging navigation areas")]
    [InlineData("  Connecting navigation areas...", "Connecting navigation areas")]
    [InlineData("L 10/17/2026 - 12:20:49: \"Don<1><BOT><>\" joined team \"TERRORIST\"", null)]
    public void ReadsZBotSteps(string line, string? step) {
        Assert.Equal(step, NavFileScanner.ReadStep(line));
    }

    [Fact]
    public void RecognizesTheSavedLine() {
        Assert.True(NavFileScanner.IsDoneLine("Navigation file 'cstrike\\maps\\de_dust2.nav' saved."));
        Assert.False(NavFileScanner.IsDoneLine("ERROR: Failed to load 'maps/35hp_2.nav' file navigation map!"));
    }

    [Fact]
    public void ReadsKillIconRegionsForThe640Hud() {
        string[] hud = [
            "d_knife 320 320hud1 96 0 24 8",
            "d_knife 640 640hud1 192 0 48 16",
            "d_headshot\t640\t640hud1\t0\t240\t36\t16",
            "broken line",
        ];

        var regions = KillIcons.ParseHudRegions(hud);

        Assert.Equal(2, regions.Count);
        Assert.Equal(new KillIcons.HudRegion("640hud1", 192, 0, 48, 16), regions["d_knife"]);
        Assert.Equal(36, regions["d_headshot"].Width);
    }

    [Fact]
    public void CountsSpawnsAndObjectiveOfDust2WhenInstalled() {
        string? file = TestPaths.FindServerFile("cstrike", "maps", "de_dust2.bsp");
        if (file == null) {
            return;
        }

        IReadOnlyDictionary<string, int> entities = MapDetailsReader.ReadEntityCounts(file);

        Assert.True(entities["info_player_deathmatch"] >= 10);
        Assert.True(entities["info_player_start"] >= 10);
        Assert.True(entities.ContainsKey("func_bomb_target"));
    }

    [Fact]
    public void TellsTheNavHelperFromTheGameServer() {
        const string helper = "hlds.exe -console -game cstrike -port 27016 +ip 127.0.0.1 +maxplayers 2 +map de_dust";
        const string gameServer = "hlds.exe -console -condebug -game cstrike -port 27015 +ip 192.168.1.50 +map de_dust2";

        Assert.True(ServerProcess.IsNavHelperCommandLine(helper));
        Assert.False(ServerProcess.IsNavHelperCommandLine(gameServer));
    }

    [Fact]
    public void ANavFileForAnotherBuildOfTheMapIsOutdated() {
        string folder = Path.Combine(Path.GetTempPath(), "cs16-nav-outdated-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try {
            string bspFile = Path.Combine(folder, "map.bsp");
            string navFile = Path.Combine(folder, "map.nav");
            File.WriteAllBytes(bspFile, new byte[1000]);

            WriteNavHeader(navFile, bspSize: 1000);
            Assert.False(NavFileScanner.IsOutdated(navFile, bspFile));

            WriteNavHeader(navFile, bspSize: 999);
            Assert.True(NavFileScanner.IsOutdated(navFile, bspFile));
            Assert.Equal(999, NavMeshReader.ReadBspSize(navFile));
        } finally {
            Directory.Delete(folder, recursive: true);
        }
    }

    private static void WriteNavHeader(string navFile, uint bspSize) {
        using var writer = new BinaryWriter(File.Create(navFile));
        writer.Write(0xFEEDFACEu);
        writer.Write(5u);
        writer.Write(bspSize);
    }
}
