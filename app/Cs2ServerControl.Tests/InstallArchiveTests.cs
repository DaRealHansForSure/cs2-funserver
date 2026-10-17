using System.IO;
using Cs2ServerControl.Install;
using Xunit;

namespace Cs2ServerControl.Tests;

/// <summary>
/// The archive extractor that unpacks Metamod and CounterStrikeSharp.
/// </summary>
public class InstallArchiveTests : IDisposable {
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "cs2-archive-tests-" + Guid.NewGuid().ToString("N"));

    public InstallArchiveTests() {
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose() {
        FileTree.DeleteFolderQuietly(_tempDir);
    }

    [Fact]
    public void ReportsAnArchiveNoReaderUnderstands() {
        string broken = Path.Combine(_tempDir, "broken.rar");
        File.WriteAllText(broken, "this is not an archive");

        InstallException exception = Assert.Throws<InstallException>(() => new ArchiveExtractor(Path.Combine(_tempDir, "work")).Expand(broken));

        Assert.Equal("Could not extract broken.rar. Install 7-Zip (7-zip.org) and run the installer again.", exception.Message);
    }
}
