using System.IO;
using Cs2ServerControl.Install;
using Xunit;

namespace Cs2ServerControl.Tests;

/// <summary>
/// Extracts archives from the download cache, when the package has one. On a
/// machine without tools\downloads the tests have nothing to check and pass.
/// </summary>
public class InstallArchiveTests : IDisposable {
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "cs16-archive-tests-" + Guid.NewGuid().ToString("N"));

    public InstallArchiveTests() {
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose() {
        FileTree.DeleteFolderQuietly(_tempDir);
    }

    [Fact]
    public void ExtractsTheZBotProfiles() {
        string? archive = CachedDownload("zbot-profiles.zip");
        if (archive == null) {
            return;
        }

        string extracted = new ArchiveExtractor(_tempDir).Expand(archive);

        Assert.Equal(Path.Combine(_tempDir, "zbot-profiles"), extracted);
        Assert.NotNull(FileTree.FindFirstFile(extracted, "BotProfile.db"));
        Assert.NotNull(FileTree.FindFirstFile(extracted, "BotChatter.db"));
    }

    [Theory]
    [InlineData(@"maps\fy_snow.rar")]
    [InlineData(@"maps\de_rats.rar")]
    [InlineData(@"maps\surf_ski_2.rar")]
    [InlineData(@"maps\deathrun_dojo.rar")]
    [InlineData(@"maps\awp_india.7z")]
    [InlineData(@"maps\de_simpsons.7z")]
    [InlineData(@"maps\aim_map.zip")]
    public void ExtractsMapArchivesAndFindsTheirMaps(string cachedFile) {
        string? archive = CachedDownload(cachedFile);
        if (archive == null) {
            return;
        }
        string cstrike = Path.Combine(_tempDir, "cstrike");

        string extracted = new ArchiveExtractor(Path.Combine(_tempDir, "work")).Expand(archive);
        IReadOnlyList<string> maps = new MapContentCopier(cstrike).Install(extracted);

        string expectedMap = Path.GetFileNameWithoutExtension(archive);
        Assert.Contains(expectedMap, maps);
        Assert.True(File.Exists(Path.Combine(cstrike, "maps", expectedMap + ".bsp")));
    }

    [Theory]
    [InlineData(@"sounds\blackshot_killstreak_quakesounds.rar", "Blackshot")]
    [InlineData(@"sounds\kill_sounds_pack_ut_recreation.rar", "UtRecreation")]
    public void ExtractsTheSoundPacksWithTheExpectedLayout(string cachedFile, string packName) {
        string? archive = CachedDownload(cachedFile);
        if (archive == null) {
            return;
        }

        string extracted = new ArchiveExtractor(_tempDir).Expand(archive);

        string packFolder = Path.Combine(extracted, InstallComponents.QuakeSoundPacks[packName].Folder);
        IEnumerable<QuakeSound> sounds = InstallComponents.QuakeSounds.Where(sound => sound.PackName == packName);
        Assert.All(sounds, sound => Assert.True(File.Exists(Path.Combine(packFolder, sound.SourceName)), sound.SourceName));
    }

    [Fact]
    public void ReportsAnArchiveNoReaderUnderstands() {
        string broken = Path.Combine(_tempDir, "broken.rar");
        File.WriteAllText(broken, "this is not an archive");

        InstallException exception = Assert.Throws<InstallException>(() => new ArchiveExtractor(Path.Combine(_tempDir, "work")).Expand(broken));

        Assert.Equal("Could not extract broken.rar. Install 7-Zip (7-zip.org) and run the installer again.", exception.Message);
    }

    private static string? CachedDownload(string relativePath) {
        string path = Path.Combine(InstallMapTests.PackageRoot(), "tools", "downloads", relativePath);
        return File.Exists(path) ? path : null;
    }
}
