using System.IO;
using Cs2ServerControl.Install;
using Xunit;

namespace Cs2ServerControl.Tests;

public class FileTreeTests : IDisposable {
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "cs2-filetree-tests-" + Guid.NewGuid().ToString("N"));

    public FileTreeTests() {
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose() {
        FileTree.DeleteFolderQuietly(_tempDir);
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
}
