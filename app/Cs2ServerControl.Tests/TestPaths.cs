using System.IO;
using System.Runtime.CompilerServices;

namespace Cs2ServerControl.Tests;

internal static class TestPaths {
    /// <summary>A file below the package's server\ folder, or null when the server is not installed.</summary>
    public static string? FindServerFile(params string[] parts) {
        // Found from this source file, so the tests also work when they are
        // built into another folder (dotnet test --artifacts-path uses its own folder).
        string? directory = Path.GetDirectoryName(SourceFile());
        while (directory != null) {
            string serverDir = Path.Combine(directory, "server");
            if (Directory.Exists(serverDir) && Directory.Exists(Path.Combine(directory, "tools"))) {
                string file = Path.Combine([serverDir, .. parts]);
                return File.Exists(file) ? file : null;
            }
            directory = Path.GetDirectoryName(directory);
        }
        return null;
    }

    private static string SourceFile([CallerFilePath] string path = "") {
        return path;
    }
}
