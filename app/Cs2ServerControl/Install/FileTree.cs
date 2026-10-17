using System.IO;
using System.Text;

namespace Cs2ServerControl.Install;

/// <summary>File system helpers shared by the installer steps.</summary>
internal static class FileTree {
    private static readonly EnumerationOptions Recursive = new() {
        RecurseSubdirectories = true,
        MatchType = MatchType.Simple,
        MatchCasing = MatchCasing.CaseInsensitive,
    };

    private static readonly EnumerationOptions TopLevel = new() {
        RecurseSubdirectories = false,
        MatchType = MatchType.Simple,
        MatchCasing = MatchCasing.CaseInsensitive,
    };

    // Config files from the archives are plain ASCII, but a hand edited one
    // may hold ANSI or UTF-8 bytes. Latin-1 maps every byte to one char and
    // back, so a rewrite never changes lines the installer did not touch.
    private static readonly Encoding ByteExact = Encoding.Latin1;

    public static string? FindFirstFile(string root, string fileName) {
        if (!Directory.Exists(root)) {
            return null;
        }
        return Directory.EnumerateFiles(root, fileName, Recursive).FirstOrDefault();
    }

    public static string? FindFirstFolder(string root, string folderName) {
        if (!Directory.Exists(root)) {
            return null;
        }
        return Directory.EnumerateDirectories(root, folderName, Recursive).FirstOrDefault();
    }

    public static IEnumerable<string> FilesWithExtension(string root, string extension, bool recursive) {
        if (!Directory.Exists(root)) {
            return [];
        }
        // Filtered by hand instead of "*.bsp", so a pattern can never match
        // longer extensions like ".bsp.ztmp".
        return Directory.EnumerateFiles(root, "*", recursive ? Recursive : TopLevel)
            .Where(file => Path.GetExtension(file).Equals(extension, StringComparison.OrdinalIgnoreCase));
    }

    public static IEnumerable<string> AllFiles(string root) {
        if (!Directory.Exists(root)) {
            return [];
        }
        return Directory.EnumerateFiles(root, "*", Recursive);
    }

    public static IEnumerable<string> FoldersNamed(string root, string folderName) {
        if (!Directory.Exists(root)) {
            return [];
        }
        return Directory.EnumerateDirectories(root, folderName, Recursive);
    }

    /// <summary>
    /// Copies everything inside <paramref name="sourceDir"/> into
    /// <paramref name="targetDir"/>, merging folders and replacing files.
    /// </summary>
    public static void CopyContents(string sourceDir, string targetDir) {
        Directory.CreateDirectory(targetDir);
        foreach (string sourceFile in AllFiles(sourceDir)) {
            string targetFile = Path.Combine(targetDir, Path.GetRelativePath(sourceDir, sourceFile));
            Directory.CreateDirectory(Path.GetDirectoryName(targetFile)!);
            File.Copy(sourceFile, targetFile, overwrite: true);
        }
        // Empty folders of the source still show up in the target.
        foreach (string sourceFolder in Directory.EnumerateDirectories(sourceDir, "*", Recursive)) {
            Directory.CreateDirectory(Path.Combine(targetDir, Path.GetRelativePath(sourceDir, sourceFolder)));
        }
    }

    public static void DeleteFolderQuietly(string folder) {
        try {
            if (Directory.Exists(folder)) {
                Directory.Delete(folder, recursive: true);
            }
        } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
            // A leftover temp folder is harmless, the next run starts by clearing it.
        }
    }

    public static List<string> ReadLines(string path) {
        if (!File.Exists(path)) {
            return [];
        }
        return [.. File.ReadAllLines(path, ByteExact)];
    }

    public static void WriteLines(string path, IEnumerable<string> lines) {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var content = new StringBuilder();
        foreach (string line in lines) {
            content.Append(line).Append("\r\n");
        }
        File.WriteAllText(path, content.ToString(), ByteExact);
    }
}
